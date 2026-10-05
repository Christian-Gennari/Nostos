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
  ElementRef,
  untracked,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { ButtonComponent } from '../../ui/button/button.component';
import ePub, { Book, Rendition, Contents } from 'epubjs';
import { Subject, Subscription } from 'rxjs';
import { debounceTime, distinctUntilChanged, filter } from 'rxjs/operators';

import { EpubAnnotationManager, SelectionAnchor } from './epub-annotation-manager';
import { DEFAULT_HIGHLIGHT_COLOUR, HighlightColour } from '../highlight-colours';
import { NotesService } from '../../core/services/notes.service';
import { BooksService } from '../../core/services/books.service';
import { ThemeService, Theme } from '../../core/services/theme.service';
import { Book as BookDto } from '../../core/dtos/book.dtos';
import {
  IReader,
  ReaderProgress,
  ReaderSearchState,
  ReaderSourceTarget,
  TocItem,
} from '../reader.interface';
import { isInteractiveTarget, isTypingTarget, pageActionForKey } from '../reader-keyboard';
import { AssistantContextService } from '../../ui/assistant/assistant-context.service';

import {
  normalizeEpubHrefForComparison,
  normalizeEpubSourceText,
  normalizedEpubResourceText,
  rangeAtNormalizedResourceOffset,
  rangeForNormalizedResourceSpan,
  resolveGroundedEpubResourceHref,
  type EpubSpineSource,
} from './epub-grounded-source';

export {
  resolveGroundedEpubResourceHref,
  type EpubSpineSource,
} from './epub-grounded-source';

/**
 * Rendition theme names. Both Nostos normalizations are registered once per
 * rendition; the selected one always mirrors the app theme (ThemeService),
 * so the page never renders white inside a dark UI or vice versa.
 */
const NOSTOS_LIGHT_THEME = 'nostos-light';
const NOSTOS_DARK_THEME = 'nostos-dark';
/**
 * Color-only rules for the epub.js iframe, mirroring the Nostos light tokens
 * from styles.css (the iframe is a separate document and cannot read the
 * parent's CSS variables). Only foreground, background, links, and selection
 * colors are overridden; book typography, layout, emphasis, and images are
 * left untouched (images are never inverted).
 */
const NOSTOS_LIGHT_RULES: Record<string, Record<string, string>> = {
  html: { background: '#ffffff !important', color: '#1a1a1a !important' },
  body: { background: '#ffffff !important', color: '#1a1a1a !important' },
  // Publisher CSS often sets explicit text colors (e.g. h1 { color: #000 });
  // normalize every element to inherit the theme text color so headings and
  // body text stay readable. `a` comes AFTER `body *` so the link color wins
  // for links (and their descendants).
  'body *': { color: 'inherit !important' },
  a: { color: '#60a5fa !important' },
  '::selection': { background: 'rgba(96, 165, 250, 0.3) !important' },
};

/**
 * Color-only rules for the dark UI, mirroring the `:root[data-theme='dark']`
 * tokens from styles.css (ground #121318, ink #f0f1f4). Same contract as the
 * light rules: colors only, book typography and images untouched.
 */
const NOSTOS_DARK_RULES: Record<string, Record<string, string>> = {
  html: { background: '#121318 !important', color: '#f0f1f4 !important' },
  body: { background: '#121318 !important', color: '#f0f1f4 !important' },
  'body *': { color: 'inherit !important' },
  a: { color: '#8fbfae !important' },
  '::selection': { background: 'rgba(143, 191, 174, 0.35) !important' },
};

/** The theme surface of `rendition.themes` the reader drives. */
interface ReaderThemes {
  register(name: string, rules: Record<string, Record<string, string>>): void;
  select(name: string): void;
}

/** Registers both Nostos themes on a rendition (once per rendition). */
export function registerNostosReaderThemes(themes: ReaderThemes): void {
  themes.register(NOSTOS_LIGHT_THEME, NOSTOS_LIGHT_RULES);
  themes.register(NOSTOS_DARK_THEME, NOSTOS_DARK_RULES);
}

/**
 * Selects the Nostos theme matching the app theme in every rendered section.
 *
 * epub.js (0.3.93) keeps one `<style id="epubjs-inserted-css-<theme>">` per
 * theme in each section document and never removes or reorders it: selecting
 * a theme again appends its rules to the EXISTING element. Both Nostos themes
 * use the same `!important` selectors, so whichever element sits later in
 * <head> wins. After dark -> light -> dark the light element is the later one,
 * and the page stayed white inside a dark app until a reload. Removing both
 * Nostos stylesheets first means epub.js re-creates only the selected one,
 * last in <head>; sections rendered later only ever receive the current theme.
 */
export function selectNostosReaderTheme(
  themes: ReaderThemes,
  contents: readonly Pick<Contents, 'document'>[],
  theme: Theme,
): void {
  for (const content of contents) {
    for (const name of [NOSTOS_LIGHT_THEME, NOSTOS_DARK_THEME]) {
      content.document?.getElementById(`epubjs-inserted-css-${name}`)?.remove();
    }
  }
  themes.select(theme === 'dark' ? NOSTOS_DARK_THEME : NOSTOS_LIGHT_THEME);
}

/**
 * Reading typefaces offered by the typography panel. `libron` is the bundled
 * Nostos reading face; `publisher` leaves the book's authored typeface alone.
 */
export type EpubFontFamily = 'libron' | 'publisher' | 'sans' | 'mono';

export type EpubMargin = 'narrow' | 'normal' | 'wide';

export interface EpubTypography {
  fontFamily: EpubFontFamily;
  lineHeight: number;
  margin: EpubMargin;
}

export const EPUB_FONT_OPTIONS: { value: EpubFontFamily; label: string }[] = [
  { value: 'libron', label: 'Libron' },
  { value: 'publisher', label: 'Publisher' },
  { value: 'sans', label: 'Sans' },
  { value: 'mono', label: 'Mono' },
];

export const EPUB_LINE_OPTIONS = [1.4, 1.6, 1.8, 2.0];

export const EPUB_MARGIN_OPTIONS: { value: EpubMargin; label: string }[] = [
  { value: 'narrow', label: 'Narrow' },
  { value: 'normal', label: 'Normal' },
  { value: 'wide', label: 'Wide' },
];

export const DEFAULT_TYPOGRAPHY: EpubTypography = {
  fontFamily: 'libron',
  lineHeight: 1.6,
  margin: 'normal',
};

const FONT_STACKS: Record<Exclude<EpubFontFamily, 'publisher'>, string> = {
  // The serif fallback keeps a book readable if a Libron asset fails to load.
  libron: '"Libron", Georgia, "Times New Roman", Times, serif',
  sans: 'system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
  mono: 'ui-monospace, SFMono-Regular, monospace',
};

/**
 * Margin presets, as a percentage of the reader's own width. These are OUTER
 * margins around the epub.js page: `narrow` leaves epub.js's own gutter as the
 * only inset, so it reads as "the book as published".
 *
 * They are applied to our own container rather than to the contents body on
 * purpose. epub.js writes its own inline `padding: 42px !important` on every
 * contents body while it lays a section out; that out-ranks any stylesheet rule
 * and is not ordered against our events, so a body-padding margin was either
 * inert or won only by race. Padding our container and resizing the rendition
 * means epub.js simply paginates into a narrower page — nothing to race.
 */
const MARGIN_INSET_PERCENT: Record<EpubMargin, number> = {
  narrow: 0,
  normal: 4,
  wide: 8,
};

export function marginInsetPercent(margin: EpubMargin): number {
  return MARGIN_INSET_PERCENT[margin];
}

const TYPOGRAPHY_STYLE_ID = 'nostos-typography';
const READER_FONTS_STYLE_ID = 'nostos-reader-fonts';

/**
 * The bundled Libron faces (SIL OFL 1.1, see `public/fonts/libron/`). Only 400
 * and 700 exist; the browser's weight matching maps every authored weight onto
 * one of them, so bold and italic text resolve to a real face, never a
 * synthesised one.
 */
const LIBRON_FACES: { file: string; style: 'normal' | 'italic'; weight: 400 | 700 }[] = [
  { file: 'Libron-Regular.woff2', style: 'normal', weight: 400 },
  { file: 'Libron-Italic.woff2', style: 'italic', weight: 400 },
  { file: 'Libron-Bold.woff2', style: 'normal', weight: 700 },
  { file: 'Libron-BoldItalic.woff2', style: 'italic', weight: 700 },
];

/**
 * Absolute URLs of the Libron files. They must be absolute: epub.js renders
 * each section in its own iframe document (srcdoc/blob), where a relative URL
 * would not resolve against the app.
 */
function libronFaceUrls(baseUri: string): string[] {
  return LIBRON_FACES.map((face) => new URL(`fonts/libron/${face.file}`, baseUri).href);
}

/**
 * The `@font-face` rules that make Libron available inside one contents
 * document. Pure for testability. Declaring a face costs nothing until text
 * actually uses it, so the rules are injected whatever typeface is selected —
 * switching to Libron later needs no second injection.
 */
export function libronFontFaceCss(baseUri: string): string {
  const urls = libronFaceUrls(baseUri);
  return LIBRON_FACES.map(
    (face, i) =>
      `@font-face{font-family:"Libron";font-style:${face.style};font-weight:${face.weight};` +
      `font-display:block;src:url("${urls[i]}") format("woff2");}`,
  ).join('');
}

/**
 * Stored typeface values and what they mean now. `default` and `serif` are the
 * names earlier versions wrote: Publisher keeps being Publisher, and the old
 * generic Serif becomes Libron, the Nostos serif. Anything else is not a
 * preference we can honour and falls back to the default.
 */
const STORED_FONT_FAMILIES: Record<string, EpubFontFamily> = {
  libron: 'libron',
  publisher: 'publisher',
  sans: 'sans',
  mono: 'mono',
  default: 'publisher',
  serif: 'libron',
};

export function fontFamilyFromStored(value: unknown): EpubFontFamily {
  return typeof value === 'string' && Object.hasOwn(STORED_FONT_FAMILIES, value)
    ? STORED_FONT_FAMILIES[value]
    : DEFAULT_TYPOGRAPHY.fontFamily;
}

/**
 * Quiet window for coalescing a burst of typography changes into ONE
 * re-pagination.
 *
 * Measured on a real chapter of a real book (33.5k chars rendered as a single
 * 12.4k-px-wide paginated strip): a lone text-size step settles in 32ms, but five
 * steps in a row took 226ms to their first change and were still re-laying-out
 * 1.2s later, with the inner document growing 11.9k -> 29.5k px. Each step
 * re-paginates the whole section and widens the strip, so every step costs more
 * than the one before it.
 *
 * Readers click A+ several times running to find a comfortable size. The first
 * change of a burst still applies immediately (so one deliberate step keeps its
 * 32ms feel) and the rest collapse into a single trailing apply.
 */
const TYPOGRAPHY_APPLY_QUIET_MS = 180;

/**
 * Reader-wide typography preference: one key for every EPUB, because the reader
 * should remember the setting rather than the book. Earlier versions wrote
 * `nostos.epub-typography.<bookId>`; `restoreSavedTypography` adopts that value
 * once so an existing choice is not lost.
 */
const TYPOGRAPHY_STORAGE_KEY = 'nostos.epub-typography';

/**
 * Text size follows the same reader-wide persistence semantics as the controls
 * beside it in View settings. Older builds stored size per book; the first
 * valid legacy value encountered is adopted into this key.
 */
const FONT_SIZE_STORAGE_KEY = 'nostos.epub-font-size';

/**
 * Elements whose own publisher font declarations should yield to an explicit
 * Nostos typeface choice. This is intentionally a semantic reading-text list,
 * NOT `body *`: descendants such as `code`, `pre`, icon spans, SVG and
 * MathML keep their own directly assigned fonts.
 */
const READING_FONT_SELECTOR = [
  'body',
  'body p',
  'body blockquote',
  'body li',
  'body dt',
  'body dd',
  'body figcaption',
  'body caption',
  'body td',
  'body th',
  'body h1',
  'body h2',
  'body h3',
  'body h4',
  'body h5',
  'body h6',
].join(',');

/**
 * Prose blocks that should honour the reader's line-height choice even when an
 * EPUB assigns line-height directly. Headings and preformatted/code elements
 * are deliberately omitted so their authored vertical rhythm can remain
 * distinct; they still inherit the body value when the publication does not
 * specify one.
 */
const PROSE_LINE_HEIGHT_SELECTOR = [
  'body',
  'body p',
  'body blockquote',
  'body li',
  'body dt',
  'body dd',
  'body figcaption',
  'body caption',
  'body td',
  'body th',
].join(',');

/**
 * The injected typography rules for one contents document. Pure for
 * testability. `publisher` keeps the book's own typeface (no font-family
 * override); the reader's line height applies directly to normal prose so an
 * authored `p { line-height: ... }` cannot silently defeat the control.
 *
 * Typeface and line height intentionally use separate selector sets. A chosen
 * Nostos face overrides normal reading text and headings, while special
 * descendants such as code and icon glyphs keep their direct fonts. Text size
 * is also deliberately absent: epub.js owns that setting through
 * `rendition.themes.fontSize()`.
 *
 * Margins are deliberately NOT here — see {@link marginInsetPercent}: they are
 * padding on our own viewer, because epub.js's own inline-important body
 * padding cannot be beaten from a stylesheet.
 */
export function typographyCss(t: EpubTypography): string {
  const lineHeight =
    `${PROSE_LINE_HEIGHT_SELECTOR}{line-height:${t.lineHeight} !important;}`;
  if (t.fontFamily === 'publisher') return lineHeight;

  return (
    `${READING_FONT_SELECTOR}{font-family:${FONT_STACKS[t.fontFamily]} !important;}` +
    lineHeight
  );
}
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

/**
 * The progress pill's text: a percentage, plus the chapter it belongs to when
 * the TOC can place us. It deliberately carries no time estimate — the previous
 * "3h 43m left" was one 1,000-character location treated as one minute, so it
 * was fiction presented with minute precision (issue #225 §1.3).
 */
export function progressLabel(percent: number, chapter: string | null): string {
  return chapter ? `${percent}% • ${chapter}` : `${percent}%`;
}


/**
 * Quiet window used to decide that epub.js has finished re-laying-out after a
 * grounded display.
 *
 * A resolved `rendition.display()` is not a settled display: epub.js
 * re-paginates on container size changes and then re-displays the view's last
 * seen location (`Rendition.onResized`). On a real nested-OPF book (The Idea of
 * Justice, chapter015, offset 9202) the grounded CFI display resolved 90 ms
 * after the click and the reflow re-displayed the *section start* 23 ms later,
 * silently undoing the navigation.
 *
 * The window is deliberately longer than the 350 ms resize debounce above: a
 * queued `rendition.resize` re-layout must fall inside the quiet window and
 * restart it, instead of being confirmed away just before it lands.
 */
const GROUNDED_SETTLE_QUIET_MS = 400;

/**
 * Hard ceiling for one settle wait. `resized` events restart the quiet window,
 * so a stream of resizes (or a rendition that never stops re-laying-out) must
 * still reach a verdict: at the deadline the target is verified regardless of
 * later events, rather than holding progress persistence closed for ever.
 */
const GROUNDED_SETTLE_MAX_MS = 2000;

@Component({
  selector: 'app-epub-reader',
  standalone: true,
  imports: [CommonModule, ButtonComponent],
  templateUrl: './epub-reader.component.html',
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
  noteCreated = output<void>();
  highlightMode = input<boolean>(false);
  /** The book's highlighter pen (issue #208), owned by the shell. */
  highlightColour = input<HighlightColour>(DEFAULT_HIGHLIGHT_COLOUR);
  selectionCaptured = output<string>();
  /**
   * Where the captured selection sits in the page's viewport, emitted right
   * after `selectionCaptured` so the shell can anchor its menu at the text
   * (#650). Null when it could not be measured.
   */
  selectionAnchored = output<SelectionAnchor | null>();
  commitFailed = output<void>();
  exitRequested = output<void>();
  /** Ctrl/Cmd+F pressed inside the epub.js iframe, whose events do not bubble to the shell. */
  searchRequested = output<void>();

  private notesService = inject(NotesService);
  private booksService = inject(BooksService);
  private themeService = inject(ThemeService);
  private injector = inject(Injector);
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
  private rendition: Rendition | null = null;
  private annotationManager: EpubAnnotationManager | null = null;
  private currentCfi: string | null = null;
  private pendingGroundedSource: ReaderSourceTarget | null = null;

  // --- In-book search (#761) ---
  searchState = signal<ReaderSearchState>({ status: 'idle', current: 0, total: 0 });
  private searchCorpus: Array<{ href: string; index: number; text: string }> | null = null;
  private searchCorpusPromise: Promise<Array<{ href: string; index: number; text: string }>> | null = null;
  private searchMatches: Array<{ href: string; index: number; offset: number; length: number }> = [];
  private activeSearchIndex = -1;
  private searchGeneration = 0;

  /**
   * Monotonic navigation generation. Every navigation (grounded apply, user
   * page turn/keyboard/TOC, book load) bumps it, and every async continuation
   * of a grounded navigation captures it and no-ops once superseded — a stale
   * settle must never override a newer navigation.
   */
  private navigationGeneration = 0;

  /**
   * The grounded target currently protected through epub.js's layout settle.
   * While it is set, relocated events are not persisted as reading progress:
   * they describe reflow churn, not where the reader has been grounded.
   */
  private groundedNavigation: {
    generation: number;
    /** The exact CFI the grounded navigation displayed. */
    cfi: string;
    /** The resource the target was resolved to, for re-deriving its range. */
    href: string | null;
    /** Normalized text offset of the cited passage, when the locator had one. */
    offset: number | null;
    /** The single controlled retry has already been issued. */
    reasserted: boolean;
    /** Ends the in-flight settle wait (quiet timer + resize listeners). */
    cancelSettle: (() => void) | null;
  } | null = null;

  /** Keydown listeners registered inside each iframe's contents document. */
  private readonly keyboardDocuments = new Map<Document, () => void>();

  /**
   * Progress writes stay closed until the saved position has been applied. The
   * rendition reports its opening section as soon as it lays out, and writing
   * that used to overwrite the reader's real position whenever the restore
   * arrived second (issue #225 §1.2). Unlock happens in unlockProgress().
   */
  private progressUnlocked = false;

  /** Spine index of the displayed section — the coarse fallback position. */
  private spineIndex = signal<number | null>(null);

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

  // Internal text-size state (reader-wide, with legacy per-book fallback).
  private currentFontSize = signal(100); // 100%

  /** Reader typography (typeface, line height, margins), persisted reader-wide. */
  readonly typography = signal<EpubTypography>({ ...DEFAULT_TYPOGRAPHY });
  /** Outer margin for the current preset, as a percentage of the reader width. */
  readonly marginInset = computed(() => marginInsetPercent(this.typography().margin));
  /** Text size in percent — surfaced so the shell's typography panel can show the
   *  current step beside the A−/A+ controls (the toolbar used to own them). */
  readonly fontSizePercent = this.currentFontSize.asReadonly();

  readonly fontOptions = EPUB_FONT_OPTIONS;
  readonly lineOptions = EPUB_LINE_OPTIONS;
  readonly marginOptions = EPUB_MARGIN_OPTIONS;

  // RxJS Subjects
  private progressUpdater$ = new Subject<{ location: string; percentage: number }>();
  private resizeSubject$ = new Subject<void>();
  private resizeObserver: ResizeObserver | null = null;

  loading = signal(true);
  errorMessage = signal<string | null>(null);
  readonly sourceNavigationMessage = signal<string | null>(null);

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
        // loadBook reads highlightMode() to sync the manager before display;
        // untracked keeps that read out of this effect's dependencies so a
        // mode toggle never re-triggers a full book reload.
        untracked(() => this.loadBook(this.bookId()));
      }
    });

    effect(() => {
      const mode = this.highlightMode();
      if (this.annotationManager) {
        this.annotationManager.setHighlightMode(mode);
      }
    });

    effect(() => {
      const colour = this.highlightColour();
      if (this.annotationManager) {
        this.annotationManager.setHighlightColour(colour);
      }
    });

    // The rendition is a separate document: re-select the matching Nostos
    // theme whenever the app theme changes, so the page follows light/dark
    // without a reload.
    effect(() => {
      const theme = this.themeService.theme();
      if (this.rendition) {
        this.selectReaderTheme(theme);
      }
    });
  }

  ngOnInit() {
    // Handle Window Resizing
    this.resizeSubject$.pipe(debounceTime(350)).subscribe(() => {
      if (this.rendition) {
        const page = this.elementRef.nativeElement.querySelector('#epub-page');
        if (page) {
          const { clientWidth, clientHeight } = page;
          try {
            this.rendition.resize(clientWidth, clientHeight);
          } catch (e) {
            console.warn('Rendition resize failed (book might not be ready):', e);
          }
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
          (prev, curr) => prev.location === curr.location && prev.percentage === curr.percentage,
        ),
      )
      .subscribe((data) => {
        this.booksService.updateProgress(this.bookId(), data.location, data.percentage).subscribe();
      });
  }

  // --- IReader Methods ---

  async search(query: string): Promise<void> {
    const normalizedQuery = normalizeEpubSourceText(query);
    const generation = ++this.searchGeneration;

    this.annotationManager?.clearSearchHighlight();
    this.searchMatches = [];
    this.activeSearchIndex = -1;

    if (!normalizedQuery) {
      this.searchState.set({ status: 'idle', current: 0, total: 0 });
      return;
    }

    this.searchState.set({ status: 'searching', current: 0, total: 0 });

    try {
      const corpus = await this.ensureSearchCorpus();
      if (generation !== this.searchGeneration) return;

      const escaped = normalizedQuery.replace(/[.*+?^$\{\}()|[\]\\]/g, '\\  // --- IReader Methods ---

  next() {');
      const matcher = new RegExp(escaped, 'giu');
      const matches: Array<{ href: string; index: number; offset: number; length: number }> = [];

      for (const section of corpus) {
        matcher.lastIndex = 0;
        let match: RegExpExecArray | null;
        while ((match = matcher.exec(section.text)) !== null) {
          matches.push({
            href: section.href,
            index: section.index,
            offset: match.index,
            length: match[0].length,
          });
          // Literal queries are never empty after normalization, but keep this
          // guard so a future matcher change cannot spin forever.
          if (match[0].length === 0) matcher.lastIndex += 1;
        }
      }

      if (generation !== this.searchGeneration) return;
      this.searchMatches = matches;

      if (matches.length === 0) {
        this.searchState.set({ status: 'not-found', current: 0, total: 0 });
        return;
      }

      this.activeSearchIndex = 0;
      this.searchState.set({ status: 'ready', current: 1, total: matches.length });
      await this.activateSearchResult(generation);
    } catch (error) {
      if (generation !== this.searchGeneration) return;
      console.warn('EPUB search failed:', error);
      this.searchState.set({ status: 'not-found', current: 0, total: 0 });
    }
  }

  async nextSearchResult(): Promise<void> {
    if (this.searchMatches.length === 0) return;
    const generation = this.searchGeneration;
    this.activeSearchIndex = (this.activeSearchIndex + 1) % this.searchMatches.length;
    this.searchState.set({
      status: 'ready',
      current: this.activeSearchIndex + 1,
      total: this.searchMatches.length,
    });
    await this.activateSearchResult(generation);
  }

  async previousSearchResult(): Promise<void> {
    if (this.searchMatches.length === 0) return;
    const generation = this.searchGeneration;
    this.activeSearchIndex =
      (this.activeSearchIndex - 1 + this.searchMatches.length) % this.searchMatches.length;
    this.searchState.set({
      status: 'ready',
      current: this.activeSearchIndex + 1,
      total: this.searchMatches.length,
    });
    await this.activateSearchResult(generation);
  }

  clearSearch(): void {
    this.searchGeneration++;
    this.searchMatches = [];
    this.activeSearchIndex = -1;
    this.searchState.set({ status: 'idle', current: 0, total: 0 });
    this.annotationManager?.clearSearchHighlight();
  }

  private async ensureSearchCorpus(): Promise<Array<{ href: string; index: number; text: string }>> {
    if (this.searchCorpus) return this.searchCorpus;
    if (this.searchCorpusPromise) return this.searchCorpusPromise;

    const book = this.epubBook;
    if (!book) return [];

    const promise = (async () => {
      await book.ready;
      if (this.epubBook !== book) return [];

      const spine = book.spine as unknown as {
        spineItems?: Array<{
          href?: unknown;
          index?: unknown;
          linear?: unknown;
          load?: (request: (path: string) => Promise<unknown>) => Promise<Element>;
          unload?: () => void;
        }>;
      };

      const corpus: Array<{ href: string; index: number; text: string }> = [];
      for (const [fallbackIndex, section] of (spine.spineItems ?? []).entries()) {
        if (section.linear === false || typeof section.load !== 'function') continue;

        const href = typeof section.href === 'string' ? section.href : '';
        if (!href) continue;
        const index = typeof section.index === 'number' ? section.index : fallbackIndex;

        try {
          const contents = await section.load(book.load.bind(book) as (path: string) => Promise<unknown>);
          if (this.epubBook !== book) return [];
          const document = contents?.ownerDocument;
          if (!document) continue;
          const text = normalizedEpubResourceText(document);
          if (text) corpus.push({ href, index, text });
        } finally {
          section.unload?.();
        }
      }

      if (this.epubBook === book) this.searchCorpus = corpus;
      return corpus;
    })();

    this.searchCorpusPromise = promise;
    try {
      return await promise;
    } finally {
      if (this.searchCorpusPromise === promise) this.searchCorpusPromise = null;
    }
  }

  private async activateSearchResult(generation: number): Promise<void> {
    const match = this.searchMatches[this.activeSearchIndex];
    if (!match || generation !== this.searchGeneration) return;

    await this.goToSource({
      type: 'epub',
      epubResourceHref: match.href,
      epubSpineIndex: match.index,
      epubTextOffset: match.offset,
    });
    if (generation !== this.searchGeneration) return;

    try {
      const contents =
        this.renderedContents().find((candidate) => this.contentMatchesHref(candidate, match.href)) ??
        this.renderedContents()[0];
      if (!contents?.document) return;

      const range = rangeForNormalizedResourceSpan(contents.document, match.offset, match.length);
      const cfi = range ? (contents as any).cfiFromRange?.(range) : null;
      if (typeof cfi === 'string' && cfi.length > 0) {
        this.annotationManager?.showSearchHighlight(cfi);
      }
    } catch (error) {
      // Navigation is still useful if a malformed publisher DOM prevents the
      // optional temporary paint.
      console.warn('Could not paint EPUB search result:', error);
    }
  }

  next() {
    this.beginNavigation();
    this.sourceNavigationMessage.set(null);
    this.rendition?.next();
  }

  previous() {
    this.beginNavigation();
    this.sourceNavigationMessage.set(null);
    this.rendition?.prev();
  }

  goTo(target: string | number) {
    this.beginNavigation();
    this.sourceNavigationMessage.set(null);
    this.rendition?.display(target.toString());
  }

  async goToSource(target: ReaderSourceTarget): Promise<void> {
    if (target.type !== 'epub') return;

    this.beginNavigation();
    this.sourceNavigationMessage.set(null);

    // A source chip can be clicked before epub.js finishes its opening display.
    // In that window the rendition may exist but the normal opening/restore
    // chain can still overwrite a navigation. Keep the exact grounded target
    // and apply it after the opening display settles.
    if (!this.rendition || !this.progressUnlocked) {
      this.pendingGroundedSource = target;
      return;
    }

    await this.applyGroundedSource(target);
  }

  private async applyGroundedSource(target: ReaderSourceTarget): Promise<void> {
    if (target.type !== 'epub' || !this.rendition) return;

    const generation = this.beginNavigation();
    this.sourceNavigationMessage.set(null);

    if (target.epubCfi) {
      // Protect before the display resolves: epub.js's settle can re-display a
      // stale location as soon as it does, and nothing in between may persist
      // that churn as the reader's progress.
      this.protectGroundedTarget(
        generation,
        target.epubCfi,
        target.epubResourceHref ?? null,
        target.epubTextOffset ?? null,
      );
      try {
        await this.rendition.display(target.epubCfi);
        if (generation !== this.navigationGeneration) return;
        this.watchGroundedSettle(generation);
        return;
      } catch {
        // A CFI belongs to one exact source revision but an older epub.js build
        // can still reject it. Fall through to the structural locator rather
        // than inventing a page or silently opening the wrong place.
        this.cancelGroundedNavigation(generation);
      }
      if (generation !== this.navigationGeneration) return;
    }

    if (!target.epubResourceHref) {
      this.failGroundedSourceNavigation();
      return;
    }

    let displayedHref = target.epubResourceHref;
    try {
      // Canonical/current locators take the direct path first.
      await this.rendition.display(displayedHref);
    } catch (exactError) {
      // Legacy v1 locators are archive-root-relative. Resolve them against the
      // actual epub.js spine only when the relationship is deterministic.
      const compatibleHref = resolveGroundedEpubResourceHref(
        target.epubResourceHref,
        target.epubSpineIndex,
        this.epubSpineSources(),
      );

      if (!compatibleHref || compatibleHref === displayedHref) {
        this.failGroundedSourceNavigation(exactError);
        return;
      }

      displayedHref = compatibleHref;
      try {
        await this.rendition.display(displayedHref);
      } catch (compatibilityError) {
        this.failGroundedSourceNavigation(compatibilityError);
        return;
      }
    }
    if (generation !== this.navigationGeneration) return;

    if (target.epubTextOffset === null || target.epubTextOffset === undefined) return;

    let cfi: string;
    try {
      const contents = this.renderedContents();
      const content =
        contents.find((candidate) => this.contentMatchesHref(candidate, displayedHref)) ??
        contents[0];

      if (!content?.document) {
        this.failGroundedSourceNavigation();
        return;
      }

      const range = rangeAtNormalizedResourceOffset(
        content.document,
        Math.max(0, target.epubTextOffset),
      );
      if (!range) {
        this.failGroundedSourceNavigation();
        return;
      }

      const computed = (content as any).cfiFromRange?.(range);
      if (typeof computed !== 'string' || computed.length === 0) {
        this.failGroundedSourceNavigation();
        return;
      }
      cfi = computed;
    } catch (error) {
      this.failGroundedSourceNavigation(error);
      return;
    }
    if (generation !== this.navigationGeneration) return;

    this.protectGroundedTarget(generation, cfi, displayedHref, target.epubTextOffset);
    try {
      await this.rendition.display(cfi);
    } catch (error) {
      this.cancelGroundedNavigation(generation);
      this.failGroundedSourceNavigation(error);
      return;
    }
    if (generation !== this.navigationGeneration) return;
    this.watchGroundedSettle(generation);
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

  private failGroundedSourceNavigation(error?: unknown): void {
    this.sourceNavigationMessage.set("Couldn't locate this passage in the EPUB.");
    if (error !== undefined) {
      console.warn('Could not navigate to the grounded EPUB source:', error);
    }
  }

  // --- Grounded source navigation protection ---

  /**
   * Start a navigation operation. Bumps the generation and cancels any
   * grounded protection: a new navigation — including the reader's own page
   * turn, keyboard or TOC — must never be fought by a delayed reassert.
   */
  private beginNavigation(): number {
    this.navigationGeneration++;
    this.cancelGroundedNavigation();
    return this.navigationGeneration;
  }

  /** Remember the grounded target to keep on screen through the settle. */
  private protectGroundedTarget(
    generation: number,
    cfi: string,
    href: string | null,
    offset: number | null,
  ): void {
    this.groundedNavigation = {
      generation,
      cfi,
      href,
      offset,
      reasserted: false,
      cancelSettle: null,
    };
  }

  /**
   * Drop the protected target (a newer navigation, or a failed display of a
   * candidate CFI). The optional generation keeps a stale failure path from
   * tearing down the protection a newer navigation installed.
   */
  private cancelGroundedNavigation(generation?: number): void {
    const target = this.groundedNavigation;
    if (!target || (generation !== undefined && target.generation !== generation)) return;
    this.groundedNavigation = null;
    target.cancelSettle?.();
  }

  /**
   * Wait for epub.js's layout settle before trusting where the display landed.
   *
   * The wait is event-driven — rendition `resized` events and the reader's own
   * resize stream restart a short quiet window — with a hard deadline so a
   * rendition that never stops re-laying-out still reaches a verdict instead
   * of holding progress writes closed for ever.
   */
  private watchGroundedSettle(generation: number): void {
    const target = this.groundedNavigation;
    const rendition = this.rendition;
    if (
      !target ||
      target.generation !== generation ||
      generation !== this.navigationGeneration ||
      !rendition
    ) {
      return;
    }

    let quietTimer: ReturnType<typeof setTimeout> | null = null;
    let deadlineTimer: ReturnType<typeof setTimeout> | null = null;
    let resizeSubscription: Subscription | null = null;

    const cleanup = () => {
      if (quietTimer) {
        clearTimeout(quietTimer);
        quietTimer = null;
      }
      if (deadlineTimer) {
        clearTimeout(deadlineTimer);
        deadlineTimer = null;
      }
      resizeSubscription?.unsubscribe();
      resizeSubscription = null;
      rendition.off('resized', onResized);
      const current = this.groundedNavigation;
      if (current && current.generation === generation) {
        current.cancelSettle = null;
      }
    };

    const finish = () => {
      cleanup();
      void this.verifyGroundedNavigation(generation);
    };

    // Every re-layout restarts the quiet window: the settle is not one resize
    // but however many epub.js performs before the page stops moving.
    const restartQuietWindow = () => {
      if (quietTimer) clearTimeout(quietTimer);
      quietTimer = setTimeout(finish, GROUNDED_SETTLE_QUIET_MS);
    };

    const onResized = () => restartQuietWindow();
    const cancel = () => cleanup();

    target.cancelSettle = cancel;
    rendition.on('resized', onResized);
    resizeSubscription = this.resizeSubject$.subscribe(restartQuietWindow);
    deadlineTimer = setTimeout(finish, GROUNDED_SETTLE_MAX_MS);
    restartQuietWindow();
  }

  /**
   * Once the settle is quiet: confirm the grounded target is where the reader
   * ended up, reassert it exactly once when it is not, and fail closed when
   * even the controlled retry cannot establish it.
   */
  private async verifyGroundedNavigation(generation: number): Promise<void> {
    const target = this.groundedNavigation;
    if (!target || target.generation !== generation || generation !== this.navigationGeneration) {
      return;
    }

    if (this.isGroundedTargetOnPage()) {
      this.confirmGroundedNavigation(generation);
      return;
    }

    if (target.reasserted) {
      this.failGroundedNavigation(generation);
      return;
    }
    target.reasserted = true;

    const rendition = this.rendition;
    if (!rendition) {
      this.failGroundedNavigation(generation);
      return;
    }

    try {
      await rendition.display(target.cfi);
    } catch (error) {
      this.failGroundedNavigation(generation, error);
      return;
    }
    if (generation !== this.navigationGeneration) return;
    this.watchGroundedSettle(generation);
  }

  /** Confirmed: resume progress persistence with the verified target. */
  private confirmGroundedNavigation(generation: number): void {
    const target = this.groundedNavigation;
    if (!target || target.generation !== generation) return;

    this.groundedNavigation = null;
    target.cancelSettle?.();
    this.updateProgressState(target.cfi);
  }

  /** Failed closed: surface the existing message and resume persistence. */
  private failGroundedNavigation(generation: number, error?: unknown): void {
    const target = this.groundedNavigation;
    if (!target || target.generation !== generation) return;

    this.groundedNavigation = null;
    target.cancelSettle?.();
    this.failGroundedSourceNavigation(error);
  }

  /**
   * Is the grounded target's start point inside the reading view's visible box?
   *
   * Measure the re-derived range when the environment can: the range lives in
   * the iframe's document, whose coordinates are relative to that iframe's own
   * viewport (epub.js pages by scrolling the *parent* container, not the
   * iframe), so the frame element's rect is added before comparing with the
   * page box. The comparison is inclusive because the target range is
   * collapsed to the passage's first character: a zero-width rect on the edge
   * still means the passage starts on the visible page.
   *
   * With no layout geometry to measure, accept only the rendition reporting
   * exactly the CFI that was displayed; anything else fails closed.
   */
  private isGroundedTargetOnPage(): boolean {
    const target = this.groundedNavigation;
    if (!target) return false;

    const found = this.groundedTargetRange();
    if (found) {
      const visible = this.isGroundedRangeVisible(found.range, found.content);
      if (visible !== null) return visible;
    }

    const current = this.getCurrentLocation();
    return current !== null && current === target.cfi;
  }

  /**
   * Re-derive the grounded target's Range in the currently rendered contents:
   * from the normalized text offset when the locator had one (the same
   * machinery that built the CFI), else from the CFI itself.
   */
  private groundedTargetRange(): { content: Contents; range: Range } | null {
    const target = this.groundedNavigation;
    if (!target) return null;

    const rendered = this.renderedContents().filter((content) => !!content?.document);
    if (rendered.length === 0) return null;

    const matching = rendered.filter((content) => this.contentMatchesHref(content, target.href));
    const candidates = matching.length > 0 ? matching : rendered;

    for (const content of candidates) {
      if (target.offset !== null) {
        const range = rangeAtNormalizedResourceOffset(
          content.document,
          Math.max(0, target.offset),
        );
        if (range) return { content, range };
      }

      if (target.cfi && typeof (content as any).range === 'function') {
        try {
          const range = (content as any).range(target.cfi) as Range | null;
          if (range) return { content, range };
        } catch {
          // The CFI does not resolve in this contents — try the next one.
        }
      }
    }
    return null;
  }

  /**
   * Whether the range is inside the reading view, measured in the parent
   * document's viewport. Null when there is no layout to measure — jsdom and
   * detached documents, where `getBoundingClientRect` is missing or zero.
   */
  private isGroundedRangeVisible(range: Range, content: Contents): boolean | null {
    if (typeof range?.getBoundingClientRect !== 'function') return null;

    const frame = content?.window?.frameElement;
    if (!frame || typeof frame.getBoundingClientRect !== 'function') return null;

    const view = this.readingViewRect();
    if (!view) return null;

    const rangeRect = range.getBoundingClientRect();
    const frameRect = frame.getBoundingClientRect();
    // epub.js renders these frames borderless, so the frame's border box is
    // also the origin of its content viewport.
    const left = frameRect.left + rangeRect.left;
    const right = frameRect.left + rangeRect.right;
    const top = frameRect.top + rangeRect.top;
    const bottom = frameRect.top + rangeRect.bottom;

    return right >= view.left && left <= view.right && bottom >= view.top && top <= view.bottom;
  }

  /** The visible reading box, or null when it has not been laid out yet. */
  private readingViewRect(): DOMRect | null {
    const page = this.elementRef.nativeElement.querySelector('#epub-page') as HTMLElement | null;
    const element = page ?? (this.elementRef.nativeElement as HTMLElement);
    if (typeof element?.getBoundingClientRect !== 'function') return null;

    const rect = element.getBoundingClientRect();
    if (rect.width <= 0 && rect.height <= 0) return null;
    return rect;
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
    } catch (e) {
      return null;
    }
    return null;
  }

  zoomIn() {
    this.currentFontSize.update((s) => Math.min(s + 10, 200)); // Max 200%
    this.requestFontSizeApply();
  }

  zoomOut() {
    this.currentFontSize.update((s) => Math.max(s - 10, 50)); // Min 50%
    this.requestFontSizeApply();
  }

  /**
   * Leading-edge + trailing-coalesce size application. The first step of a burst
   * applies at once; further steps within {@link TYPOGRAPHY_APPLY_QUIET_MS} keep
   * pushing the trailing apply back, so the whole burst costs two re-paginations
   * instead of one per click. The pill's `%` reads the signal, so the number
   * still moves on every click even when the layout is waiting.
   */
  private requestFontSizeApply(): void {
    this.persistFontSize();
    if (!this.fontApplyTimer) {
      this.applyFontSize();
    } else {
      clearTimeout(this.fontApplyTimer);
    }
    this.fontApplyTimer = setTimeout(() => {
      this.fontApplyTimer = null;
      this.applyFontSize();
    }, TYPOGRAPHY_APPLY_QUIET_MS);
  }

  /** Size applied to the current rendition — lets a coalesced no-op be skipped. */
  private appliedFontSize: number | null = null;

  /** Typography key applied to the open contents — same purpose as above. */
  private appliedTypographyKey: string | null = null;

  /** Pending trailing applies; see requestFontSizeApply / requestTypographyApply. */
  private fontApplyTimer: ReturnType<typeof setTimeout> | null = null;
  private typographyApplyTimer: ReturnType<typeof setTimeout> | null = null;
  /** Libron is fetched once per reader instance; see preloadLibron. */
  private libronPreloaded = false;

  private applyFontSize() {
    const size = this.currentFontSize();
    if (this.rendition && this.appliedFontSize !== size) {
      this.appliedFontSize = size;
      this.rendition.themes.fontSize(`${size}%`);
    }
    this.persistFontSize();
  }

  private persistFontSize(): void {
    try {
      localStorage.setItem(FONT_SIZE_STORAGE_KEY, String(this.currentFontSize()));
    } catch {
      // Private-mode storage can throw — the size still applies for the session.
    }
  }

  private legacyFontSizeStorageKey(): string {
    return `nostos.epub-font-size.${this.bookId()}`;
  }

  private restoreSavedFontSize(): void {
    try {
      const read = (key: string): number | null => {
        const raw = localStorage.getItem(key);
        if (raw == null) return null;
        const parsed = parseInt(raw, 10);
        if (!Number.isFinite(parsed)) return null;
        return Math.min(200, Math.max(50, parsed));
      };

      const stored = read(FONT_SIZE_STORAGE_KEY);
      if (stored !== null) {
        this.currentFontSize.set(stored);
        return;
      }

      const legacy = read(this.legacyFontSizeStorageKey());
      if (legacy === null) return;

      this.currentFontSize.set(legacy);
      localStorage.setItem(FONT_SIZE_STORAGE_KEY, String(legacy));
    } catch {
      // Storage unreadable — fall back to 100%.
    }
  }

  // --- Book Loading & Setup ---

  loadBook(id: string) {
    this.beginNavigation();
    this.clearSearch();
    this.searchCorpus = null;
    this.searchCorpusPromise = null;
    if (this.epubBook) {
      this.annotationManager?.destroy();
      this.annotationManager = null;
      this.epubBook.destroy();
      this.epubBook = null;
      this.rendition = null;
      this.currentCfi = null;
    }
    this.assistantLocation.set(null);
    this.assistantSelection.set(null);
    this.progressUnlocked = false;
    this.errorMessage.set(null);
    this.sourceNavigationMessage.set(null);

    this.loading.set(true);
    this.locationsReady.set(false);
    // A fresh rendition must be told the current size and typography even when
    // neither value changed — otherwise the applied-value guards below would
    // skip the apply and the new book would open at the publisher's defaults.
    this.appliedFontSize = null;
    this.appliedTypographyKey = null;

    // FIX 1: Append a dummy parameter ending in .epub
    // This tricks epub.js into treating the URL as a file, not a directory.
    const url = `/api/books/${id}/file?t=${Date.now()}.epub`;

    // FIX 2: Explicitly pass 'openAs: epub'
    const epubBook = ePub(url, { openAs: 'epub' });
    this.epubBook = epubBook;

    // A failed open is announced ONLY through this event. epub.js swallows the
    // rejection into `openFailed` and never settles `book.ready`/`opened`, and
    // `rendition.display()` stays pending with it — so without this listener the
    // catch handlers below never run and a corrupt/unavailable EPUB sits on
    // "Opening book..." forever instead of reaching the failure state.
    epubBook.on('openFailed', () => {
      if (this.epubBook !== epubBook) return; // a newer attempt owns the reader
      this.failOpen();
    });

    // 2. Setup Rendition Immediately
    // Render into the padded page box: the margin preset is padding on
    // #epub-viewer, so epub.js should paginate into what is left of it.
    const page = this.elementRef.nativeElement.querySelector('#epub-page');
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
    this.restoreSavedFontSize();
    this.registerReaderThemes();
    this.applyFontSize();
    this.restoreSavedTypography();
    if (this.typography().fontFamily === 'libron') this.preloadLibron();

    // 3. Register Hooks
    rendition.hooks.content.register((contents: Contents) => {
      this.injectCustomStyles(contents);
      this.annotationManager?.registerContents(contents);
      this.registerContentsKeyboard(contents);
    });

    // Initialize the annotation manager BEFORE the first display so the
    // opening section receives the injected styles and fallback listeners.
    this.annotationManager = new EpubAnnotationManager(
      rendition,
      id,
      this.injector,
      () => this.noteCreated.emit(),
      () => this.commitFailed.emit(),
    );
    this.annotationManager.setHighlightMode(this.highlightMode());
    this.annotationManager.setHighlightColour(this.highlightColour());
    this.annotationManager.setOnSelectionCaptured((text, anchor) => {
      this.assistantSelection.set(text);
      this.selectionCaptured.emit(text);
      this.selectionAnchored.emit(anchor);
    });
    this.annotationManager.init();

    rendition.on('relocated', (location: any) => {
      this.currentCfi = location.start.cfi;
      this.assistantLocation.set(location.start.cfi);
      this.currentHref.set(location.start.href);
      this.spineIndex.set(typeof location.start.index === 'number' ? location.start.index : null);
      this.updateProgressState(location.start.cfi);
    });

    // 4. Process Book Metadata (Async)
    epubBook.ready
      .then(() => {
        if (epubBook.navigation) {
          const toc = this.mapTocItems(epubBook.navigation.toc);
          this.toc.set(toc);
        }

        // --- OPTIMIZATION START ---
        // Try to fetch locations from backend first
        this.booksService.getLocations(id).subscribe({
          next: (dto) => {
            // Cache HIT: Load saved locations
            if (dto.locations) {
              epubBook.locations.load(dto.locations);
              this.locationsReady.set(true);
              if (this.currentCfi) this.updateProgressState(this.currentCfi);
            }
          },
          error: () => {
            // Cache MISS: Generate locations (Expensive). The progress pill
            // shows a spine-based percentage while this runs, so a large book
            // is never left on "Calculating…" with no position at all
            // (issue #225 §1.4).
            epubBook.locations.generate(1000).then(() => {
              this.locationsReady.set(true);
              if (this.currentCfi) this.updateProgressState(this.currentCfi);

              // Save them for next time
              const json = epubBook.locations.save();
              if (json) {
                this.booksService.saveLocations(id, json).subscribe();
              }
            });
          },
        });
        // --- OPTIMIZATION END ---
      })
      .catch((err) => {
        console.error('Book metadata setup failed:', err);
        this.failOpen();
      });

    // 5. Display Book (Starts the stream/rendering). The saved position comes
    // from the Book the shell already holds — no second GET, whose late answer
    // used to lose the race against the first progress write.
    const restoreLocation = this.book()?.lastLocation ?? null;

    rendition
      .display()
      .then(async () => {
        this.loading.set(false);
        this.applyFontSize();

        this.notesService.list(id).subscribe({
          next: (notes) => this.annotationManager?.restoreHighlights(notes),
          error: (err) => console.error('Failed to load notes:', err),
        });

        // A grounded source is stronger than the user's ordinary saved reading
        // position for this navigation: the citation click explicitly asked to
        // open evidence from the exact indexed source revision.
        if (this.pendingGroundedSource) {
          const grounded = this.pendingGroundedSource;
          this.pendingGroundedSource = null;
          await this.applyGroundedSource(grounded);
          return;
        }

        if (!restoreLocation) return;

        await rendition.display(restoreLocation).catch((err: unknown) => {
          console.warn('Could not restore the saved reading position:', err);
        });
      })
      .catch((err) => {
        console.error('Failed to render book:', err);
        this.failOpen();
      })
      .finally(() => {
        if (!this.errorMessage()) this.unlockProgress();
      });
  }

  // --- Helpers ---

  private updateProgressState(cfi: string) {
    if (!this.epubBook) return;

    // Locations give the precise percentage. Until they exist — generation can
    // take a while on a large book — fall back to how far the current spine
    // section is through the spine: coarse, but real, and never a stuck
    // "Calculating…" with no position at all (issue #225 §1.3, §1.4).
    const percent =
      this.locationsReady() && this.epubBook.locations.length() > 0
        ? Math.floor(this.epubBook.locations.percentageFromCfi(cfi) * 100)
        : spinePercentFrom(this.spineIndex(), this.spineLength());

    this.progress.set({
      label: progressLabel(percent, this.activeChapterLabel()),
      percentage: percent,
    });

    // While a grounded navigation is still settling, relocated events describe
    // epub.js's reflow churn (it re-displays the view's stale location) rather
    // than where the reader has been grounded. Holding them back keeps the
    // transient position out of the saved progress; confirmGroundedNavigation
    // emits the verified target once protection ends.
    if (this.groundedNavigation) return;

    this.progressUpdater$.next({ location: cfi, percentage: percent });
  }

  /**
   * Opens the progress stream and reports where the reader actually is. Called
   * once the opening display (and the restore, when there is one) has settled,
   * so the opening section can never overwrite the saved position
   * (issue #225 §1.2).
   */
  private unlockProgress(): void {
    this.progressUnlocked = true;
    const cfi = this.getCurrentLocation() ?? this.currentCfi;
    if (cfi) this.updateProgressState(cfi);
  }

  /** Label of the TOC entry the displayed section belongs to, for the pill. */
  private activeChapterLabel(): string | null {
    const label = this.activeTocItem()?.label?.trim();
    if (!label) return null;
    return label.length > 32 ? `${label.slice(0, 31)}…` : label;
  }

  /**
   * Number of sections in the spine. The bundled 0.3.93 typings for `Spine` do
   * not declare `spineItems`, which is what the implementation actually holds.
   */
  private spineLength(): number | null {
    const spine = this.epubBook?.spine as unknown as { spineItems?: unknown[] } | undefined;
    return spine?.spineItems?.length ?? null;
  }

  /**
   * Page keys pressed while the book itself has focus. The contents live in an
   * iframe, so those events never reach the shell's document listener; the same
   * helpers decide the action on both sides, so the binding cannot drift.
   */
  private registerContentsKeyboard(contents: Contents): void {
    const doc = contents.document;
    if (this.keyboardDocuments.has(doc)) return;

    const onKeydown = (event: KeyboardEvent) => {
      if (
        !event.defaultPrevented &&
        (event.ctrlKey || event.metaKey) &&
        !event.altKey &&
        !event.shiftKey &&
        event.key.toLowerCase() === 'f'
      ) {
        event.preventDefault();
        this.searchRequested.emit();
        return;
      }

      if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return;
      if (isTypingTarget(event.target) || isInteractiveTarget(event.target)) return;

      const action = pageActionForKey(event);
      if (!action) return;

      if (action === 'next') this.next();
      else this.previous();
      event.preventDefault();
    };

    doc.addEventListener('keydown', onKeydown);
    this.keyboardDocuments.set(doc, () => doc.removeEventListener('keydown', onKeydown));
  }

  private mapTocItems(items: any[]): TocItem[] {
    return items.map((item) => ({
      label: item.label.trim(),
      target: item.href,
      children: item.subitems ? this.mapTocItems(item.subitems) : [],
    }));
  }

  private injectCustomStyles(contents: any) {
    // EPUB content documents must not need an external request just to honour
    // a reader setting. Libron is bundled with the app; Sans/Mono use
    // dependable local/system stacks.
    this.upsertTypographyStyle(contents.document);
  }

  /** Merge a patch into the typography, persist it, and repaint open sections. */
  setTypography(patch: Partial<EpubTypography>): void {
    const next = { ...this.typography(), ...patch };
    this.typography.set(next);
    if (next.fontFamily === 'libron') this.preloadLibron();
    try {
      // One preference for the whole reader, not per book — you should not have
      // to pick your typeface again for every new EPUB you open.
      localStorage.setItem(TYPOGRAPHY_STORAGE_KEY, JSON.stringify(next));
    } catch {
      // Private-mode storage can throw — the setting still applies for the session.
    }
    this.requestTypographyApply();
  }

  /**
   * Same leading-edge + trailing-coalesce shape as the text size: switching
   * typeface, line height and margins in quick succession repaints the open
   * sections once at the end instead of once per click.
   */
  private requestTypographyApply(): void {
    if (!this.typographyApplyTimer) {
      this.applyTypography();
    } else {
      clearTimeout(this.typographyApplyTimer);
    }
    this.typographyApplyTimer = setTimeout(() => {
      this.typographyApplyTimer = null;
      this.applyTypography();
    }, TYPOGRAPHY_APPLY_QUIET_MS);
  }

  private applyTypography(): void {
    const key = JSON.stringify(this.typography());
    if (key === this.appliedTypographyKey) return;
    this.appliedTypographyKey = key;
    this.applyTypographyToOpenContents();
    // The margin is padding on our own container; the binding updates in the
    // next change-detection pass, so measure the page after that.
    setTimeout(() => this.applyMarginInset(), 0);
  }

  resetTypography(): void {
    this.currentFontSize.set(100);
    this.requestFontSizeApply();
    this.setTypography({ ...DEFAULT_TYPOGRAPHY });
  }

  retryLoad(): void {
    this.loadBook(this.bookId());
  }

  leaveReader(): void {
    this.exitRequested.emit();
  }

  private failOpen(): void {
    this.errorMessage.set('The file may be damaged, unsupported, or temporarily unavailable.');
    this.loading.set(false);
  }

  /**
   * A stored typography preference, or null when there is none to read. Values
   * are validated against the presets, so a stale or hand-edited entry cannot
   * put the reader into an unsupported state.
   */
  private readStoredTypography(key: string): EpubTypography | null {
    try {
      const raw = localStorage.getItem(key);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as Partial<EpubTypography>;
      return {
        fontFamily: fontFamilyFromStored(parsed.fontFamily),
        lineHeight:
          typeof parsed.lineHeight === 'number' && EPUB_LINE_OPTIONS.includes(parsed.lineHeight)
            ? parsed.lineHeight
            : DEFAULT_TYPOGRAPHY.lineHeight,
        margin:
          parsed.margin === 'narrow' || parsed.margin === 'wide' ? parsed.margin : 'normal',
      };
    } catch {
      // Corrupt or unreadable storage — treat it as "nothing stored".
      return null;
    }
  }

  /**
   * Load the reader-wide typography preference.
   *
   * Earlier versions stored it per book (`nostos.epub-typography.<bookId>`); that
   * value is adopted once, for the book it belongs to, so a setting already
   * chosen is not lost when the preference becomes reader-wide.
   */
  private restoreSavedTypography(): void {
    const stored = this.readStoredTypography(TYPOGRAPHY_STORAGE_KEY);
    if (stored) {
      this.typography.set(stored);
      return;
    }

    const legacy = this.readStoredTypography(this.legacyTypographyKey());
    if (!legacy) return;
    this.typography.set(legacy);
    try {
      localStorage.setItem(TYPOGRAPHY_STORAGE_KEY, JSON.stringify(legacy));
    } catch {
      // Adopting in memory is enough — the next change persists it.
    }
  }

  private legacyTypographyKey(): string {
    return `nostos.epub-typography.${this.bookId()}`;
  }

  /** Rewrite the typography style element in every already-rendered section. */
  private applyTypographyToOpenContents(): void {
    try {
      const raw = this.rendition?.getContents?.();
      const contents = Array.isArray(raw) ? raw : raw ? [raw] : [];
      for (const c of contents) {
        if (c?.document) this.upsertTypographyStyle(c.document);
      }
    } catch {
      // Best-effort repaint — the content hook covers newly rendered sections.
    }
  }

  /**
   * The margin preset is padding on our own viewer (see the binding in the
   * template), so the rendition has to be told the page got smaller — epub.js
   * paginates to the box it is given. Nothing is written into the book.
   */
  private applyMarginInset(): void {
    const page = this.elementRef.nativeElement.querySelector('#epub-page') as HTMLElement | null;
    if (!page || !this.rendition) return;
    const { clientWidth, clientHeight } = page;
    // A page box that has not been laid out yet measures 0 — resizing the
    // rendition to 0 would collapse it, so wait for the next pass instead.
    if (clientWidth <= 0 || clientHeight <= 0) return;
    try {
      this.rendition.resize(clientWidth, clientHeight);
    } catch {
      // Not laid out yet — the ResizeObserver path will catch up.
    }
  }

  /**
   * Fetch the Libron files from the app document so they are already in the
   * HTTP cache when the first section paints — the section then renders in
   * Libron at once instead of re-laying-out when the font arrives. The faces
   * are loaded, never added to `document.fonts`: Libron is the book's
   * typeface, not an app UI font.
   */
  private preloadLibron(): void {
    if (this.libronPreloaded || typeof FontFace === 'undefined') return;
    this.libronPreloaded = true;
    const urls = libronFaceUrls(document.baseURI);
    LIBRON_FACES.forEach((face, i) => {
      new FontFace('Libron', `url("${urls[i]}") format("woff2")`, {
        style: face.style,
        weight: String(face.weight),
      })
        .load()
        .catch(() => {
          // A missing asset must not break reading — the serif fallback in
          // the font stack takes over.
        });
    });
  }

  private upsertTypographyStyle(doc: Document): void {
    try {
      if (!doc.getElementById(READER_FONTS_STYLE_ID)) {
        const fonts = doc.createElement('style');
        fonts.id = READER_FONTS_STYLE_ID;
        fonts.textContent = libronFontFaceCss(document.baseURI);
        doc.head.appendChild(fonts);
      }
      let style = doc.getElementById(TYPOGRAPHY_STYLE_ID);
      if (!style) {
        style = doc.createElement('style');
        style.id = TYPOGRAPHY_STYLE_ID;
        doc.head.appendChild(style);
      }
      style.textContent = typographyCss(this.typography());
    } catch {
      // A section mid-teardown has no head to write to — skip it.
    }
  }

  private registerReaderThemes() {
    const themes = this.rendition?.themes;
    if (!themes) return;

    registerNostosReaderThemes(themes);

    // Select the matching theme so the first section is rendered with it.
    this.selectReaderTheme(this.themeService.theme());
  }

  private selectReaderTheme(theme: Theme) {
    const themes = this.rendition?.themes;
    if (!themes) return;
    selectNostosReaderTheme(themes, this.renderedContents(), theme);
  }

  public deleteHighlight(cfiRange: string) {
    this.annotationManager?.removeHighlight(cfiRange);
  }

  removeHighlight(identifier: string): void {
    this.deleteHighlight(identifier);
  }

  commitHighlight(content = ''): void {
    this.annotationManager?.commitHighlight(content);
  }

  discardHighlight(): void {
    this.annotationManager?.discardHighlight();
  }

  ngOnDestroy(): void {
    this.unregisterAssistantContext?.();
    this.unregisterAssistantContext = null;
    this.cancelGroundedNavigation();
    this.resizeObserver?.disconnect();
    if (this.fontApplyTimer) clearTimeout(this.fontApplyTimer);
    if (this.typographyApplyTimer) clearTimeout(this.typographyApplyTimer);
    this.resizeSubject$.complete();
    this.progressUpdater$.complete();
    for (const cleanup of this.keyboardDocuments.values()) {
      cleanup();
    }
    this.keyboardDocuments.clear();
    this.clearSearch();
    this.searchCorpus = null;
    this.searchCorpusPromise = null;
    this.annotationManager?.destroy();
    this.annotationManager = null;
    if (this.epubBook) {
      this.epubBook.destroy();
    }
  }
}
