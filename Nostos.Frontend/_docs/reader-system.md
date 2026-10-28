# Frontend — Reader System

## Overview

The reader supports three file formats through a unified shell. Each format has its own reader component implementing a shared `IReader` interface. The shell (`ReaderShell`) auto-detects the file type and renders the appropriate reader.

## Architecture

```
ReaderShell (app-reader-shell)
├── EpubReader   ─ epub.js
├── PdfReader    ─ ngx-extended-pdf-viewer
└── AudioReader  ─ Howler.js
```

**File type detection:**
| Extensions | Reader | Library |
|---|---|---|
| `.epub` | `EpubReader` | epub.js |
| `.pdf` | `PdfReader` | ngx-extended-pdf-viewer |
| `.m4b`, `.m4a`, `.mp3` | `AudioReader` | Howler.js (via Howl) |

## IReader Interface

**File:** `src/app/reader/reader.interface.ts`

```ts
interface IReader {
  toc: Signal<TocItem[]>;
  progress: Signal<ReaderProgress>;

  next(): void;
  previous(): void;
  goTo(target: string | number): void;
  getCurrentLocation(): string;
  zoomIn(): void;
  zoomOut(): void;
  removeHighlight(identifier: string): void;
}
```

**Supporting types:**

```ts
interface TocItem {
  label: string;
  target: string | number;
  children?: TocItem[];
}
interface ReaderProgress {
  label: string;
  percentage: number;                 // book-relative: what is saved and synced
  chapterPercentage?: number | null;  // 0–100 through the current chapter; null = no chapter
  chapterLabel?: string | null;       // the chapter it measures
  tooltip?: string;
  pageNumber?: number;
  pageCount?: number;
}
```

## ReaderShell (Orchestrator)

**File:** `src/app/reader/reader-shell.component.ts`

- Loads `Book` and `Note[]` from backend on init
- Selects the reader `@ViewChild` based on `fileType` computed signal
- Delegates all navigation/zoom to `activeReader()`
- Manages the unified notes sidebar and quick-note creation
- Saves reading progress (debounced) on every page turn

### Layout

EPUB and PDF use an immersive shell: the document owns the full viewport and
shared chrome is overlaid outside document flow. The resting state has no
persistent top/bottom bars, so revealing controls cannot resize an EPUB
rendition or shift a PDF scrollport. Revealed controls use a seamless edge
header and pager rather than floating cards; book titles remain visible on
phones. EPUB uses stable vertical reading insets and a maximum 80rem spread
measure. Narrow/Normal/Wide add 0/4/8% insets inside that capped spread, so
all three remain distinct on wide monitors. PDF reserves first-page clearance
for the header. Safe areas are paid by the active header or bottom surface; the
audio player also pads its body around the device insets.

```
┌──────────────────────────────────────────────────────┐
│ ← Title / Author                    Contents Notes    │ ← shared edge header
│                                                      │
│                 EPUB / PDF canvas                    │
│            or Audio cover and player                 │ ← audio stays visible
│                                                      │
│              ‹ quiet progress track ›                │ ← EPUB/PDF only
└──────────────────────────────────────────────────────┘
```

A neutral surface click/tap reveals or hides the shared controls. On coarse
pointers EPUB also supports edge-tap and deliberate horizontal-swipe paging;
PDF only enables those paging gestures in its explicit page-by-page mode, so
continuous PDF scrolling stays native. Text selection, authored links/controls,
and the contextual selection actions take priority over reader gestures.
The PDF adapter identifies PDF.js's focusable text layer as a reading surface;
links and nested focusable controls retain priority. On phones, selection
utilities and annotation choices use two rows. While a reader selection is
active, its contextual Ask Nostos action replaces the global floating launcher.

TOC and Notes remain overlay panels (full-width on mobile), and Search/View
settings remain transient overlays. Audio keeps its player and transport visible
at all times rather than adopting EPUB/PDF paging interactions. Its persistent
header uses the same seamless edge treatment and safe-area spacing, with the book
title and author shown once. The cover, scrubber, transport and Playback control
stay in the reader body; the Playback settings panel overlays that body without
resizing the player.

### Quick Note Flow

1. User selects text in the reader (epub/pdf) or clicks "Add note" (audio)
2. Quick-note textarea appears with the selected text pre-quoted
3. User types note content, optionally adding `[[Topic]]` tags with autocomplete
4. Save → `NotesService.create()` → highlights are updated → sidebar refreshes

### Progress Sync

Progress is saved on every page turn / audio position change:

- `location`: CFI string (epub), page number (pdf), time in seconds (audio)
- `percentage`: 0–100 normalized progress

The EPUB/PDF bar can show **book or chapter** progress (#1269). Tapping the bar
(a button with a 44px hit area around the 2px line) switches it; the choice is
reader-wide at `nostos.reader-progress-scope`. It is presentation only: the
readers always report both values and save `percentage`. A book whose TOC or
outline places no chapter keeps the book bar and has no toggle. The EPUB label
reads `62% of 7` in chapter mode; PDF keeps its page field and the bar's title
names the mode. Chapter ranges come from `epub-chapter-progress.ts` (TOC → spine
ranges, then epub.js locations; until locations exist, the section's own
pagination) and `pdf-chapter-progress.ts` (outline pages). Several TOC entries
inside one EPUB file share one range.

## EpubReader

**File:** `src/app/reader/epub-reader/epub-reader.component.ts`

- Renders via `epub.js` `Rendition` into a container div
- **Saved position on open:** the saved CFI is restored through the same settle protection as a grounded source (re-asserted through the opening re-layouts, then verified on the visible page). Progress writes stay closed until it is confirmed or given up; neither writes a position, so opening a book never overwrites the saved one. A page turn during the settle wins and is saved (#1271).
- **Location caching:** On first open, generates all locations and sends to `BooksService.saveLocations()`. On subsequent opens, loads cached locations for instant percentage calculation.
- **Text selection:** Handled by `EpubAnnotationManager`
- **Highlights:** Yellow background highlights on annotated passages, stored by CFI range
- **Keyboard:** Left/Right arrows for page navigation

### EpubAnnotationManager

**File:** `src/app/reader/epub-reader/epub-annotation-manager.ts`

Non-injectable class, manually instantiated. Manages:

- Listening for epub.js `'selected'` events
- Extracting selected text from CFI range
- Optimistic highlight rendering (add immediately, roll back on API error)
- Restoring highlights from saved notes
- Injecting yellow highlight CSS into the epub iframe

## PdfReader

**File:** `src/app/reader/pdf-reader/pdf-reader.component.ts`

- Wraps `ngx-extended-pdf-viewer` with custom configuration
- Page-level progress tracking
- Text selection → `PdfAnnotationManager.captureHighlight()`
- Highlight rectangles painted onto the text layer via `PdfAnnotationManager.paint()`

### PdfAnnotationManager

**File:** `src/app/reader/pdf-reader/pdf-annotation-manager.ts`

`@Injectable({ providedIn: 'root' })`. Methods:

- `paint(textLayerDiv, highlights)` — renders percentage-positioned rectangles
- `captureHighlight()` — reads native selection, normalizes rectangles relative to page
- `captureNoteLocation()` — returns `{ pageNumber, yPercent }` for generic annotations

## AudioReader

**File:** `src/app/reader/audio-reader/audio-reader.component.ts`

- Uses `Howler.js` (`Howl` instance) for audio playback
- Builds chapter navigation from `Book.chapters` when the file has chapter metadata
- **Play/Pause**, **Skip ±15s**, **Playback speed** (0.5×–3×, with presets and ±0.05× fine steps)
- TOC shows chapter titles; clicking jumps to `startTime`
- **Current chapter:** `currentChapter` (a computed over `currentTime` and `toc`) is the one source for the chapter line on the player, the assistant's `audioChapter` and Media Session. The line is a button between the cover and the scrubber that emits `contentsRequested`; the shell opens Contents with the active row in view. It is rendered only when the book has chapters and is positioned out of flow in the cover/scrubber gap, so the player has the same geometry with and without it
- Playback settings include speed and sleep timers, including stopping at the chapter end
- Progress bar: current time / total duration
- Saves playback progress and resumes from the last saved position; updates MediaSession metadata and controls (with chapters: chapter as `title`, book as `album`, rewritten only when the chapter changes)
- **Note time:** A quick note is saved at the playback time it was started at, not the time Save was pressed. The time is pinned on the first keystroke, shown beside Save while drafting (`at 12:34`), and stored both as `cfiRange` (seconds) and as a verified `audio_timestamp` anchor. Saved notes show it on their card (`m:ss` or `h:mm:ss`); activating it jumps there

## Notes Sidebar

Common across all reader types. Displays all `Note[]` for the book.

Features:

- **Jump to note:** "Show in book" on a note with a `cfiRange` navigates the epub; one with a page number navigates the PDF; an audiobook note jumps to its time, from "Show in book" or from the time shown on the note
- **Same note as the Brain:** Uses `NoteItemComponent` (`inBook`), with its topics as chips (read from the Brain's listing of the book), the topic picker, Keep with writing, and Edit / Delete from its `⋯` menu
- **Delete:** With confirmation
- **Topic tags:** `[[Topic]]` links rendered via `NoteFormatPipe`, clickable to open the topic in the Brain
