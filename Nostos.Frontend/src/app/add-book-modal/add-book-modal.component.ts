import {
  ChangeDetectorRef,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
  computed,
  untracked,
  viewChild,
  ChangeDetectionStrategy,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { formatFileSize } from '../core/format';
import { finalize, lastValueFrom } from 'rxjs';
import { filter } from 'rxjs/operators';
import { HttpEventType } from '@angular/common/http';
import {
  BookLookupError,
  BooksService,
  Book as BookModel,
} from '../core/services/books.service';
import { ProvidersService } from '../core/services/providers.service';
import { ImportService } from '../core/services/import.service';
import { ToastService } from '../core/services/toast.service';
import { ProviderItem, ProviderMetadataOverrides } from '../core/dtos/provider.dtos';
import { Collection } from '../core/dtos/collection.dtos';
import { ModalShell } from '../ui/modal-shell/modal-shell.component';
import { DialogActionsComponent } from '../ui/dialog-actions/dialog-actions.component';
import { ButtonComponent } from '../ui/button/button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { BulkEbookAddComponent } from './bulk-ebook-add.component';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { AddBookFileStepComponent } from './add-book-file-step.component';
import { AddBookMetadataFormComponent } from './add-book-metadata-form.component';
import {
  AddBookSourcePickerComponent,
  type SourceSelection,
} from './add-book-source-picker.component';
import { AddBookUploadPanelComponent } from './add-book-upload-panel.component';
import { AddBookUploadSession } from './add-book-upload-session';
import {
  BOOK_TYPE_OPTIONS,
  DEFAULT_LANGUAGE,
  MAX_METADATA_READ_BYTES,
  languageName,
  sanitizeDate,
  sanitizeIsbn,
  type AddBookIntentKind,
  type BookForm,
} from './book-form';

/** See `AddBookModal.prefill`. */
export interface AddBookPrefill {
  title: string;
  author?: string | null;
  isbn?: string | null;
  publishedDate?: string | null;
}

/**
 * The Add book / Edit book sheet: the step flow and the writes.
 *
 * It used to also be the catalogue search, the file run and the whole record
 * (#1639). Those are now `app-add-book-source-picker` (discovery),
 * `AddBookUploadSession` (the file run), `app-add-book-file-step` (how the book
 * arrived) and `app-add-book-metadata-form` (the record). What stays here is
 * what genuinely spans the flow: which step is open, the draft, the create or
 * update call, and the source import.
 */
@Component({
  selector: 'app-add-book-modal',
  standalone: true,
  imports: [
    ModalShell,
    DialogActionsComponent,
    ButtonComponent,
    NostosIconComponent,
    ConfirmModal,
    BulkEbookAddComponent,
    AddBookFileStepComponent,
    AddBookMetadataFormComponent,
    AddBookSourcePickerComponent,
    AddBookUploadPanelComponent,
  ],
  providers: [AddBookUploadSession],
  templateUrl: './add-book-modal.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './add-book-modal.component.css',
})
export class AddBookModal {
  readonly bookTypeOptions = BOOK_TYPE_OPTIONS;
  private booksService = inject(BooksService);
  private providers = inject(ProvidersService);
  private toast = inject(ToastService);
  private readonly changeDetector = inject(ChangeDetectorRef);
  private readonly destroyRef = inject(DestroyRef);
  /** The import feed: opened on demand once an import has actually been queued. */
  private imports = inject(ImportService);
  /** The file run; it outlives neither this modal nor a closed one. */
  private readonly uploadSession = inject(AddBookUploadSession);

  // Inputs & Outputs
  isOpen = input.required<boolean>();

  /** Open Add Book directly in unified provider discovery. */
  sourceFirst = input<boolean>(false);
  /** Legacy focused-entry hook retained for callers outside the Library. */
  initialIntent = input<AddBookIntentKind | null>(null);
  collections = input.required<Collection[]>();
  book = input<BookModel | null>(null);
  /**
   * What is already known about the book being added, for a caller that
   * arrives with a title in hand (the reading list's "I got it"). It fills the
   * form and the source search once, when the sheet opens; nothing is saved
   * until the user says so.
   */
  prefill = input<AddBookPrefill | null>(null);
  closeModal = output<void>();
  bookAdded = output<void>();
  /**
   * The id of the library book this sheet just produced: created, matched to
   * one already there, or queued as an import whose row exists. Separate from
   * `bookAdded`, which only says the library should be read again.
   */
  bookCreated = output<string>();
  /**
   * A source import was queued and its book row does not exist yet; carries
   * the job id so the caller can follow it to the book.
   */
  importQueued = output<string>();
  bookUpdated = output<BookModel>();
  deleteBook = output<void>();

  /**
   * Add Book is acquisition-first. This signal records the choice that brought
   * the reader here; Edit Book bypasses acquisition entirely.
   */
  flowIntent = signal<AddBookIntentKind>('manual');
  sourceMode = signal(false);

  private readonly fileStep = viewChild(AddBookFileStepComponent);
  private readonly metadataForm = viewChild(AddBookMetadataFormComponent);
  private readonly sourcePicker = viewChild(AddBookSourcePickerComponent);

  // Computed State
  isEditMode = computed(() => !!this.book());
  awaitingLocalFile = computed(
    () => !this.isEditMode() && this.flowIntent() === 'upload' && !this.selectedFile(),
  );
  isFetching = signal(false);
  /** Blocks re-entrant create/update submits until the whole mutation pipeline settles. */
  isSubmitting = signal(false);
  readonly hasPendingUpload = computed(() => this.uploadSession.book() !== null);
  /** A chosen book file, so the step never looks empty after a choice. */
  readonly fileSummary = computed(() => {
    const file = this.selectedFile();
    if (!file) return null;
    const tracks = this.trackFiles();
    const bytes = tracks.length > 1 ? tracks.reduce((sum, track) => sum + track.size, 0) : file.size;
    const name = tracks.length > 1 ? `${tracks.length} audio files` : file.name;
    const size = formatFileSize(bytes);
    return size ? `${name} · ${size}` : name;
  });
  readonly uploadError = this.uploadSession.error;
  /**
   * The library already holds this exact book and it has its own file. The new
   * file is held back until the user chooses; it never replaces one silently.
   */
  readonly existingWithFile = signal<BookModel | null>(null);

  // Form State
  /**
   * The draft the whole step flow reads and submit() sends. Replace it, never
   * mutate it: the metadata form is OnPush and re-reads the object when the
   * input's identity changes (its own field edits mutate it in place through
   * `ngModel`, inside its own event, so its view is already being checked).
   */
  form: BookForm = {
    // No file yet, so nothing says this is a paper copy: a hand-entered book is
    // a metadata-only digital book. "Physical book" is a choice in the format
    // dropdown, and a chosen file decides ebook vs audiobook on its own.
    type: 'ebook',
    title: '',
    subtitle: null,

    author: null,
    editor: null,
    translator: null,
    narrator: null,

    description: null,

    isbn: null,
    asin: null,

    publisher: null,
    placeOfPublication: null,
    publishedDate: null,

    edition: null,
    pageCount: null,
    duration: null,
    language: DEFAULT_LANGUAGE,
    categories: null,
    series: null,
    volumeNumber: null,
    // Membership is a set: a book may belong to any number of collections.
    // The picker owns the checkbox list; this array is the value it edits.
    collectionIds: [],

    personalReview: null,
  };

  selectedFile = signal<File | null>(null);
  /** Two or more audio files chosen together: the tracks of one audiobook. */
  trackFiles = signal<File[]>([]);
  selectedCover = signal<File | null>(null);

  /**
   * What the cover zone shows: an object URL for a locally chosen file — which
   * is ours to release — or the provider's URL when the cover comes from the
   * source. A source-seeded form deliberately does NOT set a File: the import
   * fetches and stores the provider's cover server-side (`IncludeCover` defaults
   * to true), so there is nothing for the browser to upload.
   */
  coverPreview = signal<string | null>(null);

  // Upload run state, owned by `AddBookUploadSession`.
  readonly uploadProgress = this.uploadSession.progress;
  /** The stop question, asked through the shared confirm modal. */
  readonly confirmStopOpen = signal(false);
  readonly uploadStopping = this.uploadSession.stopping;
  readonly uploadViewOpen = computed(() => this.uploadSession.viewOpen());
  /** A book file is actively moving; closing must ask before it disappears. */
  readonly isUploading = computed(
    () => this.uploadSession.book() !== null && this.isSubmitting(),
  );
  readonly uploadBookTitle = computed(
    () => this.uploadSession.book()?.title || this.form.title || 'This book',
  );
  readonly uploadFileName = computed(() => this.uploadSession.fileName());
  readonly uploadStatusText = computed(() => this.uploadSession.statusText());
  /** The same line, only while there is a run to report on the form itself. */
  readonly uploadStatusLine = computed(() =>
    this.uploadProgress() !== null ? this.uploadSession.statusText() : null,
  );

  // Drag-over highlight for the two dropzones. The native file input stretched
  // across each zone still owns the actual drop, so these only toggle styling.
  fileDragActive = signal(false);
  coverDragActive = signal(false);

  // ------------------------------------------------------------------
  // From a source (issue #167)
  // ------------------------------------------------------------------
  // The picker owns the search; the shell keeps the chosen item because the
  // review step and the import both need it.
  readonly sourceSelection = signal<SourceSelection | null>(null);
  /** Why the chosen edition could not be loaded, or why an import did not start. */
  sourceImportError = signal<string | null>(null);

  readonly selectedItem = computed(() => this.sourceSelection()?.item ?? null);
  readonly selectedDetail = computed(() => this.sourceSelection()?.detail ?? null);
  readonly selectedAssetId = computed(() => this.sourceSelection()?.assetId ?? null);
  readonly selectedSourceItem = computed(() => this.selectedDetail() ?? this.selectedItem());
  /** The asset that will actually be imported, named in the collapsed summary. */
  readonly selectedAssetLabel = computed(() => this.sourceSelection()?.assetLabel ?? '');
  readonly sourceCoverUrl = computed(() => this.selectedItem()?.coverUrl ?? null);
  readonly selectedAsset = computed(() => {
    const item = this.selectedSourceItem();
    if (!item) return null;
    const id = this.selectedAssetId();
    return (
      item.assets.find((asset) => asset.id === id) ??
      item.assets.find((asset) => asset.isPreferred) ??
      item.assets[0] ??
      null
    );
  });
  /** True when the form was seeded from a source item: the primary action imports. */
  readonly seededFromSource = computed(() => !this.isEditMode() && this.selectedItem() !== null);
  /** The selected result owns its provider identity even after source mode closes. */
  readonly seededSourceName = computed(
    () => this.sourceSelection()?.providerName ?? 'the source',
  );
  /** The footer's next action, computed by the picker that owns the selection. */
  readonly sourcePrimaryAction = computed<{ label: string; enabled: boolean }>(
    () => this.sourcePicker()?.primaryAction() ?? { label: 'Select a book', enabled: false },
  );
  /** Set when a title in hand should run the catalogue search once discovery opens. */
  readonly searchOnSourceOpen = signal(false);

  constructor() {
    // The run needs the shell's guard and the chosen files; everything else it
    // owns itself.
    this.uploadSession.bind({
      isSubmitting: this.isSubmitting,
      selectedFile: () => this.selectedFile(),
      trackFiles: () => this.trackFiles(),
      onFileRejected: () => this.selectedFile.set(null),
      onFileUploaded: (book) => void this.uploadCoverIfNeeded(book.id),
      onDiscardedBook: () => this.bookAdded.emit(),
      onStopped: () => {
        this.confirmStopOpen.set(false);
        this.closeModal.emit();
        this.bookAdded.emit();
      },
    });

    effect(() => {
      // Only external modal inputs are allowed to reinitialize the draft.
      // Helpers below read internal signals, so keep their work outside this
      // effect's dependency tracking.
      const isOpen = this.isOpen();
      const currentBook = this.book();
      const sourceFirst = this.sourceFirst();
      const initialIntent = this.initialIntent();

      untracked(() => {
        if (!isOpen) return;

        if (currentBook) {
          this.fillForm(currentBook);
          this.flowIntent.set('manual');
          this.sourceMode.set(false);
          setTimeout(() => this.metadataForm()?.focusTitle(), 0);
          return;
        }

        this.resetForm();
        const intent: AddBookIntentKind = sourceFirst ? 'source' : (initialIntent ?? 'manual');
        this.startIntent(intent);
        const prefill = this.prefill();
        if (prefill) this.applyPrefill(prefill);
      });
    });
  }

  startIntent(intent: AddBookIntentKind): void {
    if (this.isEditMode()) return;

    this.flowIntent.set(intent);
    this.sourceMode.set(false);

    if (intent === 'source') {
      this.enterSourceMode();
      setTimeout(() => this.sourcePicker()?.focusQuery(), 0);
      return;
    }

    if (intent === 'upload') {
      this.form = { ...this.form, type: 'ebook' };
      setTimeout(() => this.fileStep()?.focusFileInput(), 0);
      return;
    }

    if (intent === 'physical') {
      this.form = { ...this.form, type: 'physical' };
      setTimeout(() => this.metadataForm()?.focusIsbn(), 0);
      return;
    }

    setTimeout(() => this.metadataForm()?.focusTitle(), 0);
  }

  private applyPrefill(prefill: AddBookPrefill): void {
    this.form = {
      ...this.form,
      title: prefill.title,
      author: prefill.author ?? null,
      isbn: prefill.isbn ?? null,
      publishedDate: prefill.publishedDate ?? null,
    };

    // The title opens the catalogue search, so a book that is free somewhere is
    // already on screen. The author stays out: catalogues match the query as one
    // phrase, and "title author" found nothing.
    this.sourceQuery.set(prefill.title);
    if (this.sourceMode()) this.searchOnSourceOpen.set(true);
  }

  /** The catalogue query; the picker edits it and the shell restores it. */
  readonly sourceQuery = signal('');

  /**
   * Manual is one fallback mode. Format decides whether ISBN lookup or a local
   * book file is relevant; those are not separate top-level acquisition doors.
   */
  showManualMode(): void {
    if (this.isEditMode()) return;

    this.clearSourceSelection();
    this.sourceMode.set(false);
    this.flowIntent.set('manual');

    setTimeout(() => {
      if (this.form.type === 'physical') this.metadataForm()?.focusIsbn();
      else this.metadataForm()?.focusTitle();
    }, 0);
  }

  onTypeChange(type: string): void {
    if (type !== 'physical' && type !== 'ebook' && type !== 'audiobook') return;

    this.form = { ...this.form, type };

    // A file belongs to a digital edition, never to a physical metadata record.
    // Clear a previously chosen file when switching back to Physical so it
    // cannot be carried invisibly into a later submit.
    if (type === 'physical') {
      this.selectedFile.set(null);
      this.trackFiles.set([]);
      this.fileDragActive.set(false);
    }
  }

  fillForm(b: BookModel) {
    this.uploadSession.reset();
    this.form = {
      type: b.type || 'ebook',
      title: b.title,
      subtitle: b.subtitle || '',

      author: b.author,
      editor: b.editor || '',
      translator: b.translator || '',
      narrator: b.narrator || '',

      description: b.description || '',

      isbn: b.isbn || '',
      asin: b.asin || '',

      publisher: b.publisher || '',
      placeOfPublication: b.placeOfPublication || '',
      publishedDate: b.publishedDate || '',

      edition: b.edition || '',
      pageCount: b.pageCount || null,
      duration: b.duration || '',
      language: b.language || DEFAULT_LANGUAGE,
      categories: b.categories || '',
      series: b.series || '',
      volumeNumber: b.volumeNumber || '',
      collectionIds: b.collectionIds ?? [],

      personalReview: b.personalReview || '',
    };
    this.clearChosenFiles();
    this.resetSource();
  }

  resetForm(): void {
    this.bulkFiles.set([]);
    this.form = {
      // See the form's own default: no file is not evidence of paper.
      type: 'ebook',
      title: '',
      subtitle: null,

      author: null,
      editor: null,
      translator: null,
      narrator: null,

      description: null,

      isbn: null,
      asin: null,

      publisher: null,
      placeOfPublication: null,
      publishedDate: null,

      edition: null,
      pageCount: null,
      duration: null,
      language: DEFAULT_LANGUAGE,
      categories: null,
      series: null,
      volumeNumber: null,
      collectionIds: [],

      personalReview: null,
    };
    this.clearChosenFiles();
    this.uploadSession.reset();
    this.existingWithFile.set(null);
    this.isFetching.set(false);
    this.isSubmitting.set(false);
    this.fileDragActive.set(false);
    this.coverDragActive.set(false);
    this.confirmStopOpen.set(false);
    // The source search holds its own selection and job state; a stale
    // poll from a previous visit must not survive into this one.
    this.resetSource();
  }

  fetchMetadata(): void {
    // 1. Clean the ISBN immediately
    this.form = { ...this.form, isbn: sanitizeIsbn(this.form.isbn) };
    const isbn = this.form.isbn;

    if (!isbn) return;
    this.isFetching.set(true);

    this.booksService
      .lookup(isbn)
      .pipe(
        finalize(() => this.isFetching.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (data) => {
          this.form = {
            ...this.form,
            title: data.title || this.form.title,
            subtitle: data.subtitle || this.form.subtitle,
            author: data.author || this.form.author,
            // Note: Editor usually isn't returned clearly by basic lookup, but Place is:
            description: data.description || this.form.description,
            publisher: data.publisher || this.form.publisher,
            placeOfPublication: data.placeOfPublication || this.form.placeOfPublication, // <--- Map from API
            // Sanitize the date coming from the API
            publishedDate: sanitizeDate(data.publishedDate) || this.form.publishedDate,
            pageCount: data.pageCount || this.form.pageCount,
            language: languageName(data.language) || this.form.language,
            categories: data.categories || this.form.categories,
          };
          this.toast.success('Metadata fetched');
        },
        error: (error: unknown) => {
          if (!(error instanceof BookLookupError)) {
            this.toast.error('Could not fetch the book’s metadata');
            return;
          }

          const message =
            error.reason === 'invalid-isbn'
              ? 'Invalid ISBN. Enter a valid ISBN-10 or ISBN-13.'
              : error.reason === 'not-found'
                ? 'No book metadata found for this ISBN.'
                : error.reason === 'unavailable'
                  ? 'Book metadata services are temporarily unavailable. Please try again.'
                  : 'Could not fetch the book’s metadata';
          this.toast.error(message);
        },
      });
  }

  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const chosen = Array.from(input.files ?? []);
    this.fileDragActive.set(false);
    this.trackFiles.set([]);

    // Several files at once are the parts of one audiobook; anything else
    // stays a single-file choice.
    if (chosen.length > 1) {
      if (!chosen.every((f) => this.isAudioFile(f))) {
        this.selectedFile.set(null);
        input.value = '';
        this.toast.info('Choose several audio files for one audiobook, or one ebook file.');
        return;
      }
      this.trackFiles.set(chosen);
      this.selectedFile.set(chosen[0]);
      this.onTypeChange('audiobook');
      setTimeout(() => this.metadataForm()?.focusTitle(), 0);
      return;
    }

    const file = chosen[0] ?? null;
    this.selectedFile.set(file);

    if (!file) return;

    this.onTypeChange(this.isAudioFile(file) ? 'audiobook' : 'ebook');

    const nameTitle = file.name
      .replace(/\.[^.]+$/, '')
      .replace(/[_-]+/g, ' ')
      .replace(/\s+/g, ' ')
      .trim();
    if (!this.form.title.trim()) {
      this.form = { ...this.form, title: nameTitle };
    }

    this.prefillFromFile(file, nameTitle);

    setTimeout(() => this.metadataForm()?.focusTitle(), 0);
  }

  /**
   * An EPUB or PDF knows its own title, author and language. Fill them in once
   * the server has read the file, but only where the person has not typed
   * anything of their own: the title still being the one taken from the file
   * name, an empty author, a language still on the default.
   */
  private prefillFromFile(file: File, nameTitle: string): void {
    if (!/\.(epub|pdf)$/i.test(file.name) || file.size > MAX_METADATA_READ_BYTES) return;

    this.booksService
      .readFileMetadata(file)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (found) => {
          if (this.selectedFile() !== file) return;

          const title = this.form.title.trim();
          this.form = {
            ...this.form,
            title: found.title && (!title || title === nameTitle) ? found.title : this.form.title,
            author: found.author && !this.form.author?.trim() ? found.author : this.form.author,
            language:
              found.language && this.form.language === DEFAULT_LANGUAGE
                ? found.language
                : this.form.language,
          };
          this.changeDetector.markForCheck();
        },
        // The file name stays as the title; nothing for the person to do.
        error: () => undefined,
      });
  }

  /** Several EPUB/PDF files at once: the list view owns them from here (#1407). */
  readonly bulkFiles = signal<File[]>([]);

  onBulkFilesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []).filter((f) => /\.(epub|pdf)$/i.test(f.name));
    input.value = '';
    if (files.length > 0) this.bulkFiles.set(files);
  }

  onBulkClosed(result: { changed: boolean }): void {
    this.bulkFiles.set([]);
    if (result.changed) this.finishAdd();
    else this.closeModal.emit();
  }

  private isAudioFile(file: File): boolean {
    const lower = file.name.toLowerCase();
    return (
      file.type.startsWith('audio/') ||
      ['.mp3', '.m4a', '.m4b'].some((extension) => lower.endsWith(extension))
    );
  }

  onCoverSelected(event: Event) {
    const file = (event.target as HTMLInputElement).files?.[0] ?? null;
    this.setCoverPreview(file);
    this.selectedCover.set(file);
  }

  /** Object URLs are ours to release, and the modal is opened many times. */
  private setCoverPreview(file: File | null): void {
    const previous = this.coverPreview();
    if (previous?.startsWith('blob:')) URL.revokeObjectURL(previous);
    this.coverPreview.set(file ? URL.createObjectURL(file) : null);
  }

  /** Both reset paths clear the chosen file, the cover, and the preview URL. */
  private clearChosenFiles(): void {
    this.selectedFile.set(null);
    this.trackFiles.set([]);
    this.setCoverPreview(null);
    this.selectedCover.set(null);
  }

  onDragOver(_event: DragEvent, zone: 'file' | 'cover'): void {
    // Do not preventDefault: the native <input type="file"> is the real drop
    // target and must populate its file list on drop.
    if (zone === 'file') this.fileDragActive.set(true);
    else this.coverDragActive.set(true);
  }

  onDragLeave(_event: DragEvent, zone: 'file' | 'cover'): void {
    if (zone === 'file') this.fileDragActive.set(false);
    else this.coverDragActive.set(false);
  }

  submit(): void {
    if (!this.form.title.trim()) return;

    // A chosen source item owns its own acquisition lifecycle. The ordinary
    // create/upload guard below is for hand-entered and local-file books.
    if (this.seededFromSource()) {
      this.importSelected();
      return;
    }

    // Once the row exists, every recovery attempt belongs to that same row.
    // This branch deliberately comes before create() so pressing the primary
    // action after a failed upload cannot create a duplicate.
    if (this.hasPendingUpload()) {
      this.retryUpload();
      return;
    }

    // Set this synchronously, before the first HTTP call. A disabled button is
    // UX feedback; this guard is the correctness boundary for same-tick double
    // clicks and programmatic re-entry.
    if (this.isSubmitting()) return;
    this.isSubmitting.set(true);

    // From here on this is the ordinary REST create/update path.
    // Sanitize Language, Date and ISBN (in case the user typed them and hit save).
    this.form = {
      ...this.form,
      language: this.form.language
        ? (languageName(this.form.language) ?? this.form.language)
        : this.form.language,
      isbn: sanitizeIsbn(this.form.isbn),
      publishedDate: sanitizeDate(this.form.publishedDate),
    };

    // Membership is sent as the authoritative set from the picker. No singular
    // mirror is written any more — the column is gone, so sending one would be
    // a dead field on the wire.
    const payload = { ...this.form };

    if (this.isEditMode()) {
      this.booksService
        .update(this.book()!.id, payload)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: (updated) => {
            this.isSubmitting.set(false);
            this.bookUpdated.emit(updated);
            this.closeModal.emit();
          },
          error: () => {
            this.isSubmitting.set(false);
            this.toast.error('Could not update the book');
          },
        });
    } else {
      this.existingWithFile.set(null);
      const wantsUpload = this.form.type !== 'physical' && this.selectedFile() !== null;
      const resumable = this.uploadSession.supportsResumable();
      this.booksService
        .createOrMatch({
          ...payload,
          // The legacy one-request path marks an incomplete row so the library
          // does not present it before /file succeeds. The resumable contract
          // keeps the book visible while its parts arrive: the upload session,
          // not the row's status, says a transfer is in progress.
          fileUploadExpected: wantsUpload && (!resumable || this.trackFiles().length > 1),
          // Said on both paths: without it the resumable path looked like a
          // metadata-only add, matched an edition with its own file, and the
          // new file (another translation, say) could not be added at all.
          fileAttached: wantsUpload,
        })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: ({ book, created }) => {
            this.bookCreated.emit(book.id);
            if (!created) {
              this.handleExistingBook(book, wantsUpload);
              return;
            }
            if ((book.editionCount ?? 1) > 1) {
              this.toast.info(`Added as another edition of “${book.title}”`);
            }
            if (wantsUpload) {
              this.handleFileUpload(book);
            } else {
              void this.uploadCoverIfNeeded(book.id);
            }
          },
          error: () => {
            this.isSubmitting.set(false);
            this.toast.error('Could not create the book');
          },
        });
    }
  }

  /**
   * The server answered with a book that was already in the library. Nothing
   * of it is overwritten here: its cover is left alone, and a file is only
   * sent when that book was still waiting for one.
   */
  private handleExistingBook(book: BookModel, wantsUpload: boolean): void {
    if (!wantsUpload) {
      this.toast.info(`“${book.title}” is already in your library`);
      this.finishAdd();
      return;
    }
    if (book.hasFile) {
      this.isSubmitting.set(false);
      this.existingWithFile.set(book);
      return;
    }
    this.handleFileUpload(book);
  }

  /** Says why the library's book counts as the same one, when an identifier decided it. */
  existingNotice(book: BookModel): string {
    const sameIsbn = !!book.isbn && sanitizeIsbn(this.form.isbn) === sanitizeIsbn(book.isbn);
    const sameAsin =
      !!book.asin && (this.form.asin ?? '').trim().toUpperCase() === book.asin.toUpperCase();
    const why = sameIsbn ? ' with the same ISBN' : sameAsin ? ' with the same ASIN' : '';
    return `“${book.title}” is already in your library${why} and has its own file. Your new file was not added.`;
  }

  /** The existing-book notice's sentence, or null when there is nothing to say. */
  readonly existingNoticeText = computed(() => {
    const book = this.existingWithFile();
    return book ? this.existingNotice(book) : null;
  });

  keepExistingFile(): void {
    this.finishAdd();
  }

  replaceExistingFile(): void {
    const book = this.existingWithFile();
    if (!book || this.isSubmitting()) return;
    this.existingWithFile.set(null);
    this.handleFileUpload(book);
  }

  handleFileUpload(createdBook: BookModel): void {
    this.uploadSession.start(createdBook);
  }

  retryUpload(): void {
    this.uploadSession.retry();
  }

  /**
   * The book row exists by now; the cover is the last thing in flight. It is
   * async rather than a component subscription on purpose: the sheet may be
   * closed in the second the cover takes, and the upload must still land so the
   * library refresh the close path asks for shows the finished book.
   */
  async uploadCoverIfNeeded(bookId: string): Promise<void> {
    const cover = this.selectedCover();
    if (!cover) {
      this.finishAdd();
      return;
    }
    try {
      await lastValueFrom(
        this.booksService
          .uploadCover(bookId, cover)
          .pipe(filter((event) => event.type === HttpEventType.Response)),
      );
    } catch {
      // The book is saved; a cover failure is not the add's failure.
    }
    this.finishAdd();
  }

  finishAdd() {
    this.resetForm();
    this.closeModal.emit();
    this.bookAdded.emit();
  }

  // Escape and the backdrop now belong to `app-modal-shell`, which emits
  // `closed`; both were hand-rolled here before. Every one of those paths, and
  // the footer's Cancel, funnels through `requestClose` so a running upload is
  // never closed unseen (#1275).

  /**
   * Every dismissal path funnels here: a running upload must not vanish
   * invisibly, so closing asks first and only the Stop answer ends the run.
   */
  requestClose(): void {
    if (this.isUploading()) {
      this.confirmStopOpen.set(true);
      return;
    }
    this.closeModal.emit();
  }

  /** The Stop control in the upload view. A failed run has nothing to stop. */
  requestStopUpload(): void {
    if (!this.isUploading()) {
      void this.uploadSession.stop();
      return;
    }
    this.confirmStopOpen.set(true);
  }

  cancelStopUpload(): void {
    if (this.uploadStopping()) return;
    this.confirmStopOpen.set(false);
  }

  stopUpload(): void {
    void this.uploadSession.stop();
  }

  // ------------------------------------------------------------------
  // Source discovery: the picker owns it; these are the step's entry points.
  // ------------------------------------------------------------------

  /** Show the source search on its own, before the form. */
  enterSourceMode(): void {
    if (this.isEditMode()) return;

    this.flowIntent.set('source');
    this.sourceMode.set(true);
  }

  /** Runs the picker's search for the shell's current query. */
  searchSource(): void {
    this.sourcePicker()?.searchSource(this.sourceQuery());
  }

  setSourceKind(kind: 'all' | 'ebook' | 'audiobook'): void {
    this.sourcePicker()?.setKind(kind, this.sourceQuery());
  }

  selectSourceItem(item: ProviderItem): void {
    this.sourcePicker()?.selectItem(item);
  }

  selectFormatFamily(family: 'EPUB' | 'PDF'): void {
    this.sourcePicker()?.selectFormatFamily(family);
  }

  /**
   * Turn the chosen item into a prefilled form, instead of importing it on the
   * spot.
   *
   * Importing and adding were the same job split across two surfaces: the source
   * tab created a book from the source's metadata and closed, so anything the
   * user wanted to add — a collection, a corrected author, their own note — meant
   * finding the book afterwards and editing it. Seeding the form means the
   * metadata is reviewed and completed BEFORE anything is downloaded, and the
   * import carries the result, so the book is created once and already right.
   */
  seedFromSelectedItem(): void {
    const item = this.selectedSourceItem();
    if (!item) return;

    // Collections are deliberately not offered here: the form that follows has
    // its own picker, and the same question twice is one too many.
    const asset = this.selectedAsset();

    this.form = {
      ...this.form,
      title: item.title ?? '',
      subtitle: item.subtitle ?? '',
      author: item.author ?? '',
      narrator: item.narrator ?? '',
      description: item.description ?? '',
      language: item.language ?? DEFAULT_LANGUAGE,
      publisher: item.publisher ?? '',
      publishedDate: item.publishedDate ?? '',
      categories: item.categories ?? '',
      duration: item.duration ?? '',
      pageCount: item.pageCount,
      // The type follows what is actually being fetched. An audiobook asset makes
      // an audiobook; guessing otherwise would file it where no reader can open it.
      type:
        asset?.kind === 'audiobook'
          ? 'audiobook'
          : asset?.kind === 'ebook'
            ? 'ebook'
            : this.form.type,
    };

    this.sourceImportError.set(null);
    // The search has done its job: the compact review is the rest of the flow.
    this.sourceMode.set(false);
    this.flowIntent.set('source');
    setTimeout(() => this.metadataForm()?.focusTitle(), 0);
  }

  /**
   * Only what the user actually changed, keyed for the server.
   *
   * An untouched field goes as null so the source still owns it: echoing the
   * whole form back would pin every value to this moment and a later
   * correction at the source could never reach the library. An emptied field
   * goes as an empty string, which the server reads as "clear it".
   */
  private buildOverrides(): ProviderMetadataOverrides {
    const item = this.selectedSourceItem();
    if (!item) return {};

    const againstSource = (
      fromForm: string | null | undefined,
      fromSource: string | null | undefined,
    ): string | null => {
      const entered = (fromForm ?? '').trim();
      return entered === (fromSource ?? '').trim() ? null : entered;
    };

    return {
      title: againstSource(this.form.title, item.title),
      subtitle: againstSource(this.form.subtitle, item.subtitle),
      author: againstSource(this.form.author, item.author),
      narrator: againstSource(this.form.narrator, item.narrator),
      description: againstSource(this.form.description, item.description),
      language: againstSource(this.form.language, item.language),
      publisher: againstSource(this.form.publisher, item.publisher),
      publishedDate: againstSource(this.form.publishedDate, item.publishedDate),
      categories: againstSource(this.form.categories, item.categories),
      duration: againstSource(this.form.duration, item.duration),
      pageCount: this.form.pageCount === item.pageCount ? null : this.form.pageCount,
    };
  }

  importSelected(): void {
    const item = this.selectedItem();
    // isSubmitting covers the request itself: until the server answers, nothing
    // else marks the import as started, and a second click (or Enter plus a
    // click) would queue the same book twice.
    if (!item || this.isSubmitting()) return;

    this.sourceImportError.set(null);
    this.isSubmitting.set(true);

    this.providers
      .acquire(item.providerId, {
        externalId: item.externalId,
        assetId: this.selectedAssetId(),
        collectionIds: this.form.collectionIds,
        metadataOverrides: this.buildOverrides(),
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (job) => {
          this.isSubmitting.set(false);
          if (job?.bookId) this.bookCreated.emit(job.bookId);
          else if (job?.jobId) this.importQueued.emit(job.jobId);
          this.toast.success('Import started — it will appear in your library when it finishes.');
          // The feed is opened here, on demand: the user just created work to
          // watch, so the "Imports in progress" section starts following it before
          // the modal is even gone. Nothing in the library query is refetched.
          this.imports.ensureConnected();
          this.bookAdded.emit();
          this.closeModal.emit();
        },
        error: (error) => {
          this.isSubmitting.set(false);
          this.sourceImportError.set(
            this.describeError(error, 'The import could not be started.'),
          );
        },
      });
  }

  /** The picker's selection crossed into the shell's review step. */
  onSourceSelection(selection: SourceSelection | null): void {
    this.sourceSelection.set(selection);
    if (!selection) this.sourceImportError.set(null);
  }

  private clearSourceSelection(): void {
    this.sourceSelection.set(null);
    this.sourceImportError.set(null);
  }

  private resetSource(): void {
    this.searchOnSourceOpen.set(false);
    this.sourceQuery.set('');
    this.clearSourceSelection();
  }

  private describeError(error: unknown, fallback: string): string {
    const body = (error as { error?: { detail?: string; title?: string } })?.error;
    return body?.detail || body?.title || fallback;
  }
}
