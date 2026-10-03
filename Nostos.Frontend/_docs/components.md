# Frontend — Component Architecture

## Overview

Nostos Frontend is an Angular 21 SPA using **standalone components** exclusively (no `NgModule`). State is managed via Angular **Signals**. Change detection uses `OnPush` where performance-critical.

## Component Hierarchy

```
App (app-root)
├── ToastContainerComponent (app-toast-container)
└── <router-outlet>
    │
    ├── Home (app-home)                          [route: /]
    │
    ├── WorkspaceLayout (app-workspace-layout)   [route: / with children]
    │   ├── AppDockComponent (app-app-dock)
    │   └── <router-outlet>
    │       ├── Library (app-library)            [route: /library]
    │       │   └── SidebarCollections           [route: /library]
    │       │       └── FlatTreeComponent
    │       │
    │       ├── BookDetail (app-book-detail)     [route: /library/:id]
    │       │   ├── StarRatingComponent
    │       │   ├── NoteCardComponent (many)
    │       │   │   └── TopicInputComponent
    │       │   │       └── TopicAutocompletePanel
    │       │   └── AddBookModal (edit mode)
    │       │
    │       ├── SecondBrain (app-brain)          [route: /second-brain]
    │       │   └── NoteCardComponent (many)
    │       │
    │       └── WritingStudio (app-writing-studio) [route: /studio]
    │           ├── FlatTreeComponent
    │           ├── MarkdownEditorComponent
    │           └── NoteCardComponent (many)
    │
    └── ReaderShell (app-reader-shell)           [route: /read/:id]
        ├── EpubReader (app-epub-reader)
        ├── PdfReader (app-pdf-reader)
        ├── AudioReader (app-audio-reader)
        ├── NoteCardComponent (many)
        └── TopicInputComponent
            └── TopicAutocompletePanel
```

## Page Components

### Home (`/`)

Landing page with navigation links. Simple component with title signal.

### Library (`/library`)

Main book grid/list view. Features:

- **Grid / List toggle** — persisted view mode
- **Search** — debounced text search (queries backend)
- **Sort** — Recent, Title, Rating, LastRead
- **Filter** — via query params (`?filter=reading`, etc.) or collection selection
- **Infinite scroll** — `InfiniteScrollDirective` triggers page loads
- **Optimistic UI** — favorite/finished/rating toggles update immediately
- **Add book modal** — tabbed form with ISBN lookup
- Uses `OnPush` change detection

### BookDetail (`/library/:id`)

Full book detail with metadata, notes, and file management. Uses `BookDetailStore` (component-scoped service) for state. Features:

- **Cover upload/delete** with preview
- **File upload** with progress bar
- **Star rating** (0-5, click to toggle)
- **Favorite / Finished** toggles
- **Notes list** with inline editing, topic tags, expand/collapse
- **Description** with expandable overflow
- **Metadata edit** via `AddBookModal` in edit mode
- **Reader navigation** (opens `/read/:id`)

### SecondBrain (`/second-brain`)

Master-detail topic explorer. Features:

- **Left pane:** Searchable topic index (sorted by usage count)
- **Right pane:** All notes linked to selected topic
- **Topic tag navigation** — clicking a tag in a note switches to that topic
- **Note card actions** — navigate to source book

### WritingStudio (`/studio`)

Three-panel writing environment. Features:

- **Left sidebar:** File tree (FlatTreeComponent) with folders/documents
- **Center:** TinyMCE editor with auto-save (2s debounce, markdown round-trip)
- **Right sidebar:** Context panel with two tabs:
  - **Topics:** Browse topics → see linked notes → click to insert quote
  - **Books:** Browse books → see notes → click to insert quote
- **Mobile responsive** — sidebars collapse to overlays

### ReaderShell (`/read/:id`)

Full-screen immersive reader. Auto-detects file type:

- `.epub` → `EpubReader` (epub.js)
- `.pdf` → `PdfReader` (ngx-extended-pdf-viewer)
- `.m4b`, `.m4a`, `.mp3` → `AudioReader` (Howler.js)

Unified features across all reader types:

- **TOC sidebar** — collapsible table of contents
- **Notes sidebar** — view/create/edit/delete notes
- **Quick note** — text selection → note creation with optional topic autocomplete
- **Progress tracking** — debounced sync to backend
- **Zoom controls** — reader-type-specific zoom
- **Keyboard navigation** — arrows / page controls

## Layout Components

### WorkspaceLayout

Shell wrapper for the main application views. Contains `<router-outlet>` and `AppDockComponent`.

### AppDockComponent

Fixed bottom navigation dock with three items: Library, Brain, Studio. Features:

- **Rainbow glass effect** — animated gradient background
- **Deep-link memory** — `NavigationHistoryService` remembers last visited URL per section
- **Active state** — highlights current route
- **Responsive** — adapts sizing for mobile
