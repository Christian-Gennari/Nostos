// src/app/reader/pdf-reader/pdf-reader.ts
import {
  Component,
  ElementRef,
  input,
  computed,
  inject,
  OnInit,
  OnChanges,
  SimpleChanges,
  output,
  ViewChild,
  OnDestroy,
  signal,
  HostListener,
} from '@angular/core';
import {
  NgxExtendedPdfViewerModule,
  NgxExtendedPdfViewerComponent,
  NgxExtendedPdfViewerService,
  FindResultMatchesCount,
  FindState,
  TextLayerRenderedEvent,
  PagesLoadedEvent,
  PdfLoadedEvent,
  ScrollModeType,
} from 'ngx-extended-pdf-viewer';
import { Subject } from 'rxjs';
import { debounceTime, distinctUntilChanged, filter } from 'rxjs/operators';

import { PdfAnnotationManager, PageHighlight } from './pdf-annotation-manager';
import {
  asHighlightColour,
  DEFAULT_HIGHLIGHT_COLOUR,
  HighlightColour,
} from '../highlight-colours';
import { NotesService } from '../../core/services/notes.service';
import { BooksService } from '../../core/services/books.service';
import { ThemeService } from '../../core/services/theme.service';
import {
  IReader,
  ReaderProgress,
  ReaderSearchState,
  ReaderSourceTarget,
  TocItem,
} from '../reader.interface';
import { AssistantContextService } from '../../ui/assistant/assistant-context.service';

/**
 * Surround colours for the pdf.js viewer canvas, mirroring the Nostos tokens
 * from styles.css. Light keeps the established near-white; dark follows
 * `--bg-body` (#0d0e11). The library's `[theme]` input adds a CSS-variable
 * sheet that themes its own toolbar/sidebar/find bar chrome.
 */
const PDF_LIGHT_SURROUND = '#fefeff';
const PDF_DARK_SURROUND = '#0d0e11';

/**
 * Page fits a fixed-layout document can be read at, offered in the shell's view
 * panel. A phone default of a fitted whole page renders a 512-page book at about
 * 9.5px, so 'page-width' leads; 100 is actual size (issue #226 §9).
 */
export const PDF_ZOOM_PRESETS: { value: string | number; label: string }[] = [
  { value: 'page-width', label: 'Fit width' },
  { value: 'page-fit', label: 'Whole page' },
  { value: 100, label: 'Actual size' },
];

interface PendingPdfHighlight {
  tempId: string;
  pageNumber: number;
  rects: { left: number; top: number; width: number; height: number }[];
  selectedText: string;
  colour: HighlightColour;
}

@Component({
  selector: 'app-pdf-reader',
  standalone: true,
  // `NgxExtendedPdfViewerModule` supplies the viewer itself. It used to be
  // imported alongside `IconButtonComponent`, which was only ever there for the
  // find bar's close control; with that gone the module is the whole list.
  imports: [NgxExtendedPdfViewerModule],
  templateUrl: './pdf-reader.component.html',
  styleUrl: './pdf-reader.component.css',
})
export class PdfReader implements OnInit, OnChanges, OnDestroy, IReader {
  private highlightService = inject(PdfAnnotationManager);
  private notesService = inject(NotesService);
  private booksService = inject(BooksService);
  private themeService = inject(ThemeService);
  private assistantContext = inject(AssistantContextService);
  private pdfSearch = inject(NgxExtendedPdfViewerService);

  /**
   * Reader signals published to the assistant context registry (issue #261):
   * the current page and selection. Additive only.
   */
  private assistantPage = signal<number | null>(null);
  private assistantSelection = signal<string | null>(null);
  private unregisterAssistantContext: (() => void) | null = null;
  /**
   * This component's own host element. The library renders the find bar inside
   * it, so a query scoped here reaches the fields we need to focus without
   * touching the rest of the document.
   */
  private host = inject(ElementRef<HTMLElement>);

  // --- Theme-following surround (issue #259) ---

  /** Whether the app is currently in dark mode. */
  isDark = computed(() => this.themeService.theme() === 'dark');

  /** The library's own theme — 'dark' activates `<pdf-dark-theme>`. */
  pdfTheme = computed(() => (this.isDark() ? 'dark' : 'light'));

  /** Surround colour for the viewer canvas. */
  pdfBgColor = computed(() => (this.isDark() ? PDF_DARK_SURROUND : PDF_LIGHT_SURROUND));

  // --- Page-colour inversion (issue #259) ---

  /**
   * The user's explicit per-book choice, or null when they never made one.
   * It is a dark-mode preference: the "Page colours" row only exists in dark.
   */
  private savedInversion = signal<boolean | null>(null);

  /**
   * Whether the rendered page pixels are inverted. Derived from the live theme
   * so an in-book Light/Dark switch (#651) changes chrome and page together,
   * with no frame where they disagree: in dark the default is `true` so the
   * page looks like a native dark surface (matching how the EPUB reader
   * injects dark rules) and the user can flip it in View settings; in light
   * pages are always shown as printed. Persisted per book.
   */
  pageInverted = computed(() => this.isDark() && (this.savedInversion() ?? true));

  /** Toggle the inversion and persist the choice. */
  setPageInverted(value: boolean): void {
    this.savedInversion.set(value);
    try {
      localStorage.setItem(this.invertStorageKey(), JSON.stringify(value));
    } catch {
      // Private-mode storage — the mode still applies for the session.
    }
  }

  private invertStorageKey(): string {
    return `nostos.pdf-invert.${this.bookId()}`;
  }

  private restoreSavedInversion(): void {
    try {
      const raw = localStorage.getItem(this.invertStorageKey());
      if (raw !== null) {
        this.savedInversion.set(JSON.parse(raw) === true);
        return;
      }
    } catch {
      // fall through
    }
    // No choice yet: pageInverted follows the theme (inverted in dark).
    this.savedInversion.set(null);
  }

  @ViewChild(NgxExtendedPdfViewerComponent) pdfViewer!: NgxExtendedPdfViewerComponent;

  bookId = input.required<string>();
  initialLocation = input<string | undefined>();
  noteCreated = output<void>();
  highlightMode = input<boolean>(false);
  /** The book's highlighter pen (issue #208), owned by the shell. */
  highlightColour = input<HighlightColour>(DEFAULT_HIGHLIGHT_COLOUR);
  selectionCaptured = output<string>();
  commitFailed = output<void>();

  sidebarVisible = input<boolean>(false);
  sidebarVisibleChange = output<boolean>();

  /**
   * Text-dependent PDF tools are unavailable for image-only/scanned documents.
   * Unknown keeps the reader usable while bounded capability detection runs.
   */
  textCapability = signal<'unknown' | 'available' | 'unavailable'>('unknown');

  /** Shared shell search state; PDF.js remains the matching engine. */
  searchState = signal<ReaderSearchState>({ status: 'idle', current: 0, total: 0 });
  private pdfSearchReady = signal(false);
  readonly searchAvailable = computed(
    () => this.pdfSearchReady() && this.textCapability() !== 'unavailable',
  );
  private searchGeneration = 0;
  private activeSearchQuery = '';

  search(query: string): void {
    if (!this.searchAvailable()) {
      this.clearSearch();
      return;
    }

    const normalized = query.trim();
    if (!normalized) {
      // Clear the previous PDF.js find before replacing the tracked query with
      // an empty string, otherwise clearSearch() cannot know a mark exists.
      this.clearSearch();
      return;
    }

    const generation = ++this.searchGeneration;
    this.activeSearchQuery = normalized;
    this.searchState.set({ status: 'searching', current: 0, total: 0 });
    const counts = this.pdfSearch.find(normalized, {
      highlightAll: false,
      matchCase: false,
      dontScrollIntoView: false,
    });
    if (!counts) {
      // ngx-extended-pdf-viewer 25.6.x returns undefined until its find
      // controller is initialized. Fail back to idle instead of leaving the
      // shared panel permanently on “Searching book…”.
      if (generation === this.searchGeneration && this.activeSearchQuery === normalized) {
        this.pdfSearchReady.set(false);
        this.activeSearchQuery = '';
        this.searchState.set({ status: 'idle', current: 0, total: 0 });
      }
      return;
    }

    void Promise.all(counts).then((perPage) => {
      if (generation !== this.searchGeneration || this.activeSearchQuery !== normalized) return;
      const total = perPage.reduce((sum, count) => sum + count, 0);
      if (total === 0) {
        this.searchState.set({ status: 'not-found', current: 0, total: 0 });
        return;
      }
      const current = this.searchState().current || 1;
      this.searchState.set({ status: 'ready', current: Math.min(current, total), total });
    });
  }

  nextSearchResult(): void {
    if (!this.activeSearchQuery || this.searchState().total === 0) return;
    this.pdfSearch.findNext();
  }

  previousSearchResult(): void {
    if (!this.activeSearchQuery || this.searchState().total === 0) return;
    this.pdfSearch.findPrevious();
  }

  clearSearch(): void {
    const hadQuery = this.activeSearchQuery.length > 0;
    this.searchGeneration++;
    this.activeSearchQuery = '';
    this.searchState.set({ status: 'idle', current: 0, total: 0 });
    // Avoid touching the service during early teardown before the viewer has
    // initialized; when a query did run, an empty query is PDF.js's public clear.
    if (hadQuery) {
      this.pdfSearch.find('', { highlightAll: false, dontScrollIntoView: true });
    }
  }

  onFindMatchesCount(result: FindResultMatchesCount): void {
    if (!this.activeSearchQuery) return;
    if (result.total <= 0) {
      this.searchState.set({ status: 'not-found', current: 0, total: 0 });
      return;
    }
    this.searchState.set({ status: 'ready', current: result.current, total: result.total });
  }

  onFindState(state: FindState): void {
    if (!this.activeSearchQuery) return;
    if (state === FindState.PENDING) {
      const current = this.searchState();
      this.searchState.set({ ...current, status: 'searching' });
    } else if (state === FindState.NOT_FOUND) {
      this.searchState.set({ status: 'not-found', current: 0, total: 0 });
    }
  }

  pdfSrc = computed(() => `/api/books/${this.bookId()}/file`);
  savedHighlights: PageHighlight[] = [];

  private pendingHighlight: PendingPdfHighlight | null = null;
  private commitInFlight = false;
  highlightWarning = signal<string | null>(null);

  // --- IReader Implementation ---
  toc = signal<TocItem[]>([]);
  progress = signal<ReaderProgress>({ label: '', percentage: 0 });
  currentLocationTarget = computed(() => {
    const page = this.progress()?.pageNumber ?? 1;
    const toc = this.toc();
    if (toc.length === 0) return null;

    let activeTarget: string | number | null = null;
    let maxPage = -1;

    const traverse = (items: TocItem[]) => {
      for (const item of items) {
        const itemPage = typeof item.target === 'number' ? item.target : parseInt(item.target, 10);
        if (!isNaN(itemPage) && itemPage <= page && itemPage > maxPage) {
          maxPage = itemPage;
          activeTarget = item.target;
        }
        if (item.children) {
          traverse(item.children);
        }
      }
    };

    traverse(toc);
    return activeTarget;
  });

  // Internal State
  // `page-width` on a phone: a fitted full page renders a 512-page book at about
  // 9.5px, which is not reading, it is squinting. Desktop keeps the full-page fit.
  zoomLevel = signal<string | number>(this.initialZoom());

  /**
   * pdf.js binds bare R / Shift+R to document rotation even when the viewer
   * toolbar is hidden. Nostos owns rotation explicitly instead: those library
   * shortcuts are disabled below, while this session-local angle is exposed in
   * the existing View settings so a rotated document always has a way home.
   */
  readonly ignoredViewerKeys = ['R', 'SHIFT+R'];
  rotation = signal<0 | 90 | 180 | 270>(0);

  rotateClockwise(): void {
    this.rotation.update((angle) => ((angle + 90) % 360) as 0 | 90 | 180 | 270);
  }

  rotateCounterclockwise(): void {
    this.rotation.update((angle) => ((angle + 270) % 360) as 0 | 90 | 180 | 270);
  }

  resetRotation(): void {
    this.rotation.set(0);
  }

  currentPage = 1;
  totalPages = 0;
  private pdfDocRef: any = null;
  private pendingGroundedSourcePage: number | null = null;
  private pageLabels = signal<(string | null)[]>([]);
  private sourcePageLabels = new Map<number, string>();

  /**
   * Continuous vertical scrolling instead of one page at a time.
   *
   * The viewer was pinned to `ScrollModeType.page` (= 3), so a page could only be
   * left by clicking Next — there was no way to scroll on, which is what a reader
   * does by reflex in a 512-page book (issue #226 §3). The pager still jumps whole
   * pages, and progress still follows `pageChange`.
   *
   * Note there are two enums in this library: the `scrollMode` INPUT is typed
   * `ScrollModeType` (lowercase members) from `options/pdf-viewer`, while
   * `ScrollMode` (uppercase) is a different export. Binding the wrong one is a
   * template type error, not a silent no-op.
   */
  scrollMode = signal<ScrollModeType>(ScrollModeType.vertical);

  /**
   * The two reading modes, offered in the shell's view panel. Continuous is the
   * default (§3); page-by-page stays available because §9 asks for both.
   */
  readonly readingModes: { value: ScrollModeType; label: string }[] = [
    { value: ScrollModeType.vertical, label: 'Scroll' },
    { value: ScrollModeType.page, label: 'Page' },
  ];

  /** Set the reading mode and remember it for this book. */
  setScrollMode(mode: ScrollModeType): void {
    this.scrollMode.set(mode);
    try {
      localStorage.setItem(
        this.scrollStorageKey(),
        mode === ScrollModeType.page ? 'page' : 'scroll',
      );
    } catch {
      // Private-mode storage can throw — the mode still applies for the session.
    }
  }

  isScrollMode(mode: ScrollModeType): boolean {
    return this.scrollMode() === mode;
  }

  /** The viewer can change the mode itself (its own controls or keys). */
  onScrollModeChange(mode: ScrollModeType): void {
    this.setScrollMode(mode);
  }

  private scrollStorageKey(): string {
    return `nostos.pdf-scroll.${this.bookId()}`;
  }

  private restoreSavedScrollMode(): void {
    try {
      const saved = localStorage.getItem(this.scrollStorageKey());
      if (saved === 'page') this.scrollMode.set(ScrollModeType.page);
      else if (saved === 'scroll') this.scrollMode.set(ScrollModeType.vertical);
    } catch {
      // Unreadable storage keeps the continuous default.
    }
  }

  private initialZoom(): string {
    return typeof window !== 'undefined' && window.innerWidth <= 768 ? 'page-width' : 'page-fit';
  }

  private initialLoadComplete = false;

  private progressUpdater$ = new Subject<{ location: string; percentage: number }>();

  ngOnChanges(changes: SimpleChanges): void {
    const bookChange = changes['bookId'];
    if (!bookChange || bookChange.firstChange) return;

    // Angular can reuse this component for PDF → PDF navigation. Drop the old
    // document's transient find state immediately; the new viewer becomes
    // searchable again only after its own pagesLoaded event.
    this.clearSearch();
    this.pdfSearchReady.set(false);
    this.textCapability.set('unknown');
    this.pdfDocRef = null;
  }

  ngOnInit() {
    this.unregisterAssistantContext = this.assistantContext.register(
      () => ({
        readerType: 'pdf',
        pdfPage: this.assistantPage(),
        selectedText: this.assistantSelection(),
      }),
      { explicit: true },
    );

    // Zoom and reading mode are per-book preferences; the viewport default is only
    // a starting point (issue #226 §3, §9).
    this.restoreSavedZoom();
    this.restoreSavedScrollMode();
    this.restoreSavedInversion();
    this.loadNotes();

    this.progressUpdater$
      .pipe(
        filter(() => this.initialLoadComplete),
        debounceTime(1000),
        distinctUntilChanged((prev, curr) => prev.location === curr.location),
      )
      .subscribe((data) => {
        this.booksService.updateProgress(this.bookId(), data.location, data.percentage).subscribe();
      });
  }

  // --- IReader Methods ---

  next() {
    if (this.currentPage < this.totalPages) {
      this.goTo(this.currentPage + 1);
    }
  }

  previous() {
    if (this.currentPage > 1) {
      this.goTo(this.currentPage - 1);
    }
  }

  goTo(target: string | number) {
    let targetPage = this.currentPage;
    let targetYPercent: number | null = null;

    try {
      if (typeof target === 'number') {
        targetPage = target;
      } else if (typeof target === 'string' && target.trim().startsWith('{')) {
        const range = JSON.parse(target);
        if (range.pageNumber) targetPage = range.pageNumber;

        const storedY = Number(range.yPercent);
        if (Number.isFinite(storedY) && storedY > 0) {
          targetYPercent = storedY;
        } else if (Array.isArray(range.rects) && range.rects.length > 0) {
          const rectTop = Math.min(
            ...range.rects
              .map((rect: { top?: unknown }) => Number(rect?.top))
              .filter((top: number) => Number.isFinite(top)),
          );
          if (Number.isFinite(rectTop) && rectTop > 0) targetYPercent = rectTop;
        }
      } else if (typeof target === 'string') {
        const page = parseInt(target, 10);
        if (!isNaN(page)) targetPage = page;
      }
    } catch (e) {
      console.error('Invalid PDF location target', e);
    }

    if (targetPage > 0 && targetPage <= this.totalPages) {
      this.clearNativeSelection();
      this.highlightWarning.set(null);
      this.currentPage = targetPage;
      this.updateProgressState(targetPage);
      if (targetYPercent !== null) {
        this.scrollToWithinPage(targetPage, targetYPercent);
      }
    }
  }

  goToSource(target: ReaderSourceTarget): void {
    if (target.type !== 'pdf' || target.pdfPage === undefined) return;

    const sourceLabel = target.pdfPageLabel?.trim();
    if (sourceLabel) {
      this.sourcePageLabels.set(target.pdfPage, sourceLabel);
    }

    // ReaderShell can receive a grounded citation before pdf.js has emitted
    // pagesLoaded. goTo() rejects pages above totalPages=0, so remember this
    // exact physical page until the document is ready rather than consuming
    // the source link and silently staying on page 1.
    if (this.totalPages <= 0) {
      this.pendingGroundedSourcePage = target.pdfPage;
      return;
    }

    this.pendingGroundedSourcePage = null;
    this.goTo(target.pdfPage);
  }

  getCurrentLocation(): string {
    const loc = this.highlightService.captureNoteLocation();
    if (loc) {
      return JSON.stringify({ pageNumber: loc.pageNumber, yPercent: loc.yPercent, rects: [] });
    }
    return JSON.stringify({ pageNumber: this.currentPage, yPercent: 0, rects: [] });
  }

  zoomIn() {
    // From a named fit, the first step lands on a concrete percentage so the
    // reader is never stuck on a fit it cannot enlarge.
    const current = this.zoomLevel();
    this.setZoom(typeof current === 'number' ? Math.min(current + 10, 400) : 110);
  }

  zoomOut() {
    const current = this.zoomLevel();
    this.setZoom(typeof current === 'number' ? Math.max(current - 10, 20) : 90);
  }

  /**
   * Set the zoom and remember it for THIS book (issue #226 §9: "zoom persists
   * per book"). A named fit is stored as the name, so it keeps adapting when the
   * window changes; a chosen percentage is stored as the number picked.
   */
  setZoom(zoom: string | number): void {
    this.zoomLevel.set(zoom);
    try {
      localStorage.setItem(this.zoomStorageKey(), JSON.stringify(zoom));
    } catch {
      // Private-mode storage can throw — the zoom still applies for the session.
    }
  }

  /** Whether a preset is the zoom currently in effect (drives the active chip). */
  isZoomPreset(preset: string | number): boolean {
    const current = this.zoomLevel();
    if (typeof current === 'number' || typeof preset === 'number') return current === preset;
    return String(current).toLowerCase() === String(preset).toLowerCase();
  }

  /**
   * What to show beside the zoom steps. A named fit reads as a percentage of the
   * page it is fitting, which is the only honest number available before the
   * rendition has measured anything.
   */
  zoomLabel(): string {
    const current = this.zoomLevel();
    if (typeof current === 'number') return `${current}%`;
    return current === 'page-width' ? 'Fit width' : 'Whole page';
  }

  readonly zoomPresets = PDF_ZOOM_PRESETS;

  private zoomStorageKey(): string {
    return `nostos.pdf-zoom.${this.bookId()}`;
  }

  private restoreSavedZoom(): void {
    try {
      const raw = localStorage.getItem(this.zoomStorageKey());
      if (raw === null) return;
      const parsed = JSON.parse(raw) as string | number;
      if (typeof parsed === 'number' || typeof parsed === 'string') this.zoomLevel.set(parsed);
    } catch {
      // Unreadable storage falls back to the viewport default.
    }
  }

  // --- PDF Events ---

  onPagesLoaded(event: PagesLoadedEvent) {
    this.totalPages = event.pagesCount;
    // At pagesLoaded the viewer application has mounted and its public find
    // service is expected to be usable. search() still handles a defensive
    // undefined return if the library reports readiness too early.
    this.pdfSearchReady.set(true);
    this.loadNotes();

    // The outline needs the PDFDocumentProxy, and `pdfLoaded` cannot provide it:
    // the library's PdfLoadedEvent is `{ pagesCount }` and nothing else, so the
    // old `if (pdfDoc)` guard was always false and the contents rail stayed
    // empty for every PDF — a 512-page book with 129 bookmarks rendered
    // "No Table of Contents available." (issue #226 §1). `pagesLoaded.source`
    // IS the viewer application, and it carries the document.
    const doc = (event as any).source?.pdfDocument ?? null;
    if (doc && doc !== this.pdfDocRef) {
      this.pdfDocRef = doc;
      void this.loadPdfOutline(doc);
      void this.loadPdfPageLabels(doc);
      void this.detectTextCapability(doc, event.pagesCount);
    }

    if (this.pendingGroundedSourcePage !== null) {
      const groundedPage = this.pendingGroundedSourcePage;
      this.pendingGroundedSourcePage = null;
      this.goTo(groundedPage);
      setTimeout(() => (this.initialLoadComplete = true), 500);
    } else if (!this.initialLoadComplete) {
      const startLoc = this.initialLocation();

      if (startLoc) {
        this.goTo(startLoc);
      } else {
        this.updateProgressState(1);
      }

      setTimeout(() => (this.initialLoadComplete = true), 500);
    }
  }

  /** Best-effort second path: some loads fire this with the document attached. */
  async onPdfLoaded(event: PdfLoadedEvent) {
    const doc = (event as any).source?.pdfDocument ?? (event as any).pdfDocument ?? null;
    if (doc && doc !== this.pdfDocRef) {
      this.pdfDocRef = doc;
      await Promise.all([
        this.loadPdfOutline(doc),
        this.loadPdfPageLabels(doc),
        this.detectTextCapability(doc, event.pagesCount),
      ]);
    }
  }

  private async loadPdfPageLabels(pdfDoc: any): Promise<void> {
    if (typeof pdfDoc?.getPageLabels !== 'function') return;

    try {
      const labels = await pdfDoc.getPageLabels();
      this.pageLabels.set(
        Array.isArray(labels)
          ? labels.map((label) => (typeof label === 'string' && label.trim() ? label.trim() : null))
          : [],
      );
      if (this.totalPages > 0) this.updateProgressState(this.currentPage);
    } catch {
      this.pageLabels.set([]);
    }
  }

  /**
   * Detect whether text-dependent tools are meaningful without parsing the
   * entire document. A bounded, evenly distributed sample avoids turning a
   * 900-page scan into a second ingestion pass while still handling blank cover
   * pages in ordinary books.
   */
  private async detectTextCapability(pdfDoc: any, knownPageCount = this.totalPages): Promise<void> {
    if (typeof pdfDoc?.getPage !== 'function' || knownPageCount <= 0) return;

    const sampleCount = Math.min(knownPageCount, 8);
    const pages = [
      ...new Set(
        Array.from({ length: sampleCount }, (_, index) =>
          sampleCount === 1
            ? 1
            : 1 + Math.round((index * (knownPageCount - 1)) / (sampleCount - 1)),
        ),
      ),
    ];

    let inspected = 0;
    for (const pageNumber of pages) {
      try {
        const page = await pdfDoc.getPage(pageNumber);
        const textContent = await page.getTextContent();
        if (pdfDoc !== this.pdfDocRef) return;
        inspected += 1;
        const hasText = Array.isArray(textContent?.items)
          && textContent.items.some(
            (item: { str?: unknown }) => typeof item?.str === 'string' && item.str.trim().length > 0,
          );
        if (hasText) {
          this.textCapability.set('available');
          return;
        }
      } catch {
        // An unreadable sample is not evidence that the PDF has no text.
        this.textCapability.set('unknown');
        return;
      }
    }

    if (inspected > 0 && pdfDoc === this.pdfDocRef) {
      this.textCapability.set('unavailable');
      this.clearSearch();
      this.clearNativeSelection();
    }
  }

  /** Map the embedded PDF outline into the contents rail the shell renders. */
  private async loadPdfOutline(pdfDoc: any): Promise<void> {
    try {
      const outline = await pdfDoc.getOutline();
      if (!outline || outline.length === 0) {
        this.toc.set([]);
        return;
      }
      this.toc.set(await this.mapPdfOutline(outline, pdfDoc));
    } catch (err) {
      console.error('Error fetching PDF outline', err);
      this.toc.set([]);
    }
  }

  onPageChange(newPage: number) {
    if (newPage !== this.currentPage) {
      this.clearNativeSelection();
      this.highlightWarning.set(null);
    }
    this.currentPage = newPage;
    this.updateProgressState(newPage);
  }

  onTextLayerRendered(event: TextLayerRenderedEvent) {
    const textLayerDiv = event.source.textLayer?.div;
    if (!textLayerDiv) return;

    const pageHighlights = this.savedHighlights.filter((h) => h.pageNumber === event.pageNumber);
    const validHighlights = pageHighlights.filter((h) => h.rects && h.rects.length > 0);
    this.highlightService.paint(textLayerDiv, validHighlights, DEFAULT_HIGHLIGHT_COLOUR);
  }

  // --- Selection Logic ---

  /**
   * Keep Ask Nostos aligned with the browser's CURRENT PDF selection. This is
   * independent of highlight mode and selectionchange also clears stale context
   * when the browser selection collapses or leaves the PDF.
   */
  @HostListener('document:selectionchange')
  onNativeSelectionChange(): void {
    this.assistantSelection.set(this.highlightService.captureSelectionText?.() ?? null);
  }

  onTextSelection() {
    this.onNativeSelectionChange();
    if (
      this.commitInFlight
      || !this.highlightMode()
      || this.textCapability() === 'unavailable'
    ) return;

    const highlight = this.highlightService.captureHighlight(true);
    if (!highlight) return;

    if (highlight.status === 'cross-page') {
      // Reject the new invalid selection without destroying an older pending
      // one that the shell may still be offering to Save or Cancel.
      this.highlightWarning.set(
        'Highlights can only cover one PDF page at a time. Select text on one page at a time.',
      );
      this.clearNativeSelection();
      return;
    }

    this.highlightWarning.set(null);

    if (this.pendingHighlight) {
      const old = this.pendingHighlight;
      this.savedHighlights = this.savedHighlights.filter((h) => h.id !== old.tempId);
      this.repaintPage(old.pageNumber);
    }

    const tempId = `temp-${Date.now()}`;

    this.pendingHighlight = {
      tempId,
      pageNumber: highlight.pageNumber,
      rects: highlight.rects,
      selectedText: highlight.selectedText,
      colour: this.highlightColour(),
    };

    this.selectionCaptured.emit(highlight.selectedText);
    this.assistantSelection.set(highlight.selectedText);
  }

  // --- Helpers ---

  loadNotes() {
    this.notesService.list(this.bookId()).subscribe({
      next: (notes) => {
        this.savedHighlights = notes
          .filter((n) => n.cfiRange)
          .map((n) => {
            try {
              const range = JSON.parse(n.cfiRange!);
              return {
                id: n.id,
                pageNumber: range.pageNumber,
                rects: range.rects || [],
                colour: asHighlightColour(range.colour),
              } as PageHighlight;
            } catch {
              return null;
            }
          })
          .filter((h) => h !== null) as PageHighlight[];

        const pagesToRepaint = [...new Set(this.savedHighlights.map((h) => h.pageNumber))];
        pagesToRepaint.forEach((pageNum) => this.repaintPage(pageNum));
      },
    });
  }

  removeHighlight(id: string) {
    const index = this.savedHighlights.findIndex((h) => h.id === id);
    if (index !== -1) {
      const pageNumber = this.savedHighlights[index].pageNumber;
      this.savedHighlights.splice(index, 1);
      this.repaintPage(pageNumber);
    }
  }

  commitHighlight() {
    if (!this.pendingHighlight) {
      // Never leave the shell in Saving... if its confirmation and the reader
      // somehow drift out of sync.
      this.commitFailed.emit();
      return;
    }

    const p = this.pendingHighlight;
    this.commitInFlight = true;
    const newHighlight: PageHighlight = {
      id: p.tempId,
      pageNumber: p.pageNumber,
      rects: p.rects,
      colour: p.colour,
    };
    this.savedHighlights.push(newHighlight);
    this.repaintPage(p.pageNumber);

    this.clearNativeSelection();

    const cfiPayload = {
      pageNumber: p.pageNumber,
      rects: p.rects,
      colour: p.colour,
    };

    this.notesService
      .create(this.bookId(), {
        content: '',
        selectedText: p.selectedText,
        cfiRange: JSON.stringify(cfiPayload),
      })
      .subscribe({
        next: (createdNote) => {
          const index = this.savedHighlights.findIndex((h) => h.id === p.tempId);
          if (index !== -1) {
            this.savedHighlights[index].id = createdNote.id;
          }
          this.commitInFlight = false;
          this.pendingHighlight = null;
          this.noteCreated.emit();
        },
        error: () => {
          this.savedHighlights = this.savedHighlights.filter((h) => h.id !== p.tempId);
          this.repaintPage(p.pageNumber);
          // Preserve the exact capture. The shared shell deliberately keeps its
          // confirmation open after failure, so Save must retry this same mark.
          this.commitInFlight = false;
          this.pendingHighlight = p;
          this.commitFailed.emit();
        },
      });
  }

  discardHighlight() {
    if (this.commitInFlight) return;
    this.clearNativeSelection();
    this.pendingHighlight = null;
    this.highlightWarning.set(null);
  }

  private clearNativeSelection(): void {
    const selection = window.getSelection();
    if (selection) selection.removeAllRanges();
    this.assistantSelection.set(null);
  }

  private repaintPage(pageNumber: number) {
    const textLayerSelector = `.page[data-page-number="${pageNumber}"] .textLayer`;
    const textLayer = document.querySelector(textLayerSelector) as HTMLElement;

    if (textLayer) {
      const pageHighlights = this.savedHighlights.filter((h) => h.pageNumber === pageNumber);
      const validHighlights = pageHighlights.filter((h) => h.rects && h.rects.length > 0);
      this.highlightService.paint(textLayer, validHighlights, DEFAULT_HIGHLIGHT_COLOUR);
    }
  }

  private updateProgressState(page: number) {
    const percentage = this.totalPages > 0 ? Math.floor((page / this.totalPages) * 100) : 0;
    const pageLabel = this.logicalPageLabel(page);

    this.assistantPage.set(page);

    // Physical PDF page remains authoritative for every navigation key. A
    // printed/logical label is presentation only and is shown alongside it.
    this.progress.set({
      label: pageLabel
        ? `p. ${pageLabel} · PDF ${page} of ${this.totalPages}`
        : `Page ${page} of ${this.totalPages}`,
      percentage,
      pageNumber: page,
      pageCount: this.totalPages,
      pageLabel,
    });

    const location = JSON.stringify({ pageNumber: page, yPercent: 0, rects: [] });
    this.progressUpdater$.next({ location, percentage });
  }

  private logicalPageLabel(page: number): string | null {
    const documentLabel = this.pageLabels()[page - 1]?.trim();
    const sourceLabel = this.sourcePageLabels.get(page)?.trim();
    const label = documentLabel || sourceLabel || null;
    return label && label !== String(page) ? label : null;
  }

  /**
   * Page navigation stays exact; this only refines the viewport after the page
   * is present. It uses existing normalized y/rect data and retries briefly
   * because continuous mode may render the destination page after the page
   * binding changes. The first correction waits briefly because setting the
   * viewer's bound page can finish its own scroll after Angular has updated the
   * value; correcting at 0 ms was observed to be overwritten at high zoom.
   */
  private scrollToWithinPage(page: number, yPercent: number, attempt = 0): void {
    setTimeout(() => {
      const root = this.host.nativeElement;
      const scrollport = root.querySelector('#viewerContainer') as HTMLElement | null;
      const pageElement = root.querySelector(
        `.page[data-page-number="${page}"]`,
      ) as HTMLElement | null;

      if (!scrollport || !pageElement) {
        if (attempt < 8) this.scrollToWithinPage(page, yPercent, attempt + 1);
        return;
      }

      const clampedY = Math.min(1, Math.max(0, yPercent));
      const viewportRect = scrollport.getBoundingClientRect();
      const pageRect = pageElement.getBoundingClientRect();
      const readingOffset = Math.min(scrollport.clientHeight * 0.2, 120);
      const top =
        scrollport.scrollTop
        + (pageRect.top - viewportRect.top)
        + pageRect.height * clampedY
        - readingOffset;

      scrollport.scrollTo({ top: Math.max(0, top), behavior: 'auto' });
    }, attempt === 0 ? 140 : 50);
  }

  private async mapPdfOutline(outline: any[], pdfDoc: any): Promise<TocItem[]> {
    const items: TocItem[] = [];
    for (const item of outline) {
      let pageNumber = 1;
      try {
        if (item.dest) {
          let dest = item.dest;
          // Named destination — resolve to explicit destination array
          if (typeof dest === 'string') {
            dest = await pdfDoc.getDestination(dest);
          }
          if (Array.isArray(dest) && dest.length > 0) {
            // dest[0] is a page reference object — resolve to 0-based page index
            const pageIndex = await pdfDoc.getPageIndex(dest[0]);
            pageNumber = pageIndex + 1; // convert to 1-based
          }
        }
      } catch {
        // If resolution fails, fall back to page 1
        pageNumber = 1;
      }
      const children = item.items?.length ? await this.mapPdfOutline(item.items, pdfDoc) : [];
      items.push({ label: item.title, target: pageNumber, children });
    }
    return items;
  }

  ngOnDestroy() {
    this.clearSearch();
    this.pdfSearchReady.set(false);
    this.unregisterAssistantContext?.();
    this.unregisterAssistantContext = null;
    this.progressUpdater$.complete();
  }
}
