import {
  Component,
  input,
  output,
  OnInit,
  OnDestroy,
  signal,
  computed,
  effect,
  inject,
  Injector,
  DestroyRef,
  ElementRef,
  untracked,
  ChangeDetectionStrategy,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ButtonComponent } from '../../ui/button/button.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';
import ePub, { Book, Rendition, Contents } from 'epubjs';
import { EMPTY, Subject, firstValueFrom } from 'rxjs';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  filter,
  switchMap,
  takeUntil,
} from 'rxjs/operators';

import { EpubAnnotationManager } from './epub-annotation-manager';
import { EpubContentsInteractionController } from './epub-contents-interaction';
import { EpubFileCacheService } from './epub-file-cache.service';
import { EpubGroundedNavigator } from './epub-grounded-navigator';
import { EpubLazyResources } from './epub-lazy-resources';
import { EpubSearchController } from './epub-search';
import { EpubTypographyController } from './epub-typography-controller';
import type { SelectionAnchor } from '../selection-anchor';
import { DEFAULT_HIGHLIGHT_COLOUR, HighlightColour } from '../highlight-colours';
import { NotesService } from '../../core/services/notes.service';
import { BooksService } from '../../core/services/books.service';
import { ThemeService } from '../../core/services/theme.service';
import { Book as BookDto } from '../../core/dtos/book.dtos';
import { readUnlocatedNoteIds, rememberUnlocatedNoteId } from '../passage/note-locating';
import {
  ImportedNoteLocation,
  Note,
  importedNoteLocation,
  isAskNostosQuote,
  isImportedAnchor,
  noteHasText,
} from '../../core/dtos/note.dtos';
import type { HighlightHit } from '../highlight-hit';
import {
  IReader,
  ReaderProgress,
  ReaderSourceTarget,
  TocItem,
} from '../reader.interface';
import { applyDocumentDirection, epubReadingDirection, type EpubDirection } from './epub-direction';
import { AssistantContextService } from '../../ui/assistant/assistant-context.service';

import {
  epubPassageCandidates,
  nodeAtKoreaderXPointer,
  normalizeEpubHrefForComparison,
  normalizedResourceOffsetOfNode,
  rangeForNormalizedResourceSpan,
  type EpubSpineSource,
} from './epub-grounded-source';
import {
  chapterFractionFromLocations,
  chapterFractionFromPages,
  chapterLocationBounds,
  chapterRangeForSpine,
  EpubChapterLocations,
  EpubChapterRange,
  epubChapterRanges,
  spineIndexFromCfi,
} from './epub-chapter-progress';
import { EpubReadAloud, type ReadAloudEpubContent } from './epub-read-aloud';
import { languageTagFromCatalogue } from '../read-aloud/read-aloud-language';
import {
  EPUB_FONT_OPTIONS,
  EPUB_LINE_OPTIONS,
  EPUB_MARGIN_OPTIONS,
  type EpubTypography,
} from './epub-typography';
import { visibleEpubExcerpt } from './epub-visible-excerpt';
import {
  cfiRangeEnds,
  cfiSpineIndex,
  cutSection,
  fragmentHtml,
  passageEnds,
  passagePlainText,
} from './epub-passage';
import {
  PassageDraft,
  PassageMark,
  closingOf,
  countWords,
  excerptOf,
} from '../passage/passage-mark';

export {
  resolveGroundedEpubResourceHref,
  type EpubSpineSource,
} from './epub-grounded-source';

export { registerNostosReaderThemes, selectNostosReaderTheme } from './epub-reader-theme';
export {
  DEFAULT_TYPOGRAPHY,
  EPUB_FONT_OPTIONS,
  fontFamilyFromStored,
  libronFontFaceCss,
  marginInsetPercent,
  markFloatedIllustrations,
  markGlyphElements,
  typographyCss,
} from './epub-typography';

/**
 * The TOC entry the displayed section belongs to, or null when the TOC cannot
 * place it. Targets are `href#fragment` (EPUB), so only the part before the
 * fragment is compared; depth-first, first match wins.
 */
export function findTocItemForHref(items: TocItem[], href: string | null): TocItem | null {
  if (!href) return null;
  const baseHref = href.split('#')[0];
  for (const item of items) {
    if (item.target.toString().split('#')[0] === baseHref) return item;
    const child = findTocItemForHref(item.children ?? [], href);
    if (child) return child;
  }
  return null;
}

/**
 * Coarse position used until epub.js's locations exist: how far the current
 * spine section is through the spine. It under-reports (a section boundary, not
 * a character offset) and exists so a large book is never left on
 * "Calculating…" with no position at all (issue #225 §1.4).
 */
export function spinePercentFrom(
  spineIndex: number | null | undefined,
  spineLength: number | null | undefined,
): number {
  if (spineIndex == null || spineLength == null || spineLength <= 0) return 0;
  return Math.floor((spineIndex / spineLength) * 100);
}

/** A chapter title short enough for the progress pill. */
function shortChapterLabel(label: string | null | undefined): string | null {
  const trimmed = label?.trim();
  if (!trimmed) return null;
  return trimmed.length > 32 ? `${trimmed.slice(0, 31)}…` : trimmed;
}

/**
 * The progress pill's text: a percentage, plus the chapter it belongs to when
 * the TOC can place us. It deliberately carries no time estimate — the previous
 * "3h 43m left" was one 1,000-character location treated as one minute, so it
 * was fiction presented with minute precision (issue #225 §1.3).
 */
export function progressLabel(percent: number, chapter: string | null): string {
  return chapter ? `${percent}% • ${chapter}` : `${percent}%`;
}

interface LocatedPassage {
  cfi: string;
  href: string;
  spineIndex: number;
  /** Start of the passage in the section's normalized text. */
  offset: number;
  /** False for a bare position, which can be opened but not drawn. */
  isRange: boolean;
  certain: boolean;
}

/** How long a busy page may hold off looking a note's words up. */
const LOCATE_IDLE_TIMEOUT_MS = 2000;

/** Resolves when the browser has a moment: the first page keeps the main thread. */
function browserIdle(): Promise<void> {
  return new Promise((resolve) => {
    if (typeof requestIdleCallback === 'function') {
      requestIdleCallback(() => resolve(), { timeout: LOCATE_IDLE_TIMEOUT_MS });
    } else {
      setTimeout(resolve, 0);
    }
  });
}

// Longer than any paragraph, in normalized characters.
const PASSAGE_POINTER_TOLERANCE = 5000;

@Component({
  selector: 'app-epub-reader',
  standalone: true,
  imports: [CommonModule, ButtonComponent, LoadingIndicatorComponent, EmptyStateComponent],
  templateUrl: './epub-reader.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './epub-reader.component.css',
})
export class EpubReader implements OnInit, OnDestroy, IReader {
  bookId = input.required<string>();
  /**
   * The Book the shell has already loaded. Passing it down is what makes the
   * saved position reliable: the reader no longer issues a second
   * `GET /api/books/{id}` whose answer arrives after the first page has been
   * laid out and persisted (issue #225 §1.2). Same contract as AudioReader.
   */
  book = input<BookDto | null>(null);
  /** A highlight or note was saved here; the shell adds it to its list. */
  noteCreated = output<Note>();
  /**
   * The book's notes, read once by the shell. The reader draws its saved
   * highlights from them rather than fetching the list again; null while the
   * shell is still reading them.
   */
  notes = input<Note[] | null>(null);
  /** The book is on screen, so highlights can be drawn on it. */
  private readonly pageShown = signal(false);
  /** The notes this opening has dealt with; a later arrival is drawn on its own. */
  private readonly notesSeen = new Set<string>();
  /** Lookups run one at a time, each from the same copy of the book's text. */
  private locating: Promise<void> = Promise.resolve();
  /** The book's highlighter pen (issue #208), owned by the shell. */
  highlightColour = input<HighlightColour>(DEFAULT_HIGHLIGHT_COLOUR);
  selectionCaptured = output<string>();
  /**
   * Where the captured selection sits in the page's viewport, emitted right
   * after `selectionCaptured` so the shell can anchor its menu at the text
   * (#650). Null when it could not be measured.
   */
  selectionAnchored = output<SelectionAnchor | null>();
  /**
   * The box of a touch selection in the page's viewport (#1344). The top-docked
   * bar can sit over the first lines of a page; the shell uses this to slide
   * the page clear of it. Null when it could not be measured.
   */
  selectionTouchRect = output<SelectionAnchor | null>();
  /** A tap on the page asked to drop the passage a touch selection offered. */
  selectionDismissed = output<void>();
  commitFailed = output<void>();
  /** An imported highlight or Ask Nostos quote was found and now has a location. */
  noteLocated = output<Note>();
  exitRequested = output<void>();
  /** Ctrl/Cmd+F pressed inside the epub.js iframe, whose events do not bubble to the shell. */
  searchRequested = output<void>();
  /** Neutral reading-surface tap/click asks the shared shell to reveal or hide chrome. */
  surfaceInteracted = output<void>();
  /** A click or tap landed on saved highlights; the shell opens the one to show. */
  highlightActivated = output<HighlightHit[]>();

  private notesService = inject(NotesService);
  private booksService = inject(BooksService);
  private epubFiles = inject(EpubFileCacheService);
  private themeService = inject(ThemeService);
  private injector = inject(Injector);
  private destroyRef = inject(DestroyRef);
  private elementRef = inject(ElementRef);
  private assistantContext = inject(AssistantContextService);

  /**
   * Reader signals published to the assistant context registry (issue #261):
   * the app-known location and the current selection. Additive only — the
   * reader's own behaviour and markup are untouched.
   */
  private assistantLocation = signal<string | null>(null);
  private assistantSelection = signal<string | null>(null);
  private unregisterAssistantContext: (() => void) | null = null;

  private epubBook: Book | null = null;
  private lazyResources: EpubLazyResources | null = null;
  private rendition: Rendition | null = null;
  private annotationManager: EpubAnnotationManager | null = null;
  private currentCfi: string | null = null;

  private bookLoadGeneration = 0;
  private destroyed = false;
  private readonly bookTeardown$ = new Subject<void>();
  private readonly destroy$ = new Subject<void>();

  private resizeSubject$ = new Subject<void>();
  private resizeObserver: ResizeObserver | null = null;
  private resizeAfterFirstPage = false;

  private readonly progressUpdater$ = new Subject<{
    generation: number;
    bookId: string;
    location: string;
    percentage: number;
  }>();
  /**
   * Whether this open has reported a position yet. A reader who has only opened
   * the book has not moved it: the layout and locations settling later must
   * refresh the progress pill, but must not write the page the reader landed on
   * (the first page of a spread, which can be before the saved CFI) over the
   * saved position (#1364).
   */
  private positionReported = false;
  /**
   * Progress writes stay closed until the saved position has been applied. The
   * rendition reports its opening section as soon as it lays out, and writing
   * that used to overwrite the reader's real position whenever the restore
   * arrived second (issue #225 §1.2). Unlock happens in unlockProgress().
   */
  private progressUnlocked = false;

  private readonly contentsInteraction = new EpubContentsInteractionController({
    pageElement: () => this.pageElement(),
    isRtl: () => this.isRtl(),
    assistantSelection: () => this.assistantSelection(),
    highlightsAt: (contents, x, y, record) =>
      (record === undefined
        ? this.annotationManager?.highlightsAt(contents, x, y)
        : this.annotationManager?.highlightsAt(contents, x, y, record)) ?? [],
    next: () => this.next(),
    previous: () => this.previous(),
    onSearchRequested: () => this.searchRequested.emit(),
    onSurfaceInteracted: () => this.surfaceInteracted.emit(),
    onHighlightActivated: (hits) => this.highlightActivated.emit(hits),
  });

  private readonly groundedNavigator = new EpubGroundedNavigator({
    rendition: () => this.rendition,
    book: () => this.epubBook,
    renderedContents: () => this.renderedContents(),
    readingViewRect: () => this.readingViewRect(),
    contentMatchesHref: (content, href) => this.contentMatchesHref(content, href),
    spineSources: () => this.epubSpineSources(),
    currentLocation: () => this.getCurrentLocation(),
    progressUnlocked: () => this.progressUnlocked,
    pendingSelectionStart: () => {
      const pending = this.annotationManager?.pendingSelection();
      return pending ? (cfiRangeEnds(pending.cfiRange)?.start ?? null) : null;
    },
    resizeEvents$: this.resizeSubject$.asObservable(),
    releasePageHold: () => this.contentsInteraction.releasePageHold(),
    clearLandingMark: () => this.annotationManager?.clearLandingMark(),
    showLandingMark: (cfi) => this.annotationManager?.showLandingMark(cfi),
    onConfirmed: (cfi) => this.updateProgressState(cfi),
  });

  private readonly searchController = new EpubSearchController({
    book: () => this.epubBook,
    renderedContents: () => this.renderedContents(),
    contentMatchesHref: (content, href) => this.contentMatchesHref(content, href),
    goToSource: (target) => this.goToSource(target),
    showSearchHighlight: (cfi) => this.annotationManager?.showSearchHighlight(cfi),
    clearSearchHighlight: () => this.annotationManager?.clearSearchHighlight(),
  });

  private readonly typographyController = new EpubTypographyController({
    bookId: () => this.bookId(),
    rendition: () => this.rendition,
    renderedContents: () => this.renderedContents(),
    pageElement: () => this.pageElement(),
    activeTheme: () => this.themeService.active(),
    releasePageHold: (restore) => this.contentsInteraction.releasePageHold(restore),
    onThemeChanged: (mode) => this.annotationManager?.setTheme(mode),
  });

  /**
   * Read aloud. The cursor logic lives in {@link EpubReadAloud}; this object is
   * the reader's side of it: what is rendered, how to turn to a place, and
   * where the mark is drawn.
   */
  readonly readAloud = new EpubReadAloud({
    language: () => this.bookLanguage(),
    renderedContents: () => this.renderedContents() as unknown as ReadAloudEpubContent[],
    readingViewRect: () => this.readingViewRect(),
    spineLength: () => this.spineLength(),
    displaySection: (index) => this.readAloudDisplaySection(index),
    displayCfi: async (cfi) => {
      await this.readAloudDisplay(cfi);
    },
    isRangeVisible: (range, content) =>
      this.groundedNavigator.isRangeVisible(range, content as unknown as Contents),
    showMark: (cfi) => this.annotationManager?.showReadAloudMark(cfi),
    clearMark: () => this.annotationManager?.clearReadAloudMark(),
  });
  /** Counts page turns read-aloud asked for, so `relocated` can tell them from the person's. */
  private readAloudMoves = 0;
  private readonly readAloudMoveTimers = new Set<ReturnType<typeof setTimeout>>();

  // --- In-book search (#761) ---
  readonly searchState = this.searchController.state;
  readonly searchAvailable = this.searchController.available;

  /** Spine index of the displayed section — the coarse fallback position. */
  private spineIndex = signal<number | null>(null);

  /**
   * Chapter bar state (#1269): the book's chapters as spine ranges, their
   * location indexes once locations exist, and what the last `relocated`
   * reported about the page's end and its place in the section.
   */
  private chapterRanges: EpubChapterRange[] = [];
  private chapterBounds: EpubChapterLocations[] | null = null;
  private pageEndCfi: string | null = null;
  private sectionPage: { page: number | null; total: number | null } = { page: null, total: null };

  // Track if locations are fully generated
  private locationsReady = signal(false);

  // --- IReader Interface Implementation ---
  toc = signal<TocItem[]>([]);
  progress = signal<ReaderProgress>({ label: '', percentage: 0 });
  currentHref = signal<string | null>(null);
  /** The TOC entry the displayed section belongs to, or null. */
  private activeTocItem = computed<TocItem | null>(() =>
    findTocItemForHref(this.toc(), this.currentHref()),
  );
  currentLocationTarget = computed(() => this.activeTocItem()?.target ?? null);

  /** Reader typography (typeface, line height, margins), persisted reader-wide. */
  readonly typography = this.typographyController.typography;
  /** Outer margin for the current preset, as a percentage of the capped spread. */
  readonly marginInset = this.typographyController.marginInset;
  /** Text size in percent — surfaced so the shell's typography panel can show the
   *  current step beside the A−/A+ controls (the toolbar used to own them). */
  readonly fontSizePercent = this.typographyController.fontSizePercent;

  readonly fontOptions = EPUB_FONT_OPTIONS;
  readonly lineOptions = EPUB_LINE_OPTIONS;
  readonly marginOptions = EPUB_MARGIN_OPTIONS;

  loading = signal(true);
  errorMessage = signal<string | null>(null);
  readonly sourceNavigationMessage = this.groundedNavigator.message;

  constructor() {
    this.unregisterAssistantContext = this.assistantContext.register(
      () => ({
        readerType: 'epub',
        epubCfi: this.assistantLocation(),
        selectedText: this.assistantSelection(),
      }),
      { explicit: true },
    );

    effect(() => {
      if (this.bookId()) {
        // untracked keeps loadBook's own signal reads out of this effect's
        // dependencies, so only a new book id triggers a full reload.
        untracked(() => this.loadBook(this.bookId()));
      }
    });

    // Draw the saved highlights once, when the book is showing and the
    // shell's notes are in, whichever comes second. A note that joins the list
    // later, such as a quote Ask Nostos just saved, is drawn on its own.
    effect(() => {
      const notes = this.notes();
      if (!notes || !this.pageShown()) return;
      untracked(() => {
        const arrived = notes.filter((note) => !this.notesSeen.has(note.id));
        if (arrived.length === 0) return;
        arrived.forEach((note) => this.notesSeen.add(note.id));
        this.annotationManager?.restoreHighlights(arrived);
        this.locating = this.locating.then(() => this.locateNotes(arrived));
      });
    });

    effect(() => {
      const colour = this.highlightColour();
      if (this.annotationManager) {
        this.annotationManager.setHighlightColour(colour);
      }
    });

    // The rendition is a separate document: re-select the matching Nostos
    // theme whenever the app theme changes, so the page follows it without a
    // reload.
    effect(() => {
      const theme = this.themeService.active();
      if (this.rendition) {
        this.typographyController.selectReaderTheme(theme);
      }
    });
  }

  ngOnInit() {
    // Handle Window Resizing
    this.resizeSubject$
      .pipe(debounceTime(350), takeUntil(this.destroy$), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        // A re-layout clears the views, and clearing one that is still loading
        // leaves its display pending for ever: a book stuck on "Opening book...".
        // The size at this point is only ever the opening layout settling.
        if (this.rendition && !this.pageShown()) {
          this.resizeAfterFirstPage = true;
          return;
        }
        if (this.rendition) {
          const page = this.pageElement();
          if (page) {
            const { clientWidth, clientHeight } = page;
            try {
              // A re-layout invalidates the held offset: let epub.js place the
              // section itself rather than restoring a position from the old
              // geometry (#1254).
              this.contentsInteraction.releasePageHold(false);
              this.rendition.resize(clientWidth, clientHeight);
            } catch (e) {
              console.warn('Rendition resize failed (book might not be ready):', e);
              return;
            }
            // The re-layout re-displays epub.js's last seen location; bring the
            // passage being marked back instead (#1270).
            this.groundedNavigator.reanchorOnResize();
          }
        }
      });

    this.resizeObserver = new ResizeObserver(() => {
      this.resizeSubject$.next();
    });
    this.resizeObserver.observe(this.elementRef.nativeElement);

    // Backend Progress Sync (Debounced). The filter is the write barrier: see
    // progressUnlocked / unlockProgress().
    this.progressUpdater$
      .pipe(
        filter(() => this.progressUnlocked),
        debounceTime(1000),
        distinctUntilChanged(
          (prev, curr) =>
            prev.generation === curr.generation &&
            prev.bookId === curr.bookId &&
            prev.location === curr.location &&
            prev.percentage === curr.percentage,
        ),
        takeUntil(this.destroy$),
        switchMap((data) => {
          if (
            this.destroyed ||
            !this.progressUnlocked ||
            data.generation !== this.bookLoadGeneration ||
            data.bookId !== this.bookId()
          ) {
            return EMPTY;
          }
          return this.booksService.updateProgress(data.bookId, data.location, data.percentage).pipe(
            takeUntil(this.bookTeardown$),
            catchError((err) => {
              console.warn('Could not save reading progress:', err);
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe();
  }

  // --- IReader Methods ---

  async search(query: string): Promise<void> {
    await this.searchController.search(query);
  }

  async nextSearchResult(): Promise<void> {
    await this.searchController.next();
  }

  async previousSearchResult(): Promise<void> {
    await this.searchController.previous();
  }

  clearSearch(): void {
    this.searchController.clear();
  }

  /**
   * Open where an imported highlight sits in this book. It carries its text
   * and another reading app's position, but no location this reader wrote.
   */
  async goToPassage(location: ImportedNoteLocation): Promise<void> {
    // A passage jump replaces whatever search was showing, exactly as a new
    // query would.
    this.clearSearch();
    const generation = this.searchController.currentGeneration();
    this.groundedNavigator.clearMessage();

    let located: LocatedPassage | null = null;
    try {
      located = await this.locatePassage(location);
    } catch (error) {
      console.warn('Could not look up the imported passage:', error);
    }
    if (this.destroyed || generation !== this.searchController.currentGeneration()) return;

    if (!located) {
      this.groundedNavigator.failSourceNavigation();
      return;
    }

    // Open the section first and resolve the position in the rendered page:
    // displaying a CFI of a section that is not laid out yet can land pages off.
    await this.goToSource({
      type: 'epub',
      epubResourceHref: located.href,
      epubSpineIndex: located.spineIndex,
      epubTextOffset: located.offset,
    });
    if (this.destroyed || generation !== this.searchController.currentGeneration() || !located.isRange) {
      return;
    }
    try {
      this.annotationManager?.showSearchHighlight(located.cfi);
    } catch (error) {
      console.warn('Could not mark the imported passage:', error);
    }
  }

  /**
   * Find an imported highlight in the book's own text. `certain` is true only
   * when there is one place it can be: the passage occurs once, or its
   * KOReader position picks one occurrence. Only a certain range is ever
   * saved as the note's location; anything less is good for a jump at most.
   */
  private async locatePassage(location: ImportedNoteLocation): Promise<LocatedPassage | null> {
    const { text, spineIndex, xpointer } = location;
    const corpus = text ? await this.searchController.ensureCorpus() : [];
    const { whole, lead } = text
      ? epubPassageCandidates(corpus, text, spineIndex)
      : { whole: [], lead: [] };

    let pick = whole[0] ?? (lead.length === 1 ? lead[0] : null);
    let certain = whole.length === 1;
    if (whole.length > 1 && xpointer && spineIndex !== null) {
      const near = await this.withSectionDocument(spineIndex, (document) => {
        const node = nodeAtKoreaderXPointer(document, xpointer);
        return node ? normalizedResourceOffsetOfNode(document, node) : null;
      });
      const here = whole.filter((match) => corpus[match.section].index === spineIndex);
      if (near !== null && here.length > 0) {
        pick = here.reduce((best, match) =>
          Math.abs(match.offset - near) < Math.abs(best.offset - near) ? match : best,
        );
        // The pointer names the block the highlight starts in, so the right
        // occurrence starts within that block.
        certain = Math.abs(pick.offset - near) <= PASSAGE_POINTER_TOLERANCE;
      }
    }

    if (pick) {
      const match = pick;
      const { href, index } = corpus[match.section];
      const cfi = await this.withSectionDocument(index, (document, section) => {
        const range = rangeForNormalizedResourceSpan(document, match.offset, match.length);
        return range ? section.cfiFromRange(range) : null;
      });
      if (cfi) {
        return { cfi, href, spineIndex: index, offset: match.offset, isRange: true, certain };
      }
    }

    // A page bookmark has no text of its own, and a highlight's text may not
    // be found; the pointer still names a paragraph to open.
    if (xpointer && spineIndex !== null) {
      const href = this.epubSpineSources().find((item) => item.index === spineIndex)?.href;
      const point = await this.withSectionDocument(spineIndex, (document, section) => {
        const node = nodeAtKoreaderXPointer(document, xpointer);
        const offset = node ? normalizedResourceOffsetOfNode(document, node) : null;
        if (!node || offset === null) return null;
        const range = document.createRange();
        range.setStartBefore(node);
        range.collapse(true);
        return { cfi: section.cfiFromRange(range), offset };
      });
      if (point && href) {
        return { ...point, href, spineIndex, isRange: false, certain: false };
      }
    }

    return null;
  }

  /** Reads one spine item's document without rendering it. */
  private async withSectionDocument<T>(
    spineIndex: number,
    read: (document: Document, section: { cfiFromRange(range: Range): string }) => T,
  ): Promise<T | null> {
    const book = this.epubBook;
    const spine = book?.spine as unknown as
      | {
          spineItems?: Array<{
            index?: unknown;
            load?: (request: (path: string) => Promise<unknown>) => Promise<Element>;
            unload?: () => void;
            cfiFromRange?: (range: Range) => string;
          }>;
        }
      | undefined;
    const section = spine?.spineItems?.find((item) => item.index === spineIndex);
    if (!book || !section || typeof section.load !== 'function') return null;
    if (typeof section.cfiFromRange !== 'function') return null;

    try {
      const contents = await section.load(book.load.bind(book) as (path: string) => Promise<unknown>);
      const document = contents?.ownerDocument;
      if (!document || this.epubBook !== book) return null;
      return read(document, { cfiFromRange: (range) => section.cfiFromRange!(range) });
    } finally {
      section.unload?.();
    }
  }

  /**
   * Give imported highlights and Ask Nostos quotes a location of their own.
   * Each one found with certainty is saved once, after which it is drawn and
   * opened like a highlight made here. Runs after the book is showing, waits
   * for an idle moment, and never blocks it.
   */
  private async locateNotes(notes: Note[]): Promise<void> {
    const book = this.epubBook;
    const generation = this.bookLoadGeneration;
    const notFound = readUnlocatedNoteIds();
    const lookups = notes.flatMap((note) => {
      if (note.cfiRange?.trim()) return [];
      if (!isImportedAnchor(note.sourceAnchorKind) && !isAskNostosQuote(note)) return [];
      const location = importedNoteLocation(note);
      // Looking a passage up reads the whole book. A note that was not found
      // once (a page bookmark, a highlight from another edition) is not
      // searched for on every opening; its own jump still looks.
      return location?.text && !notFound.has(note.id) ? [{ note, location }] : [];
    });
    if (lookups.length === 0) return;

    await browserIdle();
    if (this.destroyed || this.epubBook !== book || generation !== this.bookLoadGeneration) return;
    for (const { note, location } of lookups) {
      if (this.destroyed || this.epubBook !== book || generation !== this.bookLoadGeneration) return;
      try {
        const located = await this.locatePassage(location);
        if (this.destroyed || this.epubBook !== book || generation !== this.bookLoadGeneration) return;
        if (!located?.certain || !located.isRange) {
          rememberUnlocatedNoteId(note.id);
          continue;
        }

        const saved = await firstValueFrom(
          this.notesService
            .setLocation(note.id, located.cfi)
            .pipe(takeUntil(this.bookTeardown$), takeUntilDestroyed(this.destroyRef)),
        );
        if (this.destroyed || this.epubBook !== book || generation !== this.bookLoadGeneration) return;
        // Deleted while it was being looked up: nothing to draw.
        if (!saved.cfiRange || !this.notes()?.some((held) => held.id === note.id)) continue;
        this.annotationManager?.restoreHighlights([saved]);
        this.noteLocated.emit(saved);
      } catch (error) {
        if (this.destroyed || this.epubBook !== book || generation !== this.bookLoadGeneration) return;
        // The server refused this note for good; asking again would repeat the
        // whole-book read at every opening.
        if (error instanceof HttpErrorResponse && error.status === 400) {
          rememberUnlocatedNoteId(note.id);
        }
        console.warn('Could not locate a note in the book:', error);
      }
    }
  }

  /** Reading direction of the open book (#1388): drives spreads, keys and tap zones. */
  readonly readingDirection = signal<EpubDirection>('ltr');

  /** Left/right page controls run backwards in a right-to-left book. */
  isRtl(): boolean {
    return this.readingDirection() === 'rtl';
  }

  next() {
    this.groundedNavigator.beginNavigation();
    this.groundedNavigator.clearMessage();
    this.rendition?.next();
  }

  previous() {
    this.groundedNavigator.beginNavigation();
    this.groundedNavigator.clearMessage();
    this.rendition?.prev();
  }

  goTo(target: string | number) {
    this.groundedNavigator.beginNavigation();
    this.groundedNavigator.clearMessage();
    this.rendition?.display(target.toString());
  }

  async goToSource(target: ReaderSourceTarget): Promise<void> {
    await this.groundedNavigator.goToSource(target);
  }

  private epubSpineSources(): EpubSpineSource[] {
    const spine = this.epubBook?.spine as unknown as
      | { spineItems?: Array<{ href?: unknown; index?: unknown }> }
      | undefined;
    return (spine?.spineItems ?? [])
      .map((item, fallbackIndex) => ({
        href: typeof item.href === 'string' ? item.href : '',
        index: typeof item.index === 'number' ? item.index : fallbackIndex,
      }))
      .filter((item) => item.href.length > 0);
  }

  /** The visible reading box, or null when it has not been laid out yet. */
  private readingViewRect(): DOMRect | null {
    const element = this.pageElement() ?? (this.elementRef.nativeElement as HTMLElement);
    if (typeof element?.getBoundingClientRect !== 'function') return null;

    const rect = element.getBoundingClientRect();
    if (rect.width <= 0 && rect.height <= 0) return null;
    return rect;
  }

  private pageElement(): HTMLElement | null {
    return this.elementRef.nativeElement.querySelector('#epub-page') as HTMLElement | null;
  }

  /**
   * The language the EPUB declares for itself, e.g. "en" or "it-IT". A file
   * that declares none falls back to the language recorded for the book in the
   * library.
   */
  private bookLanguage(): string | null {
    const metadata = (this.epubBook as any)?.packaging?.metadata;
    // The package's dc:language is often a name ("english") or a three-letter
    // code, which the voices would not match as written.
    const declared = typeof metadata?.language === 'string' ? metadata.language : null;
    return languageTagFromCatalogue(declared) ?? languageTagFromCatalogue(this.book()?.language);
  }

  /**
   * A page turn made for read-aloud. While one is in flight, and briefly after
   * it resolves (epub.js reports the new location a frame later), `relocated`
   * is not treated as the person moving the book.
   */
  private async readAloudDisplay(target: string): Promise<boolean> {
    const rendition = this.rendition;
    if (!rendition || this.destroyed) return false;

    // Read aloud moves the page itself; a selection hold must not fight it.
    this.contentsInteraction.releasePageHold();
    this.readAloudMoves++;
    try {
      await rendition.display(target);
      return true;
    } catch (error) {
      console.warn('Read aloud could not turn the page:', error);
      return false;
    } finally {
      const timer = setTimeout(() => {
        this.readAloudMoveTimers.delete(timer);
        if (this.destroyed || this.rendition !== rendition) return;
        this.readAloudMoves = Math.max(0, this.readAloudMoves - 1);
      }, 300);
      this.readAloudMoveTimers.add(timer);
    }
  }

  private clearReadAloudMoveTimers(): void {
    for (const timer of this.readAloudMoveTimers) clearTimeout(timer);
    this.readAloudMoveTimers.clear();
    this.readAloudMoves = 0;
  }

  private async readAloudDisplaySection(index: number): Promise<boolean> {
    const section = (this.epubBook?.spine as any)?.get?.(index);
    const href = typeof section?.href === 'string' ? section.href : null;
    if (!href) return false;
    return this.readAloudDisplay(href);
  }

  private renderedContents(): Contents[] {
    try {
      const raw = this.rendition?.getContents?.();
      if (Array.isArray(raw)) return raw as Contents[];
      return raw ? [raw as Contents] : [];
    } catch {
      return [];
    }
  }

  /**
   * The first text actually shown on the current page, for finding the same
   * place in another edition. Reads displayed text instead of assuming the
   * start of the spine section is the start of the visible page. Null while
   * loading, on an error, or on a page with no text (a cover, an image).
   */
  visibleExcerpt(): string | null {
    if (!this.rendition || this.loading() || this.errorMessage()) {
      return null;
    }

    return visibleEpubExcerpt(
      this.renderedContents(),
      this.readingViewRect(),
    );
  }

  private contentMatchesHref(content: Contents, href: string | null): boolean {
    if (!href) return true;
    const candidate = String(
      (content as any)?.section?.href ?? (content as any)?.document?.location?.pathname ?? '',
    );
    return normalizeEpubHrefForComparison(candidate) === normalizeEpubHrefForComparison(href);
  }

  getCurrentLocation(): string | null {
    if (!this.rendition) return null;
    try {
      const location = this.rendition.currentLocation() as any;
      if (location && location.start) {
        return location.start.cfi;
      }
    } catch {
      return null;
    }
    return null;
  }

  zoomIn() {
    this.typographyController.zoomIn();
  }

  zoomOut() {
    this.typographyController.zoomOut();
  }

  /** Merge a patch into the typography, persist it, and repaint open sections. */
  setTypography(patch: Partial<EpubTypography>): void {
    this.typographyController.setTypography(patch);
  }

  resetTypography(): void {
    this.typographyController.resetTypography();
  }

  // --- Book Loading & Setup ---

  loadBook(id: string, retryDownload = true) {
    const generation = ++this.bookLoadGeneration;
    this.bookTeardown$.next();
    this.clearReadAloudMoveTimers();
    this.groundedNavigator.resetForBookLoad();
    this.searchController.reset();
    // A fresh rendition must be told the current size and typography even when
    // neither value changed — otherwise the applied-value guards would skip
    // the apply and the new book would open at the publisher's defaults.
    this.typographyController.resetForBookLoad();

    if (this.epubBook) {
      this.readAloud.end();
      this.clearContentsKeyboardListeners();
      this.clearContentsInteractionListeners();
      this.contentsInteraction.releasePageHold(false);
      this.contentsInteraction.resetScroller();
      this.annotationManager?.destroy();
      this.annotationManager = null;
      this.lazyResources?.destroy();
      this.lazyResources = null;
      this.epubBook.destroy();
      this.epubBook = null;
      this.rendition = null;
      this.currentCfi = null;
    }
    this.assistantLocation.set(null);
    this.assistantSelection.set(null);
    this.pageShown.set(false);
    this.resizeAfterFirstPage = false;
    this.notesSeen.clear();
    this.locating = Promise.resolve();
    this.progressUnlocked = false;
    this.positionReported = false;
    this.chapterRanges = [];
    this.chapterBounds = null;
    this.pageEndCfi = null;
    this.sectionPage = { page: null, total: null };
    this.errorMessage.set(null);

    this.loading.set(true);
    this.locationsReady.set(false);

    // No URL here: the file comes through the cache and is handed over in step 6.
    // The rendition below can wait for it, as everything in epub.js waits on
    // `book.opened`.
    const epubBook = ePub({});
    this.epubBook = epubBook;
    // Before the rendition starts: it reads the direction from the package
    // metadata when the book opens, and many right-to-left books never say it.
    this.readingDirection.set('ltr');
    void epubBook.loaded.metadata.then((metadata: any) => {
      if (this.destroyed || this.epubBook !== epubBook || generation !== this.bookLoadGeneration) return;
      const direction = epubReadingDirection({
        pageProgressionDirection: metadata?.direction,
        language: metadata?.language,
      });
      if (metadata) metadata.direction = direction;
      this.readingDirection.set(direction);
    });
    this.lazyResources = new EpubLazyResources(epubBook);

    // 2. Setup Rendition Immediately
    // Render into the padded page box: the margin preset is padding on
    // #epub-viewer, so epub.js should paginate into what is left of it.
    const page = this.pageElement();
    const width = page ? page.clientWidth : '100%';
    const height = page ? page.clientHeight : '100%';

    const rendition = epubBook.renderTo('epub-page', {
      width: width,
      height: height,
      flow: 'paginated',
      manager: 'default',
    });
    this.rendition = rendition;

    // Register both Nostos normalizations at rendition creation and select
    // the one matching the app theme BEFORE display, so the first section
    // is painted with the right palette (no white flash in dark mode, no
    // dark flash in light mode). epub.js injects the selected theme into
    // every contents it creates afterwards, so later chapters inherit it.
    this.typographyController.initializeRendition();

    // 3. Register Hooks
    rendition.hooks.content.register((contents: Contents) => {
      applyDocumentDirection(contents.document, this.readingDirection());
      this.typographyController.injectCustomStyles(contents);
      this.annotationManager?.registerContents(contents);
      this.registerContentsKeyboard(contents);
      this.registerContentsInteraction(contents);
    });

    // Initialize the annotation manager BEFORE the first display so the
    // opening section receives the injected styles and fallback listeners.
    this.annotationManager = new EpubAnnotationManager(
      rendition,
      id,
      this.injector,
      (note) => this.noteCreated.emit(note),
      () => this.commitFailed.emit(),
    );
    this.annotationManager.setHighlightColour(this.highlightColour());
    this.annotationManager.setTheme(this.themeService.mode());
    this.annotationManager.setOnSelectionCaptured((text, anchor, touchRect) => {
      // A touch capture (no anchored menu) is a live native selection whose
      // handles are about to move; hold the page under it (#1254). A mouse
      // capture has already finished its drag and opens an anchored menu.
      if (anchor === null) this.contentsInteraction.holdPageForSelection();
      this.groundedNavigator.holdSelectionAnchor();
      this.assistantSelection.set(text);
      this.selectionCaptured.emit(text);
      this.selectionAnchored.emit(anchor);
      if (anchor === null) this.selectionTouchRect.emit(touchRect ?? null);
    });
    this.annotationManager.setOnSelectionAbandoned(() => {
      this.contentsInteraction.releasePageHold();
      this.groundedNavigator.releaseSelectionAnchor();
      this.selectionDismissed.emit();
    });
    this.annotationManager.init();

    rendition.on('relocated', (location: any) => {
      if (this.destroyed || this.rendition !== rendition || generation !== this.bookLoadGeneration) return;
      this.currentCfi = location.start.cfi;
      if (this.readAloudMoves === 0) this.readAloud.notifyUserMoved();
      this.assistantLocation.set(location.start.cfi);
      this.currentHref.set(location.start.href);
      this.spineIndex.set(typeof location.start.index === 'number' ? location.start.index : null);
      this.pageEndCfi = typeof location.end?.cfi === 'string' ? location.end.cfi : null;
      this.sectionPage = {
        page: location.start.displayed?.page ?? null,
        total: location.start.displayed?.total ?? null,
      };
      this.updateProgressState(location.start.cfi);
    });

    // Open on the saved page, not on the cover and then a jump to it. A position
    // that no longer resolves (a replaced file) still has to open the book.
    // restoreSavedPosition below stays the one that confirms the page.
    const restoreLocation = this.book()?.lastLocation ?? null;
    const opening = this.groundedNavigator.hasPendingSource()
      ? undefined
      : (restoreLocation ?? undefined);
    const firstPage = rendition
      .display(opening)
      .catch((err) => (opening ? rendition.display() : Promise.reject(err)));

    // 4. Process Book Metadata (Async)
    epubBook.ready
      .then(() => {
        if (this.destroyed || this.epubBook !== epubBook || generation !== this.bookLoadGeneration) return;
        if (epubBook.navigation) {
          const toc = this.mapTocItems(epubBook.navigation.toc);
          this.toc.set(toc);
          this.chapterRanges = epubChapterRanges(
            toc,
            this.epubSpineSources()
              .sort((a, b) => a.index - b.index)
              .map((item) => item.href),
          );
          this.chapterBounds = null;
          if (this.currentCfi) this.updateProgressState(this.currentCfi, false);
        }

        // --- OPTIMIZATION START ---
        // Try to fetch locations from backend first
        const generateLocations = () => {
          if (this.destroyed || this.epubBook !== epubBook || generation !== this.bookLoadGeneration) return;
          // Cache MISS: Generate locations (Expensive). The progress pill
          // shows a spine-based percentage while this runs, so a large book
          // is never left on "Calculating…" with no position at all
          // (issue #225 §1.4). It waits for the first page: it parses every
          // chapter on this thread, and the page is what the reader came for.
          void firstPage.then(() => {
            if (this.destroyed || this.epubBook !== epubBook || generation !== this.bookLoadGeneration) return;
            void epubBook.locations.generate(1000).then(() => {
              if (this.destroyed || this.epubBook !== epubBook || generation !== this.bookLoadGeneration) return;
              this.chapterBounds = null;
              this.locationsReady.set(true);
              if (this.currentCfi) this.updateProgressState(this.currentCfi, this.positionReported);

              // Save them for next time
              const json = epubBook.locations.save();
              if (json) {
                this.booksService
                  .saveLocations(id, json)
                  .pipe(takeUntil(this.bookTeardown$), takeUntilDestroyed(this.destroyRef))
                  .subscribe();
              }
            });
          });
        };
        this.booksService
          .getLocations(id)
          .pipe(takeUntil(this.bookTeardown$), takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: (dto) => {
              if (this.destroyed || this.epubBook !== epubBook || generation !== this.bookLoadGeneration) return;
              // Cache HIT: Load saved locations
              if (dto.locations) {
                epubBook.locations.load(dto.locations);
                this.chapterBounds = null;
                this.locationsReady.set(true);
                if (this.currentCfi) this.updateProgressState(this.currentCfi, this.positionReported);
              } else {
                generateLocations();
              }
            },
            error: generateLocations,
          });
        // --- OPTIMIZATION END ---
      })
      .catch((err) => {
        if (this.destroyed || this.epubBook !== epubBook || generation !== this.bookLoadGeneration) return;
        console.error('Book metadata setup failed:', err);
        this.failOpen();
      });

    // 5. Display Book. The saved position comes from the Book the shell already
    // holds — no second GET, whose late answer used to lose the race against the
    // first progress write.
    let reportOnUnlock = true;

    firstPage
      .then(async () => {
        if (this.destroyed || rendition !== this.rendition || generation !== this.bookLoadGeneration) return;
        this.loading.set(false);
        this.typographyController.applyFontSize();

        // The saved highlights are drawn from the shell's list as soon as it
        // is in (see the effect in the constructor); no fetch of our own.
        this.pageShown.set(true);
        if (this.resizeAfterFirstPage) {
          this.resizeAfterFirstPage = false;
          this.resizeSubject$.next();
        }

        // A grounded source is stronger than the user's ordinary saved reading
        // position for this navigation: the citation click explicitly asked to
        // open evidence from the exact indexed source revision.
        const grounded = this.groundedNavigator.consumePendingSource();
        if (grounded) {
          await this.groundedNavigator.applySource(grounded);
          return;
        }

        if (!restoreLocation) return;

        // Progress stays closed until the restored page has survived the
        // opening re-layouts. Only a move by the reader reports a new place;
        // a confirmed or failed restore leaves the saved position as it was.
        const outcome = await this.groundedNavigator.restoreSavedPosition(rendition, restoreLocation);
        if (this.destroyed || rendition !== this.rendition || generation !== this.bookLoadGeneration) return;
        reportOnUnlock = outcome === 'superseded';
      })
      .catch((err) => {
        if (this.destroyed || rendition !== this.rendition || generation !== this.bookLoadGeneration) return;
        console.error('Failed to render book:', err);
        this.failOpen();
      })
      .finally(() => {
        if (
          this.destroyed ||
          this.errorMessage() ||
          rendition !== this.rendition ||
          generation !== this.bookLoadGeneration
        ) {
          return;
        }
        this.unlockProgress(reportOnUnlock);

        // The opening display consumes a queued source once, before the saved
        // position is restored. A source that arrives during that restore was
        // queued after the one point that would have applied it, and the
        // reader stayed where it was left: a landing from another edition
        // opened at the old place about every other time on a cached book.
        const late = this.groundedNavigator.consumePendingSource();
        if (late && rendition === this.rendition) {
          void this.groundedNavigator.applySource(late);
        }
      });

    // 6. Hand the file over.
    void this.openFile(id, epubBook, retryDownload, generation);
  }

  /**
   * A failed open has to be caught here. epub.js never settles `book.ready` or
   * `opened` after one, and `rendition.display()` stays pending with them, so the
   * catch handlers in `loadBook` never run and a corrupt or unavailable EPUB would
   * sit on "Opening book..." for ever.
   */
  private async openFile(
    id: string,
    epubBook: Book,
    retryDownload: boolean,
    generation: number,
  ): Promise<void> {
    let fromCache = false;
    try {
      const file = await this.epubFiles.load(id);
      fromCache = file.fromCache;
      if (this.destroyed || this.epubBook !== epubBook || generation !== this.bookLoadGeneration) {
        return; // a newer attempt owns the reader
      }
      this.fitRenditionToPage();
      await epubBook.open(file.bytes, 'binary');
    } catch {
      if (this.destroyed || this.epubBook !== epubBook || generation !== this.bookLoadGeneration) {
        return;
      }
      if (fromCache && retryDownload) {
        // A bad copy must not take the book away: drop it and fetch the real one.
        await this.epubFiles.forget(id);
        if (!this.destroyed && this.epubBook === epubBook && generation === this.bookLoadGeneration) {
          this.loadBook(id, false);
        }
        return;
      }
      this.failOpen();
    }
  }

  /**
   * The rendition was sized before the margin preset's padding reached the page,
   * and nothing has been laid out yet, so correcting it now is free. Left alone,
   * the first page is laid out at the wrong width and again at the right one.
   */
  private fitRenditionToPage(): void {
    const page = this.pageElement();
    const settings = this.rendition?.settings;
    if (!page?.clientWidth || !settings) return;
    settings.width = page.clientWidth;
    settings.height = page.clientHeight;
  }

  // --- Helpers ---

  private updateProgressState(cfi: string, report = true) {
    if (!this.epubBook) return;

    // Locations give the precise percentage. Until they exist — generation can
    // take a while on a large book — fall back to how far the current spine
    // section is through the spine: coarse, but real, and never a stuck
    // "Calculating…" with no position at all (issue #225 §1.3, §1.4).
    const percent =
      this.locationsReady() && this.epubBook.locations.length() > 0
        ? Math.floor(this.epubBook.locations.percentageFromCfi(cfi) * 100)
        : spinePercentFrom(this.spineIndex(), this.spineLength());

    const chapter = this.chapterProgress(cfi);
    this.progress.set({
      label: progressLabel(percent, this.activeChapterLabel()),
      percentage: percent,
      chapterPercentage: chapter?.percentage ?? null,
      chapterLabel: chapter?.label ?? null,
    });
    if (!report) return;

    // While a grounded navigation is still settling, relocated events describe
    // epub.js's reflow churn rather than where the reader has been grounded.
    if (this.groundedNavigator.isProtectingProgress()) return;

    if (this.progressUnlocked) this.positionReported = true;
    this.progressUpdater$.next({
      generation: this.bookLoadGeneration,
      bookId: this.bookId(),
      location: cfi,
      percentage: percent,
    });
  }

  /**
   * Opens the progress stream and, unless `report` is false, reports where the
   * reader actually is. Called once the opening display (and the restore, when
   * there is one) has settled, so the opening section can never overwrite the
   * saved position (issue #225 §1.2). A restore that was confirmed or failed
   * passes false: the saved position is already the right one (#1271).
   */
  private unlockProgress(report = true): void {
    this.progressUnlocked = true;
    const cfi = this.getCurrentLocation() ?? this.currentCfi;
    if (cfi && report) this.updateProgressState(cfi);
    // Search navigation reuses goToSource(), which deliberately queues requests
    // while progress is locked. Do not expose search until the opening display
    // and saved-position restore are finished, or a query can become a stranded
    // pending grounded target after the one startup consumption point.
    this.searchController.setAvailable(true);
  }

  /**
   * How far through its chapter the page is (#1269). Precise from locations
   * once they exist, else from the section's own pagination. Null when the TOC
   * places no chapter at all.
   */
  private chapterProgress(cfi: string): { percentage: number; label: string | null } | null {
    const spine = this.spineIndex() ?? spineIndexFromCfi(cfi);
    const range = chapterRangeForSpine(this.chapterRanges, spine);
    if (!range || spine === null) return null;

    let fraction: number;
    const locations = this.epubBook?.locations as any;
    if (this.locationsReady() && locations?.length?.() > 0) {
      this.chapterBounds ??= chapterLocationBounds(this.chapterRanges, locations._locations ?? []);
      const bounds = this.chapterBounds[this.chapterRanges.indexOf(range)];
      const end = this.pageEndCfi ? locations.locationFromCfi(this.pageEndCfi) : null;
      fraction = chapterFractionFromLocations(
        bounds,
        locations.locationFromCfi(cfi),
        typeof end === 'number' && end >= 0 ? end : null,
      );
    } else {
      fraction = chapterFractionFromPages(
        range,
        spine,
        this.sectionPage.page,
        this.sectionPage.total,
      );
    }

    const label = this.activeChapterLabel() ?? shortChapterLabel(range.label);
    return { percentage: Math.floor(fraction * 100), label };
  }

  /** Label of the TOC entry the displayed section belongs to, for the pill. */
  private activeChapterLabel(): string | null {
    return shortChapterLabel(this.activeTocItem()?.label);
  }

  /**
   * Number of sections in the spine. The bundled 0.3.93 typings for `Spine` do
   * not declare `spineItems`, which is what the implementation actually holds.
   */
  private spineLength(): number | null {
    const spine = this.epubBook?.spine as unknown as { spineItems?: unknown[] } | undefined;
    return spine?.spineItems?.length ?? null;
  }

  readonly keyboardDocuments = this.contentsInteraction.keyboardDocuments;
  readonly interactionDocuments = this.contentsInteraction.interactionDocuments;

  private registerContentsKeyboard(contents: Contents): void {
    this.contentsInteraction.registerKeyboard(contents);
  }

  private clearContentsKeyboardListeners(): void {
    this.contentsInteraction.clearKeyboardListeners();
  }

  /** The page's quiet outer margins are part of the reading surface too.
   * Iframe events stay format-owned and do not bubble into this document. */
  onViewerMarginClick(event: MouseEvent): void {
    if (event.target !== event.currentTarget || this.loading() || this.errorMessage()) return;
    const selected = this.renderedContents().some((contents) =>
      contents.document.getSelection()?.toString().trim(),
    );
    if (!selected && !this.assistantSelection()) this.surfaceInteracted.emit();
  }

  private registerContentsInteraction(contents: Contents): void {
    this.contentsInteraction.registerInteraction(contents);
  }

  private clearContentsInteractionListeners(): void {
    this.contentsInteraction.clearInteractionListeners();
  }

  private mapTocItems(items: any[]): TocItem[] {
    return items.map((item) => ({
      label: item.label.trim(),
      target: item.href,
      children: item.subitems ? this.mapTocItems(item.subitems) : [],
    }));
  }

  retryLoad(): void {
    this.loadBook(this.bookId());
  }

  leaveReader(): void {
    this.exitRequested.emit();
  }

  private failOpen(): void {
    this.searchController.setAvailable(false);
    this.errorMessage.set('The file may be damaged, unsupported, or temporarily unavailable.');
    this.loading.set(false);
  }

  public deleteHighlight(cfiRange: string) {
    this.annotationManager?.removeHighlight(cfiRange);
  }

  removeHighlight(identifier: string): void {
    this.deleteHighlight(identifier);
  }

  recolourHighlight(note: Note, colour: HighlightColour): void {
    if (note.cfiRange) {
      this.annotationManager?.recolourHighlight(note.cfiRange, colour, noteHasText(note));
    }
  }

  commitHighlight(content = '', colour?: HighlightColour, topics: readonly string[] = []): void {
    const committed = this.annotationManager?.commitHighlight(content, colour, topics);
    void committed?.then((success) => {
      if (success && !this.destroyed) {
        this.assistantSelection.set(null);
        this.contentsInteraction.releasePageHold();
        this.groundedNavigator.releaseSelectionAnchor();
      }
    });
  }

  discardHighlight(): void {
    this.annotationManager?.discardHighlight();
    this.assistantSelection.set(null);
    this.contentsInteraction.releasePageHold();
    this.groundedNavigator.releaseSelectionAnchor();
  }

  /**
   * The selection on offer as one end of a passage (issue #1014). Null when
   * nothing is selected or its location cannot be read.
   */
  passageMarkFromSelection(): PassageMark | null {
    const pending = this.annotationManager?.pendingSelection();
    if (!pending) return null;
    const ends = cfiRangeEnds(pending.cfiRange);
    if (!ends) return null;
    return {
      format: 'epub',
      start: ends.start,
      end: ends.end,
      excerpt: excerptOf(pending.selectedText),
      label: this.chapterLabelFor(ends.start),
    };
  }

  /**
   * The passage between two marks, in book order whichever was made first:
   * its text lifted from every section between them without rendering one.
   * Null when either end can no longer be found in this book.
   */
  async buildPassage(a: PassageMark, b: PassageMark): Promise<PassageDraft | null> {
    if (a.format !== 'epub' || b.format !== 'epub' || !this.epubBook) return null;
    const { start, end } = passageEnds(a, b);
    const first = cfiSpineIndex(start);
    const last = cfiSpineIndex(end);
    if (!Number.isInteger(first) || !Number.isInteger(last) || last < first) return null;

    const html: string[] = [];
    const text: string[] = [];
    for (let index = first; index <= last; index++) {
      const part = await this.withSectionDocument(index, (document) => {
        const fragment = cutSection(
          document,
          index === first ? start : null,
          index === last ? end : null,
        );
        return fragment ? { html: fragmentHtml(fragment), text: passagePlainText(fragment) } : null;
      });
      if (!part) {
        // The ends must resolve; a section between them that cannot be read
        // (missing, non-linear) is left out rather than failing the passage.
        if (index === first || index === last) return null;
        continue;
      }
      if (part.text) {
        html.push(part.html);
        text.push(part.text);
      }
    }

    const plain = text.join('\n\n').trim();
    if (!plain) return null;
    return {
      anchorKind: 'epub_cfi',
      start,
      end,
      title: this.chapterLabelFor(start) ?? excerptOf(plain, 48),
      html: html.join(''),
      text: plain,
      words: countWords(plain),
      opening: excerptOf(plain, 140),
      closing: closingOf(plain, 140),
    };
  }

  /** The TOC entry for the section a CFI points into. */
  private chapterLabelFor(cfi: string): string | null {
    try {
      const index = cfiSpineIndex(cfi);
      const href = this.epubSpineSources().find((item) => item.index === index)?.href ?? null;
      return findTocItemForHref(this.toc(), href)?.label?.trim() || null;
    } catch {
      return null;
    }
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.bookLoadGeneration++;
    this.destroy$.next();
    this.destroy$.complete();
    this.bookTeardown$.next();
    this.bookTeardown$.complete();
    this.clearReadAloudMoveTimers();
    this.readAloud.end();
    this.unregisterAssistantContext?.();
    this.unregisterAssistantContext = null;
    this.groundedNavigator.destroy();
    this.typographyController.destroy();
    this.resizeObserver?.disconnect();
    this.resizeSubject$.complete();
    this.progressUpdater$.complete();
    this.contentsInteraction.destroy();
    this.searchController.reset();
    this.annotationManager?.destroy();
    this.annotationManager = null;
    this.lazyResources?.destroy();
    this.lazyResources = null;
    if (this.epubBook) {
      this.epubBook.destroy();
      this.epubBook = null;
    }
    this.rendition = null;
  }
}
