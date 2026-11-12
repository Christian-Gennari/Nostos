# #1531 — Brain list and detail panes on wide screens

Before/after evidence for the wide Brain: the list becomes an index and the
selected note opens beside it at 1280px and wider.

## Files

- `before-*` — `origin/main` (`78fbeae2`), production build, the Brain as it
  was: the list only (right half empty) at each width, plus the note's own page.
- `after-*` — this branch, production build: `?note=` opens the note in the
  pane; the phone keeps the note's full page.
- The 33 PNGs cover 1280, 1440 and 1920 at light and dark, sidebar open and
  closed (the `-sidebar-open` / `-sidebar-closed` suffix), the empty pane, the
  `?note=` deep link, and the 390px phone at light and dark.

## How they were taken

Both builds ran against the same seeded SelfHosted instance: two physical books
with six notes across three shared topics, and one writing citing the open note.
The before build was served through `scripts/preview.mjs` with its API proxied
to that instance; the after build was served by it directly. Each shot is a
1440×900 (or 390×844) viewport at device scale factor 1, with the theme and the
Library sidebar preference set in `localStorage` before load. Every page was
checked for page errors and sideways scroll; none were reported.

The Cloud bundle is the same frontend and the change reads no account-specific
data, so the SelfHosted shots stand for both deployments.
