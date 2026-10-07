# Visual Verification — Standing PR Gate

Every PR that changes rendered UI (reader, writing studio, library, layout,
or any component visible to the user) must pass the visual-verification
protocol below **before merge**. This is the mechanical, executable form of the
mandatory protocol from the UI defect-remediation plan (expert section 4).

The app ships exactly **one light rendering** — the theme system was removed.
The visual matrix is therefore fixed-light: 18 images, no theme
parameterization, no theme-toggle interaction, and the EPUB/PDF reader checks
are hardcoded **fixed rendering invariants** (see below).

The harness lives in `Nostos.Frontend/e2e/`:

| File | Role |
| --- | --- |
| `visual-regression.spec.ts` | The 18-image fixed-light matrix: parameterized `capture(surface, viewport, state)` -> PNG + geometry JSON |
| `support/visual-capture.ts` | Reusable capture/geometry helpers (fixed light invariants, viewport contexts, artifact paths, checks) |
| `visual-evidence/*.png` | Committed evidence artifacts (exact protocol filenames) |
| `visual-evidence/*.json` | Per-capture geometry report: checks, metrics, pass/skip/fail |

## The 18-image matrix

Viewports are **exactly** `1440x900` (desktop), `390x844` (mobile) and
`844x390` (phone landscape, audio only — a 334px-tall area cannot hold the
player's column composition, so it is the one surface with a second mobile
geometry and its own contract). PNGs are captured at `deviceScaleFactor: 1` so
artifact pixels are exact.

| # | Artifact | Surface | Viewport | State |
| --- | --- | --- | --- | --- |
| 1 | `epub-light-desktop.png` | EPUB reader | 1440x900 | fixed light, initial |
| 2 | `epub-light-mobile.png` | EPUB reader | 390x844 | fixed light, initial |
| 3 | `pdf-light-desktop.png` | PDF reader | 1440x900 | final page bottom |
| 4 | `pdf-light-mobile-bottom.png` | PDF reader | 390x844 | final page bottom |
| 5 | `studio-document-desktop.png` | Writing Studio | 1440x900 | document open |
| 6 | `studio-zen-desktop.png` | Writing Studio | 1440x900 | zen |
| 7 | `studio-zen-mobile.png` | Writing Studio | 390x844 | zen |
| 8 | `studio-empty-desktop.png` | Writing Studio | 1440x900 | no document |
| 9 | `library-filters-desktop.png` | Library | 1440x900 | sidebar + toolbar |
| 10 | `library-filters-mobile.png` | Library | 390x844 | drawer open |
| 11 | `brain-empty-desktop.png` | Second Brain | 1440x900 | no topics, `[[ ]]` empty state |
| 12 | `brain-index-desktop.png` | Second Brain | 1440x900 | seeded topic index, list view |
| 13 | `brain-index-mobile.png` | Second Brain | 390x844 | seeded topic index, list view |
| 14 | `brain-topic-desktop.png` | Second Brain | 1440x900 | selected topic, notes grid |
| 15 | `brain-map-desktop.png` | Second Brain | 1440x900 | co-occurrence graph |
| 16 | `audio-light-desktop.png` | Audio reader | 1440x900 | composition |
| 17 | `audio-light-mobile.png` | Audio reader | 390x844 | composition |
| 18 | `audio-light-landscape.png` | Audio reader | 844x390 | phone landscape, two columns |

The first ten rows preserve the honest 14 → 10 reduction: the four redundant desktop dark/sepia
reader captures are gone and the two mobile reader geometries formerly
covered only in dark mode are captured in the app's single light rendering.

### Book detail (real-library only)

Two further artifacts cover the book detail page's cover-derived background
wash. They are real-library-only for the same reason the reader surfaces are:
the echo is derived from the book's own cover art, and the isolated fixture has
no covers.

| Artifact | Surface | Viewport | State |
| --- | --- | --- | --- |
| `book-detail-hero-desktop.png` | Book detail | 1440x900 | cover echo present |
| `book-detail-hero-mobile.png` | Book detail | 390x844 | cover echo present |

## The committed pixel baseline (`check:pixels`)

The acceptance gate for a *value-preserving* CSS change is byte-identity against
`e2e/visual-evidence/design-baseline/` (24 PNGs + the computed-value sweep):

```sh
cd Nostos.Frontend
npm run capture:baseline -- --port <port> --out /tmp/after
node scripts/check-pixels.mjs /tmp/after
```

**The baseline is a claim about a specific build, so it must be regenerated when a
change is *meant* to move pixels — in the same PR that moves them.** It is not
optional cleanup: a stale baseline makes the gate red for every change, which is
worse than no gate, because the next reader learns to ignore it. Left un-regenerated
across the palette migration (#153/#155/#156) it drifted 15.5M px from `main`, so
every surface failed on unmodified code.

Before trusting a red gate, prove which side is wrong with a controlled capture —
one server, one DB, two builds:

```
pxdiff(committed_baseline, pristine_main)   # large  -> the BASELINE is stale
pxdiff(pristine_main,      your_branch)     # in-scope must be 0
```

If pristine `main` diffs by the same count with and without your change, the
baseline is at fault; regenerate it deliberately and say so in the commit. Do not
regenerate to make a real regression green.

### Regenerating safely

1. Serve the build the branch actually produces (a Release backend run rebuilds and
   serves `wwwroot`), and confirm the served stylesheet hash equals `dist`'s.
2. `npm run capture:baseline -- --port <port>` (writes the committed baseline).
3. Capture again into a scratch dir and assert the gate passes with a **zero** noise
   floor — 24/24 byte-identical. A non-zero floor means the capture is
   nondeterministic and the baseline would bake in randomness.
4. **Falsify it**: introduce a real defect (e.g. `--radius-md: 4px` to `8px`), rebuild,
   capture, and confirm the gate FAILS naming the bbox. Restore the source and confirm
   `git status` on `src/` is clean. A baseline whose gate cannot fire is decoration.

**`check-pixels.mjs` is single-pass by necessity.** It used to collect one `[x,y,delta]`
tuple per differing pixel and reduce those arrays with `Math.min(...xs)`, which threw
`RangeError: Maximum call stack size exceeded` on any diff past a few tens of thousands
of pixels — i.e. it crashed instead of reporting exactly when the diff was large. Keep
every statistic folded inside the scan and return scalars plus a bounded sample.

### Work membership modal

The membership surface is a **modal**. Automatic grouping cannot serve every
case — e.g. `Nicomachean Ethics` (ebook) and `The Nicomachean Ethics` (physical),
same author, different works, which the normalizer correctly declines to merge
because the leading article is real metadata. Linking them is the override that
exists for exactly this case.

Two entry points, because one is not enough: a footer action on the editions card
for a book that already has editions, and the hero **Edit** chooser for every book
— including the lone book, which has no editions card at all and is the book that
most needs linking.

**The Edit chooser has no divider between its rows, and that is deliberate.** An
earlier revision drew a `border-top` on the second item. Because the item spans
584–806 while the panel is 579–811, the border's ends stopped 5px short of the
panel edge as two hard vertical caps, and squaring the top corners
(`0 0 6px 6px`) left the hover fill as a flat-topped block beside the panel's
rounded corners — at 1:1 that reads as a cut edge, sharpest at the ends of the
seam. Keeping the line without squaring the corners leaves the caps; replacing it
with a permanent tint ties with `:hover` on specificity (0,2,0) and kills the
hover state, and in dark `--bg-hover` (#20222a) sits within ~1/255 of
`--border-color` (#2a2d37), so a permanent tint is indistinguishable from a
permanent hover. Measured in the browser: each item now computes
`border-top: 0px` and a uniform `border-radius: 6px`.

Measured, not eyeballed:

- **Contrast (dark):** every text tier in the modal passes AA against
  `--bg-surface` — title 14.37:1, section labels and `THIS BOOK` 10.03:1, count
  badge 9.23:1. The lowest is the smallest type, and it still clears AA.
- **The Edit menu's hint text is 6.89:1** (light), so the two-line menu entries
  are readable rather than decorative.
- **No nested scrolling.** The modal body is the single scroll container. An
  earlier revision capped the candidate list at 240px as well, which measured as
  1189px of content inside a second scroll area whose bottom edge sat flush
  against the card — two scrollbars and no cue that the list continued.
- **The footer action is 278x44**, at the touch-target minimum, and sits below the
  rows behind a divider. As the first element in the stack it shared the rows'
  surface, border and radius, so it read as edition row #1 and made the card's
  count badge appear to contradict the number of bordered boxes on screen.
- **The candidate row carries an explicit `Link` label**, mirroring `Unlink` on a
  member row: an unlabelled click target reads as static text, and the only other
  cue was `:hover`, which touch never shows.

## How to run

Default run (isolated fixture — no book assets needed):

```sh
cd Nostos.Frontend
npm run e2e          # builds backend + frontend, boots temp-SQLite fixture, runs everything
```

This captures images 5–15 (studio + library + Second Brain, fixture-served) and **skips**
images 1–4 with a documented message: the isolated fixture has no EPUB/PDF
book files and the harness never invents assets.

Full 15-image run against a real library (reader surfaces need real books):

```sh
cd Nostos.Frontend
VISUAL_QA_LIBRARY_URL=https://your-instance npm run e2e
```

`VISUAL_QA_LIBRARY_URL` must point at an instance that (a) serves the
**build under test** (the branch's own build output) and (b) has at
least one EPUB and one PDF book with files in the library. The harness
discovers them via `/api/books` and skips any missing surface with a clear
message. The established local recipe is the dev build proxied to the real
backend: a temp `src/proxy.local.json` pointing `/api` at the running backend
plus `npx ng serve --port <fresh-port> --proxy-config src/proxy.local.json`,
then `VISUAL_QA_LIBRARY_URL=http://localhost:<fresh-port> npm run e2e`
(delete the temp proxy file afterwards).

Book detail only (needs a book with cover art):

```sh
cd Nostos.Frontend
VISUAL_QA_LIBRARY_URL=http://localhost:4310 npm run e2e -- book-detail-visual.spec.ts
```

`book-detail-visual.spec.ts` captures both viewports from one spec (via
`newCapturePage`'s own contexts), so it runs in the desktop project only — its
file name deliberately avoids the `mobile*.spec.ts` pattern the desktop project
ignores. Without `VISUAL_QA_LIBRARY_URL` both cases skip with a documented reason.

## Second Brain feature contract

The Second Brain indexes topic references written as `[[Name]]` in note
content. Saving a note creates any referenced topics and refreshes its note
links; the hourly cleanup worker deletes topics with zero note links, so a
topic with no references is expected to disappear.

The surface has three views: the index lists, searches, sorts and counts
topics; the detail pane filters and sorts linked notes and shows related
topics; and the map renders co-occurring topics as a force-directed graph.
Index sort and list/map view persist under `nostos.brain.indexSort` and
`nostos.brain.viewMode` respectively.

**The Brain has a persistent header**, rendered in both view modes. It owns the
surface title, the topic stats, the topic search and the list/map mode
switch, and it does not move or change shape when the mode changes — the switch
you clicked to enter the map is the same control, in the same place, that
returns you to the list. Sort stays in the index rail, because it orders the
list and the list is the only thing it can act on.

**The topic search is a persistent filter, not a per-mode one.** It filters
the topic set in both modes and its query carries across the toggle, so the
list and the map always agree about which topics they are showing; an empty
graph says `No topics match "…"` when a query is responsible and
`No connections yet` only when the connections genuinely do not exist.

**Map view is a whole-surface mode.** Toggling into it closes the index rail and
the layout collapses to a single column, so the map is never on screen beside a
list it has already replaced. **Double-clicking a node opens that topic's
notes** — the same destination an index row click reaches — which also returns
to list view. A single click selects, and **clicking empty space clears the
selection** (the map otherwise stays stuck on the last node clicked). A camera
pan does not clear it: Sigma suppresses the click that follows a drag, so only a
genuine click on empty space deselects.

Management actions have narrow, deliberate semantics:

- Rename changes the topic name. If that name already exists, the two
  topics are merged; note text is not rewritten, so saving an old `[[Name]]`
  reference can recreate it.
- Merge moves unique note links from the source to the selected target and
  deletes the source topic. Note text is unchanged.
- Delete removes the topic and its note links but does not edit note text;
  saving a note containing the reference can recreate the topic.
- Note edit updates note content and re-processes its topic links. Note
  delete permanently removes the note and its links after confirmation.

## Automated geometry checks (run on every capture)

| Check | Applies to | Pass criterion |
| --- | --- | --- |
| `epub-iframe-light` | EPUB captures | iframe `body`/`html` background+foreground equal the fixed light constants; `#epub-viewer` shell surface matches the same light surface (no pale rim) |
| `pdf-scrollport-clearance` | PDF captures | scrolled to final page bottom, `#viewerContainer` bottom is at/above `header.reader-toolbar` top (toolbar covers no content) |
| `audio-composition` | audio captures | **desktop:** one control row, no dead band > 48px, no horizontal overflow. **Phone (≤768px wide or ≤520px tall):** the composition *fits* — the reading area does not scroll (≤1px), nothing is pushed above its top edge, the transport trio stays on one row, no horizontal overflow. Then per composition: **portrait** stacks the Playback control under the transport (2 control rows, non-negative gap between them — the owner's preference for standing use) with the cover ≤ 58% of the area; **short landscape** is two columns with transport and pill inline (1 row, cover ≤ 60%, and the cover's right edge clear of the controls column). The desktop bar is a *fill* contract and is the wrong criterion on a phone: measured before the phone passes, the 320×480 cover left the pill below the fold and scrolled the area by 52px at 390×730, 78px at 360×640 and 89px at 320×568, and in landscape (844×390) the column could not fit a 334px-tall area at all — 121px of scroll, with the overflow it split above the top edge unreachable |
| `zen-fills-viewport` | zen captures | `.studio-layout` equals the viewport size |
| `zen-chrome-hidden` | zen captures | sidebars, editor header/status, TinyMCE menubar + formatting toolbar all `display:none` |
| `zen-gutters-balanced` | zen captures | editor surface horizontally centered: left/right gutters within 3px |
| `library-no-progress-combobox` | library captures | toolbar progress filter is not a `<select>`; the only toolbar select is sort |
| `library-six-sidebar-filters` | library captures | sidebar/drawer exposes exactly: All Books, Not Started, In Progress, Favorites, Finished, Unsorted — and no toolbar progress surface |
| `brain-no-arrival-animation` | selected Brain topic capture | detail pane has no `.wait-field`, no `is-waiting` class, `.topic-header`/`.note-card` computed `animation-name: none`, and no running animation targets in the pane |
| `brain-layout-overflow` | Brain captures | desktop index/detail tracks stay within the grid and the 390px surface has no horizontal overflow |
| `brain-map-geometry` | Brain map capture | map node count matches the topic badge and every node radius stays within the documented 4–16px bounds |
| `brain-empty-state` | Brain empty capture | the fixture-served `[[Topic Name]]` empty state is visible and the topic list has no rows |
| `book-detail-hero` | book detail captures | the hero band spans the scroll container's full width (±2px) and is ≥260px tall; both decorative art layers are real `<img>`s that actually loaded (never a stripped `[style.background-image]`); the hero copy's last line ends above the sharp cover's top edge (a negative-margin overhang must never paint over the author line); `.book-title` owns its own pixel; no horizontal overflow |
| `book-detail-fade` | book detail captures | the hero's fade into the page is a smooth **ease-in-out from its own gradient stops**: the scrim releases monotonically downward (it must never strengthen in the region the fade has to lighten) and the fade's per-segment slope rises then falls, with both end segments ≤ half the peak slope (a steeper end draws a visible onset/stop line across the band). The stops are a smoothstep in **lightness**, not in alpha — compositing a light fade over dark art is non-linear, so an alpha smoothstep comes out front-loaded. Measure it with `npm run profile:fade -- --url <book-detail-url>`, which reports the ramp in OKLab L plus the deviation from a true smoothstep; the guard above only protects the *shape*, so a front-loading regression has to be caught by that profiler |

Every check is recorded in the capture's `.json` report with its
metrics. **Skips are never failures and never fakes**: a check is skipped only
when a documented dependency is absent (see below).

### Documented skips

1. **Reader surfaces (images 1–4)** skip in the default run: no EPUB/PDF test
   assets exist in-repo and they are never invented. Run with
   `VISUAL_QA_LIBRARY_URL` to capture them. The committed reader PNGs are
   regenerated only in real-library mode.
2. **EPUB highlights**: the app has no programmatic highlight-placement API —
   highlights require real user selection inside a book, which the harness
   cannot synthesize. The screenshot documents the fixed-light reader;
   highlight visuals are covered by the vision review step when a real
   library is used.
3. **`library-six-sidebar-filters`** skips while the progress-filter repair
   (expert section 3) is not merged into main: the current merged UI still has
   the toolbar progress dropdown and five sidebar filters. The check activates
   automatically when the repair lands — no harness change needed. Until then
   the capture still runs (evidence of the current state) and the report marks
   the check `skipped` with the exact reason.

## Multi-edition selector contract

**Available Formats & Editions** is the details rail's second card, and it
renders only when the book's work has at least one other edition — a
single-edition book must never gain an empty section.

**The current edition is stated three ways, never by fill alone.** Its row is
filled with the app's selection role (`--selection-surface` / `--selection-ink`,
the same pair the Library sidebar and the Brain index row use), it carries a
`Current` pill, and it is marked `aria-current`. The current row is a `<div>`,
not a button: it is where the user already is, so it has no action to offer and
is not a tab stop. Alternate editions are real `<button>`s with a visible
`:focus-visible` ring, and every text tier on the filled row derives from
`--selection-ink` — including the `Finished` pill, which measured 1.35:1 on that
fill when it kept its light-theme success ink.

**The rows are a list inside the card.** They are one column in the 320px rail;
under 900px, where the rail is the full body width, the list becomes an
`auto-fit` grid so a row is not stranded next to empty space, collapsing back to
one column as the width falls.

## Work membership modal

Automatic grouping stays the default. The explicit override for a wrong automatic
grouping is a **modal** launched from two entry points: a footer action on the
Available Formats & Editions card, and the hero **Edit** chooser. It is not part of
the Edit Book form, and it is not a rail disclosure any more.

**Why a modal, and why not inside Edit Book.** Two separate reasons, and both are
load-bearing:

- **Room.** The job needs a search field, a result list and per-candidate
  disambiguation. In the 320px rail — or stacked at the bottom of a 390px page —
  that is a cramped working area inside a reading surface.
- **The Save/Cancel contract.** Edit Book is a deferred form: one `PUT` on Save
  Changes, discard on Cancel. Link and unlink apply immediately through the domain
  service. An unlink confirmed inside that form would survive a Cancel the user
  believed undid everything, and `PUT /api/books/{id}` carries no version or
  `If-Match` header, so nothing would catch the contradiction.

**Why two entry points.** A book alone in its work has no editions card, and that
is precisely the book that needs linking — so the card action cannot be the only
route. The Edit chooser is present for every book and is the lone book's route.

- **Not in the DOM until opened, and it fetches nothing while closed.** The
  candidate list loads on open, so an ordinary book-detail visit costs no extra
  query. "Secondary" is structural, not just visual.
- **Confirmed, with the real consequence named.** A link merges whole work
  groups, not just the two named books, and the dialog says so when either side
  already has more than one edition. Unlink is confirmed too, because detaching
  the wrong edition is a quiet way to break a valid group.
- **The server owns the decision.** Both actions POST to the domain service and
  then re-read the book; the client never writes a `WorkId`, and never predicts
  the resulting group from two book ids.
- **Each row names its own book.** `EditionSummaryDto` carries `title`/`author`
  for this reason: a sibling row labelled with the current book's title names the
  wrong book as the one Unlink detaches.
- **Candidates are deduplicated by work.** Linking is group-level, so a work with
  three editions offers one choice, not three rows under the same title.
- **`WorkId` is the only thing that moves.** Files, progress, notes, ratings and
  reviews, metadata and collection memberships are per book, and the backend
  tests assert them unchanged across a link and an unlink.

## Fixed rendering invariants

The reader's light appearance is not a selectable state — it is the only
rendering. Two invariants are asserted by the harness and must not regress:

- **EPUB iframe normalization** is a fixed publisher-CSS override registered
  once per rendition (`epub-reader.component.ts` `NOSTOS_LIGHT_RULES`):
  iframe background `#ffffff`, foreground `#1a1a1a`, links and selection
  styled, publisher backgrounds/heading colors suppressed.
- **PDF light surround** is a fixed constant: the viewer canvas surround stays
  `#fefeff` with the base light page outline/shadow (never the library's gray
  default), and the shell toolbar never covers the final page.

`support/visual-capture.ts` mirrors these constants (`READER_IFRAME_LIGHT`,
`READER_SHELL_LIGHT`). If the app tokens change, update the constants and this
section **in the same PR** — a stale constant table is a false FAIL.

## Vision review (mandatory, human or vision-model)

Screenshots plus geometry are the gate; pixel-diff snapshots are not. For
each PNG, record PASS/FAIL against the expert vision criteria:

- EPUB iframe renders the fixed light normalization (no publisher
  background/heading bleed, no pale iframe rim or first-section white flash).
- PDF page boundary is visible; the surround is the fixed light color and PDF
  colors/images are not inverted or corrupted.
- Bottom toolbar covers no document content.
- Zen fills the viewport.
- Zen prose has balanced left/right margins; no empty right half.
- No detached control strip remains.
- Mobile dock overlays neither zen nor reader content.
- Exactly one progress-filter surface is visible.
- No clipping, horizontal overflow, illegible contrast, or overlapping controls.
- Second Brain: the index, selected topic notes, map nodes and empty state are legible at their named viewports; the detail pane swaps without a covering flash or entrance animation; the map has no clipped nodes or non-tappable node sizes.
- The reader shell has no theme controls and no second toolbar row on mobile.
- Book detail: the cover wash has **no visible edge, seam, rectangle, band or
  corner** where it stops — it must fade smoothly into the paper background on
  every side. A straight boundary is a FAIL even if subtle (this shipped three
  times as "a weird square in the upper left").
- Book detail: the wash sits **behind** the cover and title and reads as derived
  from that cover's own art; the cover stays the clear focal point and text
  remains the most legible element. If the wash competes with either, lower its
  opacity rather than removing the check.

Verdicts must be attached to the PR alongside the artifact names (e.g.
"PASS 15/15 — `brain-topic-desktop.png` verified against criterion list").
**Any unexplained FAIL blocks merge.**

## Human-override rule

A FAIL may be overridden only by an explicit human decision that states why
the visible result is intentional (e.g. a deliberate layout change that the
PR describes). The override must name the artifact and the criterion it
waives. Silent or unexplained failures never merge.

## PR checklist (copy into every UI PR description)

- [ ] `npx ng test --watch=false` green, no reduced test count
- [ ] `npm run e2e` green (existing specs + visual matrix; skips documented)
- [ ] `npx ng build` green
- [ ] 15-image matrix captured from the branch's actual build
      (real-library run for images 1–4)
- [ ] Geometry reports: no failed checks; skips documented
- [ ] Vision review recorded per artifact (PASS/FAIL + artifact names)
- [ ] No unexplained FAIL; human overrides explicitly explained
- [ ] `git diff --check` clean
