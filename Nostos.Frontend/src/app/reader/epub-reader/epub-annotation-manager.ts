// Nostos.Frontend/src/app/reader/epub-reader/epub-annotation-manager.ts
import { Rendition, Contents } from 'epubjs';
import { signal, Injector } from '@angular/core';
import { NotesService } from '../../core/services/notes.service';
import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HighlightColour,
  noteHighlightColour,
  resolveHighlightFill,
} from '../highlight-colours';
import { Note, noteHasText } from '../../core/dtos/note.dtos';
import type { Theme } from '../../core/services/theme.service';
import { EpubTextMarks, TextMark, sectionOf, textMarksSupported } from './epub-text-marks';
import {
  HighlightHit,
  RectLike,
  anchorAroundHit,
  offsetAnchor,
  rectContains,
} from '../highlight-hit';

interface PendingEpubHighlight {
  cfiRange: string;
  selectedText: string;
  contents: Contents;
  temporaryAnnotation?: PendingHighlightAnnotation;
  /** The same preview when it is painted into the text (EpubTextMarks). */
  temporaryMark?: TextMark;
  /** Offered from a live touch selection, which is still on the page. */
  touch: boolean;
}

type SelectionInput = 'mouse' | 'touch';

/**
 * Shape of the annotation object returned at runtime by epub.js
 * `rendition.annotations.highlight(...)`. The bundled 0.3.93 typings declare
 * that method as `void`, so the returned object is carried as this local
 * shape and removed by object identity (never by re-derived cfiRange).
 */
interface PendingHighlightAnnotation {
  type: string;
  cfiRange: string;
  sectionIndex?: number;
  mark?: { element?: HTMLElement };
}

/**
 * `rendition.views()` returns epub.js's `Views` *collection*, not an array: it
 * exposes `all()`, `forEach()` and `get()`, and it is NOT iterable. Iterating
 * it with `for…of` throws `TypeError: views is not iterable` at runtime — and
 * because the spec stubbed it with a plain array, the suite stayed green while
 * every highlight save failed in the browser (issue #225 §1.1). `all()` is the
 * collection's own array accessor.
 */
interface EpubViews {
  all?: () => EpubView[];
}

interface EpubView {
  index?: number;
  pane?: { removeMark?: (mark: unknown) => void };
}

/**
 * Where a captured selection sits, in the reader page's viewport coordinates
 * (the iframe's own offset already applied), so the shell can anchor the
 * selection menu at the text (#650).
 */
export interface SelectionAnchor {
  top: number;
  bottom: number;
  left: number;
  right: number;
}

/**
 * Whether a contextmenu event came from a real mouse right-click. Touch
 * long-press also fires `contextmenu` (Android Chrome reports it as a
 * PointerEvent with pointerType "touch"); taking it over would fight the
 * phone's own selection menu (#16), so only a mouse on a fine-pointer device
 * qualifies. Exported for tests.
 */
export function isMouseContextMenu(event: Event, win: Window): boolean {
  const pointerType = (event as PointerEvent).pointerType;
  if (typeof pointerType === 'string' && pointerType !== '' && pointerType !== 'mouse') return false;
  const coarse = typeof win.matchMedia === 'function' && win.matchMedia('(pointer: coarse)').matches;
  return !coarse;
}

/**
 * Whether a mouseup completes a desktop mouse selection that should open the
 * selection menu at the text without a right-click (#657). Primary button
 * only, and only on a fine-pointer device: a phone's selection belongs to its
 * own handles and OS menu (#16), which emulated mouse events must not race.
 * Exported for tests.
 */
export function isDesktopMouseSelection(event: MouseEvent, win: Window): boolean {
  if (typeof event.button === 'number' && event.button !== 0) return false;
  const coarse = typeof win.matchMedia === 'function' && win.matchMedia('(pointer: coarse)').matches;
  return !coarse;
}

/**
 * `--color-highlight` from styles.css in its light rendering, used when the
 * token cannot be resolved (unit tests, or a document that is not yet styled).
 */
const DEFAULT_HIGHLIGHT_FILL = '#ffda00';

export class EpubAnnotationManager {
  public highlights = signal<string[]>([]);

  private notesService: NotesService;

  /**
   * Marks painted by the text itself, which follow every reflow. Null in a
   * browser without the CSS Custom Highlight API, where epub.js's overlay
   * (measured once, redrawn only on a frame resize) is the fallback.
   */
  private readonly textMarks: EpubTextMarks | null = textMarksSupported()
    ? new EpubTextMarks(() => this.renderedContents())
    : null;
  /** Saved highlights painted by textMarks, by location. */
  private readonly savedMarks = new Map<string, TextMark>();
  /**
   * Saved locations that carry a note. They are drawn with a dotted rule, and
   * a recolour must keep it.
   */
  private readonly notedHighlights = new Set<string>();
  /**
   * Saved locations resolved to live DOM Ranges, per section document, for the
   * hit-test. A Range follows the text through every reflow, so an entry stays
   * true for as long as its document is on screen.
   */
  private readonly hitRanges = new WeakMap<Document, Map<string, Range | null>>();
  private searchMark: TextMark | null = null;
  private landingMark: TextMark | null = null;
  private readAloudMark: TextMark | null = null;

  /**
   * The book's chosen pen (issue #208): the colour of the NEXT highlight made
   * here. A saved highlight is drawn in its own stored pen (issue #861).
   */
  private highlightColour: HighlightColour = DEFAULT_HIGHLIGHT_COLOUR;
  private pendingHighlight: PendingEpubHighlight | null = null;
  /** Ephemeral search underline; a distinct epub.js annotation type cannot collide with saved highlights. */
  private searchCfiRange: string | null = null;
  private landingCfiRange: string | null = null;
  private readAloudCfiRange: string | null = null;
  private lastCapturedKey: string | null = null;
  private readonly documentCleanups = new Map<Document, () => void>();
  /**
   * Documents in which a left-button mouse drag is in progress. epub.js emits
   * `selected` from a debounced selectionchange (~250 ms), so a reader who
   * pauses mid-drag used to be captured with a partial range and have the
   * drag cut short (#304 again, via the epub.js path). A desktop drag is
   * completed by mouseup instead; touch has no mouse state and is unaffected.
   */
  private readonly mouseDragDocuments = new Set<Document>();
  private selectedHandler: ((cfiRange: string, contents: Contents) => void) | null = null;
  private onSelectionCaptured?: (text: string, anchor: SelectionAnchor | null) => void;
  private onSelectionAbandoned?: () => void;
  /**
   * Whether the reader's last pointer was a finger or pen. A touch selection
   * stays native (handles, OS menu) and is offered as it settles; a mouse one
   * is taken over on release. Seeded from the device, then kept current per
   * pointerdown so a touchscreen laptop follows whichever input is in use.
   */
  private touchInput =
    typeof window !== 'undefined'
    && typeof window.matchMedia === 'function'
    && window.matchMedia('(pointer: coarse)').matches;
  private committing = false;

  constructor(
    private rendition: Rendition,
    private bookId: string,
    private injector: Injector,
    private onNoteCreated?: () => void,
    private onCommitFailed?: () => void,
  ) {
    this.notesService = this.injector.get(NotesService);
  }

  /**
   * Set the book's pen. It applies to the next highlight made; highlights
   * already saved keep the pen stored with them.
   */
  setHighlightColour(colour: HighlightColour): void {
    if (colour === this.highlightColour) return;
    this.highlightColour = colour;

    // An offered passage previews the pen it will be saved in. The fill is
    // baked into the mark when it is drawn, so a new pen is a redraw.
    const pending = this.pendingHighlight;
    if ((pending?.temporaryAnnotation || pending?.temporaryMark) && !this.committing) {
      this.removePendingAnnotation();
      this.drawPending(pending);
    }
  }

  private drawPending(pending: PendingEpubHighlight): void {
    if (this.textMarks) {
      pending.temporaryMark = this.textMarks.add(pending.cfiRange, {
        kind: 'pen',
        colour: this.highlightColour,
      });
    } else {
      pending.temporaryAnnotation = this.drawPendingAnnotation(pending.cfiRange);
    }
  }

  /** The page changed theme; painted marks are tinted to suit it. */
  setTheme(theme: Theme): void {
    this.textMarks?.setTheme(theme);
  }

  private renderedContents(): Contents[] {
    try {
      const raw = this.rendition.getContents?.() as unknown;
      if (Array.isArray(raw)) return raw as Contents[];
      return raw ? [raw as Contents] : [];
    } catch {
      return [];
    }
  }

  private drawPendingAnnotation(cfiRange: string): PendingHighlightAnnotation {
    return this.rendition.annotations.highlight(
      cfiRange,
      { nostosPending: true },
      undefined,
      'epubjs-hl-pending',
      { fill: this.highlightFill() },
    ) as unknown as PendingHighlightAnnotation;
  }

  setOnSelectionCaptured(callback: (text: string, anchor: SelectionAnchor | null) => void) {
    this.onSelectionCaptured = callback;
  }

  /** A tap on the page asked to drop the passage a touch selection offered. */
  setOnSelectionAbandoned(callback: () => void) {
    this.onSelectionAbandoned = callback;
  }

  public init() {
    this.selectedHandler = (cfiRange: string, contents: Contents) =>
      this.handleSelected(cfiRange, contents);
    this.rendition.on('selected', this.selectedHandler);
  }

  /**
   * Wires one epub.js Contents document: makes its text selectable and
   * registers the selection-completion listeners. Registered through
   * `rendition.hooks.content` so every newly rendered document is covered.
   */
  public registerContents(contents: Contents): void {
    const document = contents.document;
    if (this.documentCleanups.has(document)) {
      return;
    }
    this.injectSelectionStyles(contents);
    this.textMarks?.attach(contents);

    /**
     * Captures the current selection. A mouse capture takes the selection
     * over and anchors the menu at the text (#650, #657); a touch capture
     * leaves the native selection and its handles alone and follows it as it
     * changes. Returns whether a selection is now pending.
     */
    const captureSelection = (input: SelectionInput): boolean => {
      const selection = contents.window.getSelection();
      if (!selection || selection.rangeCount === 0 || selection.isCollapsed) {
        return false;
      }

      const range = selection.getRangeAt(0);
      const selectedText = selection.toString().trim();

      if (!selectedText) {
        return false;
      }

      const cfiRange = contents.cfiFromRange(range.cloneRange());
      this.capturePendingHighlight(cfiRange, selectedText, contents, input);
      return this.pendingHighlight !== null;
    };

    const onContextMenu = (event: Event) => {
      // Right-click on a selection opens the selection menu at the text
      // (#650). Since #657 mouseup usually captures first; this remains the
      // path for keyboard-extended selections. Mouse only: a touch long-press
      // keeps the platform's own behaviour (#16), and a right-click with
      // nothing selected keeps the native menu.
      if (!isMouseContextMenu(event, contents.window)) {
        return;
      }
      if (captureSelection('mouse')) {
        event.preventDefault();
      }
    };

    const onPointerDown = (event: PointerEvent) => {
      if (event.pointerType) this.touchInput = event.pointerType !== 'mouse';
    };

    // selectionchange fires repeatedly while a desktop drag is still growing.
    // Treating its first non-collapsed range as complete clears the browser
    // selection and cuts the drag short (issue #304). Mouseup is the first
    // reliable desktop completion signal.
    const onMouseDown = (event: MouseEvent) => {
      if (event.button === 0) this.mouseDragDocuments.add(document);
    };

    // A drag released outside the iframe can miss mouseup here; the next move
    // with no button held ends the drag state so it can never stick.
    const onMouseMove = (event: MouseEvent) => {
      if (event.buttons === 0) this.mouseDragDocuments.delete(document);
    };

    // Releasing a desktop mouse selection opens the selection menu straight
    // away (#657): no right-click needed. A plain click leaves a collapsed
    // selection and captures nothing, so links and page focus are unaffected.
    const onMouseUp = (event: MouseEvent) => {
      this.mouseDragDocuments.delete(document);
      // A tap's emulated mouseup belongs to touchend below; a non-primary
      // release belongs to the right-click path.
      if (this.touchInput || (typeof event.button === 'number' && event.button !== 0)) return;
      // A mouse on a touch-first device gets the docked, touch-style offer.
      const input = isDesktopMouseSelection(event, contents.window) ? 'mouse' : 'touch';
      requestAnimationFrame(() => captureSelection(input));
    };

    // Lifting the finger after a long-press is the first touch completion
    // signal; later handle drags fire no touch events and arrive through
    // epub.js's debounced `selected` instead (handleSelected).
    const onTouchEnd = () => {
      this.touchInput = true;
      requestAnimationFrame(() => captureSelection('touch'));
    };

    // A tap on the page collapses a touch selection. The docked bar has no
    // scrim to tap away on, so that tap is the way out; deferred so the
    // reader's own click handler still sees the passage as pending and does
    // not also turn the page.
    const onClick = () => {
      const pending = this.pendingHighlight;
      if (!pending?.touch || this.committing) return;
      const selection = contents.window.getSelection();
      if (selection && !selection.isCollapsed) return;
      setTimeout(() => {
        if (this.pendingHighlight === pending) this.onSelectionAbandoned?.();
      }, 0);
    };

    document.addEventListener('contextmenu', onContextMenu, {
      capture: true,
    });
    document.addEventListener('pointerdown', onPointerDown, { passive: true });
    document.addEventListener('mousedown', onMouseDown);
    document.addEventListener('mousemove', onMouseMove, { passive: true });
    document.addEventListener('mouseup', onMouseUp);
    document.addEventListener('touchend', onTouchEnd, {
      passive: true,
    });
    document.addEventListener('click', onClick);

    this.documentCleanups.set(document, () => {
      document.removeEventListener('contextmenu', onContextMenu, true);
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('mousedown', onMouseDown);
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
      document.removeEventListener('touchend', onTouchEnd);
      document.removeEventListener('click', onClick);
      this.mouseDragDocuments.delete(document);
    });
  }

  private injectSelectionStyles(contents: Contents) {
    // A book stylesheet that disables selection would make its text
    // impossible to highlight. No `.epubjs-hl*` fill rules belong here:
    // epub.js builds the highlight marks in a pane SVG it appends to the view
    // element in the PARENT document (`new Pane(this.iframe, this.element)`),
    // never inside this contents document. The colour is therefore passed to
    // epub.js as an explicit fill — see highlightFill().
    const style = contents.document.createElement('style');
    style.innerHTML = `
      body,
      body * {
        -webkit-user-select: text !important;
        user-select: text !important;
      }
    `;
    contents.document.head.appendChild(style);
  }

  /**
   * The highlight colour, read from the app's `--color-highlight` token.
   * epub.js defaults the SVG fill to the raw keyword `yellow`, which could not
   * follow the theme and disagreed with the PDF reader's
   * `var(--color-highlight)`. The pane SVG is in the parent document (see
   * injectSelectionStyles), where the token is readable, so it is resolved here
   * and handed to epub.js as an explicit fill.
   */
  private highlightFill(colour: HighlightColour = this.highlightColour): string {
    // The pen's own token, so a colour is defined in exactly one place. The
    // fallback is the themed default's light value, for unit tests and for a
    // document that is not yet styled.
    return resolveHighlightFill(colour, DEFAULT_HIGHLIGHT_FILL);
  }

  /** Adds a persisted highlight in the pen saved with it. */
  private addPersistedHighlight(cfiRange: string, colour: HighlightColour, noted: boolean): void {
    if (noted) this.notedHighlights.add(cfiRange);
    else this.notedHighlights.delete(cfiRange);
    if (this.textMarks) {
      this.textMarks.remove(this.savedMarks.get(cfiRange));
      this.savedMarks.set(cfiRange, this.textMarks.add(cfiRange, { kind: 'pen', colour, noted }));
      return;
    }
    this.rendition.annotations.add('highlight', cfiRange, {}, undefined, undefined, {
      fill: this.highlightFill(colour),
    });
  }

  /**
   * Paint the active search hit as an underline rather than an epub.js
   * "highlight". epub.js keys annotations by CFI + type, so using a second
   * highlight at the exact CFI of a saved Nostos highlight could overwrite the
   * stored annotation object. A search-only underline is visually distinct and
   * has its own key.
   */
  public showSearchHighlight(cfiRange: string): void {
    this.clearSearchHighlight();
    this.searchCfiRange = cfiRange;
    if (this.textMarks) {
      this.searchMark = this.textMarks.add(cfiRange, { kind: 'search' });
      return;
    }
    this.rendition.annotations.underline(
      cfiRange,
      { nostosSearch: true },
      undefined,
      'epubjs-search-current',
      {
        stroke: 'var(--color-accent)',
        'stroke-opacity': '0.9',
        'stroke-width': '2',
      },
    );
  }

  public clearSearchHighlight(): void {
    if (!this.searchCfiRange) return;
    if (this.textMarks) {
      this.textMarks.remove(this.searchMark);
      this.searchMark = null;
      this.searchCfiRange = null;
      return;
    }
    this.rendition.annotations.remove(this.searchCfiRange, 'underline');
    this.searchCfiRange = null;
  }

  /**
   * Underlines the sentence the reader landed on from another edition. It is
   * a line under the words, not a pen: nothing is saved, and it is separate
   * from the search mark so that neither clears the other.
   */
  public showLandingMark(cfiRange: string): void {
    this.clearLandingMark();
    this.landingCfiRange = cfiRange;
    if (this.textMarks) {
      this.landingMark = this.textMarks.add(cfiRange, { kind: 'landing' });
      return;
    }
    this.rendition.annotations.underline(
      cfiRange,
      { nostosLanding: true },
      undefined,
      // Drawn by the reader's stylesheet: epub.js gives the rule its own
      // black stroke, which attributes passed here cannot replace.
      'epubjs-landing-mark',
    );
  }

  public clearLandingMark(): void {
    if (!this.landingCfiRange) return;
    if (this.textMarks) {
      this.textMarks.remove(this.landingMark);
      this.landingMark = null;
      this.landingCfiRange = null;
      return;
    }
    this.rendition.annotations.remove(this.landingCfiRange, 'underline');
    this.landingCfiRange = null;
  }

  /**
   * Marks the sentence being read aloud. Like the landing mark it is drawn,
   * not saved. Both are epub.js underlines keyed by their range, so the
   * landing mark is cleared first: the two could otherwise share a key.
   */
  public showReadAloudMark(cfiRange: string): void {
    this.clearLandingMark();
    if (this.readAloudCfiRange === cfiRange) return;
    this.clearReadAloudMark();
    this.readAloudCfiRange = cfiRange;
    if (this.textMarks) {
      this.readAloudMark = this.textMarks.add(cfiRange, { kind: 'read-aloud' });
      return;
    }
    this.rendition.annotations.underline(
      cfiRange,
      { nostosReadAloud: true },
      undefined,
      'epubjs-read-aloud-mark',
    );
  }

  public clearReadAloudMark(): void {
    if (!this.readAloudCfiRange) return;
    if (this.textMarks) {
      this.textMarks.remove(this.readAloudMark);
      this.readAloudMark = null;
      this.readAloudCfiRange = null;
      return;
    }
    this.rendition.annotations.remove(this.readAloudCfiRange, 'underline');
    this.readAloudCfiRange = null;
  }

  /**
   * epub.js's `selected`: a debounced selectionchange. It is how a touch
   * selection is followed while its handles are dragged, which fires no touch
   * events. A mouse selection is completed by mouseup and a keyboard one by
   * right-click, so only touch input is captured here.
   */
  private handleSelected(cfiRange: string, contents: Contents): void {
    if (!this.touchInput || this.mouseDragDocuments.has(contents.document)) {
      return;
    }

    const text = contents.window.getSelection()?.toString().trim() ?? '';
    if (!text) {
      return;
    }

    this.capturePendingHighlight(cfiRange, text, contents, 'touch');
  }

  private capturePendingHighlight(
    cfiRange: string,
    selectedText: string,
    contents: Contents,
    input: SelectionInput,
  ): void {
    const key = `${cfiRange}\u0000${selectedText}`;

    if (this.committing || this.lastCapturedKey === key) {
      return;
    }
    if (this.pendingHighlight) {
      // A touch selection is still live: adjusting its handles replaces the
      // offered passage. A mouse capture already cleared its selection, so a
      // second one can only be a stray event.
      if (input !== 'touch' || !this.pendingHighlight.touch) return;
      this.removePendingAnnotation();
      this.pendingHighlight = null;
    }

    this.pendingHighlight = {
      cfiRange,
      selectedText,
      contents,
      touch: input === 'touch',
    };
    this.drawPending(this.pendingHighlight);
    this.lastCapturedKey = key;

    if (input === 'touch') {
      // The phone's handles and OS menu belong to the native selection (#16);
      // the docked bar sits clear of both.
      this.onSelectionCaptured?.(selectedText, null);
      return;
    }

    // Measured before the native selection is cleared, which empties it.
    const anchor = this.anchorForSelection(contents);
    contents.window.getSelection()?.removeAllRanges();
    this.onSelectionCaptured?.(selectedText, anchor);
  }

  /**
   * The live selection's box in the parent page's viewport: the range's rect
   * inside the iframe plus the iframe's own position. Null when it cannot be
   * measured (tests, a detached document); the shell then docks the menu.
   */
  private anchorForSelection(contents: Contents): SelectionAnchor | null {
    try {
      const selection = contents.window.getSelection();
      if (!selection || selection.rangeCount === 0) return null;
      const rect = selection.getRangeAt(0).getBoundingClientRect();
      if (!rect || (rect.width === 0 && rect.height === 0)) return null;

      const frame = contents.window.frameElement?.getBoundingClientRect();
      const dx = frame?.left ?? 0;
      const dy = frame?.top ?? 0;
      return {
        top: rect.top + dy,
        bottom: rect.bottom + dy,
        left: rect.left + dx,
        right: rect.right + dx,
      };
    } catch {
      return null;
    }
  }

  /**
   * Persists the note first; only on success swaps the temporary annotation
   * for the normal persisted one and reports success. On failure the pending
   * highlight and its temporary annotation are retained so the shell can keep
   * the confirmation bar open — the user never loses a difficult selection to
   * a transient request failure.
   */
  commitHighlight(content = '', pen?: HighlightColour): Promise<boolean> {
    const pending = this.pendingHighlight;
    if (!pending) {
      return Promise.resolve(false);
    }

    const snapshot: PendingEpubHighlight = {
      cfiRange: pending.cfiRange,
      selectedText: pending.selectedText,
      contents: pending.contents,
      temporaryAnnotation: pending.temporaryAnnotation,
      temporaryMark: pending.temporaryMark,
      touch: pending.touch,
    };
    // The pen in hand when Save was pressed is the one stored and drawn, even
    // if the reader picks another while the request is in flight.
    const colour = pen ?? this.highlightColour;

    this.committing = true;
    return new Promise<boolean>((resolve) => {
      this.notesService
        .create(this.bookId, {
          // Empty for a plain highlight; the typed text for "Add note" (#650).
          content,
          cfiRange: snapshot.cfiRange,
          selectedText: snapshot.selectedText,
          highlightColour: colour,
        })
        .subscribe({
          next: () => {
            // Persisted: swap the temporary annotation for the permanent one,
            // then report success so the shell closes the bar.
            this.committing = false;
            this.addPersistedHighlight(snapshot.cfiRange, colour, !!content.trim());
            this.removeAnnotation(snapshot.temporaryAnnotation);
            this.textMarks?.remove(snapshot.temporaryMark);
            // A touch selection is still live; left in place it would be
            // offered again at the next touch.
            snapshot.contents.window.getSelection()?.removeAllRanges();
            this.pendingHighlight = null;
            this.lastCapturedKey = null;
            this.highlights.update((current) => [...current, snapshot.cfiRange]);
            this.onNoteCreated?.();
            resolve(true);
          },
          error: () => {
            this.committing = false;
            // Failed: keep the pending highlight and its visual feedback.
            if (this.pendingHighlight?.cfiRange !== snapshot.cfiRange) {
              this.pendingHighlight = null;
              this.lastCapturedKey = null;
            }
            this.onCommitFailed?.();
            resolve(false);
          },
        });
    });
  }

  discardHighlight(): void {
    this.removePendingAnnotation();
    this.pendingHighlight?.contents.window.getSelection()?.removeAllRanges();
    this.pendingHighlight = null;
    this.lastCapturedKey = null;
  }

  private removePendingAnnotation(): void {
    const pending = this.pendingHighlight;
    this.removeAnnotation(pending?.temporaryAnnotation);
    this.textMarks?.remove(pending?.temporaryMark);
    if (pending) pending.temporaryMark = undefined;
  }

  /**
   * Removes the temporary annotation by object identity (its mark element on
   * the matching view's pane), never by cfiRange: `annotations.remove(cfi,
   * 'highlight')` is keyed by cfiRange and could remove a persisted annotation
   * at the same CFI.
   */
  private removeAnnotation(annotation?: PendingHighlightAnnotation): void {
    if (!annotation) {
      return;
    }

    // `all()` is the collection's array accessor; the collection itself is not
    // iterable, which is what broke every committed highlight (see EpubViews).
    const views = this.rendition.views() as unknown as EpubViews;
    const rendered = typeof views.all === 'function' ? views.all() : [];

    for (const view of rendered) {
      if (view.index !== annotation.sectionIndex) {
        continue;
      }
      const mark = annotation.mark;
      if (mark && typeof view.pane?.removeMark === 'function') {
        view.pane.removeMark(mark);
      }
    }

    annotation.mark = undefined;
  }

  public restoreHighlights(notes: Note[]) {
    notes.forEach((note) => {
      if (note.cfiRange) {
        this.addPersistedHighlight(note.cfiRange, noteHighlightColour(note), noteHasText(note));
        this.highlights.update((current) => [...current, note.cfiRange!]);
      }
    });
  }

  /**
   * Redraw one saved highlight in another pen, or with or without its note
   * rule. The fill is baked into the epub.js SVG when a mark is drawn, so a
   * redraw is a remove and a re-add; a location that was never drawn here is
   * left alone. `noted` omitted keeps the rule the mark already has.
   */
  public recolourHighlight(
    cfiRange: string,
    colour: HighlightColour,
    noted = this.notedHighlights.has(cfiRange),
  ): void {
    if (!this.highlights().includes(cfiRange)) return;
    if (!this.textMarks) this.rendition.annotations.remove(cfiRange, 'highlight');
    this.addPersistedHighlight(cfiRange, colour, noted);
  }

  /**
   * The saved highlights under a point in one section's document, in that
   * document's viewport coordinates. Hit-tested against the marks' own line
   * boxes: a painted Highlight is not an element and receives no events, and
   * epub.js's fallback overlay sits under the iframe. With `measure`, each hit
   * carries the box to open the highlight card at, in the parent page's
   * viewport; without it this is cheap enough to follow the mouse.
   */
  public highlightsAt(contents: Contents, x: number, y: number, measure = true): HighlightHit[] {
    const section = contents.sectionIndex;
    const hits: HighlightHit[] = [];
    for (const cfi of new Set(this.highlights())) {
      if (sectionOf(cfi) !== section) continue;
      const range = this.hitRange(contents, cfi);
      if (!range) continue;

      let rects: RectLike[];
      try {
        rects = Array.from(range.getClientRects?.() ?? []);
      } catch {
        continue;
      }
      const hit = rects.find((rect) => rect.right > rect.left && rectContains(rect, x, y));
      if (!hit) continue;

      hits.push({ identifier: cfi, anchor: measure ? this.frameAnchor(contents, rects, hit) : null });
    }
    return hits;
  }

  private hitRange(contents: Contents, cfi: string): Range | null {
    const doc = contents.document;
    let cache = this.hitRanges.get(doc);
    if (!cache) {
      cache = new Map();
      this.hitRanges.set(doc, cache);
    }
    if (!cache.has(cfi)) {
      let range: Range | null = null;
      try {
        const resolved = contents.range(cfi);
        range = resolved && !resolved.collapsed ? resolved : null;
      } catch {
        // A CFI this edition's markup cannot resolve cannot be hit either.
      }
      cache.set(cfi, range);
    }
    return cache.get(cfi) ?? null;
  }

  private frameAnchor(contents: Contents, rects: RectLike[], hit: RectLike) {
    let dx = 0;
    let dy = 0;
    try {
      const frame = contents.window.frameElement?.getBoundingClientRect();
      dx = frame?.left ?? 0;
      dy = frame?.top ?? 0;
    } catch {
      // A detached frame: the inner coordinates are the best measure there is.
    }
    return offsetAnchor(anchorAroundHit(rects, hit), dx, dy);
  }

  public removeHighlight(cfiRange: string) {
    this.notedHighlights.delete(cfiRange);
    if (this.textMarks) {
      this.textMarks.remove(this.savedMarks.get(cfiRange));
      this.savedMarks.delete(cfiRange);
    } else {
      this.rendition.annotations.remove(cfiRange, 'highlight');
    }
    this.highlights.update((current) => current.filter((cfi) => cfi !== cfiRange));
  }

  /**
   * Removes document listeners, the rendition `selected` listener and any
   * pending temporary annotation. Called before the rendition is destroyed.
   */
  public destroy(): void {
    this.discardHighlight();
    this.clearSearchHighlight();
    this.clearLandingMark();
    this.clearReadAloudMark();
    this.textMarks?.clear();
    this.savedMarks.clear();
    this.notedHighlights.clear();

    if (this.selectedHandler) {
      this.rendition.off('selected', this.selectedHandler);
      this.selectedHandler = null;
    }

    for (const cleanup of this.documentCleanups.values()) {
      cleanup();
    }
    this.documentCleanups.clear();
  }
}
