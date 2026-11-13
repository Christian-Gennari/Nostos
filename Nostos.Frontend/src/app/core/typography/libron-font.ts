/**
 * The bundled Libron faces (SIL OFL 1.1, see `public/fonts/libron/`), and the
 * `@font-face` CSS that makes them available. Only 400 and 700 exist; the
 * browser's weight matching maps every authored weight onto one of them, so
 * bold and italic text resolve to a real face, never a synthesised one.
 *
 * This lives in `core/` rather than with the EPUB reader because anything that
 * renders book text in its own document — the passage viewer included — needs
 * the same faces, and must not depend on reader internals to get them.
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
