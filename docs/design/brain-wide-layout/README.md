# Wide-screen Brain — design mockups (#1530, #1531, epic #1533)

**These are mockups, not a built feature.** Every page here is static HTML and CSS
with sample content; none of the behaviour in the issues exists yet. The owner
picks a direction from these, then the chosen issue is built separately.

The three options and the space they use:

| | Layout | Right-hand side | Widths mocked |
| --- | --- | --- | --- |
| **A** | list + detail, `#1531` | the selected note | 1440, 1920 |
| **B** | current list + rail, `#1530` | Books, Topics, Unsorted, Activity | 1440, 1920 |
| **C** | list + a right-hand zone | place panel when browsing, note when reading | 1440, 1920 |

Sidebar open in every mockup, light and dark. A also has the 1920 light
sidebar-closed variant. `current-1440-light.png` is the **baseline**: a real
screenshot of `/brain` from the production build of this branch's HEAD
(`origin/main`) at 1440 light, kept beside the mockups so the owner can compare
the density and chrome at the same width.

## Files

```
a-list-detail.html            option A
b-context-rail.html           option B
c-combined.html               option C (?note=1 shows the note-open half)
mockups.css                   tokens, type, sidebar, toolbar, list, rail
fonts/                        the app's self-hosted Hanken Grotesk + Newsreader (OFL)
shots/                        the PNGs: 1440×900 and 1920×900, scale 1
shots.mjs                     the screenshot script
current-1440-light.png        the real current page, 1440 light (baseline)
```

The HTML uses the app's real design tokens and typefaces. Token values, the
sidebar recipe, the Brain toolbar and list, the note item and the buttons/chips
are copied from `Nostos.Frontend/src/styles.css`,
`brain-sidebar.component.css`, `brain-list.page.css` and
`note-item.component.css`; the wide-screen geometry (split panes, rail, place
panel) exists only in `mockups.css`. The floating app dock is deliberately
omitted from the mockup pages so it does not cover the bottom of the layout;
the baseline shows it in place.

## Viewing and regenerating

Fonts do not always load over `file://`, so serve the folder:

```bash
cd docs/design/brain-wide-layout
python3 -m http.server 8000
# http://localhost:8000/a-list-detail.html?theme=dark
# http://localhost:8000/c-combined.html?note=1
```

Screenshots (one browser, one page at a time; requires the frontend's
`node_modules`):

```bash
cd docs/design/brain-wide-layout
node shots.mjs --out shots
```

The script checks that no page scrolls sideways, that both faces loaded, and
that no page error was raised. `?theme=dark` switches the token block;
`?sidebar=closed` shows the 72px rail; `?note=1` opens the note in C.

## The content is sample data

The notes, dates, counts and writings are invented for the mockups, not read
from a library. The books are public-domain titles (*Walden*, *Middlemarch*,
*The Souls of Black Folk*, *Pride and Prejudice*, *Moby-Dick*, Montaigne's
*Essays*, *A Room of One's Own*, *The Common Reader*, *Leaves of Grass*); the
quotations are short public-domain lines, and the words under them are written
for the mockup. The numbers are internally consistent: 312 notes across 9
books, 27 unsorted, and the twelve activity bars sum to 312. They are not
statistics.

## The options, and what each trade-off is

### A — list and detail panes (#1531)

**Space.** The right two-thirds becomes the note itself. The list narrows to a
420px index of compact rows (book, three-line excerpt, location); the detail
holds the passage at the app's large quote size, the user's words, the book with
`Open in book`, the topics, related notes and citing writings, in a 736px
reading measure. At 1920 the list is 440 and the detail takes the rest. The
sidebar closing (#1528) gives that extra width to the detail.

**Phone.** Below 1280 nothing changes: `/brain` remains the list and
`/brain/notes/:id` remains the full note page, both fed by the same detail
component. At 1280–1439 the split is tight (about 600px of detail with the
sidebar open), so the breakpoint should be 1280 only if the sidebar can close
there, otherwise 1360–1440.

**Effort.** Medium–large: extract the note body from `BrainNotePage` into one
shared component, add the compact list row, selection state, `?note=` URL sync,
keyboard (↑/↓, Enter, Esc), two independent scroll containers, and the e2e the
issue asks for. No new API.

**Risk.** Medium. The dangerous parts are routing and selection: reload, back,
shared links and `/brain/notes/:noteId` must all land on the same note in the
same list. The list also loses the full-card scan (quote + words + topics) and
becomes an index, which is the intended trade but should be looked at; long
notes need the pane's own scroll; 1280 is the tight width.

### B — context rail for the current place (#1530)

**Space.** A 340px rail (360 at 1920) on the right: Books with note counts and
`All books`, the most-used topics here, the Unsorted count with `Sort one by
one`, and a twelve-month activity bar with the peak annotated. The list keeps
today's full cards in the middle and the rail replaces the Book dropdown while
it is shown. The rail reads `books()`, the topic counts, the summary's Unsorted
count and the date groups that already exist.

**Phone.** The rail is a ≥1440 idea, so below 1440 the toolbar's Book control
stays and nothing changes on a phone. This is the smallest mobile surface of
the three.

**Effort.** Small–medium. The data and filters exist; this is layout plus calls
that write the same URL state as the toolbar. No new endpoints and no routing
change. The rail's own scroll is one extra container.

**Risk.** Low–medium. It duplicates the sidebar's Topics and the Book dropdown,
so the two filter paths must produce the same URL and the same list (the issue's
done-when). It takes width from the list at 1440, so the reading measure grows
from about 640 to 704px, and you still leave the list to read a note. Its
jump-to-month bars depend on #1529's date buckets.

### C — rail when browsing, detail when reading (#1530 + #1531)

**Space.** The list keeps one width. The right-hand zone is a wide place panel
when no note is open (Books and Topics in two columns, Unsorted, a legible
activity chart) and the note detail when one is. Only the right zone swaps;
the list does not reflow between the two states.

**Phone.** Below 1280 the current list and full note page remain. 1280–1439
would need its own decision: the detail half can follow A there, but the place
panel is a ≥1440 idea (at 1280 its two columns are cramped). On a phone the
right zone does not exist.

**Effort.** Large. A plus B plus the state machine for the right zone, two
right-hand surfaces to keep coherent, URL state for both, focus behaviour when
the panel is replaced, and a test matrix for both modes. The panel reuses the
rail's data but needs its own layout.

**Risk.** High. Two modes on one page, two panels, and the duplicated context
is more visible than in B because the panel is wide: the sidebar's Topics and
the panel's Topics sit on the same screen. A wide panel can also read as a
dashboard, which the design manifesto warns against. The payoff is the best
browse state and no list reflow.

## Recommendation

**Build A first.** It is the only option that removes a repeated action (list →
note → back) and spends the width on the thing you opened the Brain for, and it
also makes the list more scannable. B is pleasant context but mostly repeats
what the sidebar, the Book dropdown and Unsorted already say, and it does not
remove a step; C is the most complete but pays for the rail twice and only shows
it when you are not reading.

If A ships and the place still feels anonymous, add the rail's two unique
pieces — the Unsorted count with `Sort one by one`, and the activity bars — as a
compact block in the detail pane or above the list. That keeps the rail's value
without a second full layout state.

## What was actually checked

- `npm run build` in `Nostos.Frontend` succeeded (production build; the
  baseline screenshot was taken from that build's `/brain` page at 1440 light,
  served by `npm run preview -- shots`).
- `npm run check` passed: 70 stylesheets parse, every colour token has a dark
  counterpart, and the design guardrails report no findings across 99
  stylesheets.
- `node shots.mjs` produced all 15 PNGs with no page error, no sideways scroll,
  both faces loaded, at 1440×900 and 1920×900, device scale 1.
- All 15 were looked at at 1:1, and the toolbar/list were compared against the
  1440 baseline.

## What these mockups do not prove

Interaction. The keyboard flow, `?note=` URL, reload, back button, deep-link
routing, the phone page and the real data volume are design intent, not
evidence. The mockups cover 1440 and 1920 as asked; 1280 (the split's lower
bound) and the 1280 sidebar-closed state are the next shots for the build.
Sample note text and counts are not a real library.
