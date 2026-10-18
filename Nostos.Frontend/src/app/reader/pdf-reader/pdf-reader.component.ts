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
import { PdfReadAloud } from './pdf-read-aloud';
import { languageTagFromCatalogue } from '../read-aloud/read-aloud-language';
import { buildPdfPageText, type PageRect } from './pdf-read-aloud-text';
import { PassageDraft, PassageMark, closingOf, countWords, excerptOf } from '../passage/passage-mark';
import {
  isDesktopMouseSelection,
  type SelectionAnchor,
} from '../epub-reader/epub-annotation-manager';
import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HighlightColour,
  noteHighlightColour,
} from '../highlight-colours';
import { NotesService } from '../../core/services/notes.service';
import { Note, noteHasText } from '../../core/dtos/note.dtos';
import type { HighlightHit } from '../highlight-hit';
import { BooksService } from '../../core/services/books.service';
import { ThemeService, themeOption } from '../../core/services/theme.service';
import {
  IReader,
  ReaderProgress,
  ReaderSearchState,
  ReaderSourceTarget,
  TocItem,
} from '../reader.interface';
import { AssistantContextService } from '../../ui/assistant/assistant-context.service';
import {
  shouldSuppressTouchClick,
  surfaceActionForPoint,
  swipePageAction,
  touchClickSuppressionForMovement,
  type TouchClickSuppression,
} from '../reader-keyboard';

/**
 * Surround colour for the pdf.js viewer canvas. Light keeps the established
 * near-white; every other theme follows its `--bg-body` (THEMES' chrome
 * colour), so Sepia's page sits on parchment and Writer's Study's on its desk.
 * The library's `[theme]` input adds a CSS-variable sheet that themes its own
 * toolbar/sidebar/find bar chrome.
 */
const PDF_LIGHT_SURROUND = '#fefeff';

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
}

/**
 * How long a touch selection must hold still before it is offered: long
 * enough that a handle being dragged is not captured at every step.
 */
const TOUCH_SELECTION_SETTLE_MS = 300;

/** How far past a page with almost no text the excerpt for another edition may look. */
const PDF_EXCERPT_LOOKAHEAD_PAGES = 2;

/** Enough text to be one place in a book: more than a caption or a chapter title. */
export function isSubstantialPageText(text: string): boolean {
  return text.trim().length >= 100;
}

const UNSPACED_SCRIPT = /[\u3040-\u30ff\u3400-\u9fff\u0e00-\u0eff\u1000-\u109f\u1780-\u17ff]/g;

/**
 * The phrase the reader's search marks after a landing from another edition:
 * the passage's last six words, or, in a script written without spaces between
 * words, its last dozen characters. Null when the passage is too short to find.
 */
export function sourcePassagePhrase(excerpt: string | null | undefined): string | null {
  return sourcePassagePhrases(excerpt)[0] ?? null;
}

/**
 * The phrases to try, best first. After the passage's last six words come its
 * last runs of five or six words that carry no punctuation: a scan's text
 * layer most often differs from the book's text at a quotation mark, a hyphen
 * or a line end, and a run of plain words is still long enough to be one place.
 */
export function sourcePassagePhrases(excerpt: string | null | undefined): string[] {
  const flat = (excerpt ?? '').replace(/\s+/g, ' ').trim();
  if (!flat) return [];

  const unspaced = (flat.match(UNSPACED_SCRIPT)?.length ?? 0) * 2 > flat.length;
  if (unspaced) {
    // A line break in the page text is a space here; the search cannot cross it.
    const tail = flat.split(' ').filter(Boolean).at(-1) ?? '';
    return tail.length >= 4 ? [tail.slice(-12)] : [];
  }

  const words = flat.split(' ').filter(Boolean);
  if (words.length < 3) return [];

  const phrases = [words.slice(-6).join(' ')];
  let run: string[] = [];
  const close = () => {
    if (run.length >= 5) phrases.push(run.slice(-6).join(' '));
    run = [];
  };

  for (let index = words.length - 1; index >= 0 && phrases.length < 3; index--) {
    if (/^[\p{L}\p{N}]+$/u.test(words[index])) run.unshift(words[index]);
    else close();
  }
  if (phrases.length < 3) close();

  return [...new Set(phrases)];
}

/**
 * The longest page range whose text is gathered for a PDF passage. Longer
 * ranges are kept as pages alone: the viewer still shows them, but search does
 * not read them.
 */
const PDF_PASSAGE_TEXT_PAGE_LIMIT = 400;

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
  isDark = computed(() => this.themeService.mode() === 'dark');

  /** The library's own theme — 'dark' activates `<pdf-dark-theme>`. */
  pdfTheme = computed(() => (this.isDark() ? 'dark' : 'light'));

  /** Surround colour for the viewer canvas. */
  pdfBgColor = computed(() => {
    const theme = this.themeService.active();
    return theme === 'light' ? PDF_LIGHT_SURROUND : themeOption(theme).chrome;
  });

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
  /** The book's highlighter pen (issue #208), owned by the shell. */
  highlightColour = input<HighlightColour>(DEFAULT_HIGHLIGHT_COLOUR);
  selectionCaptured = output<string>();
  /**
   * Where a released mouse selection sits in the viewport, so the shell can
   * anchor its menu at the text like the EPUB reader (#650). Null when it could
   * not be measured or the selection was made by touch, which docks the bar.
   */
  selectionAnchored = output<SelectionAnchor | null>();
  /** A tap or click on the page asked to drop the passage that was offered. */
  selectionDismissed = output<void>();
  commitFailed = output<void>();
  /** Neutral PDF surface click asks the shared shell to reveal or hide chrome. */
  surfaceInteracted = output<void>();
  /** A click or tap landed on saved highlights; the shell opens the one to show. */
  highlightActivated = output<HighlightHit[]>();
  /** The mouse is over a saved highlight, which shows the pointer cursor. */
  readonly overHighlight = signal(false);
  private hoverFrame = 0;
  private hoverPoint: { target: EventTarget | null; x: number; y: number } | null = null;

  /** The book's catalogue language, for choosing a voice. A PDF rarely says its own. */
  language = input<string | null>(null);

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
    // A text-capable PDF should earn the search affordance. While capability
    // detection is still unknown, keep Search disabled rather than briefly
    // promising it for a scanned/image-only document.
    () => this.pdfSearchReady() && this.textCapability() === 'available',
  );
  private searchGeneration = 0;
  private activeSearchQuery = '';

  private surfaceTouchStart:
    | { x: number; y: number; at: number; target: EventTarget | null }
    | null = null;
  private surfaceClickSuppression: TouchClickSuppression | null = null;

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
  /**
   * Whether the reader's last pointer was a finger or pen. A touch selection
   * is offered as it settles and docks the bar; a mouse one is offered on
   * release and anchors the menu. Seeded from the device, then kept current
   * per pointerdown so a touchscreen laptop follows the input in use.
   */
  private touchInput =
    typeof window !== 'undefined'
    && typeof window.matchMedia === 'function'
    && window.matchMedia('(pointer: coarse)').matches;
  private touchSelectionTimer: ReturnType<typeof setTimeout> | undefined;
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

  /**
   * Read aloud. The cursor lives in {@link PdfReadAloud}; this is the reader's
   * side of it: a page's text runs, turning to a page, and drawing the mark.
   */
  readonly readAloud = new PdfReadAloud({
    language: () => languageTagFromCatalogue(this.language()),
    pageCount: () => this.totalPages,
    currentPage: () => this.currentPage,
    loadPage: (pageNumber) => this.readAloudPage(pageNumber),
    goToPage: (pageNumber) => this.readAloudGoTo(pageNumber),
    showMark: (pageNumber, rects) => this.showReadAloudMark(pageNumber, rects),
    clearMark: () => this.showReadAloudMark(0, []),
  });
  private readAloudMark: { pageNumber: number; rects: PageRect[] } | null = null;
  /** Counts page turns read-aloud asked for, so `pageChange` can tell them from the person's. */
  private readAloudMoves = 0;

  private async readAloudPage(pageNumber: number) {
    const doc = this.pdfDocRef;
    if (!doc) return null;
    try {
      const page = await doc.getPage(pageNumber);
      const content = await page.getTextContent();
      if (doc !== this.pdfDocRef) return null;
      const viewport = page.getViewport({ scale: 1 });
      return {
        items: Array.isArray(content?.items) ? content.items : [],
        geometry: { width: viewport.width, height: viewport.height, transform: viewport.transform },
      };
    } catch {
      return null;
    }
  }

  private readAloudGoTo(pageNumber: number): void {
    this.readAloudMoves++;
    this.goTo(pageNumber);
    setTimeout(() => {
      this.readAloudMoves = Math.max(0, this.readAloudMoves - 1);
    }, 400);
  }

  private showReadAloudMark(pageNumber: number, rects: PageRect[]): void {
    const previous = this.readAloudMark;
    this.readAloudMark = rects.length > 0 ? { pageNumber, rects } : null;
    if (previous && previous.pageNumber !== pageNumber) this.paintReadAloudMark(previous.pageNumber, []);
    if (pageNumber > 0) this.paintReadAloudMark(pageNumber, rects);
  }

  private paintReadAloudMark(pageNumber: number, rects: PageRect[]): void {
    const layer = document.querySelector(
      `.page[data-page-number="${pageNumber}"] .textLayer`,
    ) as HTMLElement | null;
    // A page that has not drawn its text yet is painted when it does.
    if (!layer) return;
    this.highlightService.paintReadAloudMark(layer, rects);
    this.keepReadAloudMarkInView(layer);
  }

  /**
   * A page is often taller than the window (and wider, zoomed in), so the
   * sentence being read can be off screen on the right page. Scrolling can make
   * the viewer report another page as current; that is read-aloud's doing, not
   * the person's.
   */
  private keepReadAloudMarkInView(layer: HTMLElement): void {
    const box = layer.querySelector('.read-aloud-mark-box') as HTMLElement | null;
    const container = document.getElementById('viewerContainer');
    if (!box || !container || typeof box.scrollIntoView !== 'function') return;

    const mark = box.getBoundingClientRect();
    const view = container.getBoundingClientRect();
    if (mark.height === 0 || view.height === 0) return;
    // The transport floats over the bottom of the page, hence the larger margin there.
    const visible =
      mark.top >= view.top + 8 &&
      mark.bottom <= view.bottom - 96 &&
      mark.left >= view.left &&
      mark.left <= view.right - 40;
    if (visible) return;

    this.readAloudMoves++;
    box.scrollIntoView({ block: 'center', inline: 'nearest' });
    setTimeout(() => {
      this.readAloudMoves = Math.max(0, this.readAloudMoves - 1);
    }, 400);
  }
  private pendingGroundedSourcePage: number | null = null;
  private sourceHighlightGeneration = 0;
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
    // An offered passage previews the pen it will be saved in.
    if (changes['highlightColour'] && this.pendingHighlight && !this.commitInFlight) {
      this.repaintPage(this.pendingHighlight.pageNumber);
    }

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
      // The pager and the contents list come through here, and set the page
      // before the viewer reports it, so `pageChange` would see no change.
      if (targetPage !== this.currentPage && this.readAloudMoves === 0) {
        this.readAloud.notifyUserMoved();
      }
      this.currentPage = targetPage;
      this.updateProgressState(targetPage);
      if (targetYPercent !== null) {
        this.scrollToWithinPage(targetPage, targetYPercent);
      }
    }
  }

  /**
   * The first text on the current page, for finding the same place in another
   * edition. Read from the document itself rather than the rendered layer, so
   * it works before the page has painted. Null for a page with no text layer.
   */
  async visibleExcerpt(): Promise<string | null> {
    const doc = this.pdfDocRef;
    if (!doc || this.totalPages <= 0) return null;

    // A plate or a chapter title page says too little to be found in another
    // edition, and a caption often repeats a line of the text. Reading resumes
    // on the next page with text, so that page speaks for this one.
    let sparse: string | null = null;
    const lastPage = Math.min(this.totalPages, this.currentPage + PDF_EXCERPT_LOOKAHEAD_PAGES);
    for (let pageNumber = this.currentPage; pageNumber <= lastPage; pageNumber++) {
      const text = await this.pageExcerptText(doc, pageNumber);
      if (doc !== this.pdfDocRef) return null;
      if (text && isSubstantialPageText(text)) return text.slice(0, 280);
      if (pageNumber === this.currentPage) sparse = text;
    }

    return sparse ? sparse.slice(0, 280) : null;
  }

  private async pageExcerptText(doc: any, pageNumber: number): Promise<string | null> {
    try {
      const page = await doc.getPage(pageNumber);
      const content = await page.getTextContent();
      if (doc !== this.pdfDocRef) return null;

      const text = (Array.isArray(content?.items) ? content.items : [])
        .map((item: { str?: unknown }) => (typeof item?.str === 'string' ? item.str : ''))
        .join(' ')
        .replace(/\s+/g, ' ')
        .trim();

      return text || null;
    } catch {
      return null;
    }
  }

  /**
   * After landing on a page from another edition, mark the passage with the
   * reader's own search. A handful of the passage's last words is enough to
   * find it and short enough to survive a line break in the page text. Not
   * finding it is fine: the page is already the right one.
   */
  private highlightSourcePassage(excerpt: string | null | undefined): void {
    // A newer landing, another document or a destroyed reader cancels a
    // pending highlight: it must never search a page it was not meant for.
    const generation = ++this.sourceHighlightGeneration;

    const phrases = sourcePassagePhrases(excerpt);
    if (phrases.length === 0) return;

    const doc = this.pdfDocRef;
    const current = () =>
      generation === this.sourceHighlightGeneration && (doc === null || doc === this.pdfDocRef);

    // The passage is the book's text as the server read it; the page's own
    // text layer can differ by a word. When a phrase is not on the page, the
    // next one from the same passage is tried.
    const attempt = (index: number, remaining: number) => {
      if (!current()) return;

      if (!this.searchAvailable()) {
        if (remaining > 0) setTimeout(() => attempt(index, remaining - 1), 500);
        return;
      }

      const phrase = phrases[index];
      this.search(phrase);

      const settle = (polls: number) => {
        if (!current() || this.activeSearchQuery !== phrase) return;

        const status = this.searchState().status;
        if (status === 'searching' && polls > 0) {
          setTimeout(() => settle(polls - 1), 250);
        } else if (status === 'not-found') {
          if (index + 1 < phrases.length) attempt(index + 1, remaining);
          else this.clearSearch();
        }
      };
      settle(40);
    };
    attempt(0, 20);
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
      this.highlightSourcePassage(target.excerpt);
      return;
    }

    this.pendingGroundedSourcePage = null;
    this.goTo(target.pdfPage);
    this.highlightSourcePassage(target.excerpt);
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
        // An unreadable sample is not evidence that the PDF has no text. An
        // obsolete document must also not overwrite the capability state of a
        // PDF that replaced it while this async sample was in flight.
        if (pdfDoc !== this.pdfDocRef) return;
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
      if (this.readAloudMoves === 0) this.readAloud.notifyUserMoved();
    }
    this.currentPage = newPage;
    this.updateProgressState(newPage);
  }

  onTextLayerRendered(event: TextLayerRenderedEvent) {
    const textLayerDiv = event.source.textLayer?.div;
    if (!textLayerDiv) return;

    this.highlightService.paint(
      textLayerDiv,
      this.highlightsOnPage(event.pageNumber),
      DEFAULT_HIGHLIGHT_COLOUR,
    );

    // A page redraws its text layer on zoom and when it scrolls back in.
    if (this.readAloudMark?.pageNumber === event.pageNumber) {
      this.highlightService.paintReadAloudMark(textLayerDiv, this.readAloudMark.rects);
      this.keepReadAloudMarkInView(textLayerDiv);
    }
  }

  /**
   * The marks to draw on a page: the saved ones plus the pending passage, so
   * it stays visible once the native selection is gone (typing a note moves
   * the selection into the note field).
   */
  private highlightsOnPage(pageNumber: number): PageHighlight[] {
    const marks = this.savedHighlights.filter(
      (h) => h.pageNumber === pageNumber && h.rects && h.rects.length > 0,
    );
    const pending = this.pendingHighlight;
    // While a commit is in flight the pending mark is already among the saved.
    if (pending?.pageNumber === pageNumber && !marks.some((h) => h.id === pending.tempId)) {
      marks.push({
        id: pending.tempId,
        pageNumber,
        rects: pending.rects,
        colour: this.highlightColour(),
      });
    }
    return marks;
  }

  // --- Selection Logic ---

  /**
   * Keep Ask Nostos aligned with the browser's CURRENT PDF selection;
   * selectionchange also clears stale context when the browser selection
   * collapses or leaves the PDF. It is also how a touch selection is followed
   * while its handles are dragged, which fires no touch events.
   */
  @HostListener('document:selectionchange')
  onNativeSelectionChange(): void {
    this.assistantSelection.set(this.highlightService.captureSelectionText?.() ?? null);
    if (!this.touchInput) return;
    clearTimeout(this.touchSelectionTimer);
    this.touchSelectionTimer = setTimeout(
      () => this.captureSelection('touch'),
      TOUCH_SELECTION_SETTLE_MS,
    );
  }

  onSurfacePointerDown(event: PointerEvent): void {
    if (event.pointerType) this.touchInput = event.pointerType !== 'mouse';
  }

  onSurfaceClick(event: MouseEvent): void {
    const now = Date.now();
    if (
      shouldSuppressTouchClick(
        this.surfaceClickSuppression,
        event.clientX,
        event.clientY,
        now,
      )
    ) {
      this.surfaceClickSuppression = null;
      return;
    }
    if (this.surfaceClickSuppression && now > this.surfaceClickSuppression.untilMs) {
      this.surfaceClickSuppression = null;
    }

    const liveSelection = this.highlightService.captureSelectionText?.() ?? null;
    // The docked bar has no scrim to tap away on, so a tap or click on the
    // page with nothing selected is the way out. Deferred, and the pending
    // passage still counts as selected below, so this tap cannot also turn
    // the page.
    const pending = this.pendingHighlight;
    if (pending && !liveSelection) {
      setTimeout(() => {
        if (this.pendingHighlight === pending) this.selectionDismissed.emit();
      }, 0);
    }
    const selectedText = liveSelection ?? this.assistantSelection() ?? pending?.selectedText;

    // A saved highlight takes the tap before paging or chrome. A selection,
    // live or offered, still comes first.
    if (!selectedText) {
      const hits = this.highlightsAtPoint(event.target, event.clientX, event.clientY);
      if (hits.length > 0) {
        this.highlightActivated.emit(hits);
        return;
      }
    }

    const target = event.currentTarget as HTMLElement | null;
    const width = target?.clientWidth ?? window.innerWidth;
    const coarsePointer =
      typeof window.matchMedia === 'function' && window.matchMedia('(pointer: coarse)').matches;
    const action = surfaceActionForPoint({
      target: event.target,
      focusableSurface: '.textLayer',
      selectedText,
      clientX: event.clientX - (target?.getBoundingClientRect().left ?? 0),
      width,
      coarsePointer,
      edgePaging: this.scrollMode() === ScrollModeType.page,
    });

    if (action === 'next') this.next();
    else if (action === 'previous') this.previous();
    else if (action === 'toggle-chrome') this.surfaceInteracted.emit();
  }

  /** Follows the mouse to show the pointer over a highlight, once per frame at most. */
  onSurfacePointerMove(event: PointerEvent): void {
    if (event.pointerType !== 'mouse') return;
    this.hoverPoint = event.buttons === 0 ? { target: event.target, x: event.clientX, y: event.clientY } : null;
    if (this.hoverFrame) return;
    this.hoverFrame = requestAnimationFrame(() => {
      this.hoverFrame = 0;
      const point = this.hoverPoint;
      const over =
        !!point &&
        !this.pendingHighlight &&
        this.highlightsAtPoint(point.target, point.x, point.y, false).length > 0;
      if (over !== this.overHighlight()) this.overHighlight.set(over);
    });
  }

  onSurfacePointerLeave(): void {
    this.hoverPoint = null;
    if (this.overHighlight()) this.overHighlight.set(false);
  }

  /** The saved highlights under a viewport point, on the page the point is over. */
  private highlightsAtPoint(
    target: EventTarget | null,
    x: number,
    y: number,
    measure = true,
  ): HighlightHit[] {
    const page = (target as Element | null)?.closest?.('.page[data-page-number]');
    const pageNumber = Number(page?.getAttribute('data-page-number'));
    const textLayer = page?.querySelector<HTMLElement>('.textLayer');
    if (!textLayer || !Number.isInteger(pageNumber)) return [];

    // A mark still being saved has a temporary id and nothing to open yet.
    const saved = this.savedHighlights.filter(
      (h) => h.pageNumber === pageNumber && !!h.id && !h.id.startsWith('temp-'),
    );
    if (saved.length === 0) return [];
    return this.highlightService.hitTest(textLayer, saved, x, y, measure);
  }

  onSurfaceTouchStart(event: TouchEvent): void {
    if (event.touches.length !== 1) {
      this.surfaceTouchStart = null;
      return;
    }
    const touch = event.touches[0];
    this.surfaceTouchStart = {
      x: touch.clientX,
      y: touch.clientY,
      at: Date.now(),
      target: event.target,
    };
  }

  onSurfaceTouchEnd(event: TouchEvent): void {
    const start = this.surfaceTouchStart;
    this.surfaceTouchStart = null;

    // Snapshot first: a cross-page/invalid capture may deliberately clear the
    // browser selection, but that must not turn the same finger movement into a
    // page gesture afterwards.
    const selectionBeforeCapture =
      this.highlightService.captureSelectionText?.() ?? this.assistantSelection();
    this.touchInput = true;
    this.captureSelection('touch');

    if (!start || event.changedTouches.length !== 1) return;

    const touch = event.changedTouches[0];
    const now = Date.now();
    this.surfaceClickSuppression = touchClickSuppressionForMovement({
      startX: start.x,
      startY: start.y,
      endX: touch.clientX,
      endY: touch.clientY,
      nowMs: now,
    });

    // Continuous mode stays a native scroll surface. We still suppress a
    // synthetic click after a real drag so scrolling cannot flash reader chrome.
    if (this.scrollMode() !== ScrollModeType.page) return;

    const action = swipePageAction({
      target: start.target,
      focusableSurface: '.textLayer',
      selectedText:
        this.highlightService.captureSelectionText?.()
        ?? this.assistantSelection()
        ?? selectionBeforeCapture,
      startX: start.x,
      startY: start.y,
      endX: touch.clientX,
      endY: touch.clientY,
      durationMs: now - start.at,
    });
    if (!action) return;

    if (action === 'next') this.next();
    else this.previous();
  }

  onSurfaceTouchCancel(): void {
    this.surfaceTouchStart = null;
    this.surfaceClickSuppression = null;
  }

  /**
   * A mouse release completes a desktop selection and anchors the menu at the
   * text (#657). Touch has its own completion signals: touchend and the
   * settled selectionchange.
   */
  onSurfaceMouseUp(event: MouseEvent): void {
    // A tap's emulated mouseup belongs to touchend; only the primary button
    // completes a selection.
    if (this.touchInput || (typeof event.button === 'number' && event.button !== 0)) return;
    // A mouse on a touch-first device gets the docked, touch-style offer.
    this.captureSelection(isDesktopMouseSelection(event, window) ? 'mouse' : 'touch');
  }

  /**
   * Offers the current selection as a pending highlight. The native selection
   * is always left in place: on a phone its handles and OS menu belong to it
   * (#16), and adjusting them replaces the offered passage.
   */
  private captureSelection(input: 'mouse' | 'touch'): void {
    if (this.commitInFlight || this.textCapability() === 'unavailable') return;

    const highlight = this.highlightService.captureHighlight(true);
    if (!highlight) {
      this.highlightWarning.set(null);
      return;
    }

    if (highlight.status === 'cross-page') {
      // Say why nothing is offered, but leave the selection (it may be for
      // copying) and any older pending mark the shell is still offering.
      this.highlightWarning.set(
        'Highlights can only cover one PDF page at a time. Select text on one page at a time.',
      );
      return;
    }

    this.highlightWarning.set(null);

    const old = this.pendingHighlight;
    if (
      old
      && old.pageNumber === highlight.pageNumber
      && old.selectedText === highlight.selectedText
    ) {
      // The same settled selection reported again by another signal.
      return;
    }

    this.pendingHighlight = {
      tempId: `temp-${Date.now()}`,
      pageNumber: highlight.pageNumber,
      rects: highlight.rects,
      selectedText: highlight.selectedText,
    };
    if (old) this.repaintPage(old.pageNumber);
    this.repaintPage(highlight.pageNumber);

    this.selectionCaptured.emit(highlight.selectedText);
    this.assistantSelection.set(highlight.selectedText);
    this.selectionAnchored.emit(input === 'mouse' ? this.anchorForSelection() : null);
  }

  /** The live selection's box in the viewport; null when it cannot be measured. */
  private anchorForSelection(): SelectionAnchor | null {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return null;
    const rect = selection.getRangeAt(0).getBoundingClientRect?.();
    if (!rect || (rect.width === 0 && rect.height === 0)) return null;
    return { top: rect.top, bottom: rect.bottom, left: rect.left, right: rect.right };
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
                colour: noteHighlightColour(n),
                noted: noteHasText(n),
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

  /** Repaint one saved highlight in another pen. */
  recolourHighlight(note: Note, colour: HighlightColour): void {
    const highlight = this.savedHighlights.find((h) => h.id === note.id);
    if (!highlight) return;
    highlight.colour = colour;
    highlight.noted = noteHasText(note);
    this.repaintPage(highlight.pageNumber);
  }

  removeHighlight(id: string) {
    const index = this.savedHighlights.findIndex((h) => h.id === id);
    if (index !== -1) {
      const pageNumber = this.savedHighlights[index].pageNumber;
      this.savedHighlights.splice(index, 1);
      this.repaintPage(pageNumber);
    }
  }

  commitHighlight(content = '', pen?: HighlightColour) {
    if (!this.pendingHighlight) {
      // Never leave the shell in Saving... if its confirmation and the reader
      // somehow drift out of sync.
      this.commitFailed.emit();
      return;
    }

    const p = this.pendingHighlight;
    this.commitInFlight = true;
    // The pen in hand when Save was pressed is the one stored and drawn.
    const colour = pen ?? this.highlightColour();
    const newHighlight: PageHighlight = {
      id: p.tempId,
      pageNumber: p.pageNumber,
      rects: p.rects,
      colour,
      noted: !!content.trim(),
    };
    this.savedHighlights.push(newHighlight);
    this.repaintPage(p.pageNumber);

    this.clearNativeSelection();

    // The pen travels in the note's own `highlightColour` (issue #861), no
    // longer inside this location payload: one place to read it from, and one
    // to change when the highlight is recoloured.
    const cfiPayload = {
      pageNumber: p.pageNumber,
      rects: p.rects,
    };

    this.notesService
      .create(this.bookId(), {
        content,
        selectedText: p.selectedText,
        cfiRange: JSON.stringify(cfiPayload),
        highlightColour: colour,
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
    const old = this.pendingHighlight;
    this.pendingHighlight = null;
    if (old) this.repaintPage(old.pageNumber);
    this.highlightWarning.set(null);
  }

  // --- Passages (issue #1018): a PDF passage is a range of pages ---

  /** The page the offered selection is on, as one end of a passage. */
  passageMarkFromSelection(): PassageMark | null {
    const pending = this.pendingHighlight;
    if (!pending) return null;
    return this.pageMark(pending.pageNumber, excerptOf(pending.selectedText));
  }

  /**
   * The page on screen as one end of a passage. The only way to mark one in a
   * scanned PDF, whose pages have no text to select.
   */
  passageMarkHere(): PassageMark | null {
    return this.totalPages > 0 ? this.pageMark(this.currentPage, '') : null;
  }

  /**
   * The pages between two marks, whichever came first, and their reading
   * text where the PDF has a text layer (running heads and page numbers left
   * out). A scanned range is kept as pages alone.
   */
  async buildPassage(a: PassageMark, b: PassageMark): Promise<PassageDraft | null> {
    const doc = this.pdfDocRef;
    if (a.format !== 'pdf' || b.format !== 'pdf' || !doc) return null;
    const pages = [a.start, a.end, b.start, b.end].map(Number).filter((n) => Number.isInteger(n) && n > 0);
    if (pages.length !== 4) return null;
    const first = Math.min(...pages);
    const last = Math.max(...pages);

    const blocks: string[] = [];
    if (last - first < PDF_PASSAGE_TEXT_PAGE_LIMIT) {
      // Read one page past each end too: a running head is recognised by
      // standing on a neighbouring page, and the range's own first and last
      // pages have one neighbour inside it at most.
      const from = Math.max(1, first - 1);
      const to = Math.min(this.totalPages || last, last + 1);
      const read: Array<{ pageNumber: number; items: any[]; geometry: any } | null> = [];
      for (let pageNumber = from; pageNumber <= to; pageNumber++) {
        try {
          const page = await doc.getPage(pageNumber);
          const content = await page.getTextContent();
          if (doc !== this.pdfDocRef) return null;
          const viewport = page.getViewport({ scale: 1 });
          read.push({
            pageNumber,
            items: Array.isArray(content?.items) ? content.items : [],
            geometry: { width: viewport.width, height: viewport.height, transform: viewport.transform },
          });
        } catch {
          // A page that cannot be read leaves only its place in the range.
          read.push(null);
        }
      }
      const margins = read.map((page) => (page ? buildPdfPageText(page.items, page.geometry).marginLines : []));
      read.forEach((page, index) => {
        if (!page || page.pageNumber < first || page.pageNumber > last) return;
        const neighbours = [...(margins[index - 1] ?? []), ...(margins[index + 1] ?? [])];
        const text = buildPdfPageText(page.items, page.geometry, neighbours).text.trim();
        if (text) blocks.push(text);
      });
    }

    const text = blocks.join('\n\n').trim() || null;
    const range = first === last ? `Page ${this.pageName(first)}` : `Pages ${this.pageName(first)}–${this.pageName(last)}`;
    return {
      anchorKind: 'pdf_page',
      start: String(first),
      end: String(last),
      title: this.chapterTitleAt(first) ?? range,
      html: null,
      text,
      words: countWords(text),
      opening: text ? excerptOf(text, 140) : range,
      closing: text ? closingOf(text, 140) : range,
    };
  }

  private pageMark(pageNumber: number, excerpt: string): PassageMark {
    return {
      format: 'pdf',
      start: String(pageNumber),
      end: String(pageNumber),
      excerpt,
      label: `p. ${this.pageName(pageNumber)}`,
    };
  }

  /** The printed page name when the PDF has one, else the page number. */
  private pageName(pageNumber: number): string {
    return this.pageLabels()[pageNumber - 1] ?? String(pageNumber);
  }

  /** The deepest outline entry that starts on or before a page. */
  private chapterTitleAt(pageNumber: number): string | null {
    let best: { label: string; page: number } | null = null;
    const visit = (items: TocItem[]) => {
      for (const item of items) {
        const page = Number(item.target);
        if (Number.isInteger(page) && page <= pageNumber && (!best || page >= best.page)) {
          best = { label: item.label, page };
        }
        visit(item.children ?? []);
      }
    };
    visit(this.toc());
    return (best as { label: string } | null)?.label.trim() || null;
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
      this.highlightService.paint(
        textLayer,
        this.highlightsOnPage(pageNumber),
        DEFAULT_HIGHLIGHT_COLOUR,
      );
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
    this.readAloud.end();
    if (this.hoverFrame) cancelAnimationFrame(this.hoverFrame);
    ++this.sourceHighlightGeneration;
    clearTimeout(this.touchSelectionTimer);
    this.clearSearch();
    this.pdfSearchReady.set(false);
    this.unregisterAssistantContext?.();
    this.unregisterAssistantContext = null;
    this.progressUpdater$.complete();
  }
}
