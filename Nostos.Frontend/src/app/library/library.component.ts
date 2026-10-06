import {
  Component,
  inject,
  OnInit,
  OnDestroy,
  signal,
  computed,
  ChangeDetectionStrategy,
  effect,
  untracked,
  ViewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { BooksService } from '../core/services/books.service';
import { CollectionsService } from '../core/services/collections.service';
import { Collection } from '../core/dtos/collection.dtos';
import { CommonModule, DOCUMENT } from '@angular/common';
import { AddBookModal } from '../add-book-modal/add-book-modal.component';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { StarRatingComponent } from '../ui/star-rating/star-rating.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { ButtonComponent } from '../ui/button/button.component';
import { ChipComponent } from '../ui/chip/chip.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { DropdownComponent, type DropdownOption } from '../ui/dropdown/dropdown.component';
import { SidebarCollections } from './sidebar-collections/sidebar-collections.component';
import { Book, EditionSummaryDto, PaginatedResponse } from '../core/dtos/book.dtos';
import { Subject } from 'rxjs';
import { debounceTime, distinctUntilChanged } from 'rxjs/operators';
import { InfiniteScrollDirective } from '../core/directives/infinite-scroll.directive';
import { BloomArtDirective } from '../ui/bloom-art/bloom-art.directive';
import { BookSort } from '../core/dtos/book.enums';
import { ImportActivity, importStageLabel } from '../core/dtos/import.dtos';
import { LibraryFilterService } from './library-filter.service';
import { LibraryPreferencesService } from '../core/services/library-preferences.service';
import { ImportService } from '../core/services/import.service';
import { ToastService } from '../core/services/toast.service';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { ViewToggleComponent, type ViewToggleOption } from '../ui/view-toggle/view-toggle.component';
import { NOSTOS_CONCEPTS } from '../ui/icon/nostos-concepts';
import type { NostosIconName } from '../ui/icon/nostos-icons';

function statusFilterLabel(value: string | null | undefined): string | null {
  switch ((value ?? '').toLowerCase()) {
    case 'notstarted':
      return 'Not Started';
    case 'reading':
      return 'In Progress';
    case 'favorites':
      return 'Favorites';
    case 'finished':
      return 'Finished';
    case 'unsorted':
      return 'Unsorted';
    default:
      return null;
  }
}

function formatFilterLabel(value: string | null | undefined): string | null {
  switch ((value ?? '').toLowerCase()) {
    case 'audiobook':
    case 'audio':
      return 'Audiobooks';
    case 'ebook':
    case 'epub':
      return 'eBooks';
    case 'pdf':
      return 'PDFs';
    case 'physical':
      return 'Physical';
    default:
      return null;
  }
}

/**
 * Results cross-fade: the out-phase is the one duration TypeScript must honour
 * (it gates when the new page may be committed), so it lives here and must stay
 * equal to — never longer than — `--library-swap-out` in library.component.css,
 * or the DOM would swap while the old results are still moving.
 * The in-phase (300ms desktop / 260ms mobile, `--library-swap-in`) needs no TS
 * scheduler: it is purely the CSS release of `.is-swapping` once the commit lands.
 */
const SWAP_OUT_MS = 200;

/** True when the OS asks for reduced motion; the swap then commits instantly. */
function prefersReducedMotion(): boolean {
  return (
    typeof window !== 'undefined' &&
    window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true
  );
}

type WorkFormatType = 'audio' | 'epub' | 'pdf' | 'physical';

interface WorkFormatGlyph {
  type: WorkFormatType;
  /** A Nostos glyph NAME, not icon data: the badge asks for an icon, it does not own one. */
  icon: NostosIconName;
  label: string;
}

@Component({
  selector: 'app-library',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    NostosIconComponent,
    LoadingIndicatorComponent,
    ViewToggleComponent,
    AddBookModal,
    ConfirmModal,
    StarRatingComponent,
    IconButtonComponent,
    ButtonComponent,
    ChipComponent,
    InputDirective,
    DropdownComponent,
    SidebarCollections,
    InfiniteScrollDirective,
    BloomArtDirective,
  ],
  templateUrl: './library.component.html',
  styleUrls: ['./library.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Library implements OnInit, OnDestroy {
  readonly sortOptions = [
    { value: BookSort.LastRead, label: 'Last Read' },
    { value: BookSort.Recent, label: 'Recently Added' },
    { value: BookSort.Title, label: 'Title (A-Z)' },
    { value: BookSort.Rating, label: 'Highest Rated' },
  ] satisfies readonly DropdownOption[];
  private booksService = inject(BooksService);
  private collectionsService = inject(CollectionsService);
  private preferences = inject(LibraryPreferencesService);
  private toast = inject(ToastService);
  private readonly document = inject(DOCUMENT);
  readonly filters = inject(LibraryFilterService);
  /** The background-import feed. Owned by the service, not by this list. */
  readonly imports = inject(ImportService);

  // Enums for Template Access
  BookSort = BookSort;

  loading = signal(true);
  loadingMore = signal(false);

  /** True while the existing results blur out before the new page is swapped in. */
  swapping = signal(false);

  private requestSeq = 0;
  private swapStartedAt = 0;

  /** The importing book rows as of the last time the page was re-read for them. */
  private lastInFlightBookIds: string[] | null = null;

  /** A finished import whose visibility the next committed page decides. */
  private pendingReadyNotice: { id: string; title: string } | null = null;

  // Pagination State
  currentPage = signal(1);
  pageSize = this.preferences.pageSize;
  totalItems = signal(0);

  // Data
  rawBooks = signal<Book[]>([]);
  collections = signal<Collection[]>([]);

  @ViewChild(SidebarCollections) private sidebar?: SidebarCollections;

  viewMode = this.preferences.viewMode;
  /**
   * The view toggle's options. Icons are Nostos glyph names; the labels are the
   * ONLY names the icon-only control has, so they are asserted in the spec.
   *
   * Both glyphs render in the control's one shared 18px box. The grid glyph
   * carried a 20px optical size (PR #430) to match the Brain's `map-trifold` ink
   * extent, and it was reverted on review: at the shared box its ink already
   * matches the map glyph's, and the extra size only read as weight beside the
   * list glyph. `ViewToggleComponent` keeps the measured rationale; the whole
   * control is identical to the Brain's again, which the e2e parity gate asserts.
   */
  readonly viewToggleOptions = [
    { value: 'list', icon: 'list-bullets', label: 'List view' },
    { value: 'grid', icon: 'squares-four', label: 'Grid view' },
  ] satisfies readonly ViewToggleOption[];
  readonly sidebarExpanded = this.preferences.sidebarExpanded;
  showAddModal = signal(false);

  toggleSidebar(): void {
    this.preferences.setSidebarExpanded(!this.sidebarExpanded());
  }

  /**
   * Takes a plain string because `nostos-view-toggle` renders whatever options a
   * surface hands it and is deliberately not generic; the union is enforced by the
   * preferences service, which is the single validator for a persisted value.
   */
  setViewMode(mode: string): void {
    this.preferences.setViewMode(mode as 'list' | 'grid');
  }

  // Search & Sort State
  searchQuery = signal('');

  activeSort = signal<BookSort>(this.preferences.sort());

  private searchSubject = new Subject<string>();

  // Modal edit & delete system
  showEditModal = signal(false);
  editTarget = signal<Book | null>(null);
  deleteTarget = signal<Book | null>(null);
  deletingBook = signal(false);

  /** The confirmation question, composed here so the modal stays generic. */
  deleteHeading = computed(() => {
    const target = this.deleteTarget();
    return target ? `Delete “${target.title}”?` : 'Delete book?';
  });

  books = computed(() => this.rawBooks());

  hasMoreBooks = computed(() => {
    return this.rawBooks().length < this.totalItems();
  });

  activeFilterChips = computed(() => {
    const chips: { key: string; label: string }[] = [];
    const status = statusFilterLabel(this.filters.status());
    if (status) chips.push({ key: 'status', label: status });
    const format = formatFilterLabel(this.filters.format());
    if (format) chips.push({ key: 'format', label: format });
    const collectionId = this.filters.collectionId();
    if (collectionId) {
      const name = this.collections().find((c) => c.id === collectionId)?.name ?? 'Collection';
      chips.push({ key: 'collection', label: name });
    }
    const search = this.searchQuery().trim();
    if (search) chips.push({ key: 'search', label: `"${search}"` });
    return chips;
  });

  clearChip(key: string): void {
    switch (key) {
      case 'status':
        this.filters.status.set('all');
        break;
      case 'format':
        this.filters.format.set('all');
        break;
      case 'collection':
        this.filters.collectionId.set(null);
        break;
      case 'search':
        this.searchQuery.set('');
        this.refreshBooks();
        break;
    }
  }

  constructor() {
    // Phones scroll the document itself. Reserve the styled scrollbar gutter for
    // as long as the library is mounted so a filter that returns a short or empty
    // result set cannot make the whole page jump sideways by 8px.
    this.document.body?.classList.add('nostos-library');

    // An import that finishes while the user is looking at the library is patched
    // into the list in place — the one book the server named, not a refetch. The
    // rest of the list did not change, and re-requesting it would re-order and
    // re-render the page under the reader's hands.
    this.imports.bookPatched.pipe(takeUntilDestroyed()).subscribe((book) => this.onImportPatched(book));

    // Returning from the Studio or the Second Brain rebuilds this component with
    // no results in hand. The user has already seen the library in this session,
    // so cross-fade the results in instead of flashing a placeholder again.
    if (this.preferences.hasLoadedBooks()) this.loading.set(false);

    // Search Subscription
    this.searchSubject
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed())
      .subscribe((term) => {
        this.searchQuery.set(term);
        this.refreshBooks();
      });

    // Filter-driven load: exactly one refresh per filter change.
    effect(() => {
      this.filters.status();
      this.filters.format();
      this.filters.collectionId();
      untracked(() => this.refreshBooks());
    });

    // An import changes what the library should contain, and the page in hand was
    // fetched before that change: a book that did not exist when the query ran cannot
    // be in its results, however the server orders them. So the page is re-read when
    // the set of IMPORTING BOOK ROWS changes — and only when that change is one the
    // page cannot already satisfy:
    //
    //   - a row appears  → re-read only if the book is not already rendered (starting
    //     an import from the modal needs this; arriving on a page that already shows
    //     the book does not);
    //   - an import ends → always re-read, because the book stops being sorted first
    //     and belongs wherever the current sort puts it.
    //
    // Nothing else re-reads: a job that has no row yet cannot be in a list, and
    // percentages move several times a second.
    effect(() => {
      const current = this.imports.inFlightBookIds();

      const previous = this.lastInFlightBookIds;
      if (previous === null) {
        this.lastInFlightBookIds = current;
        return;
      }

      const appeared = current.filter((id) => !previous.includes(id));
      const ended = previous.filter((id) => !current.includes(id));
      if (!appeared.length && !ended.length) return;

      this.lastInFlightBookIds = current;

      untracked(() => {
        const onPage = new Set(this.rawBooks().map((book) => book.id));
        const missing = appeared.some((id) => !onPage.has(id));

        if (ended.length || missing) this.refreshBooks();
      });
    });
  }

  ngOnInit(): void {
    this.loadCollections();
    // Only opens a stream if something is actually in flight; the service re-reads
    // /api/imports/active to decide.
    this.imports.ensureConnected();
  }

  ngOnDestroy(): void {
    this.document.body?.classList.remove('nostos-library');
  }

  refreshBooks(reset = true, showWaiting = true): void {
    if (reset) {
      this.currentPage.set(1);
      if (!this.preferences.hasLoadedBooks()) {
        // Genuine first paint: the results region stays empty until the page
        // lands. Nothing is scheduled here — the results resolve in as they
        // arrive, so the arrival needs no timer.
        if (showWaiting) this.loading.set(true);
      } else if (showWaiting && !prefersReducedMotion()) {
        // Filter/sort/search change, or re-entering the library from another
        // section: fade the results through the swap instead of tearing them
        // down.
        // (Reduced motion skips the swap state entirely — no dimming, no blur.)
        this.swapping.set(true);
        this.swapStartedAt = performance.now();
      }
    } else {
      this.loadingMore.set(true);
    }

    const seq = ++this.requestSeq;
    const status = this.filters.status();
    const format = this.filters.format();
    const collectionId = this.filters.collectionId();
    const sort = this.activeSort();
    const search = this.searchQuery();
    const page = this.currentPage();
    // Mobile cover decoding is the dominant first-paint cost. Load a smaller
    // first batch there; the existing sentinel still fetches every book as the
    // user scrolls.
    const pageSize = window.innerWidth < 768 ? Math.min(this.pageSize(), 12) : this.pageSize();

    this.booksService
      .list({
        filter: status === 'all' ? undefined : status,
        sort,
        search,
        page,
        pageSize,
        collectionId: collectionId ?? undefined,
        groupByWork: this.preferences.groupByWork(),
        format: format === 'all' ? null : format,
      })
      .subscribe({
        next: (data) => {
          // A stale response must never paint over a newer request's results.
          if (seq !== this.requestSeq) return;
          this.commitResults(data, reset, seq);
        },
        error: () => {
          if (seq !== this.requestSeq) return;
          this.toast.error('Failed to load books');
          this.loading.set(false);
          this.loadingMore.set(false);
          this.swapping.set(false);
        },
      });
  }

  /**
   * Replaces the visible page. When the stage is blurred out, the commit is
   * deferred to the end of the out-phase so the DOM swap is never visible.
   * The swap itself is a pure signal flip, which is legal from any async
   * callback — the timer only co-ordinates the visual clock.
   */
  private commitResults(data: PaginatedResponse<Book>, reset: boolean, seq: number): void {
    const apply = () => {
      if (seq !== this.requestSeq) return;
      if (reset) {
        this.rawBooks.set(data.items);
      } else {
        this.rawBooks.update((current) => [...current, ...data.items]);
      }
      this.totalItems.set(data.totalCount);
      this.preferences.hasLoadedBooks.set(true);
      this.loading.set(false);
      this.loadingMore.set(false);
      this.swapping.set(false); // releases the in-phase: new books resolve in

      // A finished import can now be judged honestly: it was on the page while it ran
      // (the server sorts an import first), and the question the user cares about is
      // whether it is still in front of them once it stopped being an import.
      this.flushReadyNotice();
    };

    if (!reset || prefersReducedMotion()) {
      apply();
      return;
    }

    // Only fade out something the user can actually see. With real results on
    // screen the swap is deferred to the bottom of the blur; with an empty stage
    // — a cold load's waiting field, or re-entering from another section — there
    // is nothing to blur out, so the page commits now and the field dissolves
    // itself in CSS under the arriving results. Holding an empty stage for the
    // length of the out-phase would only delay a load the user is watching.
    if (this.rawBooks().length === 0) {
      apply();
      return;
    }

    if (!this.swapping()) {
      this.swapping.set(true);
      this.swapStartedAt = performance.now();
    }

    // Network time already spent counts toward the out-phase, so a fast local
    // response still gets the full blur instead of a one-frame blink.
    const elapsed = performance.now() - this.swapStartedAt;
    setTimeout(apply, Math.max(0, SWAP_OUT_MS - elapsed));
  }

  loadMore(): void {
    if (!this.hasMoreBooks() || this.loadingMore()) return;

    this.currentPage.update((p) => p + 1);
    this.refreshBooks(false);
  }

  onSearch(event: Event): void {
    const val = (event.target as HTMLInputElement).value;
    this.searchSubject.next(val);
  }

  setSort(sort: BookSort | string): void {
    const parsedSort =
      sort && Object.values(BookSort).includes(sort as BookSort) ? (sort as BookSort) : null;
    if (!parsedSort) return;

    this.activeSort.set(parsedSort);
    this.preferences.setSort(parsedSort);
    this.refreshBooks(true);
  }

  loadCollections(): void {
    this.collectionsService.list().subscribe({
      next: (data) => this.collections.set(data),
      error: () => this.toast.error('Failed to load collections'),
    });
  }

  // ... (Modals and Actions remain unchanged)
  /**
   * Add Book opens with focused provider discovery. Choosing a result moves to
   * review; manual intake is a secondary escape hatch from that first step.
   */
  openAddModal(): void {
    this.showAddModal.set(true);
  }

  closeAddModal(): void {
    this.showAddModal.set(false);
  }

  openEditModal(book: Book): void {
    this.editTarget.set(book);
    this.showEditModal.set(true);
  }
  closeEditModal(): void {
    this.showEditModal.set(false);
    this.editTarget.set(null);
  }

  onBookUpdated(_updated: Book): void {
    this.refreshBooks(true, false);
    this.refreshStatusCounts();
    this.closeEditModal();
  }

  onBookAdded(): void {
    this.refreshBooks();
    this.refreshStatusCounts();
  }

  private matchesActiveFilter(book: Book): boolean {
    switch (this.filters.status()) {
      case 'favorites':
        return book.isFavorite;
      case 'finished':
        return !!book.finishedAt;
      case 'reading':
        return !book.finishedAt && book.progressPercent > 0;
      case 'notstarted':
        return book.progressPercent === 0;
      case 'unsorted':
        // Membership is a set now: a book is sorted if it is in ANY collection.
        // Reading the singular field here would call a multi-collection book
        // unsorted, which is the bug this lookup exists to avoid.
        return !(book.collectionIds?.length ?? 0);
      default:
        return true;
    }
  }

  private syncToggledBook(updated: Book): void {
    if (this.matchesActiveFilter(updated)) {
      this.rawBooks.update((books) => books.map((b) => (b.id === updated.id ? updated : b)));
    } else {
      this.rawBooks.update((books) => books.filter((b) => b.id !== updated.id));
      this.totalItems.update((c) => Math.max(0, c - 1));
    }
    this.refreshStatusCounts();
  }

  private refreshStatusCounts(): void {
    this.sidebar?.loadStatusCounts();
  }

  // --- Import progress, ON the item -------------------------------------
  // The library renders its own card and row; an item that is being imported simply
  // knows its own progress. Progress itself reads nothing but the feed (a lookup by
  // book id, so no sort, filter or page is involved in drawing a bar) — but for the
  // book to be THERE at all, two things must hold: the server puts importing books
  // first in every sort, and the page is re-read when an import starts or finishes.

  /** The import driving this book, or undefined when it is not importing. */
  bookProgress(book: Book): ImportActivity | undefined {
    return this.imports.progressByBookId().get(book.id);
  }

  /**
   * The stage word for an in-flight book. Falls back to the book's own status
   * when the feed has not reported it yet (a page load mid-import, for the
   * fraction of a second before the first frame lands) — the card then looks
   * exactly as it did before this change, never wrong.
   */
  bookStageLabel(book: Book): string {
    const progress = this.bookProgress(book);
    return progress ? importStageLabel(progress) : book.status === 2 ? 'Transcoding' : 'Downloading';
  }

  /** Screen-reader text for the item's bar. */
  bookProgressLabel(book: Book): string {
    const progress = this.bookProgress(book);
    const stage = this.bookStageLabel(book);
    return progress ? `${stage}, ${progress.percent} percent` : stage;
  }

  /** Retry or dismiss a failed import, from the item it belongs to. */
  retryImport(activity: ImportActivity, event: Event): void {
    event.stopPropagation();
    this.imports.retry(activity);
  }

  dismissImport(activity: ImportActivity, event: Event): void {
    event.stopPropagation();
    this.imports.dismiss(activity);
  }

  /**
   * Replace ONE book in the visible page, in place.
   *
   * A book that is not in the current page is left alone: it is not in the page
   * because of the sort, the filter or pagination, and adding it here would put a
   * row on screen that the current query did not ask for. The next real query picks
   * it up — and while it is importing, the server sorts it first, so it is in the
   * page to begin with.
   */
  private patchBookInPlace(book: Book): void {
    this.rawBooks.update((books) =>
      books.map((existing) => (existing.id === book.id ? book : existing)),
    );
  }

  /**
   * A finished import is announced only when the user can no longer see it.
   *
   * The two cases cannot be judged at the moment the import ends. While it runs the
   * server sorts it first, so its card IS on the page; the page is then re-read, and
   * the book takes its ordinary place in the current sort — which under Last Read is
   * behind everything the user has ever opened, because a book they have not started
   * has no reading history to sort on. So the answer is only known after the refetch
   * commits, and `flushReadyNotice` is where it is given.
   */
  private onImportPatched(book: Book): void {
    const isOnPage = this.rawBooks().some((existing) => existing.id === book.id);

    this.patchBookInPlace(book);

    // Already off the page (a filter, a search, another page): nothing will change
    // that on this fetch, so say so now.
    if (!isOnPage) {
      this.toast.success(`Import finished — ${book.title} is in your library.`);
      return;
    }

    this.pendingReadyNotice = { id: book.id, title: book.title };
  }

  /** Announces a finished import that the re-read page did not keep. */
  private flushReadyNotice(): void {
    const pending = this.pendingReadyNotice;
    if (!pending) return;

    // Consumed by the next page that lands, which is the one the import triggered.
    this.pendingReadyNotice = null;

    if (!this.rawBooks().some((book) => book.id === pending.id)) {
      this.toast.success(`Import finished — ${pending.title} is in your library.`);
    }
  }

  getFormatLabel(book: Book | EditionSummaryDto): string {
    const extension = book.fileName?.split('.').pop()?.trim().toLowerCase();
    if (extension) {
      return extension.toUpperCase();
    }

    const format = 'format' in book ? book.format?.trim().toLowerCase() : undefined;
    if (format) {
      switch (format) {
        case 'audiobook':
        case 'audio':
          return 'AUDIO';
        case 'ebook':
        case 'epub':
          return 'EPUB';
        case 'physical':
          return 'PHYSICAL';
        default:
          return format.toUpperCase();
      }
    }

    switch (book.type) {
      case 'audiobook':
        return 'AUDIO';
      case 'ebook':
        return 'EPUB';
      case 'physical':
        return 'PHYSICAL';
      default:
        return book.type.toUpperCase();
    }
  }

  getLibraryCoverUrl(coverUrl: string | null): string | null {
    if (!coverUrl) return null;
    return `${coverUrl}/thumbnail?width=320`;
  }

  /** Hero art in flight, keyed by URL, so a hover does not re-request it. */
  private readonly prefetchedCovers = new Map<string, HTMLImageElement>();

  /**
   * Warm the HTTP cache for a book's hero art as soon as the pointer signals
   * intent, so opening the detail page does not flash an empty dark band.
   *
   * The hero cannot start its own fetch until the book JSON arrives, because
   * `heroArtUrl` is derived from `coverUrl` — the two requests are serialised.
   * Prefetching on hover overlaps them, so by the time the click lands the 640px
   * thumbnail is in flight or already cached and the band paints on its first
   * frame. Costs nothing unless the user actually hovers.
   */
  prefetchHeroCover(coverUrl: string | null): void {
    if (!coverUrl) return;

    const url = `${coverUrl}/thumbnail?width=640`;
    if (this.prefetchedCovers.has(url)) return;

    const img = new Image();
    img.decoding = 'async';
    const release = () => this.prefetchedCovers.delete(url);
    img.addEventListener('load', release);
    img.addEventListener('error', release);
    img.src = url;
    // Hold a reference while in flight — an Image collected mid-request can have
    // the fetch cancelled before it reaches the cache.
    this.prefetchedCovers.set(url, img);
  }

  getBookRouteId(book: Book): string {
    return this.preferences.getActiveEditionId(book.workId, book.id);
  }

  getWorkFormatGlyphs(book: Book): WorkFormatGlyph[] {
    const editions: (Book | EditionSummaryDto)[] = [book, ...(book.otherEditions || [])];
    const presentFormats = new Set(editions.map((edition) => this.getWorkFormatType(edition)));
    const formats: WorkFormatGlyph[] = [
      // Format icons come from the semantic registry, so a format means the same
      // glyph here, in the book row and in the audio reader.
      { type: 'audio', icon: NOSTOS_CONCEPTS.audiobook, label: 'Audiobook' },
      { type: 'epub', icon: NOSTOS_CONCEPTS.ebook, label: 'eBook' },
      { type: 'pdf', icon: NOSTOS_CONCEPTS.pdf, label: 'PDF' },
      { type: 'physical', icon: NOSTOS_CONCEPTS.physical, label: 'Physical Book' },
    ];

    return formats.filter((format) => presentFormats.has(format.type));
  }

  getWorkFormatsTooltip(book: Book): string {
    const labels = this.getWorkFormatGlyphs(book).map((glyph) => glyph.label);
    return `Available in ${labels.join(', ')}`;
  }

  private getWorkFormatType(book: Book | EditionSummaryDto): WorkFormatType {
    const type = book.type.toLowerCase();
    const format = 'format' in book ? book.format?.trim().toLowerCase() : undefined;
    const extension = book.fileName?.split('.').pop()?.trim().toLowerCase();

    if (type === 'audiobook' || type === 'audio' || format === 'audiobook' || format === 'audio') {
      return 'audio';
    }
    if (type === 'physical' || format === 'physical') return 'physical';
    if (extension === 'pdf' || format === 'pdf' || type === 'pdf') return 'pdf';
    return 'epub';
  }

  openDeleteModal(book: Book, event?: Event): void {
    event?.stopPropagation();
    this.deleteTarget.set(book);
  }

  cancelDelete(): void {
    if (this.deletingBook()) return;
    this.deleteTarget.set(null);
  }

  confirmDelete(): void {
    const target = this.deleteTarget();
    if (!target || this.deletingBook()) return;

    this.deletingBook.set(true);
    this.booksService.delete(target.id).subscribe({
      next: () => {
        this.rawBooks.update((books) => books.filter((b) => b.id !== target.id));
        this.totalItems.update((c) => Math.max(0, c - 1));
        this.refreshStatusCounts();
        this.toast.success(`"${target.title}" deleted`);
        this.deleteTarget.set(null);
        this.deletingBook.set(false);
      },
      error: () => {
        this.deletingBook.set(false);
        this.toast.error('Failed to delete book');
      },
    });
  }

  deleteBook(bookOrId: Book | string, event: Event): void {
    event.stopPropagation();
    if (typeof bookOrId === 'string') {
      const found = this.rawBooks().find((b) => b.id === bookOrId) ?? null;
      this.deleteTarget.set(found ?? ({ id: bookOrId, title: 'Book' } as Book));
    } else {
      this.deleteTarget.set(bookOrId);
    }
  }

  toggleFavorite(book: Book, event: Event): void {
    event.stopPropagation();
    const newStatus = !book.isFavorite;

    // Optimistic update via signal (creates new object reference for OnPush)
    this.rawBooks.update((books) =>
      books.map((b) => (b.id === book.id ? { ...b, isFavorite: newStatus } : b)),
    );

    this.booksService.update(book.id, { isFavorite: newStatus }).subscribe({
      next: (updated) => this.syncToggledBook(updated),
      error: () => {
        this.rawBooks.update((books) =>
          books.map((b) => (b.id === book.id ? { ...b, isFavorite: !newStatus } : b)),
        );
        this.toast.error('Failed to update favorite status');
      },
    });
  }

  toggleFinished(book: Book, event: Event): void {
    event.stopPropagation();
    const isCurrentlyFinished = !!book.finishedAt;
    const newIsFinished = !isCurrentlyFinished;
    const oldFinishedAt = book.finishedAt;
    const oldProgress = book.progressPercent;

    const newFinishedAt = newIsFinished ? new Date().toISOString() : null;
    const newProgress = newIsFinished ? 100 : book.progressPercent;

    // Optimistic update via signal
    this.rawBooks.update((books) =>
      books.map((b) =>
        b.id === book.id ? { ...b, finishedAt: newFinishedAt, progressPercent: newProgress } : b,
      ),
    );

    this.booksService.update(book.id, { isFinished: newIsFinished }).subscribe({
      next: (updated) => this.syncToggledBook(updated),
      error: () => {
        this.rawBooks.update((books) =>
          books.map((b) =>
            b.id === book.id
              ? { ...b, finishedAt: oldFinishedAt, progressPercent: oldProgress }
              : b,
          ),
        );
        this.toast.error('Failed to update finished status');
      },
    });
  }

  updateRating(book: Book, newRating: number): void {
    const oldRating = book.rating;

    // Optimistic update via signal
    this.rawBooks.update((books) =>
      books.map((b) => (b.id === book.id ? { ...b, rating: newRating } : b)),
    );

    this.booksService.update(book.id, { rating: newRating }).subscribe({
      error: () => {
        this.rawBooks.update((books) =>
          books.map((b) => (b.id === book.id ? { ...b, rating: oldRating } : b)),
        );
        this.toast.error('Failed to update rating');
      },
    });
  }
}
