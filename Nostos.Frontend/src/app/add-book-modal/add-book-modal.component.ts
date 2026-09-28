import { Component, ElementRef, inject, input, output, signal, computed, effect, viewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpEventType } from '@angular/common/http';
import { finalize } from 'rxjs';
import {
  BookLookupError,
  BooksService,
  Book as BookModel,
} from '../core/services/books.service';
import { ProvidersService } from '../core/services/providers.service';
import { ImportService } from '../core/services/import.service';
import { ToastService } from '../core/services/toast.service';
import {
  ACQUISITION_FINISHED_STATES,
  ProviderAcquisition,
  ProviderDiscoverySourceStatus,
  ProviderItem,
  ProviderMetadataOverrides,
  ProviderSummary,
} from '../core/dtos/provider.dtos';
import { Collection } from '../core/dtos/collection.dtos';
import { BookType } from '../core/dtos/book.dtos';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { CollectionPickerComponent } from '../ui/collection-picker/collection-picker.component';
import { ModalShell } from '../ui/modal-shell/modal-shell.component';
import { DialogActionsComponent } from '../ui/dialog-actions/dialog-actions.component';
import { ButtonComponent } from '../ui/button/button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { FormFieldComponent } from '../ui/form-field/form-field.component';
import { InputDirective, TextareaDirective } from '../ui/form-control/form-control.directive';
import { DropdownComponent, type DropdownOption } from '../ui/dropdown/dropdown.component';

type AddBookIntentKind = 'upload' | 'source' | 'physical' | 'manual';

@Component({
  selector: 'app-add-book-modal',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    NostosIconComponent,
    IconButtonComponent,
    CollectionPickerComponent,
    FormFieldComponent,
    InputDirective,
    DropdownComponent,
    TextareaDirective,
    ModalShell,
    DialogActionsComponent,
    ButtonComponent,
  ],
  templateUrl: './add-book-modal.component.html',
  styleUrl: './add-book-modal.component.css',
})
export class AddBookModal {
  readonly bookTypeOptions = [
    { value: 'physical', label: 'Physical Book' },
    { value: 'ebook', label: 'E-book (EPUB, PDF)' },
    { value: 'audiobook', label: 'Audiobook' },
  ] satisfies readonly DropdownOption[];
  private booksService = inject(BooksService);
  private providers = inject(ProvidersService);
  private toast = inject(ToastService);
  /** The import feed: opened on demand once an import has actually been queued. */
  private imports = inject(ImportService);

  // Inputs & Outputs
  isOpen = input.required<boolean>();

  /** Open Add Book directly in unified provider discovery. */
  sourceFirst = input<boolean>(false);
  /** Legacy focused-entry hook retained for callers outside the Library. */
  initialIntent = input<AddBookIntentKind | null>(null);
  collections = input.required<Collection[]>();
  book = input<BookModel | null>(null);
  closeModal = output<void>();
  bookAdded = output<void>();
  bookUpdated = output<BookModel>();
  deleteBook = output<void>();

  /**
   * Add Book is acquisition-first. This signal records the choice that brought
   * the reader here; Edit Book bypasses acquisition entirely.
   */
  flowIntent = signal<AddBookIntentKind>('manual');
  sourceMode = signal(false);
  private titleInput = viewChild<ElementRef<HTMLInputElement>>('titleInput');
  private localFileInput = viewChild<ElementRef<HTMLInputElement>>('localFileInput');
  private isbnInput = viewChild<ElementRef<HTMLInputElement>>('isbnInput');
  private sourceQueryInput = viewChild<ElementRef<HTMLInputElement>>('sourceQueryInput');

  // Computed State
  isEditMode = computed(() => !!this.book());
  awaitingLocalFile = computed(
    () => !this.isEditMode() && this.flowIntent() === 'upload' && !this.selectedFile(),
  );
  isFetching = signal(false);
  /** Blocks re-entrant create/update submits until the whole mutation pipeline settles. */
  isSubmitting = signal(false);
  /** Existing row kept after a failed file upload so retry never creates another book. */
  private pendingUploadBook = signal<BookModel | null>(null);
  readonly hasPendingUpload = computed(() => this.pendingUploadBook() !== null);
  readonly uploadError = signal<string | null>(null);

  // Form State
  form = {
    type: 'physical' as BookType,
    title: '',
    subtitle: '' as string | null,

    author: '' as string | null,
    editor: '' as string | null,
    translator: '' as string | null,
    narrator: '' as string | null,

    description: '' as string | null,

    isbn: '' as string | null,
    asin: '' as string | null,

    publisher: '' as string | null,
    placeOfPublication: '' as string | null,
    publishedDate: '' as string | null,

    edition: '' as string | null,
    pageCount: null as number | null,
    duration: '' as string | null,
    language: 'en',
    categories: '' as string | null,
    series: '' as string | null,
    volumeNumber: '' as string | null,
    // Membership is a set: a book may belong to any number of collections.
    // The picker owns the checkbox list; this array is the value it edits.
    collectionIds: [] as string[],

    personalReview: '' as string | null,
  };

  selectedFile = signal<File | null>(null);
  selectedCover = signal<File | null>(null);

  /**
   * What the cover zone shows: an object URL for a locally chosen file — which
   * is ours to release — or the provider's URL when the cover comes from the
   * source. A source-seeded form deliberately does NOT set a File: the import
   * fetches and stores the provider's cover server-side (`IncludeCover` defaults
   * to true), so there is nothing for the browser to upload. A File set here on
   * that path would be silently discarded, because the import never calls the
   * cover upload.
   */
  coverPreview = signal<string | null>(null);

  /** The picked book's cover, shown as already handled on the import path. */
  sourceCoverUrl = computed(() => this.selectedSourceItem()?.coverUrl ?? null);

  /** A chosen book file, so the zone never looks empty after a choice. */
  fileSummary = computed(() => {
    const file = this.selectedFile();
    if (!file) return null;
    const size = this.formatBytes(file.size);
    return size ? `${file.name} · ${size}` : file.name;
  });
  uploadProgress = signal<number | null>(null);
  uploadStartTime: number | null = null;

  // Drag-over highlight for the two dropzones. The native file input stretched
  // across each zone still owns the actual drop, so these only toggle styling.
  fileDragActive = signal(false);
  coverDragActive = signal(false);

  constructor() {
    effect(() => {
      if (!this.isOpen()) return;

      const currentBook = this.book();
      if (currentBook) {
        this.fillForm(currentBook);
        this.flowIntent.set('manual');
        this.sourceMode.set(false);
        setTimeout(() => this.titleInput()?.nativeElement?.focus(), 0);
        return;
      }

      this.resetForm();
      const intent: AddBookIntentKind = this.sourceFirst()
        ? 'source'
        : (this.initialIntent() ?? 'manual');
      this.startIntent(intent);
    });
  }

  startIntent(intent: AddBookIntentKind): void {
    if (this.isEditMode()) return;

    this.flowIntent.set(intent);
    this.sourceMode.set(false);

    if (intent === 'source') {
      this.enterSourceMode();
      setTimeout(() => this.sourceQueryInput()?.nativeElement?.focus(), 0);
      return;
    }

    if (intent === 'upload') {
      this.form.type = 'ebook';
      setTimeout(() => this.localFileInput()?.nativeElement?.focus(), 0);
      return;
    }

    if (intent === 'physical') {
      this.form.type = 'physical';
      setTimeout(() => this.isbnInput()?.nativeElement?.focus(), 0);
      return;
    }

    setTimeout(() => this.titleInput()?.nativeElement?.focus(), 0);
  }

  /** Switch the same Add Book sheet back to unified provider discovery. */
  showSourceMode(): void {
    if (this.isEditMode() || this.runningAcquisition()) return;

    this.flowIntent.set('source');
    this.enterSourceMode();
    setTimeout(() => this.sourceQueryInput()?.nativeElement?.focus(), 0);
  }

  /**
   * Manual is one fallback mode. Format decides whether ISBN lookup or a local
   * book file is relevant; those are not separate top-level acquisition doors.
   */
  showManualMode(): void {
    if (this.isEditMode() || this.runningAcquisition()) return;

    this.clearSourceSelection();
    this.sourceMode.set(false);
    this.flowIntent.set('manual');

    setTimeout(() => {
      if (this.form.type === 'physical') this.isbnInput()?.nativeElement?.focus();
      else this.titleInput()?.nativeElement?.focus();
    }, 0);
  }

  onTypeChange(type: string): void {
    if (type !== 'physical' && type !== 'ebook' && type !== 'audiobook') return;

    const bookType: BookType = type;
    this.form.type = bookType;

    // A file belongs to a digital edition, never to a physical metadata record.
    // Clear a previously chosen file when switching back to Physical so it
    // cannot be carried invisibly into a later submit.
    if (bookType === 'physical') {
      this.selectedFile.set(null);
      this.fileDragActive.set(false);
    }
  }

  fillForm(b: BookModel) {
    this.form = {
      type: b.type || 'physical',
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
      language: b.language || 'en',
      categories: b.categories || '',
      series: b.series || '',
      volumeNumber: b.volumeNumber || '',
      collectionIds: b.collectionIds ?? [],

      personalReview: b.personalReview || '',
    };
    this.clearChosenFiles();
    this.resetSourceTab();
  }

  resetForm(): void {
    this.form = {
      type: 'physical',
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
      language: 'en',
      categories: null,
      series: null,
      volumeNumber: null,
      collectionIds: [],

      personalReview: null,
    };
    this.clearChosenFiles();
    this.uploadProgress.set(null);
    this.uploadError.set(null);
    this.pendingUploadBook.set(null);
    this.isFetching.set(false);
    this.isSubmitting.set(false);
    this.fileDragActive.set(false);
    this.coverDragActive.set(false);
    // The source search holds its own selection and job state; a stale
    // poll from a previous visit must not survive into this one.
    this.resetSourceTab();
  }

  /**
   * Helper to format dates into YYYY or YYYY-MM-DD
   */
  sanitizeDate(dateStr: string | null): string | null {
    if (!dateStr) return null;
    const clean = dateStr.trim();

    // 1. If it's already just a year (e.g. "1999"), keep it.
    if (/^\d{4}$/.test(clean)) return clean;

    // 2. Try parsing as a standard date (handles "Jan 1, 2020", "2020/01/01")
    const timestamp = Date.parse(clean);
    if (!isNaN(timestamp)) {
      const date = new Date(timestamp);
      return date.toISOString().split('T')[0]; // Returns YYYY-MM-DD
    }

    // 3. Fallback: Try to just find a 4-digit year in the string
    const yearMatch = clean.match(/\d{4}/);
    return yearMatch ? yearMatch[0] : clean;
  }

  /**
   * Helper to clean ISBNs (remove dashes, spaces, keep only digits and X)
   */
  sanitizeIsbn(isbn: string | null): string | null {
    if (!isbn) return null;
    // Removes anything that is NOT a digit or 'X', and uppercases it
    return isbn.replace(/[^0-9X]/gi, '').toUpperCase();
  }

  fetchMetadata(): void {
    // 1. Clean the ISBN immediately
    this.form.isbn = this.sanitizeIsbn(this.form.isbn);
    const isbn = this.form.isbn;

    if (!isbn) return;
    this.isFetching.set(true);

    this.booksService
      .lookup(isbn)
      .pipe(finalize(() => this.isFetching.set(false)))
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
            publishedDate: this.sanitizeDate(data.publishedDate) || this.form.publishedDate,
            pageCount: data.pageCount || this.form.pageCount,
            language: this.getFullLanguageName(data.language) || this.form.language,
            categories: data.categories || this.form.categories,
          };
          this.toast.success('Metadata fetched successfully.');
        },
        error: (error: unknown) => {
          if (!(error instanceof BookLookupError)) {
            this.toast.error('Unable to fetch book metadata.');
            return;
          }

          const message =
            error.reason === 'invalid-isbn'
              ? 'Invalid ISBN. Enter a valid ISBN-10 or ISBN-13.'
              : error.reason === 'not-found'
                ? 'No book metadata found for this ISBN.'
                : error.reason === 'unavailable'
                  ? 'Book metadata services are temporarily unavailable. Please try again.'
                  : 'Unable to fetch book metadata.';
          this.toast.error(message);
        },
      });
  }

  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    this.selectedFile.set(file);
    this.fileDragActive.set(false);

    if (!file) return;

    const lower = file.name.toLowerCase();
    const audio =
      file.type.startsWith('audio/') ||
      ['.mp3', '.m4a', '.m4b'].some((extension) => lower.endsWith(extension));
    this.form.type = audio ? 'audiobook' : 'ebook';

    if (!this.form.title.trim()) {
      this.form.title = file.name
        .replace(/\.[^.]+$/, '')
        .replace(/[_-]+/g, ' ')
        .replace(/\s+/g, ' ')
        .trim();
    }

    setTimeout(() => this.titleInput()?.nativeElement?.focus(), 0);
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
    // Sanitize Language
    if (this.form.language) {
      this.form.language = this.getFullLanguageName(this.form.language) || this.form.language;
    }

    // Sanitize Date
    const cleanDate = this.sanitizeDate(this.form.publishedDate);

    // Sanitize ISBN (in case user typed it and hit save directly)
    this.form.isbn = this.sanitizeIsbn(this.form.isbn);

    // Membership is sent as the authoritative set from the picker. No singular
    // mirror is written any more — the column is gone, so sending one would be
    // a dead field on the wire.
    const payload = { ...this.form, publishedDate: cleanDate || null };

    if (this.isEditMode()) {
      this.booksService.update(this.book()!.id, payload).subscribe({
        next: (updated) => {
          this.isSubmitting.set(false);
          this.bookUpdated.emit(updated);
          this.closeModal.emit();
        },
        error: () => {
          this.isSubmitting.set(false);
          this.toast.error('Failed to update book');
        },
      });
    } else {
      this.booksService.create({
        ...payload,
        // A digital row created for a chosen local file is not a completed
        // library item until /file succeeds. Metadata-only digital books omit
        // this flag and remain legitimate Ready rows without a file.
        fileUploadExpected: this.form.type !== 'physical' && this.selectedFile() !== null,
      }).subscribe({
        next: (createdBook) => {
          if (this.form.type !== 'physical' && this.selectedFile()) {
            this.handleFileUpload(createdBook);
          } else {
            this.uploadCoverIfNeeded(createdBook.id);
          }
        },
        error: () => {
          this.isSubmitting.set(false);
          this.toast.error('Failed to create book');
        },
      });
    }
  }

  handleFileUpload(createdBook: BookModel): void {
    const file = this.selectedFile();
    if (!file) {
      this.isSubmitting.set(false);
      this.uploadError.set('Choose a book file before retrying the upload.');
      return;
    }

    this.pendingUploadBook.set(createdBook);
    this.uploadError.set(null);
    this.isSubmitting.set(true);
    this.uploadStartTime = performance.now();
    this.booksService.uploadFile(createdBook.id, file).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.UploadProgress) {
          const percent = Math.round((event.loaded / (event.total ?? 1)) * 100);
          this.uploadProgress.set(percent);
        }
        if (event.type === HttpEventType.Response) {
          const elapsed = performance.now() - (this.uploadStartTime ?? 0);
          this.pendingUploadBook.set(null);
          this.uploadError.set(null);
          setTimeout(
            () => {
              this.uploadProgress.set(null);
              this.uploadCoverIfNeeded(createdBook.id);
            },
            Math.max(0, 1200 - elapsed),
          );
        }
      },
      error: (error: unknown) => {
        this.uploadProgress.set(null);
        this.isSubmitting.set(false);
        this.uploadError.set(this.describeUploadFailure(error));
      },
    });
  }

  retryUpload(): void {
    const createdBook = this.pendingUploadBook();
    if (!createdBook || this.isSubmitting()) return;
    this.handleFileUpload(createdBook);
  }

  private describeUploadFailure(error: unknown): string {
    const status = (error as { status?: number } | null)?.status;
    if (status === 0) {
      return 'The connection was interrupted. Your book is saved; retry the file when you are ready.';
    }
    if (status === 408 || status === 504) {
      return 'The upload timed out. Your book is saved; try the file again.';
    }
    if (status === 413) {
      return 'This file is larger than this server allows. Choose a smaller file to continue.';
    }
    if (status === 429) {
      return 'Too many uploads are running right now. Your book is saved; try again in a moment.';
    }
    if (status != null && status >= 400 && status < 500) {
      return 'The server could not accept this file. Your book is saved; check the file and try again.';
    }
    return 'The upload could not be completed. Your book is saved; try the file again.';
  }

  uploadCoverIfNeeded(bookId: string) {
    const cover = this.selectedCover();
    if (!cover) {
      this.finishAdd();
      return;
    }
    this.booksService.uploadCover(bookId, cover).subscribe({
      next: (event) => {
        if (event.type === 4) this.finishAdd();
      },
      error: () => this.finishAdd(),
    });
  }

  finishAdd() {
    this.resetForm();
    this.closeModal.emit();
    this.bookAdded.emit();
  }

  // Escape and the backdrop now belong to `app-modal-shell`, which emits
  // `closed`; both were hand-rolled here before.

  // ------------------------------------------------------------------
  // From a source (issue #167)
  // ------------------------------------------------------------------
  // An acquisition surface, not a second library. Its whole job is to put a
  // file into the user's own library and then get out of the way — so there is
  // no feed, no recommendations and no browsing beyond the search the user
  // actually asked for.

  providerList = signal<ProviderSummary[]>([]);
  providersLoading = signal(false);
  providersError = signal<string | null>(null);

  sourceQuery = signal('');
  sourceKind = signal<'all' | 'ebook' | 'audiobook'>('all');
  sourceResults = signal<ProviderItem[]>([]);
  sourceStatuses = signal<ProviderDiscoverySourceStatus[]>([]);
  sourceHasMore = signal(false);
  sourceSearching = signal(false);
  sourceSearched = signal(false);
  sourceSearchError = signal<string | null>(null);
  private sourceSearchVersion = 0;

  selectedItem = signal<ProviderItem | null>(null);

  /**
   * The asset that will actually be imported, named in the collapsed summary so
   * the answer is visible without opening the list.
   */
  selectedAssetLabel = computed(() => {
    const item = this.selectedSourceItem();
    if (!item) return '';

    const id = this.selectedAssetId();
    const asset =
      item.assets.find((a) => a.id === id) ??
      item.assets.find((a) => a.isPreferred) ??
      item.assets[0];
    if (!asset) return '';

    const size = this.formatBytes(asset.sizeBytes);
    return size ? `${asset.label} · ${size}` : asset.label;
  });
  /**
   * The catalogue's rights text, minus the full stop it ends with: the line
   * finishes with the attribution in parentheses, so the source's own terminal
   * punctuation would sit mid-sentence. Only the final one is dropped — any
   * full stops inside the statement are the source's and stay.
   */
  rightsText = computed(() => {
    const statement = this.selectedSourceItem()?.rightsStatement ?? '';
    return statement.trim().replace(/\.+$/, '');
  });

  /** The full item behind the selected result: this is what carries the assets. */
  selectedDetail = signal<ProviderItem | null>(null);
  selectedAssetId = signal<string | null>(null);

  /**
   * The picked result with its assets. A search result carries none of its own,
   * so the fetched detail wins as soon as it arrives and the thin search result
   * is only a stand-in until then.
   */
  selectedSourceItem = computed(() => this.selectedDetail() ?? this.selectedItem());

  /** The live job, or null when nothing has been started. */
  acquisition = signal<ProviderAcquisition | null>(null);
  sourceImportError = signal<string | null>(null);


  sourceFailures = computed(() => this.sourceStatuses().filter((source) => !source.succeeded));
  sourceNotices = computed(() =>
    this.sourceStatuses().filter((source) => source.succeeded && !!source.notice),
  );
  allSourcesFailed = computed(
    () =>
      this.sourceSearched() &&
      this.sourceStatuses().length > 0 &&
      this.sourceStatuses().every((source) => !source.succeeded),
  );

  /**
   * True when the form was seeded from a source item, which is what turns the
   * primary action into "Import" instead of "Create".
   */
  seededFromSource = computed(() => !this.isEditMode() && this.selectedItem() !== null);

  /** The selected result owns its provider identity even after source mode closes. */
  seededSourceName = computed(() => {
    const item = this.selectedItem();
    return item ? this.providerName(item.providerId) : 'the source';
  });

  selectedAsset = computed(() => {
    const item = this.selectedSourceItem();
    if (!item) return null;

    const selectedId = this.selectedAssetId();
    return (
      item.assets.find((asset) => asset.id === selectedId) ??
      item.assets.find((asset) => asset.isPreferred) ??
      item.assets[0] ??
      null
    );
  });

  ebookFormatFamilies = computed(() => {
    const item = this.selectedSourceItem();
    if (!item || item.mediaKind !== 'ebook') return [] as Array<'EPUB' | 'PDF'>;

    const families = item.assets
      .map((asset) => this.assetFormatFamily(asset.sourceFormat))
      .filter((family): family is 'EPUB' | 'PDF' => family !== null);

    return [...new Set(families)];
  });

  selectedFormatFamily = computed(() => {
    const asset = this.selectedAsset();
    return asset ? this.assetFormatFamily(asset.sourceFormat) : null;
  });

  /** Non-null only while an import is actually in flight. */
  runningAcquisition = computed(() => {
    const job = this.acquisition();
    return job && !ACQUISITION_FINISHED_STATES.has(job.state) ? job : null;
  });

  /**
   * Human wording for the pipeline's stage names. The stages are deliberately
   * coarse and stable so the client never has to know which source is running.
   */
  private readonly stageLabels: Record<string, string> = {
    queued: 'Queued',
    starting: 'Starting',
    resolving: 'Contacting the source',
    checking: 'Checking your library',
    downloading: 'Downloading',
    validating: 'Validating the download',
    assembling: 'Preparing the file',
    importing: 'Adding to your library',
    done: 'Finished',
    failed: 'Failed',
  };

  stageLabel(job: ProviderAcquisition): string {
    return this.stageLabels[job.stage] ?? job.stage;
  }

  formatBytes(bytes: number | null): string {
    if (!bytes || bytes <= 0) return '';
    if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  /**
   * Show the source search on its own, before the form.
   *
   * Reached from "Import from a source". The user searches, picks a result, and
   * the form opens prefilled — so the metadata is reviewed and completed before
   * anything is downloaded.
   */
  enterSourceMode(): void {
    if (this.isEditMode()) return;

    this.flowIntent.set('source');
    this.sourceMode.set(true);
    if (this.providerList().length === 0 && !this.providersLoading()) this.loadProviders();
  }

  private loadProviders(): void {
    this.providersLoading.set(true);
    this.providersError.set(null);

    this.providers
      .list()
      .pipe(finalize(() => this.providersLoading.set(false)))
      .subscribe({
        next: (list) => {
          this.providerList.set(list);
          if (list.length === 0) {
            this.providersError.set('No content sources are configured on this server.');
          }
        },
        error: () => this.providersError.set('Could not load the available sources.'),
      });
  }

  setSourceKind(kind: 'all' | 'ebook' | 'audiobook'): void {
    if (kind === this.sourceKind()) return;

    this.sourceKind.set(kind);
    this.clearSourceSelection();

    if (this.sourceQuery().trim().length >= 2) this.searchSource();
  }

  searchSource(): void {
    const query = this.sourceQuery().trim();
    if (query.length < 2) return;

    const kind = this.sourceKind();
    const version = ++this.sourceSearchVersion;
    this.sourceSearching.set(true);
    this.sourceSearchError.set(null);
    this.clearSourceSelection();

    this.providers
      .searchAll(query, kind === 'all' ? undefined : kind)
      .pipe(
        finalize(() => {
          if (this.sourceSearchVersion === version) this.sourceSearching.set(false);
        }),
      )
      .subscribe({
        next: (result) => {
          // A response for a filter/query the user has already moved away from
          // must not replace the current discovery state.
          if (
            this.sourceSearchVersion !== version ||
            this.sourceQuery().trim() !== query ||
            this.sourceKind() !== kind
          )
            return;

          this.sourceResults.set(result.items);
          this.sourceStatuses.set(result.sources);
          this.sourceHasMore.set(result.hasMore);
          this.sourceSearched.set(true);
        },
        error: (error) => {
          if (
            this.sourceSearchVersion !== version ||
            this.sourceQuery().trim() !== query ||
            this.sourceKind() !== kind
          )
            return;

          this.sourceResults.set([]);
          this.sourceStatuses.set([]);
          this.sourceSearched.set(true);
          this.sourceSearchError.set(
            this.describeError(error, 'Free sources could not be searched right now.'),
          );
        },
      });
  }

  selectSourceItem(item: ProviderItem): void {
    this.selectedItem.set(item);
    this.selectedDetail.set(null);
    this.selectedAssetId.set(null);
    this.sourceImportError.set(null);

    // Detail and acquisition are routed by the result's own provider. Capturing
    // both halves of the identity prevents identical external ids from two
    // sources from colliding when responses arrive out of order.
    const selectedKey = this.sourceItemKey(item);

    this.providers.item(item.providerId, item.externalId).subscribe({
      next: (detail) => {
        const current = this.selectedItem();
        if (!current || this.sourceItemKey(current) !== selectedKey) return;
        if (this.sourceItemKey(detail) !== selectedKey) return;

        this.selectedDetail.set(detail);
        const preferredDetail = detail.assets.find((a) => a.isPreferred) ?? detail.assets[0];
        this.selectedAssetId.set(preferredDetail?.id ?? null);
      },
      error: () => {
        const current = this.selectedItem();
        if (!current || this.sourceItemKey(current) !== selectedKey) return;

        this.sourceImportError.set(
          "That book's details could not be loaded, so it cannot be imported right now.",
        );
      },
    });
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
    const asset =
      item.assets.find((a) => a.id === this.selectedAssetId()) ??
      item.assets.find((a) => a.isPreferred) ??
      item.assets[0];

    this.form.title = item.title ?? '';
    this.form.subtitle = item.subtitle ?? '';
    this.form.author = item.author ?? '';
    this.form.narrator = item.narrator ?? '';
    this.form.description = item.description ?? '';
    this.form.language = item.language ?? 'en';
    this.form.publisher = item.publisher ?? '';
    this.form.publishedDate = item.publishedDate ?? '';
    this.form.categories = item.categories ?? '';
    this.form.duration = item.duration ?? '';
    this.form.pageCount = item.pageCount;

    // The type follows what is actually being fetched. An audiobook asset makes
    // an audiobook; guessing otherwise would file it where no reader can open it.
    if (asset?.kind === 'audiobook') this.form.type = 'audiobook';
    else if (asset?.kind === 'ebook') this.form.type = 'ebook';

    this.sourceImportError.set(null);
    this.acquisition.set(null);
    // The search has done its job: the compact review is the rest of the flow.
    this.sourceMode.set(false);
    this.flowIntent.set('source');
    setTimeout(() => this.titleInput()?.nativeElement?.focus(), 0);
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
    if (!item || this.runningAcquisition()) return;

    this.sourceImportError.set(null);
    this.acquisition.set(null);

    this.providers
      .acquire(item.providerId, {
        externalId: item.externalId,
        assetId: this.selectedAssetId(),
        collectionIds: this.form.collectionIds,
        metadataOverrides: this.buildOverrides(),
      })
      .subscribe({
        next: () => {
          this.toast.success(
            'Import started — it will appear in your library when it finishes.',
          );
          // The feed is opened here, on demand: the user just created work to
          // watch, so the "Imports in Progress" section starts following it before
          // the modal is even gone. Nothing in the library query is refetched.
          this.imports.ensureConnected();
          this.bookAdded.emit();
          this.closeModal.emit();
        },
        error: (error) =>
          this.sourceImportError.set(
            this.describeError(error, 'The import could not be started.'),
          ),
      });
  }

  cancelImport(): void {
    const job = this.runningAcquisition();
    if (!job) return;

    this.providers.cancel(job.jobId).subscribe({
      next: () => {
        this.acquisition.set({ ...job, state: 'cancelled', stage: 'cancelled' });
      },
      error: () => undefined,
    });
  }

  private clearSourceSelection(): void {
    this.selectedItem.set(null);
    this.selectedDetail.set(null);
    this.selectedAssetId.set(null);
    this.sourceImportError.set(null);
  }

  private clearSourceResults(): void {
    this.sourceResults.set([]);
    this.sourceStatuses.set([]);
    this.sourceHasMore.set(false);
    this.sourceSearched.set(false);
    this.sourceSearchError.set(null);
    this.clearSourceSelection();
  }

  private resetSourceTab(): void {
    this.clearSourceResults();
    this.sourceQuery.set('');
    this.sourceKind.set('all');
    this.acquisition.set(null);
    this.sourceImportError.set(null);
  }

  private describeError(error: unknown, fallback: string): string {
    const body = (error as { error?: { detail?: string; title?: string } })?.error;
    return body?.detail || body?.title || fallback;
  }

  /**
   * The facts that decide between two recordings of the same work: who reads
   * it, how long it is and how many sections it has. Empty for an ebook, so a
   * Gutenberg row renders exactly as it did.
   */
  sourceFacts(item: ProviderItem): string {
    return [
      item.narrator ? `Narrated by ${item.narrator}` : null,
      item.duration,
      item.partCount && item.partCount > 1 ? `${item.partCount} sections` : null,
    ]
      .filter((part): part is string => !!part)
      .join(' · ');
  }

  sourceItemKey(item: Pick<ProviderItem, 'providerId' | 'externalId'>): string {
    return `${encodeURIComponent(item.providerId)}::${encodeURIComponent(item.externalId)}`;
  }

  isSelectedSourceItem(item: ProviderItem): boolean {
    const selected = this.selectedItem();
    return !!selected && this.sourceItemKey(selected) === this.sourceItemKey(item);
  }

  providerName(providerId: string): string {
    return (
      this.providerList().find((provider) => provider.id === providerId)?.displayName ??
      this.sourceStatuses().find((source) => source.providerId === providerId)?.displayName ??
      providerId
    );
  }

  mediaKindLabel(item: ProviderItem): string {
    return item.mediaKind === 'audiobook' ? 'Audiobook' : 'E-book';
  }

  assetFormatFamily(sourceFormat: string | null): 'EPUB' | 'PDF' | null {
    const format = sourceFormat?.toLowerCase();
    if (!format) return null;
    if (format === 'application/epub+zip' || format.startsWith('epub')) return 'EPUB';
    if (format === 'application/pdf' || format.startsWith('pdf')) return 'PDF';
    return null;
  }

  selectFormatFamily(family: 'EPUB' | 'PDF'): void {
    const item = this.selectedSourceItem();
    if (!item) return;

    const candidates = item.assets.filter(
      (asset) => this.assetFormatFamily(asset.sourceFormat) === family,
    );
    const selected = candidates.find((asset) => asset.isPreferred) ?? candidates[0];
    if (selected) this.selectedAssetId.set(selected.id);
  }

  assetsForSelectedFamily(): ProviderItem['assets'] {
    const item = this.selectedSourceItem();
    const family = this.selectedFormatFamily();
    if (!item || !family) return [];

    return item.assets.filter((asset) => this.assetFormatFamily(asset.sourceFormat) === family);
  }

  private getFullLanguageName(input: string | null): string | null {
    if (!input) return null;
    const clean = input.trim();
    if (clean.length > 3) return clean;
    try {
      return new Intl.DisplayNames(['en'], { type: 'language' }).of(clean) || clean;
    } catch (e) {
      return clean;
    }
  }
}
