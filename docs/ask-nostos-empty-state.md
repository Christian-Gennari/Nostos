# Ask Nostos Empty State — Design Brief

**Issue:** [#1589](https://github.com/Christian-Gennari/Nostos/issues/1589) (follows #1579, #1559; part of #1464)  
**Status:** Decided — implementing **Direction A (Composer-led)**.

---

## Why this needs a redesign

The current Ask Nostos empty state renders eight bordered pill buttons (`appButton variant="secondary"`) stacked inside three uppercase gutter groups (`ASK`, `CAPTURE`, `ORGANISE`), followed by a muted disclaimer (*"It can't delete books or notes, or change settings."*), with the composer pinned to the bottom edge.

Measured against production and the #1579 visual evidence (`390×844` phone and `1440×900` compact/expanded desktop):

- **No hierarchy.** Title, three gutter labels, eight bordered pills and a disclaimer carry identical visual weight. Eight stacked outlines are louder than the composer the reader came to use.
- **Feature taxonomy, not reader intent.** `ASK / CAPTURE / ORGANISE` mirrors internal tool buckets rather than how a reader thinks.
- **Bloat and dead space.** On a 390 px phone the 64 px label gutter plus 44 px-tall wrapped pills consume the upper two-thirds of the screen, leaving a large empty gap above the bottom-docked composer. In the compact desktop panel (408 px wide) the pill list fills the surface and pushes the composer to the bottom edge. In expanded mode the layout splits into three mismatched shapes (full-width rectangles in Ask/Capture, auto-width pills in Organise).
- **Broken copy and invented content.** *"Save this thought:"* and *"Save this quote:"* end in a colon and read as truncated buttons. Leading with *"It can't delete…"* puts a defensive disclaimer in place of a welcome. The Organise examples hard-code *"my Le Guin books"*, *"Piranesi"*, and *"my Studio draft about memory"* — naming books and drafts most libraries do not have.

---

## What the first screen must do

1. **Invite one action immediately.** The eye should land on a single clear focal point — the composer (or one calm invitation directly bound to it) — so typing or speaking a thought is the obvious first move.
2. **Show range without listing features.** A reader should be able to tell at a glance that Ask Nostos can answer questions about their reading, capture thoughts/quotes, and help organise the library — without a feature matrix, section gutters, or a wall of buttons.
3. **Stay truthful to this library.** Every suggestion must be true for the reader's actual library: either personalised from real context/data (`AssistantContext`, current book, real notes/shelf) or phrased generically. Never name a book, author, or draft the user does not own.
4. **Keep the #1559 prefill rule cleanly.** Clicking any suggestion places text into the composer and focuses it; it never auto-sends. Capture prompts read as natural, complete phrases (never truncated labels ending in `:`), while still prefilling a ready-to-continue stem in the composer.
5. **Adapt as one design across all three shells.** Compact desktop flyout (408 px docked above the app dock), expanded desktop card (up to 1000 px over the modal scrim), and full-screen phone (`390×844`) share one visual grammar and component structure — not three separate layouts.
6. **Fit `390×844` with zero scroll.** On a phone, the invitation, composer, and all visible suggestions (at most 3–4) fit inside the first viewport with no vertical scrolling and no horizontal overflow (`scrollWidth <= innerWidth`).
7. **Support contextual doors (`capabilityDoors()`).** Opening Ask Nostos from the Reader (with or without a selected passage), Book Detail, Brain (or a Brain review note), Studio, or Library swaps the suggestions to fit the current room while keeping the exact same layout and count cap.

---

## What the first screen must not do

- **No bordered pill wall or card grid.** Suggestions must not wear full `1px` pill outlines or compete visually with the composer.
- **No uppercase category gutters** (`ASK / CAPTURE / ORGANISE`).
- **No negative disclaimer in the welcome hierarchy.** *"It can't delete books or notes, or change settings."* belongs behind a quiet help/info affordance (e.g., an `info` icon in the panel header) or at the moment a refused action comes up — not underneath the greeting.
- **No dead void between prompt area and composer** on phones or expanded cards.
- **No raw colours, raw radii, or ad-hoc iconography.** Every colour, border, shadow, radius, and spacing step comes from `Nostos.Frontend/src/styles.css` (both light and Nordic Nocturne dark) and Phosphor icons via `nostos-icon` / `appIconButton`, passing `npm run check`.

---

## Reference points in Nostos

1. **Writing Studio blank page (`writing-studio.component.html`, `markdown-editor.component.ts`):** When Studio opens with no document selected, it opens directly onto a quiet blank page with a single serif placeholder (*"Start writing…"*). When imported highlights exist, a single borderless inline bar offers *"Start a draft from them"* using `variant="link"`. Richer capabilities (`/` commands, `@` source citations, `Ctrl+/`) stay hidden until invoked.
2. **Reader notes panel & `nostos-empty-state` (`empty-state.component.ts`, `reader-notes-panel.component.html`):** *"It owns no box: no border, fill or card. The words are the state."* Centred in the available space, one calm line in primary ink and one quieter supporting line, with generous breathing room.
3. **Design Manifesto & surface rule (`docs/design-manifesto.md`, `styles.css`):** *"Literary publication on the surface, silent digital study in behavior."* *"Nostos should hide sophistication. Complexity appears only when the user deliberately asks for it."* And from `styles.css`: *"Do not put a bordered row inside a surface, and do not repeat an outlined button once per item in a list."*
4. **The composer as the hero:** `assistant-composer` already speaks Nostos's editorial voice — Newsreader serif inside a crisp `--bg-input` / `--radius-field` shell with a quiet `--color-accent` focus edge and push-to-talk mic. Making the composer the unmistakable centre of gravity when the transcript is empty aligns Ask Nostos with every other room in the app.

---

## Three directions explored (Stage 1)

Three static HTML directions — captured at `390×844` (phone), `1440×900` compact desktop, and `1440×900` expanded desktop in both light and dark themes — were posted for review in [issue #1589 comment #7046641924](https://github.com/Christian-Gennari/Nostos/issues/1589#issuecomment-7046641924).

| Direction | Focal point | Suggestions on first screen | Boundary note | Contextual doors (`capabilityDoors()`) |
| --- | --- | --- | --- | --- |
| **A — Composer-led** *(picked)* | Hero composer near vertical centre, one quiet Newsreader invitation above | 3 borderless hairline-separated editorial links directly beneath the composer (Ask, Capture, Organise by example — no gutters, no pills) | Header `info` icon popover (off first screen) | Same 3 slots swap copy cleanly per surface using `AssistantContext` (e.g. current book title in Reader/Book Detail; generic truths on Library) |
| **B — Context-led** | Quiet shelf-grounded list anchored directly above the composer (no dead gap) | 3 real-shelf items drawn from the user's actual library (*Reading*, *Last note*, *To read* — subtle inline tag, hairline rows, zero generic filler) | Header `info` icon popover (off first screen) | On Reader/Brain/Studio, slots reflect the active book/note/draft first, then recent shelf items; falls back to safe generics on an empty library |
| **C — Minimal (Studio-style)** | Serif invitation + centred hero composer only | None visible at rest; quiet `/` trigger inside the composer footer opens a lightweight on-demand prompt popover | Footer of the `/` ideas popover + header `info` | The `/` popover renders the 3–4 contextual prompts for the active surface |

---

## Decision

**Picked: Direction A — Composer-led** (following review on [issue #1589](https://github.com/Christian-Gennari/Nostos/issues/1589#issuecomment-7046641924)).

**Rationale:**

- **One clear focal point (the composer)** across phone (`390×844`), compact desktop flyout (`408 px`), and expanded desktop card (`1000 px`), eliminating the dead void on phone and turning the empty state from a menu into a writing/thinking surface.
- **Range shown in three quiet rows without a pill wall.** Three borderless hairline-separated links directly below the composer show Ask, Capture (complete phrasing with no trailing colon, still prefilling a ready-to-continue stem), and Organise without uppercase taxonomy gutters or repeating button outlines.
- **Truthful at every library size with no extra fetch.** Suggestions never name books, authors, or drafts the reader lacks, and `capabilityDoors()` swaps those exact three slots to match Reader, Book Detail, Brain, Studio, or Library from `AssistantContext`.
- **Why not B or C:** Direction B pulls the eye above the composer toward a list and requires extra library-summary hydration (plus empty-library fallbacks); Direction C hides Capture and Organise behind `/`.
- **Boundary placement:** *"It can't delete books or notes, or change settings."* moves off the first-screen hierarchy into the header `info` popover, reachable by pointer, keyboard, and screen reader.

