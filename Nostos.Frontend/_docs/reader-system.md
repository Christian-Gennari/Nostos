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
  percentage: number;
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
all three remain distinct on wide monitors. PDF reserves first-page clearance for the header. Safe areas are paid
by the active bottom surface, including contextual selection actions.

```
┌───────────────────────────────────────────────────┐
│ ← Title                       TOC Notes Search Aa  │ ← transient overlay
│                                                   │
│                  Reader Canvas                    │
│                    EPUB / PDF                     │
│                                                   │
│                ‹   Progress   ›                   │ ← transient overlay
└───────────────────────────────────────────────────┘
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
settings remain transient overlays. Audio keeps its established persistent
player shell rather than adopting EPUB/PDF paging interactions.

### Quick Note Flow

1. User selects text in the reader (epub/pdf) or clicks "Add note" (audio)
2. Quick-note textarea appears with the selected text pre-quoted
3. User types note content, optionally adding `[[Topic]]` tags with autocomplete
4. Save → `NotesService.create()` → highlights are updated → sidebar refreshes

### Progress Sync

Progress is saved on every page turn / audio position change:

- `location`: CFI string (epub), page number (pdf), time in seconds (audio)
- `percentage`: 0–100 normalized progress

## EpubReader

**File:** `src/app/reader/epub-reader/epub-reader.component.ts`

- Renders via `epub.js` `Rendition` into a container div
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
- Displays chapter list from `Book.chapters` (parsed from M4B metadata by backend)
- **Play/Pause**, **Skip ±30s**, **Playback speed** (0.5×–3×)
- TOC shows chapter titles; clicking jumps to `startTime`
- Progress bar: current time / total duration
- **Add timestamp:** Inserts `[mm:ss]` into quick-note textarea

## Notes Sidebar

Common across all reader types. Displays all `Note[]` for the book.

Features:

- **Jump to note:** Clicking a note with a `cfiRange` navigates the epub; clicking one with a page number navigates the PDF
- **Edit inline:** Uses `NoteCardComponent` with topic tag support
- **Delete:** With confirmation
- **Topic tags:** `[[Topic]]` links rendered via `NoteFormatPipe`, clickable to navigate to Second Brain
