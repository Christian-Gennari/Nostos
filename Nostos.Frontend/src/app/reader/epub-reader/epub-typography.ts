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
 * Margin presets, as a percentage of the available spread (capped at 80rem). These are OUTER
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

/**
 * The bundled Libron faces (SIL OFL 1.1, see `public/fonts/libron/`). Only 400
 * and 700 exist; the browser's weight matching maps every authored weight onto
 * one of them, so bold and italic text resolve to a real face, never a
 * synthesised one.
 */
export const LIBRON_FACES: { file: string; style: 'normal' | 'italic'; weight: 400 | 700 }[] = [
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
export function libronFaceUrls(baseUri: string): string[] {
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
 * Inline elements that carry running text. Converted books often wrap every
 * run in one, e.g. `<p><span class="S-DefaultFontStyle">…</span></p>` with
 * `.S-DefaultFontStyle{font-family:"Times New Roman"}`, so a typeface that only
 * reached the blocks above would stop at the span. Code-like elements and
 * anything inside them keep their fonts, as do glyph-only elements tagged by
 * {@link markGlyphElements}.
 */
const INLINE_TEXT_TAGS = [
  'span',
  'a',
  'em',
  'i',
  'strong',
  'b',
  'cite',
  'small',
  'big',
  'sup',
  'sub',
  'abbr',
  'q',
  'dfn',
  'mark',
  'u',
  's',
  'font',
];

export const GLYPH_ATTRIBUTE = 'data-nostos-glyph';

const INLINE_TEXT_SELECTOR =
  `body :is(${INLINE_TEXT_TAGS.join(',')})` +
  `:not([${GLYPH_ATTRIBUTE}],:is(pre,code,kbd,samp,tt,svg,math) *)`;

/** Text made only of whitespace and Private Use Area code points, where icon fonts put their glyphs. */
const GLYPH_ONLY_TEXT = /^[\s\p{Co}]*$/u;

/**
 * Tag inline elements that hold no readable text — only icon-font glyphs, or
 * nothing at all (an icon drawn by `::before`) — so a chosen reading typeface
 * leaves their publisher font alone. A section's markup does not change after
 * it renders, so one pass per contents document is enough.
 */
export function markGlyphElements(doc: Document): void {
  for (const el of Array.from(doc.body?.querySelectorAll(INLINE_TEXT_TAGS.join(',')) ?? [])) {
    if (GLYPH_ONLY_TEXT.test(el.textContent ?? '')) el.setAttribute(GLYPH_ATTRIBUTE, '');
  }
}

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
 * `rendition.themes.fontSize()`, and epub-text-size.ts carries it to text the
 * publisher sized absolutely.
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
    `${READING_FONT_SELECTOR},${INLINE_TEXT_SELECTOR}` +
    `{font-family:${FONT_STACKS[t.fontFamily]} !important;}` +
    lineHeight
  );
}
