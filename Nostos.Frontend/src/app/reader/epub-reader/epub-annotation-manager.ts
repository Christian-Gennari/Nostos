// Nostos.Frontend/src/app/reader/epub-reader/epub-annotation-manager.ts
import { Rendition, Contents } from 'epubjs';
import { signal, Injector } from '@angular/core';
import { NotesService } from '../../core/services/notes.service';
import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HighlightColour,
  resolveHighlightFill,
} from '../highlight-colours';
import { Note } from '../../core/dtos/note.dtos';

interface PendingEpubHighlight {
  cfiRange: string;
  selectedText: string;
  contents: Contents;
  temporaryAnnotation?: PendingHighlightAnnotation;
}

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

  private highlightMode = false;
  /** The book's chosen pen (issue #208), used for every highlight drawn here. */
  private highlightColour: HighlightColour = DEFAULT_HIGHLIGHT_COLOUR;
  private pendingHighlight: PendingEpubHighlight | null = null;
  /** Ephemeral search underline; a distinct epub.js annotation type cannot collide with saved highlights. */
  private searchCfiRange: string | null = null;
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
   * Set the book's pen. Highlights already on the page keep the colour they were
   * drawn with until the book is re-rendered, because a fill is baked into the
   * epub.js SVG at draw time; the choice applies to what is drawn next.
   */
  setHighlightColour(colour: HighlightColour): void {
    this.highlightColour = colour;
  }

  setHighlightMode(enabled: boolean): void {
    this.highlightMode = enabled;

    const contents = this.rendition.getContents() as unknown as Contents[];
    for (const item of contents) {
      this.applyModeToContents(item);
    }

    if (!enabled) {
      this.discardHighlight();
    }
  }

  setOnSelectionCaptured(callback: (text: string, anchor: SelectionAnchor | null) => void) {
    this.onSelectionCaptured = callback;
  }

  public init() {
    this.selectedHandler = (cfiRange: string, contents: Contents) =>
      this.handleSelected(cfiRange, contents);
    this.rendition.on('selected', this.selectedHandler);
  }

  /**
   * Wires one epub.js Contents document: injects styles, registers the
   * mode-scoped callout suppression and selection-completion fallbacks, and
   * applies the current highlight mode. Registered through
   * `rendition.hooks.content` so every newly rendered document is covered.
   */
  public registerContents(contents: Contents): void {
    this.injectHighlightStyles(contents);

    const document = contents.document;
    if (this.documentCleanups.has(document)) {
      this.applyModeToContents(contents);
      return;
    }

    /**
     * Captures the current selection. Touch completion (touchend) only
     * captures while highlight mode is on; a desktop mouse selection (#657,
     * on mouseup) and a desktop right-click (#650) capture regardless of the
     * mode. Returns whether a selection was captured.
     */
    const captureSelection = (evenOutsideHighlightMode = false): boolean => {
      if ((!this.highlightMode && !evenOutsideHighlightMode) || this.pendingHighlight) {
        return false;
      }

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
      this.capturePendingHighlight(cfiRange, selectedText, contents);
      return this.pendingHighlight !== null;
    };

    const onContextMenu = (event: Event) => {
      if (this.highlightMode) {
        event.preventDefault();
      }

      // Right-click on a selection opens the selection menu at the text, with
      // or without highlight mode (#650). Since #657 mouseup usually captures
      // first; this remains the path for keyboard-extended selections. Mouse
      // only: a touch long-press keeps the platform's own behaviour, and a
      // right-click with nothing selected keeps the native menu.
      if (!isMouseContextMenu(event, contents.window)) {
        return;
      }
      if (captureSelection(true)) {
        event.preventDefault();
      }
    };

    // selectionchange fires repeatedly while a desktop drag is still growing.
    // Treating its first non-collapsed range as complete clears the browser
    // selection and cuts the drag short (issue #304). Mouseup is the first
    // reliable desktop completion signal; touch keeps its existing touchend
    // fallback for browsers where epub.js never emits `selected`.
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
      const desktop = isDesktopMouseSelection(event, contents.window);
      requestAnimationFrame(() => captureSelection(desktop));
    };

    const onTouchEnd = () => {
      requestAnimationFrame(() => captureSelection());
    };

    document.addEventListener('contextmenu', onContextMenu, {
      capture: true,
    });
    document.addEventListener('mousedown', onMouseDown);
    document.addEventListener('mousemove', onMouseMove, { passive: true });
    document.addEventListener('mouseup', onMouseUp);
    document.addEventListener('touchend', onTouchEnd, {
      passive: true,
    });

    this.documentCleanups.set(document, () => {
      document.removeEventListener('contextmenu', onContextMenu, true);
      document.removeEventListener('mousedown', onMouseDown);
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
      document.removeEventListener('touchend', onTouchEnd);
      this.mouseDragDocuments.delete(document);
    });

    this.applyModeToContents(contents);
  }

  public injectHighlightStyles(contents: Contents) {
    // Only the selection-mode rules belong here. The `.epubjs-hl*` fill rules
    // that used to live in this block were DEAD CODE: epub.js builds the
    // highlight marks in a pane SVG it appends to the view element in the
    // PARENT document (`new Pane(this.iframe, this.element)`), never inside
    // this contents document. The colour is therefore passed to epub.js as an
    // explicit fill — see highlightFill() — instead of being styled here.
    const style = contents.document.createElement('style');
    style.innerHTML = `
      body.nostos-highlight-mode,
      body.nostos-highlight-mode * {
        -webkit-touch-callout: none !important;
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
   * injectHighlightStyles), where the token is readable, so it is resolved here
   * and handed to epub.js as an explicit fill.
   */
  private highlightFill(): string {
    // The pen's own token, so a colour is defined in exactly one place. The
    // fallback is the themed default's light value, for unit tests and for a
    // document that is not yet styled.
    return resolveHighlightFill(this.highlightColour, DEFAULT_HIGHLIGHT_FILL);
  }

  /** Adds a persisted highlight with the reader's own colour. */
  private addPersistedHighlight(cfiRange: string): void {
    this.rendition.annotations.add('highlight', cfiRange, {}, undefined, undefined, {
      fill: this.highlightFill(),
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
    this.rendition.annotations.remove(this.searchCfiRange, 'underline');
    this.searchCfiRange = null;
  }

  /**
   * Standard epub.js path: `rendition.on('selected')`. Routes into the same
   * capture/deduplication pipeline as the iframe-level fallback.
   */
  private handleSelected(cfiRange: string, contents: Contents): void {
    if (!this.highlightMode) {
      return;
    }

    // Mid-drag: mouseup will capture the finished range.
    if (this.mouseDragDocuments.has(contents.document)) {
      return;
    }

    const text = contents.window.getSelection()?.toString().trim() ?? '';
    if (!text) {
      return;
    }

    this.capturePendingHighlight(cfiRange, text, contents);
  }

  /**
   * Central capture: derives the temporary annotation, stores the pending
   * highlight, clears the native selection and reports the capture.
   * Deduplicated against `pendingHighlight` and the last captured key so the
   * iframe fallback and epub.js `selected` never double-fire.
   */
  private capturePendingHighlight(
    cfiRange: string,
    selectedText: string,
    contents: Contents,
  ): void {
    const key = `${cfiRange}\u0000${selectedText}`;

    if (this.pendingHighlight || this.lastCapturedKey === key) {
      return;
    }

    const temporaryAnnotation = this.rendition.annotations.highlight(
      cfiRange,
      { nostosPending: true },
      undefined,
      'epubjs-hl-pending',
      { fill: this.highlightFill() },
    ) as unknown as PendingHighlightAnnotation;

    this.pendingHighlight = {
      cfiRange,
      selectedText,
      contents,
      temporaryAnnotation,
    };
    this.lastCapturedKey = key;

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
  commitHighlight(content = ''): Promise<boolean> {
    const pending = this.pendingHighlight;
    if (!pending) {
      return Promise.resolve(false);
    }

    const snapshot: PendingEpubHighlight = {
      cfiRange: pending.cfiRange,
      selectedText: pending.selectedText,
      contents: pending.contents,
      temporaryAnnotation: pending.temporaryAnnotation,
    };

    return new Promise<boolean>((resolve) => {
      this.notesService
        .create(this.bookId, {
          // Empty for a plain highlight; the typed text for "Add note" (#650).
          content,
          cfiRange: snapshot.cfiRange,
          selectedText: snapshot.selectedText,
        })
        .subscribe({
          next: () => {
            // Persisted: swap the temporary annotation for the permanent one,
            // then report success so the shell closes the bar.
            this.addPersistedHighlight(snapshot.cfiRange);
            this.removeAnnotation(snapshot.temporaryAnnotation);
            this.pendingHighlight = null;
            this.lastCapturedKey = null;
            this.highlights.update((current) => [...current, snapshot.cfiRange]);
            this.onNoteCreated?.();
            resolve(true);
          },
          error: () => {
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
    this.removeAnnotation(this.pendingHighlight?.temporaryAnnotation);
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
        this.addPersistedHighlight(note.cfiRange);
        this.highlights.update((current) => [...current, note.cfiRange!]);
      }
    });
  }

  public removeHighlight(cfiRange: string) {
    this.rendition.annotations.remove(cfiRange, 'highlight');
    this.highlights.update((current) => current.filter((cfi) => cfi !== cfiRange));
  }

  /**
   * Removes document listeners, the rendition `selected` listener and any
   * pending temporary annotation. Called before the rendition is destroyed.
   */
  public destroy(): void {
    this.discardHighlight();
    this.clearSearchHighlight();

    if (this.selectedHandler) {
      this.rendition.off('selected', this.selectedHandler);
      this.selectedHandler = null;
    }

    for (const cleanup of this.documentCleanups.values()) {
      cleanup();
    }
    this.documentCleanups.clear();
  }

  private applyModeToContents(contents: Contents): void {
    contents.document.body?.classList.toggle('nostos-highlight-mode', this.highlightMode);
  }
}
