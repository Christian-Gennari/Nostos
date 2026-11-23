# Library = source of truth. Copy, do not invent.

The Brain is not a separate design; it is the same app. It is built like the
Library on purpose: a sidebar of places on the left, a toolbar and one column
on the right. Every control whose equivalent exists in the Library uses the
Library's component, geometry, colour roles and motion. Divergence is a bug.

**Before building anything, find the component the Library itself uses.** The
Library's toolbar controls are shared primitives (`appInput`,
`app-dropdown`, `appButton`, `appChip`); the Brain uses the same ones. Copying
CSS that merely looks similar is how the old Brain drifted.

## What maps to what

| Library | Brain | Shared by |
| --- | --- | --- |
| Left sidebar (`248px`, `72px` collapsed rail) | Left sidebar (`248px`, `72px` collapsed rail) | `.side-nav` recipe (`56px` `.sidebar-header` with `1px solid var(--border-color)` bottom border and collapse `toggle-btn` in the header) |
| Shelves (All books, In progress, …) | Places (All notes, Passages, Unsorted, Map) | `.nav-item` rows with a count badge |
| Collections | Topics | `.topic-row` mirrors the collection `.tree-row` |
| Toolbar: title, search, sort, view | Split per column (#1808): `56px` `.list-header` (title + count, order, Select, topic `⋯`) + `.list-toolbar` row (search, book filter, Unsorted's Sort one by one) in the `380px` list column; `56px` `.note-detail-head` (book title, author, `⋯` actions) in the reader column | `appInput`, `app-dropdown`, `appButton`, `appIconButton` |
| Filter chips above the grid | Filter chips above the list | `appChip` |
| Phone: shelf name ▾ opens the drawer | Phone: place name ▾ opens the drawer | the drawer pattern |

The Brain has no view toggle: the map is a place in the sidebar, not a mode.

## Measured values (the parity spec, desktop, light)

| Control | Library | Brain |
| --- | --- | --- |
| Left sidebar width | `248px` (`72px` collapsed rail); Studio's `.sidebar-left` is `248px` too | `248px` (`72px` collapsed rail) |
| Column header height | `56px` `.sidebar-header` (`1px solid var(--border-color)` bottom rule, collapse toggle in header) | `56px` across `.sidebar-header`, `.list-header` and `.note-detail-head`, sharing one continuous `1px` bottom border at `y = 56px` |
| List column width (≥1280px) | — | `380px` (`--bg-pane-list`), separated from the reader (`--bg-pane-reader`, `744px` container / `680px` text with `32px` padding) by a full-height `1px` border |
| Search field | 36px tall, radius 8px, padding `6.4px 11.2px 6.4px 40px`, 14px | the same, inside `.list-toolbar` in the list column |
| Sort / order dropdown | 36px tall | the same |
| Sidebar row | 34px tall, radius 8px, padding `0 8px 0 12px`, 14.08px | the same |
| Row count | 20px line box, 11.52px, no chip ground | the same |
| Row actions | hover overlay on fine pointers; one 44px dots-three menu on touch | the same |
| Dock (`app-app-dock`) | `--dock-bar-h: 50px`, `40px` items, anchored at `left: var(--dock-anchor-x)` (`calc(50% + 124px)`, `calc(50% + 36px)` collapsed) | the same as the Library on every Brain page |

On a touch screen the Brain's toolbar controls step up to the 44px rung
(`--control-h-touch`), as the Library's do. The sidebar keeps the Library's
row geometry: the 44px target is the topic row's in-flow dots-three menu, not a
taller row. See `docs/brain.md`.

## Dock anchor across Library / Brain / Studio (#1808)

Decision (2026-11-23, revised after review of the first build): **centre the
dock on everything to the right of the left sidebar**, in Library and Brain
alike. The first build centred it on the Brain's reader pane; it never crossed
a border there, but it looked pushed to the right and jumped between Library
and Brain, and the owner chose the centred look over that guarantee.

- `.workspace-layout` sets `--dock-anchor-x`:
  - **Library and Brain** (every Brain page): `calc(50% + 124px)`, or
    `calc(50% + 36px)` when the `72px` sidebar rail is collapsed.
  - **Writing Studio** `.editor-pane`: `calc(50% + 124px)` with the `248px` left
    sidebar open, `calc(50% - 180px)` with the `360px` right reference drawer
    open, `calc(50% - 56px)` with both open, and `50%` with both closed.
- Known trade-off: in the wide Brain the list/reader border is at `x = 628px`.
  The dock without Ask Nostos (335px) clears it from about 1342px wide; with
  Ask Nostos on (588px) it crosses it below about 1596px. The panes' bottom
  padding keeps the last line of each column clear of the dock.
- `app-app-dock` positions at `left: var(--dock-anchor-x, 50%)` and animates
  `left` with `--motion-slow` / `--ease-standard` (disabled under
  `prefers-reduced-motion: reduce` and on phone rail layouts ≤768px).
- Every scroll container the floating dock can overlap (`.nav-scroll-area`,
  `.brain-list`, `.note-detail-body`) reserves
  `calc(var(--dock-bar-h) + var(--dock-float-bottom) + 40px)` of bottom padding.

## Sidebar rows — the row with actions

A topic row carries a count and Rename/Delete. That is the Library's collection
row, so the Brain copies `.tree-row`, not `.nav-item`:

- the fill is painted on the row, not by a `::before` pill;
- with a fine pointer the actions overlay the count's slot, so the name never
  reflows, and the name's tail fades into them;
- at rest the actions are hidden and the count shows; on hover the count
  yields to the actions;
- on touch there is no hover, so each row carries one always-visible 44px
  dots-three menu in flow, the Library's #1514 pattern. The count never yields
  and the name ellipsizes; a tap on the row is never a hidden action.

The selected row keeps one fill through rest, hover and focus, in both themes
(`scripts/probe-selection.mjs` prints it).

## Search

Brain search is scoped to the active place (`"Search in <topic>…"`), lives in
the list column's `.list-toolbar` row alongside the Book filter, and filters the
list; global search across the app is the command palette. The icon is
absolutely positioned and the input owns the left padding (40px), so the field
you see is the box you centre.

## Colour roles

Text and icons in the sidebar, list and reader use semantic theme tokens across
all five themes (Light, Sepia, Mist, Writer's Study, Dark): `--bg-pane-nav`,
`--bg-pane-list` and `--bg-pane-reader` for the three column surfaces;
`--row-hover` and `--row-selected` for list rows; `--color-text-main` for active
rows and titles, `--color-text-muted` for secondary text and section labels, and
`--color-text-light` (`#6a6b6f` in Light so every surface meets WCAG AA ≥ 4.5:1)
for counts and tertiary meta. Serif (`--font-reading`) is reserved for reading
content and the reader header book title; all UI chrome uses `--font-ui`.

