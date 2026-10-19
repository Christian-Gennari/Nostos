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
| `Nostos.Frontend/e2e/visual-evidence/` | Committed screenshot and geometry evidence |

## The standard design-baseline matrix

Each surface is captured at each viewport in both **light** and **dark**. That
is 6 surfaces × 2 viewports × 2 themes = 24 PNGs, plus
`painted-values.json`.

| Surface | Desktop | Mobile |
| --- | --- | --- |
| Reader (audio route) | 1440×900, light + dark | 390×844, light + dark |
| Library | 1440×900, light + dark | 390×844, light + dark |
| Brain | 1440×900, light + dark | 390×844, light + dark |
| Writing Studio | 1440×900, light + dark | 390×844, light + dark |
| Settings | 1440×900, light + dark | 390×844, light + dark |
| Home | 1440×900, light + dark | 390×844, light + dark |

These captures use device scale factor 1.

## Switching themes

The app offers **Settings → General → Colour theme**: Follow system (the
default) or one of five themes, Light, Sepia, Mist, Writer's Study and Dark
(issue #1074). The reader's View settings and the Studio's page settings show
the same choice as a row of swatches. The theme service stores the choice under
`localStorage['nostos.theme']` (`system`, a theme name, or nothing, which means
Follow system), and Follow system's day and night themes under
`nostos.theme.day` (Light or Sepia) and `nostos.theme.night` (Mist, Writer's
Study or Dark).

On the document root, every dark theme sets `data-theme="dark"`; light ones
remove it and use the stylesheet's `:root` values. Sepia, Mist and Writer's
Study also set `data-palette="<name>"`, whose block in `styles.css` layers over
its mode's values.

For a deterministic browser check, choose a theme in Settings. In the browser
console, the equivalent is:

```js
localStorage.setItem('nostos.theme', 'sepia'); // light, sepia, mist, study, dark or system
location.reload();
```

In a Playwright spec, set the stored value before navigating so the app applies
it during startup:

```ts
await page.addInitScript(() => {
  localStorage.setItem('nostos.theme', 'dark'); // or light, sepia, mist, study
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
- [ ] If a visual change intentionally moves the pixel baseline, the updated PNGs and `painted-values.json` are included.
- [ ] `npm run check`, applicable tests/build checks, and `git diff --check` are reported accurately.
