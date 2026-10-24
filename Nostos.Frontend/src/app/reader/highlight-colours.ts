/**
 * The reader's highlighter pens (issue #208).
 *
 * A colour is identified by NAME, not by hex, at every layer above CSS: the hex
 * lives in `styles.css` as `--highlight-<name>` and the theme decides nothing
 * about it (the reading surface is paper in every theme, so the pens are
 * theme-invariant). Storing a hex instead would freeze one theme's value into
 * whatever the user happened to be reading in.
 */
export type HighlightColour = 'sage' | 'amber' | 'blue' | 'rose';

export interface HighlightColourOption {
  id: HighlightColour;
  /** Shown to screen readers; the swatch itself carries the colour. */
  label: string;
}

export const HIGHLIGHT_COLOURS: readonly HighlightColourOption[] = [
  { id: 'sage', label: 'Sage' },
  { id: 'amber', label: 'Amber' },
  { id: 'blue', label: 'Blue' },
  { id: 'rose', label: 'Rose' },
];

/** The colour a book starts on, and the fallback for an unknown stored value. */
export const DEFAULT_HIGHLIGHT_COLOUR: HighlightColour = 'sage';

/** `--highlight-<name>`, the CSS custom property holding that pen's fill. */
export function highlightFillVar(colour: HighlightColour): string {
  return `--highlight-${colour}`;
}

/** The same token name as a `var(...)` reference, for inline styles. */
export function highlightFillRef(colour: HighlightColour): string {
  return `var(${highlightFillVar(colour)})`;
}

/**
 * Falls back to the default rather than throwing: this reads a value out of
 * localStorage or a saved note, which can be anything the user, an older build
 * or an imported archive left there.
 */
export function asHighlightColour(value: unknown): HighlightColour {
  return HIGHLIGHT_COLOURS.some((option) => option.id === value)
    ? (value as HighlightColour)
    : DEFAULT_HIGHLIGHT_COLOUR;
}

/**
 * The pen a saved highlight is drawn in.
 *
 * The note's own `highlightColour` decides. A PDF highlight made before that
 * field existed kept its pen inside the JSON location payload in `cfiRange`,
 * so that is the fallback (it also covers a library restored from an archive
 * older than the field). A highlight with neither is drawn in the default
 * pen: an EPUB highlight never recorded one, and an import has none to bring.
 */
export function noteHighlightColour(note: {
  highlightColour?: string | null;
  cfiRange?: string | null;
}): HighlightColour {
  if (note.highlightColour) return asHighlightColour(note.highlightColour);

  const location = note.cfiRange?.trim();
  if (location?.startsWith('{')) {
    try {
      return asHighlightColour((JSON.parse(location) as { colour?: unknown }).colour);
    } catch {
      // Not a PDF location payload after all.
    }
  }
  return DEFAULT_HIGHLIGHT_COLOUR;
}

/**
 * The pen a quotation is shown in, or null when it is not known: a note with
 * no quotation, or a payload that does not carry the pen at all (a kept
 * writing source). Null keeps a quote's rail neutral instead of guessing.
 */
export function quotePen(note: {
  selectedText?: string | null;
  highlightColour?: string | null;
  cfiRange?: string | null;
}): HighlightColour | null {
  if (!note.selectedText || note.highlightColour === undefined) return null;
  return noteHighlightColour(note);
}

/** localStorage key for a book's chosen pen. */
export function highlightColourKey(bookId: string): string {
  return `nostos.highlight.${bookId}`;
}

/** The book's remembered pen, or the default. Never throws on storage errors. */
export function readHighlightColour(bookId: string): HighlightColour {
  try {
    return asHighlightColour(localStorage.getItem(highlightColourKey(bookId)));
  } catch {
    return DEFAULT_HIGHLIGHT_COLOUR;
  }
}

/** Remember the book's pen. Storage being unavailable is not worth failing over. */
export function writeHighlightColour(bookId: string, colour: HighlightColour): void {
  try {
    localStorage.setItem(highlightColourKey(bookId), colour);
  } catch {
    // The choice still applies for this session.
  }
}

/**
 * The fill to draw, resolved from the stylesheet so a highlighter pen is defined
 * in exactly one place. Returns `fallback` when the document is unavailable or
 * unstyled (unit tests), rather than handing a raw `var()` to a canvas or SVG.
 */
export function resolveHighlightFill(
  colour: HighlightColour,
  fallback: string,
  root: HTMLElement | null = typeof document === 'undefined'
    ? null
    : document.documentElement,
): string {
  try {
    const value = root
      ? getComputedStyle(root).getPropertyValue(highlightFillVar(colour)).trim()
      : '';
    if (value) return value;
  } catch {
    // Unreadable styles — fall through to the fallback.
  }
  return fallback;
}
