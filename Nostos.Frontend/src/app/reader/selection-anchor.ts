// Selection helpers both page readers share. They live apart from either
// reader so that loading one format never loads the other's engine (#1320).

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
