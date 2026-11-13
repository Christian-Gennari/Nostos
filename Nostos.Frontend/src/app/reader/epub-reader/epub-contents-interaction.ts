import type { Contents } from 'epubjs';

import type { HighlightHit } from '../highlight-hit';
import {
  isInteractiveTarget,
  isTypingTarget,
  pageActionForKey,
  shouldSuppressTouchClick,
  surfaceActionForPoint,
  swipePageAction,
  touchClickSuppressionForMovement,
  type TouchClickSuppression,
} from '../reader-keyboard';

/**
 * Grace period before handing the page back after a touch that did not turn a
 * page. A touch capture is deferred (rAF, then epub.js's debounced
 * selectionchange), and Android cancels the touch as soon as a long press
 * hands it to the native selection handles (#1254). Releasing on the event
 * itself dropped the page hold exactly as the handle drag began.
 */
export const PAGE_HOLD_RELEASE_MS = 500;

export interface EpubContentsInteractionHost {
  pageElement(): HTMLElement | null;
  isRtl(): boolean;
  assistantSelection(): string | null;
  highlightsAt(contents: Contents, x: number, y: number, record?: boolean): HighlightHit[];
  next(): void;
  previous(): void;
  onSearchRequested(): void;
  onSurfaceInteracted(): void;
  onHighlightActivated(hits: HighlightHit[]): void;
}

/**
 * Manages iframe `Document` keyboard/pointer/touch listeners and the Android
 * selection page-hold scroll lock (#759, #1254) for an EPUB rendition.
 */
export class EpubContentsInteractionController {
  /** Keydown listeners registered inside each iframe's contents document. */
  readonly keyboardDocuments = new Map<Document, () => void>();

  /** Tap/swipe listeners registered in each epub.js contents document (#759). */
  readonly interactionDocuments = new Map<Document, () => void>();

  /**
   * epub.js's page position (#1254). A paginated rendition lays a whole section
   * out inside one wide iframe and turns pages by setting `scrollLeft` on the
   * stage box it creates inside `#epub-page` (`.epub-container`: `overflow:
   * hidden`, but still scrollable by the browser and by script). While a
   * selection is live the offset is held and any scroll put back, so the page
   * under the text cannot move.
   */
  private pageScroller: HTMLElement | null = null;
  private pinnedPageOffset: { left: number; top: number } | null = null;
  private pageHoldReleaseTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(private readonly host: EpubContentsInteractionHost) {}

  /**
   * Page keys pressed while the book itself has focus. The contents live in an
   * iframe, so those events never reach the shell's document listener; the same
   * helpers decide the action on both sides, so the binding cannot drift.
   */
  registerKeyboard(contents: Contents): void {
    const doc = contents.document;
    if (!doc || this.keyboardDocuments.has(doc)) return;

    const onKeydown = (event: KeyboardEvent) => {
      if (
        !event.defaultPrevented &&
        (event.ctrlKey || event.metaKey) &&
        !event.altKey &&
        !event.shiftKey &&
        event.key.toLowerCase() === 'f'
      ) {
        event.preventDefault();
        this.host.onSearchRequested();
        return;
      }

      if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return;
      if (isTypingTarget(event.target) || isInteractiveTarget(event.target)) return;

      const action = pageActionForKey(event, this.host.isRtl());
      if (!action) return;

      if (action === 'next') this.host.next();
      else this.host.previous();
      event.preventDefault();
    };

    doc.addEventListener('keydown', onKeydown);
    this.keyboardDocuments.set(doc, () => doc.removeEventListener('keydown', onKeydown));
  }

  clearKeyboardListeners(): void {
    for (const cleanup of this.keyboardDocuments.values()) cleanup();
    this.keyboardDocuments.clear();
  }

  /**
   * The EPUB iframe is the immersive reader's interaction surface (#759).
   * Selection and authored controls win; on a coarse pointer the outer zones
   * turn pages and the centre reveals chrome. A deliberate horizontal swipe
   * also turns pages. None of these listeners prevent the browser's native
   * touch/selection behavior.
   */
  registerInteraction(contents: Contents): void {
    const doc = contents.document;
    if (!doc || this.interactionDocuments.has(doc)) return;

    let touchStart:
      | { x: number; y: number; at: number; target: EventTarget | null }
      | null = null;
    let clickSuppression: TouchClickSuppression | null = null;

    // Native selection is cleared once Nostos captures a contextual mark. The
    // reader-owned assistantSelection keeps that pending passage visible to the
    // gesture arbiter so an edge tap/swipe cannot turn the page underneath the
    // selection action surface.
    const selectedText = () =>
      doc.getSelection?.()?.toString().trim() || this.host.assistantSelection()?.trim() || '';
    const coarsePointer = () =>
      typeof window !== 'undefined' &&
      typeof window.matchMedia === 'function' &&
      window.matchMedia('(pointer: coarse)').matches;

    const onClick = (event: MouseEvent) => {
      const now = Date.now();
      if (shouldSuppressTouchClick(clickSuppression, event.clientX, event.clientY, now)) {
        clickSuppression = null;
        return;
      }
      if (clickSuppression && now > clickSuppression.untilMs) clickSuppression = null;

      // A saved highlight takes the tap before paging or chrome: opening what
      // was written about a passage is why the reader touched it. A pending
      // selection and the book's own links still come first.
      if (!selectedText() && !isInteractiveTarget(event.target)) {
        const hits = this.host.highlightsAt(contents, event.clientX, event.clientY);
        if (hits.length > 0) {
          this.host.onHighlightActivated(hits);
          return;
        }
      }

      const width = doc.defaultView?.innerWidth ?? doc.documentElement.clientWidth;
      const action = surfaceActionForPoint({
        target: event.target,
        selectedText: selectedText(),
        clientX: event.clientX,
        width,
        coarsePointer: coarsePointer(),
        edgePaging: true,
        rtl: this.host.isRtl(),
      });

      if (action === 'next') this.host.next();
      else if (action === 'previous') this.host.previous();
      else if (action === 'toggle-chrome') this.host.onSurfaceInteracted();
    };

    const onTouchStart = (event: TouchEvent) => {
      if (event.touches.length !== 1) {
        touchStart = null;
        return;
      }
      const touch = event.touches[0];
      touchStart = {
        x: touch.clientX,
        y: touch.clientY,
        at: Date.now(),
        target: event.target,
      };
      // A long-press creates the selection (and the browser reveals it) before
      // the finger lifts, and its handles fire no touch events afterwards.
      // Holding here covers the whole gesture (#1254).
      this.holdPageForSelection();
    };

    const onTouchEnd = (event: TouchEvent) => {
      const start = touchStart;
      touchStart = null;
      if (!start || event.changedTouches.length !== 1) return;

      const touch = event.changedTouches[0];
      const now = Date.now();
      clickSuppression = touchClickSuppressionForMovement({
        startX: start.x,
        startY: start.y,
        endX: touch.clientX,
        endY: touch.clientY,
        nowMs: now,
      });
      const action = swipePageAction({
        target: start.target,
        selectedText: selectedText(),
        startX: start.x,
        startY: start.y,
        endX: touch.clientX,
        endY: touch.clientY,
        durationMs: now - start.at,
        rtl: this.host.isRtl(),
      });

      // A tap with no selection hands the page back after the deferred capture
      // has had its chance; a live selection keeps it held for the handle
      // drags that follow (and a page turn releases it in beginNavigation).
      if (!action) {
        this.schedulePageHoldRelease();
        return;
      }
      if (action === 'next') this.host.next();
      else this.host.previous();
    };

    const onTouchCancel = () => {
      touchStart = null;
      clickSuppression = null;
      // Android cancels the touch when a long press hands the gesture to the
      // native selection handles. Do not release here: the selection and its
      // reveal arrive after this event, and dropping the hold now is what let
      // the handle drag move the page (#1254).
      this.schedulePageHoldRelease();
    };

    // Desktop selection: the browser reveals the endpoint during the drag,
    // before mouseup. On release the annotation manager captures the selection
    // (and clears it), so a plain click is the only case with none to keep.
    const onMouseDown = (event: MouseEvent) => {
      if (event.button === 0) this.holdPageForSelection();
    };

    const onMouseUp = () => {
      if (!selectedText()) this.releasePageHold();
    };

    // A mouse over a saved highlight shows it can be opened. Painted marks are
    // not elements and cannot carry a cursor of their own, so the hit-test
    // follows the pointer, once per frame at most.
    let hoverFrame = 0;
    let hoverPoint: { x: number; y: number } | null = null;
    const onPointerMove = (event: PointerEvent) => {
      if (event.pointerType !== 'mouse') return;
      hoverPoint = event.buttons === 0 ? { x: event.clientX, y: event.clientY } : null;
      if (hoverFrame) return;
      hoverFrame = requestAnimationFrame(() => {
        hoverFrame = 0;
        const point = hoverPoint;
        const over =
          !!point &&
          !selectedText() &&
          this.host.highlightsAt(contents, point.x, point.y, false).length > 0;
        if (doc.body) doc.body.style.cursor = over ? 'pointer' : '';
      });
    };

    doc.addEventListener('click', onClick);
    doc.addEventListener('pointermove', onPointerMove, { passive: true });
    doc.addEventListener('touchstart', onTouchStart, { passive: true });
    doc.addEventListener('touchend', onTouchEnd, { passive: true });
    doc.addEventListener('touchcancel', onTouchCancel, { passive: true });
    doc.addEventListener('mousedown', onMouseDown);
    doc.addEventListener('mouseup', onMouseUp);

    this.interactionDocuments.set(doc, () => {
      doc.removeEventListener('click', onClick);
      doc.removeEventListener('pointermove', onPointerMove);
      if (hoverFrame) cancelAnimationFrame(hoverFrame);
      doc.removeEventListener('touchstart', onTouchStart);
      doc.removeEventListener('touchend', onTouchEnd);
      doc.removeEventListener('touchcancel', onTouchCancel);
      doc.removeEventListener('mousedown', onMouseDown);
      doc.removeEventListener('mouseup', onMouseUp);
    });
  }

  clearInteractionListeners(): void {
    for (const cleanup of this.interactionDocuments.values()) cleanup();
    this.interactionDocuments.clear();
  }

  /**
   * Hold the page under a live selection. Idempotent: extending a selection
   * (or a capture confirming one already being dragged) keeps the original
   * offset.
   */
  holdPageForSelection(): void {
    this.cancelPageHoldRelease();
    const scroller = this.resolvePageScroller();
    if (!scroller || this.pinnedPageOffset) return;

    this.pinnedPageOffset = { left: scroller.scrollLeft, top: scroller.scrollTop };
    scroller.addEventListener('scroll', this.restorePinnedPageOffset, { passive: true });
  }

  /**
   * Give the page back only once it is clear the touch did not become a
   * selection.
   */
  schedulePageHoldRelease(): void {
    this.cancelPageHoldRelease();
    this.pageHoldReleaseTimer = setTimeout(() => {
      this.pageHoldReleaseTimer = null;
      if (this.hasLiveSelection()) return;
      this.releasePageHold();
    }, PAGE_HOLD_RELEASE_MS);
  }

  cancelPageHoldRelease(): void {
    if (this.pageHoldReleaseTimer === null) return;
    clearTimeout(this.pageHoldReleaseTimer);
    this.pageHoldReleaseTimer = null;
  }

  /**
   * Hand the page back: navigation, dismissal, save, re-layout, teardown.
   * Restores the held offset once more unless the geometry is being replaced.
   */
  releasePageHold(restore = true): void {
    this.cancelPageHoldRelease();
    if (!this.pinnedPageOffset) return;
    this.pageScroller?.removeEventListener('scroll', this.restorePinnedPageOffset);
    if (restore) this.restorePinnedPageOffset();
    this.pinnedPageOffset = null;
  }

  resetScroller(): void {
    this.pageScroller = null;
  }

  destroy(): void {
    this.clearKeyboardListeners();
    this.clearInteractionListeners();
    this.releasePageHold(false);
    this.resetScroller();
  }

  private resolvePageScroller(): HTMLElement | null {
    const cached = this.pageScroller;
    if (cached?.isConnected && cached.parentElement?.id === 'epub-page') return cached;

    const page = this.host.pageElement();
    const scroller = (page?.querySelector(':scope > .epub-container') as HTMLElement | null) ?? null;
    this.pageScroller = scroller;
    return scroller;
  }

  private hasLiveSelection(): boolean {
    if (this.host.assistantSelection()?.trim()) return true;
    for (const doc of this.interactionDocuments.keys()) {
      const selection = doc.getSelection?.();
      if (selection && !selection.isCollapsed && selection.toString().trim()) return true;
    }
    return false;
  }

  private readonly restorePinnedPageOffset = (): void => {
    const held = this.pinnedPageOffset;
    const scroller = this.pageScroller;
    if (!held || !scroller?.isConnected) return;
    if (scroller.scrollLeft !== held.left) scroller.scrollLeft = held.left;
    if (scroller.scrollTop !== held.top) scroller.scrollTop = held.top;
  };
}
