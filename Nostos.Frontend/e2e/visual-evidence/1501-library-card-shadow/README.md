# #1501 — Library cover hover shadow, hard-cut along the top edge (Light)

Before/after evidence for issue #1501, captured from the production build
(`npm run build`) served by `npm run preview -- serve`. All images are at
DPR 2; the `*-card-top-zoom` crops are 1:1 device pixels (not downscaled).

## Root cause (measured, not inferred)

- The Library's sticky glass toolbar (`.toolbar`) has
  `background: var(--glass-bg-strong)` (85% page colour, `backdrop-filter: blur(14px)`)
  and `z-index: 5`. Its bottom edge is exactly the top of the scroll content.
- The first grid row starts 8px below it (`.library-right-side > .container
  { padding-top: 0.5rem }`), and `.book-card:hover .cover-wrapper` lifts the
  card another 4px, leaving 4px of clear paper above the card.
- The Light `--cover-shadow-hover` halo layer (`0 0 16px -2px
  var(--color-accent-faint)`) reached ~12px above the card's top. The part
  above the toolbar edge was painted under the toolbar's 85% glass, so the
  shadow ended at the toolbar's bottom edge in a straight line: measured a
  **12-level luminance step (250 → 238) in one device pixel** there. Dark
  themes have the same geometry, but the glass and the shadow are both
  near-black, so the cut is invisible (0-level step).
- `book-detail.component.css`'s cover shadow (`0 2px 4px` + `0 12px 30px -4px`)
  was checked on a light ground: top reach 11px, left reach 11.75px — symmetric,
  no clipping. The detail cover is unaffected.

## Fix

The Light hover halo is cast downward by its own upward reach:
`0 0 16px -2px` → `0 8px 16px -2px`. The side aura is unchanged (the offset
only moves the layer vertically), the top fade now finishes inside the 4px
clearance, and the layout, the dark/mist/study/sepia tokens and the book
detail cover stay untouched.

## Measurements

First row at 1280×900, hovered, Light (luminance across the toolbar edge):

| | before | after |
| --- | --- | --- |
| step at the edge | **12** (250 → 238) | **3** (251 → 248) |
| side aura at y=300 (x = 300/310/320/328) | 251/250/245/229 | identical |
| max pixel delta in the viewport | — | 18 (the halo's deeper bottom) |

Pixel-identical before/after (full-viewport diff):

- `library-rest-1280x900-light.png` — no layout shift, resting shadow unchanged.
- `library-rest-390x844-light.png` — phone layout unchanged.
- `library-hover-1280x900-dark.png` — the dark hover shadow is byte-identical.
- `to-read-rest-1280x900-light.png` — the reading-list shelf is unchanged.
- `book-detail-1440x900-light.png`, `book-detail-1440x900-dark.png` — the
  detail cover is unchanged.

## Files

- `before-*` / `after-*` — full viewport shots, 1280×900 (light and dark) and
  1440×900 (light); the 390×844 pair is the phone rest state.
- `*-card-top-zoom.png` — 1:1 crop of the first card's top edge; the toolbar
  edge is the hard line in the `before-*` images.

## Commands

```bash
cd Nostos.Frontend
npm run check
npm test
npm run build
NOSTOS_PREVIEW_API=http://127.0.0.1:5099 npm run preview -- shots --out <dir> --paths /library --themes light,dark
```
