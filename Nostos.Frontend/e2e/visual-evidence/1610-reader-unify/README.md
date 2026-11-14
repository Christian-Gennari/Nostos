# #1610 — shared EPUB/PDF reader logic: before/after pixels

This change moves code, it does not restyle anything: no template and no
stylesheet in the reader was touched. The captures confirm that.

- **Before:** `origin/main` at `cf5e9532`, built from
  `Nostos.Frontend/` (`npm run build`), served from its own `dist/` through a
  static+`/api` proxy.
- **After:** this branch (`refactor/reader-unify-1610`), built the same way and
  served by the same throwaway backend.
- Same seeded data in both runs: one EPUB (`e2e/assets/reader-margins.epub`)
  with one saved highlight, and one generated 8-page, text-layer PDF with one
  saved highlight. Same backend, same books re-seeded per run (the header shows
  the title, so titles have to match; leftover copies are deleted first).
- States: reader idle (chrome hidden), toolbar shown (click the reading surface
  at x = centre, y = 12), a saved highlight opened (its card), and search
  results (`Bistritz` in the EPUB, `lantern` in the PDF).
- Widths: 1280×800 and 390×844 (2× device scale). Light and dark.

Comparison: `pngjs`, every pixel of every pair, a difference is any channel
delta above 2/255 (0–1 is anti-aliasing noise).

## Result: 32 pairs, 30 identical, 2 with a single pixel

| Capture | Differing pixels |
| --- | --- |
| epub-1280-light-idle | 0 |
| epub-1280-light-toolbar | 0 |
| epub-1280-light-highlight | 1 |
| epub-1280-light-search | 0 |
| epub-1280-dark-idle | 0 |
| epub-1280-dark-toolbar | 0 |
| epub-1280-dark-highlight | 1 |
| epub-1280-dark-search | 0 |
| epub-390-light-idle | 0 |
| epub-390-light-toolbar | 0 |
| epub-390-light-highlight | 0 |
| epub-390-light-search | 0 |
| epub-390-dark-idle | 0 |
| epub-390-dark-toolbar | 0 |
| epub-390-dark-highlight | 0 |
| epub-390-dark-search | 0 |
| pdf-1280-light-idle | 0 |
| pdf-1280-light-toolbar | 0 |
| pdf-1280-light-highlight | 0 |
| pdf-1280-light-search | 0 |
| pdf-1280-dark-idle | 0 |
| pdf-1280-dark-toolbar | 0 |
| pdf-1280-dark-highlight | 0 |
| pdf-1280-dark-search | 0 |
| pdf-390-light-idle | 0 |
| pdf-390-light-toolbar | 0 |
| pdf-390-light-highlight | 0 |
| pdf-390-light-search | 0 |
| pdf-390-dark-idle | 0 |
| pdf-390-dark-toolbar | 0 |
| pdf-390-dark-highlight | 0 |
| pdf-390-dark-search | 0 |

The two single-pixel pairs are the same pixel — `(99, 385)`, the anti-aliased
left edge of the word "were" in the EPUB page — a subpixel text-rasterisation
difference inside the same glyph, not a layout or colour change. Both themes
show it at the same coordinate because it is the same word on the same line.

The shots below are the After set, committed at 1:1.
