# Nostos — Product Roadmap

**Status:** Idea Mode  
**Last Updated:** March 2026  
**Version:** 1.0

---

> **Reviewed 2026-08-12:** several items below have since shipped — OPDS
> catalog, Reading Training, MCP, and backup. See the README feature list for
> the current surface; the items here are left untouched as historical.

## How to Read This Document

Each item is tagged with:
- **Impact** — High / Medium / Low
- **Effort** — Low (< 1 sprint) / Medium (1 sprint) / High (> 1 sprint)
- **Area** — Reading / Library / Brain / Studio / Mobile / Onboarding / Export

Items are grouped by area and sorted by impact × effort priority within each group.

---

## Recently Completed

- **Work / Multi-Edition Grouping & Matching Engine** (September 2026) — Group multiple editions (eBook, Audiobook, Physical) under a shared WorkModel, edition switcher on Book Detail page, format chips on Library cards, and smart non-collapsing title/author matcher.
- **Library Preferences & State Persistence** (September 2026) — Centralized `LibraryPreferencesService` persisting viewMode, activeSort, pageSize, sidebarExpanded across browser reloads.
- **Multi-Collection Book Assignment** (September 2026) — A book can belong to any number of collections. Replaces the single-collection dropdown with a multi-select picker (chips + checkbox tree) in the Add/Edit modal, adds `PUT /api/books/{id}/collections` (full replacement set), and drops the legacy single-value `Books.CollectionId` column so `BookCollections` is the only record of membership. Also fixed: removing a book from a collection previously did nothing — REST hardcoded `ClearCollection: false`, so "No collection" silently reported success.
- **Collections Sidebar Polish** (September 2026) — Persistent collapsed/expanded state, real-time live status count badges, refined tree action animations without layout jumps.
- **Backup & Restore System** (April 2026) — Automated scheduling, manual triggers, disk scanning, and integrity verification.
- **Maintenance Mode Middleware** (April 2026) — Standardized API protection during restore operations.
- **Project Standardization** (April 2026) — Port 5099, absolute path database resolution, and PM2 integration.

---

## 1. Reading Experience

### 1.1 Dark Mode / Reading Themes
**Impact:** High | **Effort:** Low | **Area:** Reading

The app currently ships with one light theme. Most reading apps (Kindle, Apple Books, Readwise) offer dark and sepia modes.

- Add CSS variable sets for: Dark, Sepia, AMOLED Black
- Add theme switcher in reader toolbar (sun/moon/book icons)
- Persist theme preference per-book in localStorage
- Apply via epub.js `themes.register()` and PDF CSS overrides

**Files to touch:** `styles.css`, `reader-shell.component.*`, `epub-reader.component.ts`, `pdf-reader.component.ts`

---

### 1.2 Typography Controls Panel
**Impact:** High | **Effort:** Medium | **Area:** Reading

Only EPUB has font size (50-200%). No font family, line height, or margin controls exist.

- Add collapsible "Aa" button in reader toolbar
- Panel with: Font size (50-200%), Font family (Lora, Inter, monospace), Line height (1.4–2.0), Margins (Narrow/Normal/Wide), Text alignment (Justified/Left)
- Persist typography settings per-book
- Apply to EPUB via `rendition.themes.fontSize()` and custom CSS; to PDF via zoom + CSS overrides

**Files to touch:** `reader-shell.component.*`, `epub-reader.component.ts`, `pdf-reader.component.ts`, `styles.css`

---

### 1.3 PDF Enhancement
**Impact:** Medium | **Effort:** Medium | **Area:** Reading

PDF reader has annotation editing disabled, no font size control, and in-book search disabled.

- Enable `showTextEditor="true"` for text annotation
- Enable `findButton` for in-book search
- Add font size control (zoom is available but not discoverable)
- Add highlight color options (yellow, green, blue, pink)
- Persist highlight colors per-book

**Files to touch:** `pdf-reader.component.*`, `pdf-annotation-manager.ts`

---

### 1.4 Bookmarking System
**Impact:** Medium | **Effort:** Medium | **Area:** Reading

No bookmarking exists. Users cannot quickly mark a position and return.

- Add bookmark icon in reader toolbar
- Click saves current position silently to backend
- Bookmarks panel in sidebar shows all saved positions with labels
- Tap bookmark to jump; long-press to delete or rename
- Visual bookmark indicators in TOC

**Data model:**
```
Bookmark { id, bookId, location (CFI/pageNumber/timestamp), label?, createdAt }
```

**Files to touch:** `reader-shell.component.*`, `books.service.ts`, backend `BooksRepository`

---

### 1.5 Audio Enhancements
**Impact:** Medium | **Effort:** Low | **Area:** Reading

Audio player missing common audiobook features.

- Sleep timer (15min, 30min, 45min, end of chapter)
- Speed presets: add 1.75x, 2x, 2.5x beyond current 1.5x cap
- Chapter skip buttons (not just ±15s)
- Remember playback speed per audiobook

**Files to touch:** `audio-reader.component.*`

---

### 1.6 Reading Progress Dashboard
**Impact:** Low | **Effort:** Medium | **Area:** Reading

Progress is only visible inline in readers. No holistic view of reading habits.

- New "Reading Stats" section accessible from Library
- Metrics: Books completed, pages read, hours listened, current reading streak
- Simple bar/line visualizations
- Per-book reading history (started, finished, time spent)

---

### 1.7 Highlights as Primary Artifact
**Impact:** Low | **Effort:** Low | **Area:** Reading

Highlights are secondary to notes. Users may want to collect highlights without writing notes.

- "Highlights" view per book — shows all highlights without associated notes
- ~~Bulk highlight export (Markdown, plain text)~~ Shipped in #1221: see 7.2.
- Optional highlight color coding

---

## 2. Library Management

### 2.1 Bulk Operations
**Impact:** High | **Effort:** High | **Area:** Library

No multi-select exists. Each book can only be acted on individually.

- Add checkbox multi-select on book cards/rows
- Batch actions: Delete, Move to Collection, Update reading status, Rate
  - "Move to Collection" can now reuse `CollectionPickerComponent` and the `PUT /api/books/{id}/collections` set endpoint; add/remove deltas apply per book
- "Select all in current view" option
- Confirmation dialog for destructive batch operations

**Files to touch:** `library.component.*`, `books.service.ts`, backend batch endpoints

---

### 2.2 Library Import (Calibre / OPDS / Bulk)
**Impact:** Medium | **Effort:** High | **Area:** Library

No import from Calibre, OPDS, or bulk file drop exists.

- Calibre metadata import via `calibre-tools` or direct SQLite access
- OPDS feed subscription/import
- Bulk file drop — scan folder for EPUB/PDF/audio files, auto-match via ISBN/filename
- Progress indicator for large imports

---

### 2.3 Drag Books into Collections
**Impact:** Medium | **Effort:** Medium | **Area:** Library

Collections can be drag-reordered, but books cannot be dragged into collections from the grid.

Membership itself is now many-to-many (`PUT /api/books/{id}/collections`, and the
multi-select picker in the book modal), so this item is only about the *gestures*
— the write path already exists.

- Enable CDK drag-drop from book grid to collection sidebar item
- Visual drop indicator on collection folders
- "Move to..." context menu on book cards (would reuse `CollectionPickerComponent`)

**Files to touch:** `library.component.*`, `sidebar-collections.component.*`

---

### 2.4 Filter by Reading Progress
**Impact:** Medium | **Effort:** Low | **Area:** Library

Cannot filter to "books 50% complete" or similar.

- Add progress-based filter: Not Started, In Progress, Near Finished (>80%), Finished
- Add slider filter: "Books between X% and Y%"
- Combine with existing status filters

**Files to touch:** `library.component.*`, backend filter logic

---

### 2.5 Author Filter and Series Navigation
**Impact:** Medium | **Effort:** Low | **Area:** Library

Can search by author but cannot filter to all books by one author. Series info is shown but not navigable.

- Author page: all books by that author, grouped by series
- Series detail: shows all books in series with read status
- Click author name → author page

**Files to touch:** `library.component.*`, backend: author listing endpoint

---

### 2.6 Grid/List Preference Persistence
**Impact:** Low | **Effort:** Low | **Area:** Library

View mode choice is not persisted across sessions.

- Save preference to localStorage
- Apply on app load

---

### 2.7 Collection Book Count
**Impact:** Low | **Effort:** Low | **Area:** Library

Sidebar shows collection name but not book count.

- Show count badge on each collection in sidebar
- Compute from backend or derive from existing data

---

## 3. Knowledge Graph (Brain)

### 3.1 Concept Graph Visualization
**Impact:** High | **Effort:** High | **Area:** Brain

No graph visualization exists. The concept system is purely list-based.

- Add graph view toggle in Brain (list ↔ graph)
- Use D3.js force-directed or Cytoscape.js for visualization
- Nodes = concepts, edges = co-occurrence in notes
- Zoom, pan, click to navigate
- Performance: lazy-load edges, limit to N most-connected concepts

**Files to touch:** `second-brain.component.*`, new graph component, add D3/Cytoscape dep

---

### 3.2 Orphan Concept Management UI
**Impact:** Medium | **Effort:** Medium | **Area:** Brain

Orphan cleanup runs hourly in background. Users cannot see or manage orphaned concepts.

- "Orphaned concepts" section in Brain settings/view
- Show concepts with zero linked notes
- Actions: Delete, Merge into another concept, Add note to rescue

**Files to touch:** `second-brain.component.*`, backend orphan endpoint

---

### 3.3 Concept Creation / Deletion UI
**Impact:** Medium | **Effort:** Medium | **Area:** Brain

Concepts are only auto-created via `[[Name]]` syntax in notes. No manual creation or deletion.

- "New Concept" button in Brain
- Manual concept deletion with confirmation
- "Protect from orphan" concept toggle (prevent auto-cleanup)

---

### 3.4 Concept Merging and Renaming
**Impact:** Low | **Effort:** Medium | **Area:** Brain

No way to merge duplicate concepts or rename them.

- Select source → merge into target concept
- All `[[OldName]]` references across notes auto-update
- Rename with same reference update

---

### 3.5 Usage Count Sorting Toggle
**Impact:** Low | **Effort:** Low | **Area:** Brain

Cannot toggle sort order in concept index.

- Add sort options: Most used first (default), A-Z, Z-A, Newest
- Persist preference

---

## 4. Writing Studio

The Writing Studio has a three-panel layout (file tree | editor | context sidebar) that feels like a code editor, not a writing environment. Writers need immersion and focus features.

### 4.1 Zen Mode
**Impact:** High | **Effort:** Medium | **Area:** Studio

Full-screen writing environment. Hides the dock, file tree, context sidebar, and toolbar — editor fills the entire viewport.

- Toggle via `Cmd/Ctrl+Shift+Z` or a "Zen" button in editor header
- Escape key or `Cmd/Ctrl+\` to exit
- Floating word count bar at bottom (fades after 3s of no interaction, reappears on mouse move)
- Background: subtle paper texture or solid dark/sepia per theme
- Remembers last mode (Zen or normal) per document

**Files to touch:** `writing-studio.component.*`, `styles.css`

---

### 4.2 Typewriter Mode
**Impact:** Medium | **Effort:** Low | **Area:** Studio

Long documents cause fatigue when the cursor is at the top of the screen. Typewriter mode keeps the current line vertically centered.

- Toggle in editor toolbar or Zen mode options
- Current line stays fixed at 40-50% from viewport top
- Smooth scroll follows cursor — document scrolls beneath a fixed cursor position
- Works in both normal and Zen mode

**Files to touch:** `writing-studio.component.*`, `markdown-editor.component.*`

---

### 4.3 Reference Overlay (Zen-Compatible)
**Impact:** Medium | **Effort:** Medium | **Area:** Studio

In Zen mode, users still need to pull in concepts and notes without restoring the full three-column layout.

- `Cmd/Ctrl+O` opens concepts/books as a floating overlay panel
- Search and click to insert reference, panel auto-hides when you start typing
- Last 3 inserted references shown as dismissible pills above the panel
- Works in Zen mode; does not restore full layout

**Files to touch:** `writing-studio.component.*`, `concept-autocomplete.*`

---

### 4.4 Writing Stats Bar
**Impact:** Low | **Effort:** Low | **Area:** Studio

Writers want feedback without switching context.

- Persistent bar at bottom: word count, character count, estimated reading time
- Toggle visibility per preference
- Fades in Zen mode until mouse movement

**Files to touch:** `writing-studio.component.*`

---

### 4.5 Writing Goals and Stats
**Impact:** Low | **Effort:** Medium | **Area:** Studio

No writing productivity features exist yet.

- Writing streak tracker
- Word count goals (daily/weekly)
- Session writing time per document
- Display in Studio sidebar or separate dashboard

---

### 4.6 Improved Reference Insertion
**Impact:** Low | **Effort:** Low | **Area:** Studio

Reference insertion currently only supports blockquote format.

- Insert as footnote-style reference
- Insert as hyperlinked title
- Remember last-inserted reference

---

## 5. Mobile & PWA

### 5.1 PWA Installability
**Impact:** High | **Effort:** Medium | **Area:** Mobile

Not installable as an app. No manifest, no service worker.

- Add `manifest.webmanifest` with icons, name, theme colors
- Configure Angular service worker (`ngsw-config.json`)
- Register service worker in `app.config.ts`
- Add maskable icons for home screen

---

### 5.2 Touch Gesture Navigation
**Impact:** Medium | **Effort:** Medium | **Area:** Mobile

No swipe gestures for page navigation in readers.

- Swipe left/right to navigate pages in EPUB/PDF
- Pinch-to-zoom for PDF
- Double-tap to bookmark in EPUB
- Integrate HammerJS or implement via touch event listeners

---

### 5.3 BreakpointObserver Refactor
**Impact:** Low | **Effort:** Low | **Area:** Mobile

Writing Studio uses manual `window.innerWidth` checks instead of Angular CDK `BreakpointObserver`.

- Replace `isMobile = signal(window.innerWidth < 768)` with `BreakpointObserver`
- More robust handling of orientation changes
- Consistent with Angular best practices

---

## 6. Onboarding

### 6.1 Guided First-Run Experience
**Impact:** High | **Effort:** High | **Area:** Onboarding

No onboarding flow. New users land directly in an empty library.

- Welcome modal on first visit
- Guided steps: (1) Add your first book, (2) Create a note, (3) Link a concept, (4) Explore OPDS
- Skip option with "Don't show again"
- Progress dots / step indicator

---

### 6.2 Sample Library / Demo Content
**Impact:** Medium | **Effort:** Medium | **Area:** Onboarding

Empty state is blank. Users don't know what the app looks like with content.

- "Load sample library" button in empty state
- Bundled sample EPUB with pre-populated notes and concept links
- Demonstrates the full feature set without any setup

---

## 7. Export & Interoperability

### 7.1 OPDS Subscription Updates
**Impact:** Medium | **Effort:** Medium | **Area:** Export

OPDS catalog is read-only. Cannot subscribe to updates from other apps.

- OPDS 2.0 or updated 1.2 with pagination
- Incremental feed updates
- Allow third-party apps to sync reading progress back via OPDS

---

### 7.2 Export Highlights and Notes
**Impact:** Medium | **Effort:** Low | **Area:** Export

**Shipped (#1221).** Settings → Backups & data → **Notes as Markdown**
downloads a .zip with one Markdown file per book (metadata as YAML front
matter, highlights, passages and notes in reading order with their
locations), one file per topic, the Writing Studio's documents if wanted, and
a README. `[[Topic]]` links are kept, so the folder opens as an Obsidian
vault. The whole library as data (JSON) is the `.nostos` archive.

---

## Prioritization Matrix

| # | Item | Impact | Effort | Quick Win? |
|---|------|--------|--------|-----------|
| 1 | Dark/Sepia Themes | High | Low | Yes |
| 2 | PWA Installability | High | Medium | |
| 3 | Typography Controls | High | Medium | |
| 4 | Bulk Operations | High | High | |
| 5 | Guided Onboarding | High | High | |
| 6 | Concept Graph Viz | High | High | |
| 7 | Library Import | Medium | High | |
| 8 | Grid/List Persistence | Low | Low | Yes |
| 9 | Collection Book Count | Low | Low | Yes |
| 10 | Reading Progress Filter | Medium | Low | Yes |
| 11 | Author Filter / Series Nav | Medium | Low | Yes |
| 12 | Audio Enhancements | Medium | Low | Yes |
| 13 | Export Highlights/Notes | Medium | Low | Yes |
| 14 | Highlights View | Low | Low | Yes |
| 15 | Touch Gestures | Medium | Medium | |
| 16 | PDF Enhancement | Medium | Medium | |
| 17 | Bookmarking | Medium | Medium | |
| 18 | Drag Books to Collections | Medium | Medium | |
| 19 | Orphan Concept UI | Medium | Medium | |
| 20 | Concept Create/Delete UI | Medium | Medium | |
| 21 | OPDS Subscription | Medium | Medium | |
| 22 | BreakpointObserver | Low | Low | Yes |
| 23 | Writing Goals | Low | Medium | |
| 24 | Reading Progress Dashboard | Low | Medium | |
| 25 | Concept Merge/Rename | Low | Medium | |

---

## Out of Scope

- Collaboration / multi-user
- Cloud sync (self-hosted is a design pillar)
- Web clipper / browser extension
- AI summarization or recommendations
- Social features or sharing
- Mobile native apps

---

## Appendix: Key Files Reference

| Area | Key Files |
|------|-----------|
| Reading | `reader-shell.component.*`, `epub-reader.component.*`, `pdf-reader.component.*`, `audio-reader.component.*` |
| Library | `library.component.*`, `sidebar-collections.component.*`, `add-book-modal.component.*`, `book-detail.component.*` |
| Brain | `second-brain.component.*`, `concept-autocomplete.*`, `note-item.component.*` |
| Studio | `writing-studio.component.*`, `flat-tree.component.*`, `markdown-editor.component.*` |
| Global | `styles.css`, `app.config.ts` |
