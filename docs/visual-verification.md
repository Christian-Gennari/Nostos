# Visual Verification — Standing PR Gate

For a PR that changes rendered UI, verify the affected surfaces in a real
browser and attach before/after evidence. This follows the visual-change rule
in `AGENTS.md`. Inspect screenshots at 1:1; downscaled images can hide layout
and contrast defects.

## Capture tools

| Tool | What it covers |
| --- | --- |
| `Nostos.Frontend/scripts/capture-baseline.mjs` | The committed design baseline: six surfaces, two viewports, both themes |
| `Nostos.Frontend/scripts/check-pixels.mjs` | Compares a fresh baseline capture with `Nostos.Frontend/e2e/visual-evidence/design-baseline/` |
| `Nostos.Frontend/e2e/visual-regression.spec.ts` | 18 named surface/state cases with geometry assertions; seven reader cases need a real library |
| `Nostos.Frontend/e2e/book-detail-visual.spec.ts` | Two book-detail captures; needs a book with cover art |
| `Nostos.Frontend/e2e/support/visual-capture.ts` | Capture contexts, artifact paths, and geometry checks |
| `Nostos.Frontend/e2e/visual-evidence/` | Committed screenshot and geometry evidence |

The spec-based Playwright scenarios and the design baseline are separate
capture sets. The specs are not parameterized as a light/dark cross-product;
`capture-baseline.mjs` provides that coverage.

## The standard design-baseline matrix

Each surface is captured at each viewport in both **light** and **dark**. That
is 6 surfaces × 2 viewports × 2 themes = 24 PNGs, plus
`painted-values.json`.

| Surface | Desktop | Mobile |
| --- | --- | --- |
| Reader (audio route) | 1440×900, light + dark | 390×844, light + dark |
| Library | 1440×900, light + dark | 390×844, light + dark |
| Second Brain | 1440×900, light + dark | 390×844, light + dark |
| Writing Studio | 1440×900, light + dark | 390×844, light + dark |
| Settings | 1440×900, light + dark | 390×844, light + dark |
| Home | 1440×900, light + dark | 390×844, light + dark |

These captures use device scale factor 1. The separate visual-regression
scenarios also capture audio at 844×390 (phone landscape). The default
Playwright projects use 1280×800 for desktop and 390×844 at device scale
factor 2 for mobile; the visual-capture helper creates its own contexts at the
sizes listed above.

## Switching themes

The app offers **Settings → Appearance → Colour theme → Light/Dark**. The
theme service stores the selection under `localStorage['nostos.theme']`. Dark
applies `data-theme="dark"` to the document root; light removes that attribute
and uses the stylesheet's `:root` values. With no saved choice, the app follows
the operating-system preference.

For a deterministic browser check, choose Light or Dark in Settings. In the
browser console, the equivalent is:

```js
localStorage.setItem('nostos.theme', 'dark'); // or 'light'
location.reload();
```

In a Playwright spec, set the stored value before navigating so the app applies
it during startup:

```ts
await page.addInitScript(() => {
  localStorage.setItem('nostos.theme', 'dark'); // use 'light' for light mode
});
await page.goto(url);
```

`capture-baseline.mjs` sets the same storage key itself. With no filter it captures both themes; `--theme dark` or `--theme light` limits a run to one theme.

## The committed pixel baseline

The baseline is in
`Nostos.Frontend/e2e/visual-evidence/design-baseline/`. It currently contains
the 24 matrix PNGs and `painted-values.json`.

Run the capture against an already-running app. The script defaults to
`http://127.0.0.1:5214`; pass `--port` when the app uses another port. It does
not start the app.

In an `agent-worktree`, check its generated `.agent/env.sh` for the assigned
ports and point the capture at the already-running server that serves the build
under test.

```sh
cd Nostos.Frontend
npm run capture:baseline -- --out /tmp/after
npm run check:pixels -- /tmp/after
```

The checker compares pixels with its configured per-channel tolerance and
declared flake regions, and compares the painted-value sweep. It is not a
byte-for-byte comparison of every pixel. To intentionally update the committed
baseline, omit `--out` so the capture writes to
`e2e/visual-evidence/design-baseline/`; include those updated artifacts with
the UI change.

## How to run Playwright captures

From `Nostos.Frontend/`:

```sh
npm run e2e
npm run e2e -- visual-regression.spec.ts
VISUAL_QA_LIBRARY_URL=https://your-library.example npm run e2e -- visual-regression.spec.ts
VISUAL_QA_LIBRARY_URL=https://your-library.example npm run e2e -- book-detail-visual.spec.ts
```

The default Playwright setup builds the backend and frontend, then launches an
isolated fixture with a temporary database. The fixture chooses a free local
port. The configured desktop project uses Chromium at 1280×800; the mobile
project uses Chromium at 390×844 with touch emulation and device scale factor 2.

The first command runs the configured suite. The focused command runs the
visual-regression spec: 11 fixture-served cases and seven real-library reader
cases. The book-detail spec adds two real-library cases. Reader captures need
matching EPUB, PDF, and audio books at `VISUAL_QA_LIBRARY_URL`; book-detail
captures need a book with cover art. That origin must serve the build under test.

The repository has EPUB fixtures under `Nostos.Frontend/e2e/assets/`, including
`tiny.epub` and `reader-margins.epub`. The visual-regression reader cases do
not load those fixtures: they look up books at `VISUAL_QA_LIBRARY_URL`. When
the URL is unset, or a required book is absent, those cases skip with a reason.

### Reader surfaces

The seven reader scenarios cover two EPUB viewports, two PDF viewports, and
three audio layouts. The EPUB scenario's `epub-iframe-light` assertion checks
the light normalization; it is not dark-theme coverage. When reviewing reader
changes, inspect both themes in the real browser or explicitly initialize the
Playwright page with the stored theme as shown above.

### Library

The geometry checks include `library-no-progress-combobox`, which rejects a
non-sort `<select>` in the toolbar, and `library-six-sidebar-filters`, which
checks for All Books, Not Started, In Progress, Favorites, Finished, and
Unsorted. The latter marks a recognized legacy-label state as skipped; read
the report instead of treating a skip as a pass.

### Book detail

The two `book-detail-visual.spec.ts` cases use 1440×900 and 390×844 viewports.
They require a real book with cover art and report checks for the hero and its
fade.

## Automated geometry checks

The visual scenarios report geometry checks alongside their screenshots. The current checks cover:

| Surface | Checks |
| --- | --- |
| Writing Studio | Zen viewport fill, hidden chrome, balanced gutters |
| Library | Toolbar control type, sidebar filter labels, sidebar rail geometry |
| Second Brain | Empty state, layout overflow, map geometry, and arrival animation |
| EPUB/PDF reader | Light EPUB iframe normalization and PDF final-page toolbar clearance |
| Audio reader | Composition at desktop, portrait phone, and phone landscape sizes |
| Book detail | Hero layout and fade |

A check report records pass/fail or a documented skip, with metrics where the
check provides them. A skip means the case's documented data dependency was
unavailable; it does not establish that the surface passed.

## Fixed rendering invariants

Light and dark are both supported app themes. The EPUB reader registers light
and dark publisher-CSS normalization rules, and the PDF page edge follows the
active theme. The light values in
`Nostos.Frontend/e2e/support/visual-capture.ts` belong to its light-specific
EPUB assertion; they do not describe the app's only rendering.

## Vision review

For each changed surface, inspect the real browser at relevant desktop and
mobile sizes in both themes. Check that content, controls, text, focus states,
and overlays remain legible and unclipped, and that the page has no unintended
horizontal overflow or overlap. Review the before/after evidence at 1:1 and
identify the theme and viewport for each capture. Do not count skipped cases
as reviewed.

## PR checklist

- [ ] Changed UI states were checked in a real browser in light and dark at relevant viewports.
- [ ] Before/after evidence is attached and was reviewed at 1:1.
- [ ] Relevant Playwright checks were run; any data-dependent skips are identified.
- [ ] If a visual change intentionally moves the pixel baseline, the updated PNGs and `painted-values.json` are included.
- [ ] `npm run check`, applicable tests/build checks, and `git diff --check` are reported accurately.
