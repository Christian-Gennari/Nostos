import { afterNextRender, Component, effect, inject, Injector, OnDestroy, OnInit, signal, model, computed, ViewChild, ElementRef, HostListener } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

// Store
import { BookDetailStore } from './book-detail.store';

// DTOs
import { Book, EditionSummaryDto, LinkableBookDto } from '../core/dtos/book.dtos';

/**
 * How many RENDERED LINES a review may occupy before Book Details opens it as a
 * collapsed preview with a "Show more" control (issue #159).
 *
 * Why lines and not characters: the identical review is 16 lines in the 718px
 * desktop column and 49 lines on a 320px phone (16px/1.6 at every width), so a
 * character budget is not a height budget. A line is the same amount of reading
 * everywhere, and it is what the page actually has to spend vertically.
 *
 * Why 50: the calibration anchor in the issue is the current "Devils" review
 * (1240 chars), which must stay expanded at EVERY width — and at 320px, the
 * narrowest viewport the app serves, it already renders 49 lines. Anything at or
 * below that collapses the anchor review on a phone, so the threshold sits just
 * above it. On the desktop column 50 lines is ~1280px of review, i.e. a review
 * has to run past a full screen before it takes the page over; a normal review
 * needs no interaction and sees no control at all.
 *
 * `--review-preview-lines` in the component stylesheet sets how much of a
 * collapsed review stays visible.
 */
export const REVIEW_COLLAPSE_LINES = 50;

/** A work-membership action awaiting confirmation. */
interface PendingWorkAction {
  kind: 'link' | 'unlink';
  bookId: string;
  label: string;
  /** True when the action joins two multi-edition groups, not just two books. */
  merge: boolean;
}

// UI Components
import { AddBookModal } from '../add-book-modal/add-book-modal.component';
import { EditionsModal, WorkMember } from './editions-modal/editions-modal.component';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { TopicInputComponent } from '../ui/topic-input.component/topic-input.component';
import { NoteCardComponent } from '../ui/note-card.component/note-card.component';
import { StarRatingComponent } from '../ui/star-rating/star-rating.component';
import { LibraryPreferencesService } from '../core/services/library-preferences.service';
import { BooksService } from '../core/services/books.service';
import { ToastService } from '../core/services/toast.service';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { ButtonComponent } from '../ui/button/button.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { BadgeComponent } from '../ui/badge/badge.component';
import { DialogActionsComponent } from '../ui/dialog-actions/dialog-actions.component';

@Component({
  standalone: true,
  selector: 'app-book-detail',
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    NostosIconComponent,
    LoadingIndicatorComponent,
    AddBookModal,
    EditionsModal,
    ConfirmModal,
    TopicInputComponent,
    NoteCardComponent,
    StarRatingComponent,
    ButtonComponent,
    IconButtonComponent,
    BadgeComponent,
    DialogActionsComponent,
  ],
  providers: [BookDetailStore],
  templateUrl: './book-detail.component.html',
  styleUrls: ['./book-detail.component.css'],
})
export class BookDetail implements OnInit, OnDestroy {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private injector = inject(Injector);

  // Inject the Store
  readonly store = inject(BookDetailStore);
  private preferences = inject(LibraryPreferencesService);
  private booksService = inject(BooksService);
  private toast = inject(ToastService);
  // Local UI State
  isDescriptionExpanded = signal(false);
  showMetadataModal = signal(false);
  showDeleteConfirm = signal(false);
  statusDropdownOpen = signal(false);
  pendingStatus = signal<'notstarted' | 'reading' | 'finished' | null>(null);
  deleting = signal(false);

  /**
   * A long review (issue #159). Null until the review has been MEASURED — see
   * `measureReviewOverflow`. Never derived from the review text itself.
   */
  reviewOverflows = signal<boolean | null>(null);

  /** True once the reader has opened this long review on this page visit. */
  isReviewExpanded = signal(false);

  /** The rendered review paragraph, measured to decide whether to collapse it. */
  @ViewChild('reviewText') reviewText?: ElementRef<HTMLElement>;

  private reviewResizeObserver?: ResizeObserver;
  /** Reviews are keyed by id so switching book measures the new one, not the old. */
  private measuredReviewKey?: string;

  /**
   * Collapse decision for a long review (issue #159).
   *
   * This measures the rendered paragraph instead of counting characters, because
   * the same review is 16 lines at 718px and 49 lines at 320px. A ResizeObserver
   * re-runs it when the column changes width, so a review that fits on desktop
   * still collapses when the window narrows — and expands again when it grows.
   */
  constructor() {
    effect(() => {
    const book = this.store.book();
    if (book) this.preferences.setActiveEditionId(book.workId, book.id);
    });

    effect(() => {
    const book = this.store.book();
    const review = book?.personalReview ?? null;
    // The review's own TEXT is the key, not its length: an edit that swaps one
    // review for another of the same length still changes how many lines it
    // occupies, and a length-only key would skip that re-measure.
    const key = book && review ? `${book.id}:${review}` : null;

    if (!key) {
      this.measuredReviewKey = undefined;
      this.reviewOverflows.set(null);
      this.isReviewExpanded.set(false);
      this.stopObservingReview();
      return;
    }

    // Only re-measure when the review itself changes; a resize re-measures via
    // the observer below, and re-reading the same book must not reset the
    // reader's "Show more" choice.
    if (key === this.measuredReviewKey) return;
    this.measuredReviewKey = key;
    this.isReviewExpanded.set(false);
    afterNextRender(() => this.measureReviewOverflow(), { injector: this.injector });
    });
  }

  /** A pending work link/unlink awaiting confirmation (issue #143). */
  pendingWorkAction = signal<PendingWorkAction | null>(null);

  /** The confirmation question, composed here so the modal stays generic. */
  deleteHeading = computed(() => {
    const title = this.store.book()?.title;
    return title ? `Delete “${title}”?` : 'Delete book?';
  });

  /** True when the hero's decorative cover art failed to load — the band falls back to flat colour. */
  heroArtFailed = signal(false);

  /**
   * True once the hero art has painted, so the band can fade it in.
   *
   * The art is a 128 KB webp. Measured on a throttled phone-class connection it
   * lands **~1.7 s after** the page's entrance fade has finished — i.e. it drops
   * into an already-visible, already-opaque band, which reads as a jolt rather
   * than as the entrance. On localhost the file is ready before the first frame,
   * so the defect is invisible there and only shows up on a real device.
   */
  heroArtLoaded = signal(false);

  /** True once the stage cover has painted, for the same reason as above. */
  coverLoaded = signal(false);

  onHeroArtError(): void {
    this.heroArtFailed.set(true);
  }

  onHeroArtLoad(): void {
    this.heroArtLoaded.set(true);
  }

  /**
   * Also bound to `error`, deliberately: a cover that fails must end up in the
   * same state it had before this fade existed (a visible, empty frame) rather
   * than being stranded at opacity 0.
   */
  onCoverLoad(): void {
    this.coverLoaded.set(true);
  }

  /**
   * URL for the hero band's art layers. Prefers the 640px thumbnail: the art is
   * defocused to the point where detail is irrelevant, and this keeps two
   * full-bleed layers cheap. Null when the book has no cover, which drops the
   * band back to a gradient.
   */
  readonly heroArtUrl = computed<string | null>(() => {
    const cover = this.store.book()?.coverUrl;
    return cover ? `${cover}/thumbnail?width=640` : null;
  });

  newNote = model<string>('');

  /**
   * Human-readable reading status used by the header status chip.
   * Finished wins over in-progress; any saved progress or location counts as
   * "Reading", otherwise the book has not been started.
   */
  readonly readingStatus = computed<{ label: string; modifier: string }>(() => {
    const b = this.store.book();
    if (!b) return { label: 'Not Started', modifier: 'not-started' };
    if (b.finishedAt) return { label: 'Finished', modifier: 'finished' };
    if (b.progressPercent > 0 || b.lastLocation) return { label: 'Reading', modifier: 'reading' };
    return { label: 'Not Started', modifier: 'not-started' };
  });

  // Template Refs for hidden file inputs
  @ViewChild('coverInput') coverInput!: ElementRef<HTMLInputElement>;
  @ViewChild('fileInput') fileInput!: ElementRef<HTMLInputElement>;

  /**
   * Measures the review paragraph and sets `reviewOverflows`.
   *
   * `scrollHeight` is the only honest source here: it KEEPS reporting the full
   * content height while the clamp is clipping the visible box, so the paragraph
   * can be measured in the collapsed state and nothing has to be shown expanded
   * first. Verified on the live Devils review (410px of content, a 307px clamped
   * box, `scrollHeight` still 410) and against every clamp arrangement — the
   * clamp on a wrapper, directly on the paragraph, `-webkit-line-clamp` and
   * `max-height`. Measuring a clone, or releasing and reapplying the clamp, is
   * therefore unnecessary: there is no flicker to avoid.
   */
  private measureReviewOverflow(): void {
    const el = this.reviewText?.nativeElement;
    if (!el) {
      // The paragraph is not in the DOM (no review, or the block is not
      // rendered yet). Stay undecided rather than guessing from the text.
      this.reviewOverflows.set(null);
      return;
    }

    this.reviewOverflows.set(this.reviewLineCount(el) > REVIEW_COLLAPSE_LINES);
    this.observeReview(el);
  }

  /** The review's full rendered height expressed in lines. */
  private reviewLineCount(el: HTMLElement): number {
    const cs = getComputedStyle(el);
    const lh = parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) * 1.6;
    return lh > 0 ? el.scrollHeight / lh : 0;
  }

  /** Re-measures when the review's box changes width (see `observeReview`). */
  private observedReviewWidth?: number;

  /**
   * Re-measure when the review's own box changes SIZE. The column changes width
   * across the responsive breakpoints, and a fixed line threshold is a different
   * pixel height at each width, so the verdict has to be re-taken on resize.
   *
   * Re-entrancy, named because it is the trap here: the observer also fires for
   * the height change our own clamp causes. Measured `scrollHeight` ignores the
   * clamp (above), so that delivery recomputes the SAME verdict, the signal does
   * not change, and it settles on the second pass instead of looping. The width
   * comparison is a second belt-and-braces guard for the same thing.
   * Always disconnects first: `@ViewChild` hands back a NEW element when the
   * review changes, and an observer left on the old one would keep re-measuring
   * a detached node.
   */
  private observeReview(el: HTMLElement): void {
    if (typeof ResizeObserver === 'undefined') return;
    this.reviewResizeObserver?.disconnect();
    this.observedReviewWidth = el.clientWidth;
    this.reviewResizeObserver = new ResizeObserver(() => {
      const current = this.reviewText?.nativeElement;
      if (!current || current.clientWidth === this.observedReviewWidth) return;
      this.observedReviewWidth = current.clientWidth;
      this.reviewOverflows.set(this.reviewLineCount(current) > REVIEW_COLLAPSE_LINES);
    });
    this.reviewResizeObserver.observe(el);
  }

  private stopObservingReview(): void {
    this.reviewResizeObserver?.disconnect();
    this.reviewResizeObserver = undefined;
    this.observedReviewWidth = undefined;
  }

  ngOnDestroy(): void {
    this.stopObservingReview();
  }

  @HostListener('document:click')
  onDocumentClick(): void {
    if (this.statusDropdownOpen()) {
      this.statusDropdownOpen.set(false);
    }
    if (this.editMenuOpen()) {
      this.editMenuOpen.set(false);
    }
  }

  /** The Edit chooser: it holds every Book Details action that is not reading. */
  toggleEditMenu(event: Event): void {
    event.stopPropagation();
    this.editMenuOpen.update((open) => !open);
  }

  ngOnInit(): void {
    this.route.paramMap.subscribe((params) => {
      const id = params.get('id');
      if (id) {
        this.store.loadAllData(id);
      }
    });
  }

  // --- UI Actions (Delegating to Store) ---

  toggleFavorite() {
    this.store.toggleFavorite();
  }
  toggleFinished() {
    this.store.toggleFinished();
  }
  onRate(rating: number) {
    this.store.rate(rating);
  }

  toggleStatusDropdown(event: Event): void {
    event.stopPropagation();
    this.statusDropdownOpen.update((open) => !open);
  }

  selectStatusOption(target: 'notstarted' | 'reading' | 'finished'): void {
    this.statusDropdownOpen.set(false);
    const current = this.readingStatus().modifier;
    const currentCanonical = current === 'not-started' ? 'notstarted' : current;
    if (target === currentCanonical) return;
    this.pendingStatus.set(target);
  }

  cancelStatusChange(): void {
    this.pendingStatus.set(null);
  }

  confirmStatusChange(): void {
    const target = this.pendingStatus();
    if (!target) return;

    this.pendingStatus.set(null);
    if (target === 'notstarted') {
      this.store.resetProgress();
    } else if (target === 'finished') {
      this.store.setFinished(true);
    } else if (target === 'reading') {
      this.store.setFinished(false);
    }
  }

  addNote(): void {
    const content = this.newNote().trim();
    if (!content) return;
    this.store.addNote(content);
    this.newNote.set('');
  }

  onUpdateNote(event: { id: string; content: string; selectedText?: string }): void {
    this.store.updateNote(event.id, event.content, event.selectedText);
  }

  onDeleteNote(id: string): void {
    this.pendingNoteDelete.set(id);
  }

  cancelNoteDelete(): void {
    this.pendingNoteDelete.set(null);
  }

  confirmNoteDelete(): void {
    const id = this.pendingNoteDelete();
    if (!id) return;
    this.pendingNoteDelete.set(null);
    this.store.deleteNote(id);
  }

  /** Note id awaiting delete confirmation (asked through ConfirmModal). */
  readonly pendingNoteDelete = signal<string | null>(null);

  /** True while the cover-remove question is up (asked through ConfirmModal). */
  readonly coverDeletePending = signal(false);

  onTopicClick(topicId: string): void {
    this.goToTopic(topicId);
  }

  // --- MANUAL WORK MEMBERSHIP (issue #143) ---
  // The surface itself is a modal (EditionsModal); this component owns the state
  // and the mutations, keeping work membership out of the Edit Book form, whose
  // Save/Cancel contract cannot express an immediate relationship change.

  /** Whether the editions & works modal is up. */
  readonly showEditionsModal = signal(false);

  /** Whether the hero Edit chooser is open. */
  readonly editMenuOpen = signal(false);

  /**
   * A name for an edition that arrived without one. Defensive only: every server
   * response carries `title` now, but a cached/older payload must not render the
   * detach row as a blank line.
   */
  private getEditionTitleOrFallback(edition: EditionSummaryDto): string {
    const format = edition.format?.trim().toUpperCase();
    return format ? `Unknown title (${format})` : 'Unknown title';
  }

  /** Search text for the "link another book" picker. */
  readonly linkQuery = signal('');

  /**
   * Every book in the current work, current book included.
   *
   * Each sibling is named from its OWN summary (`title`/`author`), which is why
   * those fields were added to `EditionSummaryDto`: an unlink row reading the
   * current book's title would name the wrong book as the one being detached.
   */
  readonly workMembers = computed<WorkMember[]>(() => {
    const book = this.store.book();
    if (!book) return [];

    const current: WorkMember = {
      id: book.id,
      title: book.title,
      author: book.author,
    };
    const others = (book.otherEditions ?? []).map<WorkMember>((edition) => ({
      id: edition.id,
      title: edition.title?.trim() || this.getEditionTitleOrFallback(edition),
      author: edition.author ?? null,
    }));
    return [current, ...others];
  });

  readonly linkCandidates = computed(() => this.store.linkCandidates());

  /** Open the membership modal, loading candidates for the picker. */
  openEditions(): void {
    this.editMenuOpen.set(false);
    this.showEditionsModal.set(true);
    // Candidates are fetched on open, so a book-detail visit never costs a
    // library query it does not need.
    this.store.loadLinkCandidates(this.linkQuery());
  }

  closeEditions(): void {
    if (this.store.linkingWork()) return;
    this.showEditionsModal.set(false);
  }

  onLinkQueryChange(query: string): void {
    this.linkQuery.set(query);
    this.store.loadLinkCandidates(query);
  }

  /** A link always merges whole groups, so confirm what will actually happen. */
  askLink(candidate: LinkableBookDto): void {
    const current = this.store.book();
    if (!current) return;

    const groupSize = candidate.editionCount ?? 1;
    const merge = groupSize > 1 || (current.editionCount ?? 1) > 1;
    this.pendingWorkAction.set({
      kind: 'link',
      bookId: candidate.id,
      label: candidate.title,
      merge,
    });
  }

  /** Splitting a book out of a group is worth confirming too. */
  askUnlink(member: WorkMember): void {
    this.pendingWorkAction.set({ kind: 'unlink', bookId: member.id, label: member.title, merge: false });
  }

  cancelWorkAction(): void {
    if (this.store.linkingWork()) return;
    this.pendingWorkAction.set(null);
  }

  confirmWorkAction(): void {
    const action = this.pendingWorkAction();
    if (!action || this.store.linkingWork()) return;

    this.pendingWorkAction.set(null);
    if (action.kind === 'link') this.store.linkToWork(action.bookId);
    else this.store.unlinkFromWork(action.bookId);
  }

  /** Composed here so the shared confirm modal stays generic. */
  workActionHeading = computed(() => {
    const action = this.pendingWorkAction();
    if (!action) return '';
    return action.kind === 'link'
      ? `Link “${action.label}” to this edition set?`
      : `Unlink “${action.label}”?`;
  });

  workActionDescription = computed(() => {
    const action = this.pendingWorkAction();
    if (!action) return '';
    if (action.kind === 'unlink') {
      return 'This book leaves this edition set. Its file, reading progress, notes, rating, review and collections are all kept.';
    }
    return action.merge
      ? 'Both edition sets and everything already grouped with them become one set. Only the grouping changes; every book keeps its file, progress, notes, rating, review and collections.'
      : 'Both books become editions of one set. Only the grouping changes; nothing else about either book is touched.';
  });

  // --- File Actions ---

  onCoverSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) this.store.uploadCover(file);
  }

  deleteCover() {
    if (!this.store.book()?.coverUrl) return;
    this.coverDeletePending.set(true);
  }

  cancelCoverDelete(): void {
    this.coverDeletePending.set(false);
  }

  confirmCoverDelete(): void {
    if (!this.coverDeletePending()) return;
    this.coverDeletePending.set(false);
    this.store.deleteCover();
  }

  onFileUploadSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) this.store.uploadFile(file);
  }

  openDeleteConfirm(): void {
    if (this.deleting()) return;
    this.closeMetadataModal();
    this.showDeleteConfirm.set(true);
  }

  cancelDeleteConfirm(): void {
    if (this.deleting()) return;
    this.showDeleteConfirm.set(false);
  }

  /**
   * Deletes the current book after an explicit in-app confirmation, then returns to
   * the Library. A local pending signal guards against duplicate submissions.
   */
  confirmDeleteBook(): void {
    const b = this.store.book();
    if (!b || this.deleting()) return;

    this.deleting.set(true);
    this.booksService.delete(b.id).subscribe({
      next: () => {
        this.toast.success('Book deleted');
        this.showDeleteConfirm.set(false);
        void this.router.navigate(['/library']);
      },
      error: () => {
        this.deleting.set(false);
        this.toast.error('Failed to delete book');
      },
    });
  }

  deleteBook(): void {
    this.openDeleteConfirm();
  }

  // --- Navigation & Helpers ---

  goToTopic(topicId: string): void {
    this.router.navigate(['/second-brain'], { queryParams: { topicId } });
  }

  switchEdition(id: string): void {
    this.preferences.setActiveEditionId(this.store.book()?.workId, id);
    void this.router.navigate(['/library', id]);
  }

  getEditionFormat(book: Book | EditionSummaryDto): string {
    const isAudio = book.type === 'audiobook' || book.type === 'audio';
    if (isAudio) return 'Audiobook';
    const fmt = 'format' in book ? book.format : undefined;
    const formatLabel = this.getFormatLabel(book.type, book.fileName, fmt);
    return formatLabel === 'EPUB' || formatLabel === 'PDF' ? formatLabel : 'EBook';
  }

  getEditionSubtitle(book: Book | EditionSummaryDto): string {
    const isAudio = book.type === 'audiobook' || book.type === 'audio';
    const details = isAudio
      ? [book.duration, book.narrator, book.edition]
      : [book.edition];
    return details.filter((d): d is string => !!d?.trim()).join(' • ');
  }

  getEditionLabel(book: Book): string {
    return this.buildEditionLabel(
      book.type,
      book.fileName,
      undefined,
      book.duration,
      book.narrator,
      book.edition,
    );
  }

  getSummaryEditionLabel(edition: EditionSummaryDto): string {
    return this.buildEditionLabel(
      edition.type,
      edition.fileName,
      edition.format,
      edition.duration,
      edition.narrator,
      edition.edition,
    );
  }

  private buildEditionLabel(
    type: string,
    fileName?: string | null,
    format?: string,
    duration?: string | null,
    narrator?: string | null,
    edition?: string | null,
  ): string {
    const normalizedType = type.toLowerCase();
    const isAudio = normalizedType === 'audiobook' || normalizedType === 'audio';
    const formatLabel = this.getFormatLabel(type, fileName, format);
    const label = isAudio
      ? 'Audiobook'
      : formatLabel === 'EPUB' || formatLabel === 'PDF'
        ? formatLabel
        : normalizedType === 'physical'
          ? 'Physical'
          : formatLabel;

    const details = isAudio
      ? [duration, narrator, edition]
      : [edition];
    const detailText = details.filter((detail): detail is string => !!detail?.trim()).join(', ');

    return detailText ? `${label} (${detailText})` : label;
  }

  private getFormatLabel(type: string, fileName?: string | null, format?: string): string {
    const extension = fileName?.split('.').pop()?.trim().toLowerCase();
    if (extension) return extension.toUpperCase();

    const normalizedFormat = format?.trim().toLowerCase();
    switch (normalizedFormat || type.toLowerCase()) {
      case 'audiobook':
      case 'audio':
        return 'AUDIO';
      case 'ebook':
      case 'epub':
        return 'EPUB';
      case 'physical':
        return 'PHYSICAL';
      default:
        return (normalizedFormat || type).toUpperCase();
    }
  }

  /** The review is only collapsible once it has been measured as overflowing. */
  readonly isReviewCollapsible = computed(() => this.reviewOverflows() === true);

  /** The clamp applies only while a long review is collapsed. */
  readonly isReviewClamped = computed(() => this.reviewOverflows() === true && !this.isReviewExpanded());

  /**
   * Expand/collapse a long review. Local presentation state ONLY — the stored
   * review is never truncated, rewritten or refetched (issue #159).
   */
  toggleReview(): void {
    if (!this.isReviewCollapsible()) return;
    this.isReviewExpanded.update((expanded) => !expanded);
  }

  toggleDescription() {
    this.isDescriptionExpanded.update((v) => !v);
  }
  openMetadataModal() {
    // Opened from the chooser, so the chooser closes behind it.
    this.editMenuOpen.set(false);
    if (this.store.book()) this.showMetadataModal.set(true);
  }
  closeMetadataModal() {
    this.showMetadataModal.set(false);
  }

  onBookUpdated(updatedBook: Book): void {
    this.store.book.set(updatedBook);
  }

  downloadFile() {
    const id = this.store.book()?.id;
    if (id) window.open(`/api/books/${id}/file/download`, '_blank');
  }

  openReader() {
    const id = this.store.book()?.id;
    if (id) this.router.navigate(['/read', id]);
  }

  triggerCoverPicker() {
    this.coverInput?.nativeElement.click();
  }

  triggerFilePicker() {
    this.fileInput?.nativeElement.click();
  }

  getCollectionName(id: string | null | undefined): string {
    if (!id) return '—';
    const col = this.store.collections().find((c) => c.id === id);
    return col ? col.name : '—';
  }

  /**
   * Every collection this book belongs to, by name. Membership is the set; a
   * collection that no longer exists is skipped rather than rendered as a dash.
   */
  getCollectionNames(book: Book): string[] {
    const byId = new Map(this.store.collections().map((c) => [c.id, c.name]));
    return (book.collectionIds ?? [])
      .map((id) => byId.get(id))
      .filter((name): name is string => !!name);
  }

  isAudioBook(book: Book | null): boolean {
    return book?.type === 'audiobook';
  }
}
