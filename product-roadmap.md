# Nostos — Product Roadmap

**Status:** Living document
**Last Updated:** November 2026
**Last reviewed:** 2026-11-10
**Version:** 2.0

---

## How to Read This Document

Each open item is tagged with:
- **Impact** — High / Medium / Low
- **Effort** — Low (< 1 sprint) / Medium (1 sprint) / High (> 1 sprint)
- **Area** — Reading / Library / Brain / Studio / Mobile / Onboarding / Export / Portability / Ask Nostos
- **Priority** — where an open issue carries a `priority:` label and no impact/effort score exists yet

Items are grouped by area. New epics carry their issue priorities instead of a speculative impact/effort score.

---

## Shipped

- **Work and editions** — one book page across editions, an edition switcher, format chips and the matcher (#1301, #1304).
- **Multi-collection membership** — a book can belong to any number of collections; `BookCollections` is the only record (#105).
- **Library multi-select** — select several books and delete them, add them to or remove them from a collection, or mark them finished (#1375).
- **Drag books into collections** — from the grid and the list, through the same write path as the picker (#1375).
- **Library state** — the grid/list choice and filters persist across reloads (#48); collection counts sit in the sidebar (#12); reading-state filters cover Not started, Reading, Finished and Favourites (#46).
- **Reader typography** — EPUB typeface, size, line height and margins (#203); PDF typography and zoom controls (#208).
- **PDF reading** — in-book search shared with EPUB (#762), table of contents and continuous reading (#226).
- **Highlights** — highlight mode with four colour pens, stored on the highlight and editable later (#15, #863).
- **Audio** — sleep timer including end of chapter, chapter list and speed presets (#47, #209, #227).
- **App-wide themes** — Light, Sepia, Mist, Writer's Study and Dark, with Follow system (#1074).
- **Highlight import** — Kobo, KOReader, Kindle, Apple Books and Readwise CSV, with content detection, preview, matching, skip reasons, dedupe and per-batch undo (#656, #1225, #1453).
- **Topics** — the concept list became Topics (#686), with a Sigma map (#97), the rebuilt Brain (#1077), descriptions in the reader's own words (#1082), inline rename, merge and delete (#1216, #1237), and Most used / A–Z order (#1077).
- **Writing Studio** — zen/focus mode (#49), typewriter mode (#201), the `@` source palette and Studio themes (#1059), citation styles with footnotes and a bibliography (#1109), and the word, character and reading-time stat (#1059).
- **PWA** — installable with a web manifest, an Angular service worker and maskable icons; the launch icon follow-up is #1160.
- **Reader gestures** — edge taps and deliberate horizontal swipes turn pages (#764).
- **Notes and highlights export** — a Markdown zip, one file per book and one per topic, with `[[Topic]]` links kept (#1221).
- **Several books at once** — add several EPUB or PDF files from Add a book (#1439).
- **Library transfer** — whole-library export and import from Settings and onboarding (#736).

---

## 1. Reading Experience

### 1.1 Bookmarking
**Impact:** Medium | **Effort:** Medium | **Area:** Reading

No bookmark model or UI exists. Imported Kindle bookmarks are skipped because they mark a place and hold no text, and a Kobo bookmark matters only when it carries a highlight.

- Add a bookmark icon in the reader toolbar; saving the current position is silent
- Bookmarks panel in the sidebar, with labels; tap to jump, long-press to rename or delete
- Visual bookmark marks in the table of contents

**Data model:** `Bookmark { id, bookId, location (CFI/pageNumber/timestamp), label?, createdAt }`

**Files to touch:** `reader-shell.component.*`, `books.service.ts`, the book endpoints and repository in `Nostos.Product`

---

### 1.2 Highlights View
**Impact:** Low | **Effort:** Low | **Area:** Reading

A highlight no longer needs a note: it can be saved on the page with one of four colour pens (#863), and notes and highlights export as Markdown (#1221). There is still no per-book view that lists only the highlights.

- "Highlights" view per book, separate from notes
- Optional colour coding (the pen is already stored per highlight)

No open issue.

---

## 2. Library Management

### 2.1 Filter by Reading Progress
**Impact:** Medium | **Effort:** Low | **Area:** Library

Reading-state filters exist — Not started, Reading, Finished and Favourites — and compose with search and collections (#46). A filter on how far through a book you are does not.

- Add "Near finished" (>80%)
- Add a range filter: "Books between X% and Y%"

No open issue.

---

### 2.2 Moving into Nostos, and trusting the import (epic #1532)
**Area:** Library / Export

The highlight import is solid: Kobo, KOReader, Kindle, Apple Books and a Readwise CSV, with content detection, a preview before writing, matching, skip reasons, dedupe and undo per batch (#656, #1225, #1453). Several EPUB or PDF files can be added at once (#1439), and a whole library moves from Settings → Import (#736).

Still missing, from epic #1532:
- **#1521** Reading history from Goodreads and StoryGraph — priority: high
- **#1527** A Calibre library — priority: low
- **#1526** One drop zone for every import file — priority: medium
- **#1522** A stored report per import: found, imported, already here, skipped by reason — priority: medium
- **#1523** Log the shape (never the content) of files that fail to parse — priority: medium
- **#1524** Real, versioned Kobo and Apple Books fixtures — priority: medium
- **#1525** Readwise export API, with a weekly format canary — priority: medium

OPDS feed subscription is §7.1, not an import.

---

### 2.3 Author Filter and Series Navigation
**Impact:** Medium | **Effort:** Low | **Area:** Library

Search by author works; there is no author page, and a series is plain text on the book page, not a way to move.

- Author page: every book by that author, grouped by series
- Series page: every book in the series with its reading state
- Author and series names are links

**Files to touch:** `library.component.*`, `book-detail.component.*`, an author listing endpoint

No open issue.

---

## 3. Brain

### 3.1 The Brain note list uses the page (epic #1533)
**Area:** Brain

On a wide screen the note list is a left-aligned column of about 52rem: roughly 40% of the page is empty, and the sidebar cannot close because closing it would only add empty space. The list also gives no sense of time in date order. Epic #1533 covers:

- **Now:** **#1528** centre the list and let the sidebar close, as in the Library (quick win); **#1529** date group headers in Newest first and Oldest first (quick win)
- **Next, after a mockup of both at 1440 and 1920:** **#1531** list and detail panes (preferred); **#1530** a context rail for the current place
- **Not planned:** a card or masonry grid — long notes read badly in narrow cards

---

## 4. Writing Studio

### 4.1 Writing Goals and Stats
**Impact:** Low | **Effort:** Medium | **Area:** Studio

The open document shows words, characters and reading time (#1059), but nothing is kept over time.

- Writing streak
- Daily and weekly word-count goals
- Session writing time per document
- Shown in the Studio sidebar or a separate dashboard

No open issue.

---

## 5. Mobile & PWA

### 5.1 Touch Gesture Navigation
**Impact:** Medium | **Effort:** Medium | **Area:** Mobile

Edge taps and deliberate horizontal swipes turn pages in paged EPUB and PDF readings (#764). Missing:

- Pinch-to-zoom inside the PDF page (the browser zooms the whole page; the reader has zoom presets)
- Double-tap to bookmark in the EPUB reader (bookmarking is §1.1)

---

### 5.2 BreakpointObserver Refactor
**Impact:** Low | **Effort:** Low | **Area:** Mobile

The Studio still decides its compact layout from `window.innerWidth`; the settings popover already uses CDK `BreakpointObserver`.

- Replace `isStudioCompactViewport()` with `BreakpointObserver`
- React to orientation and window changes without re-reading `window.innerWidth`
- Use one definition of "compact" across the Studio

No open issue.

---

## 6. Onboarding

### 6.1 Guided First-Run Experience
**Impact:** High | **Effort:** High | **Area:** Onboarding

Nostos Cloud has first-run signup, provisioning and recovery (#409), and the first run offers a library import (#736). There is no in-app guided tour.

- Welcome on first visit
- Guided steps: add a book, create a note, link a topic, connect an OPDS app
- Skip and "Don't show again"
- Progress indicator

No open issue.

---

### 6.2 Sample Library / Demo Content
**Impact:** Medium | **Effort:** Medium | **Area:** Onboarding

The empty library is bare. There is no sample content.

- "Load sample library" in the empty state
- A bundled sample book with notes and topics
- Shows the full surface without setup

No open issue.

---

## 7. Export & Interoperability

### 7.1 OPDS Subscription Updates
**Impact:** Medium | **Effort:** Medium | **Area:** Export

OPDS is a read-only 1.2 acquisition catalog (#186): other apps can browse and download, but Nostos cannot subscribe to a feed, and progress cannot come back.

- OPDS 2.0 or updated 1.2 with pagination
- Incremental feed updates
- Reading progress from third-party apps

No open issue.

---

## 8. Ask Nostos

### 8.1 Assess and polish Ask Nostos (spike #1464)
**Priority:** High | **Area:** Ask Nostos

Ask Nostos was built piece by piece and does not yet feel like one product: the UI is dated, results vary, and there is no clear statement of what it can do. #1464 is an assessment and a plan — it produces a short plan document and sub-issues, not the fixes themselves. It is the open product-direction item for the assistant.

---

## Prioritization Matrix

| # | Item | Impact | Effort | Quick Win? |
|---|------|--------|--------|-----------|
| 1 | Bookmarking | Medium | Medium | |
| 2 | Highlights View | Low | Low | Yes |
| 3 | Moving into Nostos (#1532) | — | — | Priority: high (#1521) |
| 4 | Author Filter / Series Navigation | Medium | Low | Yes |
| 5 | Brain note list (#1533) | — | — | Quick wins: #1528, #1529 |
| 6 | Reading-Progress Filter (percentage range) | Medium | Low | Yes |
| 7 | Writing Goals and Stats | Low | Medium | |
| 8 | Touch Gestures | Medium | Medium | |
| 9 | BreakpointObserver | Low | Low | Yes |
| 10 | Guided Onboarding | High | High | |
| 11 | Sample Library | Medium | Medium | |
| 12 | OPDS Subscription | Medium | Medium | |
| 13 | Ask Nostos polish (#1464) | — | — | Priority: high |

The epics and #1464 have no impact/effort score; use their issue priorities.

---

## Out of Scope

- Collaboration / multi-user
- Web clipper / browser extension
- Social features or sharing
- Mobile native apps (#305, closed as not planned)
- Offline downloads (#1104, closed as not planned and parked on the private board)
- A card or masonry grid for the Brain (#1533 records it as not planned)

---

## Appendix: Key Files Reference

| Area | Key Files |
|------|-----------|
| Reading | `reader-shell.component.*`, `epub-reader.component.*`, `pdf-reader.component.*`, `audio-reader.component.*` |
| Library | `library.component.*`, `sidebar-collections.component.*`, `add-book-modal.component.*`, `book-detail.component.*` |
| Brain | `brain-shell.component.*`, `brain-list.page.*`, `topic-map.component.*`, `note-item.component.*` |
| Studio | `writing-studio.component.*`, `flat-tree.component.*`, `markdown-editor.component.*` |
| Import | `Nostos.Product/Services/Notes/Imports/*`, `settings.component.*` |
| Global | `styles.css`, `app.config.ts` |
