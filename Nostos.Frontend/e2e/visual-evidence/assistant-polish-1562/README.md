# Ask Nostos surface polish (#1562) — before / after

Captured from the production build against the Playwright fixture
(`e2e/support/launch-fixture.mjs launch`), with a seeded library and the
assistant status and turn stream stubbed. No model was configured or called.
All images are at device scale 1 and were checked at 1:1.

| Surface | Files |
| --- | --- |
| Empty state, compact | `empty-compact-desktop-{light,dark}.png`, `empty-compact-phone-{light,dark}.png` |
| Empty state, expanded | `empty-expanded-desktop-{light,dark}.png` (phone has no expanded mode) |
| Answer with citations and grouped sources | `answer-citations-desktop-{light,dark}.png`, `answer-citations-phone-{light,dark}.png` |
| Applied changeset card | `changeset-desktop-{light,dark}.png`, `changeset-phone-{light,dark}.png` |

- **before**: `origin/feat/assistant-capabilities-1559` at `cc2309a8` (PR #1578 head).
- **after**: this branch (`fix/assistant-surface-polish-1562`).

`measurements.json` records the computed radii, box geometry and rendered marker
HTML for both phases.

## What the measurements show

- Compact empty state is unchanged: panel `408 × 494` at `(516, 322)`, title at
  `(535, 400)` in both phases, light and dark, desktop and phone.
- The expanded empty state now uses a centred `760 px` measure: its box is
  `(336, 134, 760 × 589)` against `(309, 134, 814 × 208)` before, and the title
  sits at `y = 288` instead of `y = 140` in the `792 px` card.
- Radii: composer `16 px → 8 px` (`--radius-field`), user bubble
  `16/16/4/16 → 14/14/3/14` (`--radius-dialog`/`--radius-sm`), changeset card
  `8 px → 8 px` via `--radius-action`, details block `6 px` via `--radius-lg`,
  source pills `999 px → --radius-pill`. No raw `px` radius is left in either
  stylesheet.
- Grouped source row now reads `1–2 · The Magic Mountain · 2 passages`; the
  ungrouped row still reads `3`.
- The phone reply's first marker stays with `to.` (`to.\u202F1`) instead of
  wrapping alone onto the next line.

## Reproduce

From `Nostos.Frontend/`:

```bash
node e2e/support/launch-fixture.mjs launch
node e2e/support/ask-nostos-polish-shots.mjs before   # or after
node e2e/support/launch-fixture.mjs kill
```

`ASK_NOSTOS_SHOTS_OUT` overrides the output directory, so one script can run
in a worktree of the base branch while writing next to the PR's branch.
