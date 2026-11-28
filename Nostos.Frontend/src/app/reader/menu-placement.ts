import type { SelectionAnchor } from './selection-anchor';

/**
 * Viewport placement of a menu anchored at text; null docks the surface.
 * `maxHeight` caps the menu to the room it was given, so it scrolls inside
 * instead of running under the reader's header or toolbar (#1829).
 */
export type MenuPlacement = {
  left: number;
  width: number;
  top: number | null;
  bottom: number | null;
  maxHeight?: number | null;
};

/** The vertical band of the viewport the menu may use: the visible reading area. */
export interface MenuArea {
  top: number;
  bottom: number;
}

const GAP = 8;
const EDGE = 8;
/** Room for the note editor when it opens: below gets it when it fits. */
const RESERVE = 240;
/** Less than this beside the text and the menu floats over the text instead. */
const MIN_ROOM = 160;

/**
 * Where a menu anchored at text sits (#650). It opens below the selection when
 * there is room, else above; it grows away from the text (top-anchored below,
 * bottom-anchored above) so neither the collapsed menu nor the note editor
 * covers the selection, and it is clamped horizontally to the viewport.
 *
 * Vertically it stays inside `area`, the visible reading area. The menu lives
 * in the reading area's stacking context, so anything outside it (the header
 * above, the toolbar below) paints over it (#1829). Room is measured from the
 * area's edges, the height is capped to that room, and when neither side has
 * enough the menu floats over the text at the foot of the area.
 */
export function placeMenuAtAnchor(
  anchor: SelectionAnchor,
  viewport: { width: number; height: number },
  area: MenuArea,
  maxWidth = 360,
): MenuPlacement {
  const vw = viewport.width;
  const vh = viewport.height;
  const width = Math.min(maxWidth, vw - EDGE * 2);
  const centre = (anchor.left + anchor.right) / 2;
  const left = Math.min(Math.max(centre - width / 2, EDGE), vw - width - EDGE);

  const ceiling = Math.max(area.top, 0) + EDGE;
  const floor = Math.min(area.bottom, vh) - EDGE;
  const belowTop = Math.max(anchor.bottom + GAP, ceiling);
  const aboveBottom = Math.min(anchor.top - GAP, floor);
  const roomBelow = floor - belowTop;
  const roomAbove = aboveBottom - ceiling;

  const below = roomBelow >= RESERVE || roomBelow >= roomAbove;
  const room = below ? roomBelow : roomAbove;
  if (room < MIN_ROOM) {
    // The selection fills the page: float over its foot, inside the area.
    return { left, width, top: null, bottom: vh - floor, maxHeight: Math.max(floor - ceiling, 0) };
  }
  if (below) return { left, width, top: belowTop, bottom: null, maxHeight: roomBelow };
  return { left, width, top: null, bottom: vh - aboveBottom, maxHeight: roomAbove };
}
