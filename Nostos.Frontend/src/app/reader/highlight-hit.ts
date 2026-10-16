// Nostos.Frontend/src/app/reader/highlight-hit.ts
import type { SelectionAnchor } from './epub-reader/epub-annotation-manager';

/**
 * A saved highlight under a click or tap. `identifier` is the same one the
 * reader's `removeHighlight` takes: the CFI for an EPUB, the note id for a PDF.
 * `anchor` is where the mark sits in the page's viewport, so the shell can open
 * the highlight card at the text; null when it could not be measured.
 */
export interface HighlightHit {
  identifier: string;
  anchor: SelectionAnchor | null;
}

/** The parts of a DOMRect the hit-test reads. */
export interface RectLike {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

/**
 * How far outside a mark's line box a tap still counts as on it, in CSS px. A
 * finger is wider than the gap between two lines; a mouse is not, but a pixel
 * or two of slack keeps a click on a highlight's very edge from falling
 * through to the page.
 */
export const HIGHLIGHT_HIT_SLOP = 2;

export function rectContains(rect: RectLike, x: number, y: number, slop = HIGHLIGHT_HIT_SLOP): boolean {
  return (
    x >= rect.left - slop &&
    x <= rect.right + slop &&
    y >= rect.top - slop &&
    y <= rect.bottom + slop
  );
}

/**
 * The box to open the highlight card at: the line that was hit, grown by every
 * line of the same mark that shares its column. A highlight that runs over a
 * column or page break has line boxes in two places; their union would span
 * the whole page and push the card off the text, so only the lines connected
 * to the hit one (overlapping it horizontally, directly or through another
 * line) are kept.
 */
export function anchorAroundHit(rects: readonly RectLike[], hit: RectLike): SelectionAnchor {
  const box = { top: hit.top, bottom: hit.bottom, left: hit.left, right: hit.right };
  const rest = rects.filter((rect) => rect !== hit && rect.right > rect.left);

  let grew = true;
  while (grew) {
    grew = false;
    for (let index = rest.length - 1; index >= 0; index--) {
      const rect = rest[index];
      if (rect.right < box.left || rect.left > box.right) continue;
      box.top = Math.min(box.top, rect.top);
      box.bottom = Math.max(box.bottom, rect.bottom);
      box.left = Math.min(box.left, rect.left);
      box.right = Math.max(box.right, rect.right);
      rest.splice(index, 1);
      grew = true;
    }
  }
  return box;
}

/** Shift a box measured inside a frame into the parent page's viewport. */
export function offsetAnchor(anchor: SelectionAnchor, dx: number, dy: number): SelectionAnchor {
  return {
    top: anchor.top + dy,
    bottom: anchor.bottom + dy,
    left: anchor.left + dx,
    right: anchor.right + dx,
  };
}
