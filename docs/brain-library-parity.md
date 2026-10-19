# Library = source of truth. Copy, do not invent.

The Brain is not a separate design; it is the same app. It is built like the
Library on purpose: a sidebar of places on the left, a toolbar and one column
on the right. Every control whose equivalent exists in the Library uses the
Library's component, geometry, colour roles and motion. Divergence is a bug.

`e2e/brain-library-parity.spec.ts` measures both pages and fails when they
drift; `e2e/brain-row-hover.spec.ts` guards the sidebar row's states.

**Before building anything, find the component the Library itself uses.** The
Library's toolbar controls are shared primitives (`appInput`,
`app-dropdown`, `appButton`, `appChip`); the Brain uses the same ones. Copying
CSS that merely looks similar is how the old Brain drifted.

## What maps to what

| Library | Brain | Shared by |
| --- | --- | --- |
| Shelves (All books, In progress, …) | Places (All notes, Passages, Unsorted, Map) | `.nav-item` rows with a count badge |
| Collections | Topics | `.topic-row` mirrors the collection `.tree-row` |
| Toolbar: title, search, sort, view | Toolbar: title, search, book, order, Select | `appInput`, `app-dropdown` |
| Filter chips above the grid | Filter chips above the list | `appChip` |
| Phone: shelf name ▾ opens the drawer | Phone: place name ▾ opens the drawer | the drawer pattern |

The Brain has no view toggle: the map is a place in the sidebar, not a mode.

## Measured values (the parity spec, desktop, light)

| Control | Library | Brain |
| --- | --- | --- |
| Search field | 36px tall, radius 8px, padding `6.4px 11.2px 6.4px 40px`, 14px | the same |
| Sort / order dropdown | 36px tall | the same |
| Sidebar row | 34px tall, radius 8px, padding `0 8px 0 12px`, 14.08px | the same |
| Row count | 20px line box, 11.52px, no chip ground | the same |
| Row actions | revealed on hover, ≥ 28px targets, the badge yields | the same |

On a touch screen the Brain's controls step up to the 44px rung
(`--control-h-touch`), as the Library's do; see `docs/brain.md`.

## Sidebar rows — the row with actions

A topic row carries a count and, on hover (or when selected on touch),
Rename and Delete. That is the Library's collection row, so the Brain copies
`.tree-row`, not `.nav-item`:

- the fill is painted on the row, not by a `::before` pill;
- the actions overlay the count's slot, so the name never reflows;
- the name's tail fades into the actions instead of hard-cutting;
- at rest the actions are hidden and the count shows; on hover the count
  yields to the actions.

The selected row keeps one fill through rest, hover and focus, in both themes
(`scripts/probe-selection.mjs` prints it).

## Search

The icon is absolutely positioned and the input owns the left padding (40px),
so the field you see is the box you centre. As a flow sibling the icon pushed
the input about 9px off centre; that was a real, measured bug.

## Colour roles

Text and icons in the sidebar and toolbar use the Library's roles: main text
for the active row and titles, muted for rows at rest, light for counts and
the search icon at rest. Accent colour is reserved for focus rings, not for
selection.
