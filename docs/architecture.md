# Nostos — Architecture Overview

> **Status note (2026-08-12):** this overview predates the issue #34 library
> service and the agent-facing integration surfaces. The **Integrations**
> section below is the current reference for MCP and the
> canonical library service; decision 1's fallback description has been
> corrected to the shipped method-constrained fallback. The remaining
> sections are historical and largely unchanged.

## System Overview

Nostos is a self-hosted personal library and knowledge management platform. It combines an **ebook/audiobook reader**, a **note-taking system with wiki-style topic linking**, a **writing studio**, and a **collection management system** — all in a single deployable application.

```
┌─────────────────────────────────────┐
│         Angular 21 Frontend         │
│  (SPA served from wwwroot/)         │
├─────────────────────────────────────┤
│         ASP.NET Core 10 API         │
│  (Minimal API endpoints)            │
├─────────────────────────────────────┤
│         SQLite Database             │
│  (nostos.db via EF Core)            │
├─────────────────────────────────────┤
│         Local File Storage          │
│  (Storage/books/{guid}/)            │
└─────────────────────────────────────┘
```

## Project Structure

```
Nostos.sln
├── Nostos.Backend/          # ASP.NET Core 10 Web API
│   ├── Data/                # EF Core DbContext, Models, Repositories
│   ├── Endpoints/           # Minimal API endpoint groups
│   ├── Services/            # Business logic services
│   ├── Mapping/             # DTO ↔ Model mapping extensions
│   ├── Workers/             # Background services
│   ├── Migrations/          # EF Core migrations
│   ├── Storage/             # File storage root
│   │   ├── books/           #   Book files and covers
│   │   └── backups/         #   Backup archives (.nostos files)
│   └── wwwroot/             # Built Angular app (production)
│
├── Nostos.Frontend/         # Angular 21 SPA
│   └── src/app/
│       ├── core/            # Services, DTOs, directives, strategies
│       ├── ui/              # Shared/reusable UI components
│       ├── layout/          # Shell layout (dock, workspace wrapper)
│       ├── library/         # Library view + sidebar collections
│       ├── book-detail/     # Book detail page + store
│       ├── reader/          # Multi-format reader (epub, pdf, audio)
│       ├── second-brain/    # Topic explorer
│       ├── writing-studio/  # Markdown writing environment
│       ├── add-book-modal/  # Book create/edit modal
│       └── home/            # Landing page
│
└── Nostos.Shared/           # Shared DTOs & Enums (.NET class library)
    ├── Dtos/                # Record types shared between backend layers
    └── Enums/               # BookFilter, BookSort enums
```

## Technology Stack

| Layer                  | Technology              | Version    |
| ---------------------- | ----------------------- | ---------- |
| **Frontend Framework** | Angular                 | 21         |
| **UI Icons**           | Lucide Angular          | 0.555+     |
| **Epub Reader**        | epub.js                 | 0.3.93     |
| **PDF Reader**         | ngx-extended-pdf-viewer | 25.6+      |
| **Audio Player**       | Howler.js               | 2.2.4      |
| **Text Editor**        | TinyMCE                 | 8.2+       |
| **Markdown**           | marked + Turndown       | 17.0 / 7.2 |
| **Drag & Drop**        | @angular/cdk            | 21+        |
| **Backend Framework**  | ASP.NET Core            | 10.0       |
| **ORM**                | Entity Framework Core   | 10.0       |
| **Database**           | SQLite                  | —          |
| **Audio Metadata**     | ATL.NET (z440.atl.core) | 7.9        |
| **Target Runtime**     | .NET 10                 | —          |

## Key Architectural Decisions

### 1. Single-Binary Deployment

The Angular frontend is built and copied into the backend's `wwwroot/` directory at release time. The ASP.NET Core app serves both the API (`/api/*`, `/opds/*`) and the SPA (via `UseStaticFiles` + a custom method-constrained fallback: the shell is served for GET/HEAD client-side routes only, and `/api`, `/mcp`, and the configured MCP path are never answered by `index.html` — unknown paths there resolve as 404 and wrong verbs as 405).

### 2. Standalone Components (Angular)

The entire frontend uses Angular's standalone component model — no `NgModule` declarations anywhere. Components declare their own imports.

### 3. Signal-Based State Management

Angular Signals are used throughout instead of external state libraries (NgRx, Akita, etc.). Services expose reactive `signal()` properties and components use `computed()` for derived state.

### 4. Scroll Position Restoration

The Angular router is configured with `scrollPositionRestoration: 'enabled'` to automatically restore scroll position on back navigation. State preservation across navigation is handled at the service/store level using signals, not at the DOM level.

### 5. Repository Pattern (Backend)

All database access goes through interface-based repositories (`IBookRepository`, etc.), registered as scoped services. This separates data access from endpoint logic.

### 6. Minimal API Endpoints

The backend uses ASP.NET Core Minimal API pattern with extension method groups (`MapBooksEndpoints()`, etc.) rather than controllers.

### 7. File-Per-Book Storage

Book files and covers are stored in `Storage/books/{bookId}/` directories on-disk, not in the database. The database stores only metadata and file name references.

### 8. Wiki-Link Topic System

Notes use `[[Topic Name]]` syntax. The `NoteProcessorService` parses these on save, auto-creates topics if new, and maintains a many-to-many `NoteTopics` join table. A background worker (`TopicCleanupWorker`) periodically removes orphaned topics.

### 10. Automated Backup & Restore

Scheduled background workers (`BackupWorker`) create ZIP archives containing the database and book files. The system implements a maintenance mode (via middleware) that blocks API access during restoration to prevent data corruption.

## Integrations (MCP, Hermes)

The backend exposes an agent-facing surface on top of the canonical domain
service:

- **MCP (Model Context Protocol)** — opt-in (`Mcp:Enabled`, default disabled)
  bearer-authenticated Streamable HTTP endpoint at `/mcp` (`Mcp:Path`). The
  token is resolved exclusively from the `Mcp:ApiKeyEnvironmentVariable`
  environment variable (default `NOSTOS_MCP_TOKEN`) at startup; enabling MCP
  without the token fails startup closed. Tools are discovered from the
  assembly (`WithToolsFromAssembly`) and register as `mcp__nostos__*`
  (double underscore). The shipped surface is the 11 Library tools
  (issue #34). The maintenance-mode guard covers the MCP route as
  well as `/api`.

The canonical service is the only domain entry point: `ILibraryService`
(books and collections) receives every command — REST endpoints, the Angular
UI, and MCP tools all forward to it, and the endpoint/MCP layers hold no domain
rules (thin connector doctrine). Books are added through
`library_create_or_match_book` (or the legacy-permissive REST create); the
earlier raw-curl add-book recipe is superseded.

The library domain is exact-once on `(clientId, idempotencyKey)` receipts and a
state version: `LibraryCommandReceipt` keys on `(ClientId, IdempotencyKey)`
(client id ≤ 64 chars, key ≤ 128 chars, enforced by the
`CK_LibraryCommandReceipts_Bounds` CHECK constraint; fixed MCP client
`nostos-mcp`), and the `LibraryState` singleton carries the version string
returned in every envelope (`{ reply, data, stateVersion, duplicate }`, where
`duplicate=true` only on receipt replay). Book identity is normalized at
write time: checksum-validated ISBN/ASIN feed filtered unique indexes
(`NormalizedIsbn`/`NormalizedAsin`), and title+author matching uses
normalized text (both required for an exact match). `DatabaseBootstrapService`
migrates on startup, backfills normalized identity — a preflight
duplicate-identifier check aborts startup with an actionable error — and
seeds the `LibraryState` row.

## Data Flow

```
User Action → Angular Component
  → Service (HttpClient)
    → /api/* endpoint
      → Repository (EF Core)
        → SQLite DB
      ← Model
    ← DTO
  ← Signal Update
← Reactive UI Update
```

## Communication

- **Frontend ↔ Backend:** REST over HTTP (JSON)
- **Dev Proxy:** Angular dev server proxies `/api` and `/opds` to `http://localhost:5099`
- **Production:** Backend serves frontend directly from `wwwroot/`
