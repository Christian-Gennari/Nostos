// src/app/reader/pdf-reader/pdf-reader.component.ts
import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  OnChanges,
  OnDestroy,
  OnInit,
  SimpleChanges,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import {
  FindResultMatchesCount,
  FindState,
  NgxExtendedPdfViewerComponent,
  NgxExtendedPdfViewerModule,
  NgxExtendedPdfViewerService,
  PagesLoadedEvent,
  PdfLoadedEvent,
  ScrollModeType,
  TextLayerRenderedEvent,
} from 'ngx-extended-pdf-viewer';
import './pdf-defaults';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, Subscription, firstValueFrom } from 'rxjs';
import { debounceTime, distinctUntilChanged, filter } from 'rxjs/operators';

import { PageHighlight, PdfAnnotationManager } from './pdf-annotation-manager';
import { bookFileUrl } from '../../core/book-url';
import { chapterRangeAt } from './pdf-chapter-progress';
import {
  buildPdfPassageDraft,
  detectPdfTextCapability,
  extractPdfPageLabels,
  extractPdfVisibleExcerpt,
  isSubstantialPageText,
  mapPdfOutline,
} from './pdf-document-helpers';
import { PdfReadAloudBridge } from './pdf-read-aloud-bridge';
import {
  DEFAULT_PDF_SCROLL_MODE,
  DEFAULT_PDF_ZOOM,
  PDF_READING_MODES,
  PDF_ZOOM_PRESETS,
  formatPdfZoomLabel,
  isPdfZoomPreset,
  nextPdfZoom,
  readPdfInversion,
  readPdfScrollMode,
  readPdfZoom,
  savePdfInversion,
  savePdfScrollMode,
  savePdfZoom,
} from './pdf-reader-settings';
import { PdfReaderTimers } from './pdf-reader-timers';
import {
  PdfSearchController,
  sourcePassagePhrase,
  sourcePassagePhrases,
} from './pdf-search-controller';
import { readUnlocatedNoteIds, rememberUnlocatedNoteId } from '../passage/note-locating';
import { PassageDraft, PassageMark, excerptOf } from '../passage/passage-mark';
import { isDesktopMouseSelection, type SelectionAnchor } from '../selection-anchor';
import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HighlightColour,
  noteHighlightColour,
} from '../highlight-colours';
import { NotesService } from '../../core/services/notes.service';
import { Note, noteHasText, pdfQuotePageToLocate } from '../../core/dtos/note.dtos';
import type { HighlightHit } from '../highlight-hit';
import { BooksService } from '../../core/services/books.service';
import { ThemeService, themeOption } from '../../core/services/theme.service';
import { IReader, ReaderProgress, ReaderSourceTarget, TocItem } from '../reader.interface';
import { AssistantContextService } from '../../ui/assistant/assistant-context.service';
import {
  shouldSuppressTouchClick,
  surfaceActionForPoint,
  swipePageAction,
  touchClickSuppressionForMovement,
  type TouchClickSuppression,
} from '../reader-keyboard';

export { PDF_ZOOM_PRESETS, isSubstantialPageText, sourcePassagePhrase, sourcePassagePhrases };

/**
 * Surround colour for the pdf.js viewer canvas. Light keeps the established
 * near-white; every other theme follows its `--bg-body` (THEMES' chrome
 * colour), so Sepia's page sits on parchment and Writer's Study's on its desk.
 * The library's `[theme]` input adds a CSS-variable sheet that themes its own
 * toolbar/sidebar/find bar chrome.
 */
const PDF_LIGHT_SURROUND = '#fefeff';

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

@Component({
  selector: 'app-pdf-reader',
  standalone: true,
  // `NgxExtendedPdfViewerModule` supplies the viewer itself. It used to be
  // imported alongside `IconButtonComponent`, which was only ever there for the
  // find bar's close control; with that gone the module is the whole list.
  imports: [NgxExtendedPdfViewerModule],
  templateUrl: './pdf-reader.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './pdf-reader.component.css',
})
export class PdfReader implements OnInit, OnChanges, OnDestroy, IReader {
  private highlightService = inject(PdfAnnotationManager);
  private notesService = inject(NotesService);
  private booksService = inject(BooksService);
  private themeService = inject(ThemeService);
  private assistantContext = inject(AssistantContextService);
  private pdfSearch = inject(NgxExtendedPdfViewerService);
  private cdr = inject(ChangeDetectorRef);
  private destroyRef = inject(DestroyRef);

  /**
   * This component's own host element. The library renders the viewer and find
   * bar inside it, so host-scoped queries reach pages, scrollport, and text
   * layers without touching the rest of the document.
   */
  private host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly timers = new PdfReaderTimers();
  private destroyed = false;

  /**
   * Reader signals published to the assistant context registry (issue #261):
   * the current page and selection. Additive only.
   */
  private assistantPage = signal<number | null>(null);
  private assistantSelection = signal<string | null>(null);
  private unregisterAssistantContext: (() => void) | null = null;

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
    savePdfInversion(this.bookId(), value);
  }

  private restoreSavedInversion(): void {
    this.savedInversion.set(readPdfInversion(this.bookId()));
  }

  @ViewChild(NgxExtendedPdfViewerComponent) pdfViewer!: NgxExtendedPdfViewerComponent;

  bookId = input.required<string>();
  initialLocation = input<string | undefined>();
  /** A highlight or note was saved here; the shell adds it to its list. */
  noteCreated = output<Note>();
  /**
   * The book's notes, read once by the shell. The saved highlights are drawn
   * from them rather than fetched again; null while the shell is reading them.
   */
  notes = input<Note[] | null>(null);
  /** An Ask Nostos quote was found on its page and now has rectangles. */
  noteLocated = output<Note>();
  /** The notes dealt with so far; a later arrival is drawn on its own. */
  private readonly notesSeen = new Set<string>();
  /** Ask Nostos quotes to look for when their page's text layer is drawn, by page. */
  private readonly quotesByPage = new Map<number, Note[]>();

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

  private pdfDocRef: any = null;

  private readonly searchController = new PdfSearchController({
    pdfSearch: this.pdfSearch,
    textCapability: this.textCapability,
    timers: this.timers,
    getPdfDoc: () => this.pdfDocRef,
  });

  /** Shared shell search state; PDF.js remains the matching engine. */
  searchState = this.searchController.searchState;
  private pdfSearchReady = this.searchController.pdfSearchReady;
  readonly searchAvailable = this.searchController.searchAvailable;

  private surfaceTouchStart:
    | { x: number; y: number; at: number; target: EventTarget | null }
    | null = null;
  private surfaceClickSuppression: TouchClickSuppression | null = null;

  pdfSrc = computed(() => bookFileUrl(this.bookId()));
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
  // `page-width` everywhere: a fitted full page renders a 512-page book at about
  // 9.5px on a phone, and a two-column page at about 7px on a 1280px screen. That
  // is not reading, it is squinting. "Whole page" stays one tap away in the zoom menu.
  zoomLevel = signal<string | number>(DEFAULT_PDF_ZOOM);

  /**
   * pdf.js binds bare R / Shift+R to document rotation even when the viewer
   * toolbar is hidden. Nostos owns rotation explicitly instead: those library
   * shortcuts are disabled below, while this session-local angle is exposed in
   * the existing View settings so a rotated document always has a way home.
   */
  readonly ignoredViewerKeys = ['R', 'SHIFT+R'];
  rotation = signal<0 | 90 | 180 | 270>(0);

  private readonly _currentPage = signal(1);
  get currentPage(): number {
    return this._currentPage();
  }
  set currentPage(value: number) {
    this._currentPage.set(value);
  }

  totalPages = 0;

  private readonly readAloudBridge = new PdfReadAloudBridge({
    language: () => this.language(),
    pageCount: () => this.totalPages,
    currentPage: () => this.currentPage,
    getPdfDoc: () => this.pdfDocRef,
    goToPage: (pageNumber) => this.goTo(pageNumber),
    hostElement: () => this.host.nativeElement,
    highlightService: this.highlightService,
    timers: this.timers,
  });

  /**
   * Read aloud. The cursor lives in {@link PdfReadAloud}; this is the reader's
   * side of it: a page's text runs, turning to a page, and drawing the mark.
   */
  readonly readAloud = this.readAloudBridge.readAloud;

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
  scrollMode = signal<ScrollModeType>(DEFAULT_PDF_SCROLL_MODE);

  /**
   * The two reading modes, offered in the shell's view panel. Continuous is the
   * default (§3); page-by-page stays available because §9 asks for both.
   */
  readonly readingModes = PDF_READING_MODES;
  readonly zoomPresets = PDF_ZOOM_PRESETS;

  private initialLoadComplete = false;
  private progressUpdater$ = new Subject<{ location: string; percentage: number }>();
  private progressSubscription: Subscription | null = null;

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.ngOnDestroy();
    });

    effect(() => {
      const notes = this.notes();
      if (!notes) return;
      untracked(() => {
        const arrived = notes.filter((note) => !this.notesSeen.has(note.id));
        if (arrived.length === 0) return;
        arrived.forEach((note) => this.notesSeen.add(note.id));
        this.applyNotes(arrived);
        this.waitForQuotes(arrived);
      });
    });
  }

  search(query: string): void {
    this.searchController.search(query);
  }

  nextSearchResult(): void {
    this.searchController.nextResult();
  }

  previousSearchResult(): void {
    this.searchController.previousResult();
  }

  clearSearch(): void {
    this.searchController.clear();
  }

  onFindMatchesCount(result: FindResultMatchesCount): void {
    this.searchController.onMatchesCount(result);
  }

  onFindState(state: FindState): void {
    this.searchController.onFindState(state);
  }

  rotateClockwise(): void {
    this.rotation.update((angle) => ((angle + 90) % 360) as 0 | 90 | 180 | 270);
  }

  rotateCounterclockwise(): void {
    this.rotation.update((angle) => ((angle + 270) % 360) as 0 | 90 | 180 | 270);
  }

  resetRotation(): void {
    this.rotation.set(0);
  }

  /** Set the reading mode and remember it for this book. */
  setScrollMode(mode: ScrollModeType): void {
    this.scrollMode.set(mode);
    savePdfScrollMode(this.bookId(), mode);
  }

  isScrollMode(mode: ScrollModeType): boolean {
    return this.scrollMode() === mode;
  }

  /** The viewer can change the mode itself (its own controls or keys). */
  onScrollModeChange(mode: ScrollModeType): void {
    this.setScrollMode(mode);
  }

  private restoreSavedScrollMode(): void {
    this.scrollMode.set(readPdfScrollMode(this.bookId()));
  }

  ngOnChanges(changes: SimpleChanges): void {
    // An offered passage previews the pen it will be saved in.
    if (changes['highlightColour'] && this.pendingHighlight && !this.commitInFlight) {
      this.repaintPage(this.pendingHighlight.pageNumber);
    }

    const bookChange = changes['bookId'];
    if (!bookChange || bookChange.firstChange) return;

    // Angular can reuse this component for PDF → PDF navigation. Consolidate
    // full per-book reset, timer cancellation, and settings reload here.
    this.resetForBook();
  }

  ngOnInit(): void {
    this.unregisterAssistantContext = this.assistantContext.register(
      () => ({
        readerType: 'pdf',
        pdfPage: this.assistantPage(),
        selectedText: this.assistantSelection(),
      }),
      { explicit: true },
    );

    this.resetForBook();
  }

  /**
   * Reset all per-book transient state, cancel active timers and pending
   * progress emissions, and reload per-book zoom/scroll/inversion preferences.
   */
  resetForBook(): void {
    this.timers.cancelAll();
    this.hoverPoint = null;
    this.overHighlight.set(false);

    this.searchController.reset();
    this.textCapability.set('unknown');
    this.pdfDocRef = null;

    this.readAloudBridge.reset();

    this.initialLoadComplete = false;
    this.pendingGroundedSourcePage = null;
    this._currentPage.set(1);
    this.totalPages = 0;
    this.toc.set([]);
    this.pageLabels.set([]);
    this.sourcePageLabels.clear();
    this.progress.set({ label: '', percentage: 0 });
    this.assistantPage.set(null);
    this.assistantSelection.set(null);
    this.rotation.set(0);

    this.notesSeen.clear();
    this.quotesByPage.clear();
    this.savedHighlights = [];
    this.pendingHighlight = null;
    this.commitInFlight = false;
    this.highlightWarning.set(null);

    this.surfaceTouchStart = null;
    this.surfaceClickSuppression = null;
    this.clearNativeSelection();

    this.bindProgressUpdater();

    // Zoom, reading mode, and inversion are per-book preferences; reset to
    // defaults first so a book without stored choices never inherits the prior
    // book's settings.
    this.zoomLevel.set(DEFAULT_PDF_ZOOM);
    this.restoreSavedZoom();
    this.scrollMode.set(DEFAULT_PDF_SCROLL_MODE);
    this.restoreSavedScrollMode();
    this.restoreSavedInversion();

    this.cdr.markForCheck();
  }

  private bindProgressUpdater(): void {
    this.progressSubscription?.unsubscribe();
    this.progressSubscription = null;
    if (this.destroyed) return;

    this.progressSubscription = this.progressUpdater$
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

  next(): void {
    if (this.currentPage < this.totalPages) {
      this.goTo(this.currentPage + 1);
    }
  }

  previous(): void {
    if (this.currentPage > 1) {
      this.goTo(this.currentPage - 1);
    }
  }

  goTo(target: string | number): void {
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
      if (targetPage !== this.currentPage) {
        this.readAloudBridge.notifyUserMoved();
      }
      this.currentPage = targetPage;
      this.updateProgressState(targetPage);
      if (targetYPercent !== null) {
        this.scrollToWithinPage(targetPage, targetYPercent);
      } else {
        this.timers.clearTimeout('scrollToWithinPage');
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
    const generation = this.timers.currentGeneration();
    return extractPdfVisibleExcerpt(
      doc,
      this.currentPage,
      this.totalPages,
      () => doc === this.pdfDocRef && this.timers.isCurrent(generation),
    );
  }

  private highlightSourcePassage(excerpt: string | null | undefined): void {
    this.searchController.highlightSourcePassage(excerpt);
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

  zoomIn(): void {
    // From a named fit, the first step lands on a concrete percentage so the
    // reader is never stuck on a fit it cannot enlarge.
    this.setZoom(nextPdfZoom(this.zoomLevel(), 'in'));
  }

  zoomOut(): void {
    this.setZoom(nextPdfZoom(this.zoomLevel(), 'out'));
  }

  /**
   * Set the zoom and remember it for THIS book (issue #226 §9: "zoom persists
   * per book"). A named fit is stored as the name, so it keeps adapting when the
   * window changes; a chosen percentage is stored as the number picked.
   */
  setZoom(zoom: string | number): void {
    this.zoomLevel.set(zoom);
    savePdfZoom(this.bookId(), zoom);
  }

  /** Whether a preset is the zoom currently in effect (drives the active chip). */
  isZoomPreset(preset: string | number): boolean {
    return isPdfZoomPreset(this.zoomLevel(), preset);
  }

  /**
   * What to show beside the zoom steps. A named fit reads as a percentage of the
   * page it is fitting, which is the only honest number available before the
   * rendition has measured anything.
   */
  zoomLabel(): string {
    return formatPdfZoomLabel(this.zoomLevel());
  }

  private restoreSavedZoom(): void {
    this.zoomLevel.set(readPdfZoom(this.bookId()));
  }

  // --- PDF Events ---

  onPagesLoaded(event: PagesLoadedEvent): void {
    this.totalPages = event.pagesCount;
    // At pagesLoaded the viewer application has mounted and its public find
    // service is expected to be usable. search() still handles a defensive
    // undefined return if the library reports readiness too early.
    this.pdfSearchReady.set(true);
    // The pages exist now: paint the highlights the shell's notes already gave us.
    this.repaintSavedHighlightPages();

    // The outline needs the PDFDocumentProxy, and `pdfLoaded` cannot provide it:
    // the library's PdfLoadedEvent is `{ pagesCount }` and nothing else, so the
    // old `if (pdfDoc)` guard was always false and the contents rail stayed
    // empty for every PDF — a 512-page book with 129 bookmarks rendered
    // "No table of contents available." (issue #226 §1). `pagesLoaded.source`
    // IS the viewer application, and it carries the document.
    const doc = (event as any).source?.pdfDocument ?? null;
    if (doc && doc !== this.pdfDocRef) {
      this.pdfDocRef = doc;
      const generation = this.timers.currentGeneration();
      void this.loadPdfOutline(doc, generation);
      void this.loadPdfPageLabels(doc, generation);
      void this.detectTextCapability(doc, event.pagesCount, generation);
    }

    if (this.pendingGroundedSourcePage !== null) {
      const groundedPage = this.pendingGroundedSourcePage;
      this.pendingGroundedSourcePage = null;
      this.goTo(groundedPage);
      this.timers.replaceTimeout('initialLoad', () => (this.initialLoadComplete = true), 500);
    } else if (!this.initialLoadComplete) {
      const startLoc = this.initialLocation();

      if (startLoc) {
        this.goTo(startLoc);
      } else {
        this.updateProgressState(1);
      }

      this.timers.replaceTimeout('initialLoad', () => (this.initialLoadComplete = true), 500);
    }
  }

  /** Best-effort second path: some loads fire this with the document attached. */
  async onPdfLoaded(event: PdfLoadedEvent): Promise<void> {
    const doc = (event as any).source?.pdfDocument ?? (event as any).pdfDocument ?? null;
    if (doc && doc !== this.pdfDocRef) {
      this.pdfDocRef = doc;
      const generation = this.timers.currentGeneration();
      await Promise.all([
        this.loadPdfOutline(doc, generation),
        this.loadPdfPageLabels(doc, generation),
        this.detectTextCapability(doc, event.pagesCount, generation),
      ]);
    }
  }

  private async loadPdfPageLabels(pdfDoc: any, generation: number): Promise<void> {
    const labels = await extractPdfPageLabels(pdfDoc);
    if (pdfDoc !== this.pdfDocRef || !this.timers.isCurrent(generation)) return;
    this.pageLabels.set(labels);
    if (this.totalPages > 0) this.updateProgressState(this.currentPage);
  }

  private async detectTextCapability(
    pdfDoc: any,
    knownPageCount: number,
    generation: number,
  ): Promise<void> {
    const capability = await detectPdfTextCapability(
      pdfDoc,
      knownPageCount,
      () => pdfDoc === this.pdfDocRef && this.timers.isCurrent(generation),
    );
    if (capability === null || pdfDoc !== this.pdfDocRef || !this.timers.isCurrent(generation)) {
      return;
    }

    this.textCapability.set(capability);
    if (capability === 'unavailable') {
      this.clearSearch();
      this.clearNativeSelection();
    }
  }

  /** Map the embedded PDF outline into the contents rail the shell renders. */
  private async loadPdfOutline(pdfDoc: any, generation: number): Promise<void> {
    try {
      const outline = await pdfDoc.getOutline();
      if (pdfDoc !== this.pdfDocRef || !this.timers.isCurrent(generation)) return;
      if (!outline || outline.length === 0) {
        this.toc.set([]);
        return;
      }
      const mapped = await mapPdfOutline(outline, pdfDoc);
      if (pdfDoc !== this.pdfDocRef || !this.timers.isCurrent(generation)) return;
      this.toc.set(mapped);
      this.refreshChapterProgress();
    } catch (err) {
      if (pdfDoc !== this.pdfDocRef || !this.timers.isCurrent(generation)) return;
      console.error('Error fetching PDF outline', err);
      this.toc.set([]);
    }
  }

  /**
   * The viewer's page is bound one way (`[page]`), and this is the only place
   * that follows it: a `[(page)]` banana would assign `currentPage` before this
   * handler runs, so a user's own page turn would never look like a move.
   */
  onPageChange(newPage: number | undefined): void {
    // The viewer's `page` model is `number | undefined` (ngx-extended-pdf-viewer 26+);
    // undefined means no document page yet, which is not a move.
    if (newPage === undefined) return;
    if (newPage !== this.currentPage) {
      this.clearNativeSelection();
      this.highlightWarning.set(null);
      this.readAloudBridge.notifyUserMoved();
    }
    this.currentPage = newPage;
    this.updateProgressState(newPage);
  }

  onTextLayerRendered(event: TextLayerRenderedEvent): void {
    const textLayerDiv = event.source.textLayer?.div;
    if (!textLayerDiv) return;

    this.highlightService.paint(
      textLayerDiv,
      this.highlightsOnPage(event.pageNumber),
      DEFAULT_HIGHLIGHT_COLOUR,
    );

    // A page redraws its text layer on zoom and when it scrolls back in.
    this.readAloudBridge.repaintOnTextLayer(event.pageNumber, textLayerDiv);

    void this.locateQuotes(event.pageNumber, textLayerDiv);
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
    this.timers.replaceTimeout(
      'touchSelection',
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
      this.timers.replaceTimeout(
        'selectionDismiss',
        () => {
          if (this.pendingHighlight === pending) this.selectionDismissed.emit();
        },
        0,
      );
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
    this.hoverPoint =
      event.buttons === 0 ? { target: event.target, x: event.clientX, y: event.clientY } : null;
    this.timers.requestAnimationFrameOnce('hover', () => {
      const point = this.hoverPoint;
      const over =
        !!point
        && !this.pendingHighlight
        && this.highlightsAtPoint(point.target, point.x, point.y, false).length > 0;
      if (over !== this.overHighlight()) this.overHighlight.set(over);
    });
  }

  onSurfacePointerLeave(): void {
    this.hoverPoint = null;
    this.timers.cancelAnimationFrame('hover');
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

  private savedHighlightOf(note: Note): PageHighlight | null {
    try {
      const range = JSON.parse(note.cfiRange!);
      return {
        id: note.id,
        pageNumber: range.pageNumber,
        rects: range.rects || [],
        colour: noteHighlightColour(note),
        noted: noteHasText(note),
      };
    } catch {
      return null;
    }
  }

  /** Adds notes' highlights. One already held, such as a mark made here, stays. */
  private applyNotes(notes: Note[]): void {
    const held = new Set(this.savedHighlights.map((highlight) => highlight.id));
    const added = notes
      .filter((note) => note.cfiRange && !held.has(note.id))
      .map((note) => this.savedHighlightOf(note))
      .filter((highlight) => highlight !== null);
    this.savedHighlights.push(...added);
    this.repaintSavedHighlightPages();
  }

  /**
   * Ask Nostos quotes whose page is known but holds no rectangles yet are drawn
   * from that page's text layer, the same text a hand-made highlight is
   * selected from. So a quote costs nothing until its page is drawn: one on
   * screen is looked for now, any other when the reader comes to it. A quote
   * with no page is not looked for.
   */
  private waitForQuotes(notes: Note[]): void {
    const notFound = readUnlocatedNoteIds();
    const pages = new Set<number>();
    for (const note of notes) {
      const pageNumber = pdfQuotePageToLocate(note);
      if (pageNumber === null || notFound.has(note.id)) continue;
      this.quotesByPage.set(pageNumber, [...(this.quotesByPage.get(pageNumber) ?? []), note]);
      pages.add(pageNumber);
    }

    for (const pageNumber of pages) {
      const layer = this.host.nativeElement.querySelector(
        `.page[data-page-number="${pageNumber}"] .textLayer`,
      ) as HTMLElement | null;
      if (layer?.textContent?.trim()) void this.locateQuotes(pageNumber, layer);
    }
  }

  /**
   * Save and draw the quotes waiting on a drawn page. Nothing is drawn unless
   * the words stand on the page exactly once; a scanned page has no text, so
   * its quote simply stays a note.
   */
  private async locateQuotes(pageNumber: number, layer: HTMLElement): Promise<void> {
    const quotes = this.quotesByPage.get(pageNumber);
    if (!quotes) return;
    this.quotesByPage.delete(pageNumber);

    const generation = this.timers.currentGeneration();
    // Read together: a zoom can replace the layer during the saves below.
    const found = quotes.map((note) => ({
      note,
      rects: this.highlightService.quoteRects(layer, note.selectedText ?? ''),
    }));
    for (const { note, rects } of found) {
      if (!this.timers.isCurrent(generation)) return;
      if (!rects) {
        rememberUnlocatedNoteId(note.id);
        continue;
      }

      try {
        const location = JSON.stringify({ pageNumber, rects });
        const saved = await firstValueFrom(this.notesService.setLocation(note.id, location));
        if (!this.timers.isCurrent(generation)) return;
        // Deleted while it was being saved: nothing to draw.
        if (!saved.cfiRange || !this.notes()?.some((held) => held.id === note.id)) continue;
        this.savedHighlights = this.savedHighlights.filter((held) => held.id !== note.id);
        this.applyNotes([saved]);
        this.noteLocated.emit(saved);
      } catch (error) {
        if (!this.timers.isCurrent(generation)) return;
        // The server refused this note for good: do not ask at every opening.
        if (error instanceof HttpErrorResponse && error.status === 400) {
          rememberUnlocatedNoteId(note.id);
        }
        console.warn('Could not save where a quote stands on its page:', error);
      }
    }
  }

  private repaintSavedHighlightPages(): void {
    const pagesToRepaint = [...new Set(this.savedHighlights.map((h) => h.pageNumber))];
    pagesToRepaint.forEach((pageNum) => this.repaintPage(pageNum));
  }

  /** Repaint one saved highlight in another pen. */
  recolourHighlight(note: Note, colour: HighlightColour): void {
    const highlight = this.savedHighlights.find((h) => h.id === note.id);
    if (!highlight) return;
    highlight.colour = colour;
    highlight.noted = noteHasText(note);
    this.repaintPage(highlight.pageNumber);
  }

  removeHighlight(id: string): void {
    const index = this.savedHighlights.findIndex((h) => h.id === id);
    if (index !== -1) {
      const pageNumber = this.savedHighlights[index].pageNumber;
      this.savedHighlights.splice(index, 1);
      this.repaintPage(pageNumber);
    }
  }

  commitHighlight(content = '', pen?: HighlightColour, topics: readonly string[] = []): void {
    if (!this.pendingHighlight) {
      // Never leave the shell in Saving... if its confirmation and the reader
      // somehow drift out of sync.
      this.commitFailed.emit();
      return;
    }

    const p = this.pendingHighlight;
    const generation = this.timers.currentGeneration();
    this.commitInFlight = true;
    // The pen in hand when Save was pressed is the one stored and drawn.
    const colour = pen ?? this.highlightColour();
    const newHighlight: PageHighlight = {
      id: p.tempId,
      pageNumber: p.pageNumber,
      rects: p.rects,
      colour,
      noted: !!content.trim() || topics.length > 0,
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
        ...(topics.length ? { topics: [...topics] } : {}),
      })
      .subscribe({
        next: (createdNote) => {
          if (!this.timers.isCurrent(generation)) return;
          const index = this.savedHighlights.findIndex((h) => h.id === p.tempId);
          if (index !== -1) {
            this.savedHighlights[index].id = createdNote.id;
          }
          this.commitInFlight = false;
          this.pendingHighlight = null;
          this.noteCreated.emit(createdNote);
        },
        error: () => {
          if (!this.timers.isCurrent(generation)) return;
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

  discardHighlight(): void {
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
    const generation = this.timers.currentGeneration();
    return buildPdfPassageDraft({
      a,
      b,
      pdfDoc: doc,
      totalPages: this.totalPages,
      isCurrent: () => doc === this.pdfDocRef && this.timers.isCurrent(generation),
      pageName: (pageNumber) => this.pageName(pageNumber),
      chapterTitleAt: (pageNumber) => this.chapterTitleAt(pageNumber),
    });
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

  /**
   * The deepest outline entry that starts on or before a page. The same helper
   * places the chapter bar, so the title and the range cannot disagree.
   */
  private chapterTitleAt(pageNumber: number): string | null {
    return chapterRangeAt(this.toc(), pageNumber, Math.max(this.totalPages, pageNumber))?.label ?? null;
  }

  private clearNativeSelection(): void {
    const selection = window.getSelection();
    if (selection) selection.removeAllRanges();
    this.assistantSelection.set(null);
  }

  private repaintPage(pageNumber: number): void {
    const textLayerSelector = `.page[data-page-number="${pageNumber}"] .textLayer`;
    const textLayer = this.host.nativeElement.querySelector(textLayerSelector) as HTMLElement | null;

    if (textLayer) {
      this.highlightService.paint(
        textLayer,
        this.highlightsOnPage(pageNumber),
        DEFAULT_HIGHLIGHT_COLOUR,
      );
    }
  }

  private updateProgressState(page: number): void {
    const percentage = this.totalPages > 0 ? Math.floor((page / this.totalPages) * 100) : 0;
    const pageLabel = this.logicalPageLabel(page);

    this.assistantPage.set(page);

    // Physical PDF page remains authoritative for every navigation key. A
    // printed/logical label is presentation only and is shown alongside it.
    const chapter = chapterRangeAt(this.toc(), page, this.totalPages);
    this.progress.set({
      label: pageLabel
        ? `p. ${pageLabel} · PDF ${page} of ${this.totalPages}`
        : `Page ${page} of ${this.totalPages}`,
      percentage,
      chapterPercentage: chapter ? Math.floor(chapter.fraction * 100) : null,
      chapterLabel: chapter?.label ?? null,
      pageNumber: page,
      pageCount: this.totalPages,
      pageLabel,
    });

    const location = JSON.stringify({ pageNumber: page, yPercent: 0, rects: [] });
    this.progressUpdater$.next({ location, percentage });
  }

  /**
   * The outline can arrive after the first page was reported: place the
   * current page in it without another progress write.
   */
  private refreshChapterProgress(): void {
    const current = this.progress();
    if (!current.pageNumber) return;
    const chapter = chapterRangeAt(this.toc(), current.pageNumber, this.totalPages);
    this.progress.set({
      ...current,
      chapterPercentage: chapter ? Math.floor(chapter.fraction * 100) : null,
      chapterLabel: chapter?.label ?? null,
    });
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
    this.timers.replaceTimeout(
      'scrollToWithinPage',
      () => {
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
      },
      attempt === 0 ? 140 : 50,
    );
  }

  ngOnDestroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    this.timers.cancelAll();
    this.readAloudBridge.reset();
    this.searchController.reset();
    this.unregisterAssistantContext?.();
    this.unregisterAssistantContext = null;
    this.progressSubscription?.unsubscribe();
    this.progressSubscription = null;
    this.progressUpdater$.complete();
  }
}
