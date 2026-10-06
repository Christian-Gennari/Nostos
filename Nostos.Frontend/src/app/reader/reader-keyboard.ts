/**
 * Page-key handling shared by the reader shell and the EPUB reader.
 *
 * The EPUB/PDF document is an iframe, so a key pressed while reading never
 * reaches the parent document's listeners. Both sides therefore use these two
 * helpers so the binding is identical wherever the focus happens to be.
 */

export type PageAction = 'next' | 'previous';

/**
 * The page action a key event asks for, or null when the key means nothing to
 * the reader. Left/right and PageUp/PageDown are the conventional pair; Space
 * is included because it is what most readers use to advance, with Shift+Space
 * going back.
 */
export function pageActionForKey(event: { key: string; shiftKey?: boolean }): PageAction | null {
  switch (event.key) {
    case 'ArrowLeft':
    case 'PageUp':
      return 'previous';
    case 'ArrowRight':
    case 'PageDown':
      return 'next';
    case ' ':
    case 'Spacebar':
      return event.shiftKey ? 'previous' : 'next';
    default:
      return null;
  }
}

/**
 * True when the event target is a text-entry surface, where a page key means
 * "move the caret" or "change the value" instead. Guards the quick-note
 * textarea, the PDF page input and any contenteditable field.
 */
export function isTypingTarget(target: EventTarget | null): boolean {
  const element = target as HTMLElement | null;
  if (!element || typeof element.tagName !== 'string') return false;
  const tag = element.tagName.toUpperCase();
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || element.isContentEditable === true;
}

/**
 * True when reader-level paging must leave the focused control alone. This is
 * broader than text entry: Space activates buttons/links/toggles natively and
 * must never also turn a page.
 */
export function isInteractiveTarget(
  target: EventTarget | null,
  focusableSurface?: string,
): boolean {
  // Avoid `instanceof Element`: EPUB events originate in an iframe realm, so
  // their elements are not instances of the parent window's Element constructor.
  const element = target as Element | null;
  if (!element || typeof element.closest !== 'function') return false;
  if (element.closest(
    'button, a[href], area[href], input, textarea, select, option, label, summary, audio, video, iframe, object, embed, [contenteditable="true"], [draggable="true"], [onclick], [role="button"], [role="link"], [role="switch"], [role="checkbox"], [role="menuitem"]',
  )) return true;
  // PDF.js makes its entire text layer a Tab stop. The adapter can identify
  // that reading surface; authored controls and nested Tab stops still win.
  const focusable = element.closest('[tabindex]:not([tabindex="-1"])');
  if (!focusable) return false;
  if (focusableSurface && focusable.matches(focusableSurface)) {
    return isInteractiveTarget(focusable.parentElement, focusableSurface);
  }
  return true;
}


/**
 * Neutral reading-surface actions for the immersive reader (#759).
 *
 * The shell is hidden while reading, so the document itself becomes the
 * control surface. Selection and authored interactive content always win.
 * Coarse pointers may use the outer edge zones for page turns; fine pointers
 * simply reveal/hide chrome wherever they click.
 */
export type ReaderSurfaceAction = 'previous' | 'toggle-chrome' | 'next';

export function surfaceActionForPoint(input: {
  target: EventTarget | null;
  /** Format-owned focusable reading surface, rather than an authored control. */
  focusableSurface?: string;
  selectedText?: string | null;
  clientX: number;
  width: number;
  coarsePointer: boolean;
  edgePaging: boolean;
}): ReaderSurfaceAction | null {
  if (input.selectedText?.trim() || isInteractiveTarget(input.target, input.focusableSurface)) return null;

  if (input.coarsePointer && input.edgePaging && input.width > 0) {
    const edge = Math.min(96, input.width * 0.24);
    if (input.clientX <= edge) return 'previous';
    if (input.clientX >= input.width - edge) return 'next';
  }

  return 'toggle-chrome';
}

/**
 * A deliberate horizontal swipe may turn a page on paged readers. It never
 * claims a gesture with an active text selection, interactive origin, long
 * press, or substantial vertical travel.
 */
export function swipePageAction(input: {
  target: EventTarget | null;
  /** Format-owned focusable reading surface, rather than an authored control. */
  focusableSurface?: string;
  selectedText?: string | null;
  startX: number;
  startY: number;
  endX: number;
  endY: number;
  durationMs: number;
}): PageAction | null {
  if (input.selectedText?.trim() || isInteractiveTarget(input.target, input.focusableSurface)) return null;
  if (input.durationMs > 800) return null;

  const dx = input.endX - input.startX;
  const dy = input.endY - input.startY;
  if (Math.abs(dx) < 56 || Math.abs(dx) <= Math.abs(dy) * 1.2) return null;

  return dx < 0 ? 'next' : 'previous';
}


/**
 * Browsers may synthesize a click after touchend, but not consistently after a
 * drag/swipe. Suppression therefore carries an expiry and the touch-end point
 * instead of a sticky "ignore the next click" boolean: a later genuine tap in
 * another place must never disappear just because no synthetic click arrived.
 */
export interface TouchClickSuppression {
  x: number;
  y: number;
  untilMs: number;
}

const TOUCH_MOVE_SUPPRESS_PX = 10;
const SYNTHETIC_CLICK_RADIUS_PX = 32;
const SYNTHETIC_CLICK_WINDOW_MS = 700;

export function touchClickSuppressionForMovement(input: {
  startX: number;
  startY: number;
  endX: number;
  endY: number;
  nowMs: number;
}): TouchClickSuppression | null {
  const dx = input.endX - input.startX;
  const dy = input.endY - input.startY;
  if (Math.hypot(dx, dy) < TOUCH_MOVE_SUPPRESS_PX) return null;
  return {
    x: input.endX,
    y: input.endY,
    untilMs: input.nowMs + SYNTHETIC_CLICK_WINDOW_MS,
  };
}

export function shouldSuppressTouchClick(
  suppression: TouchClickSuppression | null,
  clickX: number,
  clickY: number,
  nowMs: number,
): boolean {
  if (!suppression || nowMs > suppression.untilMs) return false;
  return Math.hypot(clickX - suppression.x, clickY - suppression.y) <= SYNTHETIC_CLICK_RADIUS_PX;
}
