# Nostos — Architecture Overview

> **Status note (reconciled 2026-11-13):** the solution layout and technology
> table below were checked against `Nostos.sln`, `Nostos.Frontend/package.json`
> and the `.csproj` files. The **Library service** section is the current
> reference for the canonical library service; decision 1's fallback
> description matches the shipped method-constrained fallback. The numbered
> decisions remain the durable architecture and are not versioned pins.

## System Overview

Nostos is a self-hosted personal library and knowledge management platform. It combines an **ebook/audiobook reader**, a **note-taking system with wiki-style topic linking**, a **writing studio**, and a **collection management system** — all in a single deployable application.

```
┌─────────────────────────────────────┐
│         Angular 22 Frontend         │
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
├── Nostos.Product/          # Shared product: model, endpoints, services, integrations
│   ├── Data/                # EF DbContext, models, configurations, repositories
│   ├── Endpoints/           # Minimal API endpoint groups
│   ├── Services/            # Library, notes, topics, AI, uploads, portability
│   ├── Mapping/             # DTO ↔ Model mapping extensions
│   ├── Composition/         # AddNostosProduct / MapNostosProductEndpoints
│   ├── Integrations/        # MCP server
│   ├── Alignment/           # Audio ↔ text alignment
│   ├── BookText/            # Indexed book text for search and grounding
│   ├── Search/              # Library and note search
│   ├── Providers/           # External content providers
│   └── Health/              # Health checks
│
├── Nostos.Backend/          # SelfHosted host executable
│   ├── Composition/         # Host pipeline, persistence, storage, workers
│   ├── Configuration/       # PersistenceRegistration, storage options
│   ├── Data/                # DatabaseBootstrapService
│   ├── Endpoints/           # Backup, external media, MCP credentials
│   ├── Services/            # Backup/restore, file storage, portability, MCP OAuth
│   ├── Middleware/          # Maintenance-mode lease middleware
│   ├── Migrations/          # EF Core migrations (SQLite)
│   ├── Workers/             # Background services
│   ├── Storage/             # File storage root (not committed)
│   │   ├── books/           #   Book files and covers
│   │   └── backups/         #   Backup archives (.nostos files)
│   ├── _docs/               # Backend developer reference
│   └── wwwroot/             # Built Angular app (production)
│
├── Nostos.Backend.Tests/    # xUnit suite for the backend and the product
├── Nostos.Shared/           # Shared DTOs & Enums (.NET class library)
│   ├── Dtos/                # Record types shared between backend layers
│   └── Enums/               # BookFilter, BookSort, BookStatus
│
├── Nostos.Frontend/         # Angular 22 SPA
│   └── src/app/
│       ├── core/            # Services, DTOs, directives, strategies
│       ├── ui/              # Shared/reusable UI components
│       ├── layout/          # Shell layout (dock, workspace wrapper)
│       ├── library/         # Library view + sidebar collections
│       ├── book-detail/     # Book detail page + store
│       ├── reader/          # Multi-format reader (epub, pdf, audio)
│       ├── brain/           # The Brain: places, topics, the sorter, the map (docs/brain.md)
│       ├── writing-studio/  # Markdown writing environment
│       ├── settings/        # Settings surfaces (assistant, providers, connected apps)
│       ├── library-transfer/ # Import/replace and backup/restore flows
│       ├── add-book-modal/  # Book create/edit modal
│       ├── pair/            # Carrel device pairing
│       └── cloud-entry/     # Entry points used only by the hosted deployment
│
└── tools/                   # Support projects outside the product (ReadwiseCanary, fixtures)
```

`Nostos.Backend` is the product's only executable; `Nostos.Product` is the library
it composes, so a hosted build of the same product shares every endpoint and
service and supplies only its own persistence, identity and hosting decisions.

## Technology Stack

Versions move; the exact pins are in `Nostos.Frontend/package.json` and the
`.csproj` files. This table is the layer → technology map, with the version
family rather than every patch.

| Layer                  | Technology              | Version    |
| ---------------------- | ----------------------- | ---------- |
| **Frontend Framework** | Angular                 | 22         |
| **UI Icons**           | Phosphor (`@phosphor-icons/core`) | 2.1 |
| **Epub Reader**        | epub.js                 | 0.3.93     |
| **PDF Reader**         | ngx-extended-pdf-viewer | 32         |
| **Audio Player**       | Howler.js               | 2.2.4      |
| **Page OCR**           | Tesseract.js (on device, lazy) | 7.0 |
| **Text Editor**        | TipTap                  | 3.33       |
| **Markdown**           | marked + Turndown       | 18.1 / 7.2 |
| **Drag & Drop**        | @angular/cdk            | 22.2       |
| **Graph**              | Sigma.js + Graphology   | —          |
| **Backend Framework**  | ASP.NET Core            | 10.0       |
| **ORM**                | Entity Framework Core   | 10.0       |
| **Database**           | SQLite                  | —          |
| **Audio Metadata**     | ATL.NET (z440.atl.core) | 7.19       |
| **Target Runtime**     | .NET 10                 | —          |

## Key Architectural Decisions

### 1. Single-Binary Deployment

The Angular frontend is built and copied into the backend's `wwwroot/` directory at release time. The ASP.NET Core app serves both the API (`/api/*`, `/opds/*`) and the SPA (via `UseStaticFiles` + a custom method-constrained fallback: the shell is served for GET/HEAD client-side routes only, and `/api` and `/opds` are never answered by `index.html` — unknown paths there resolve as 404 and wrong verbs as 405).

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

## Library service

The canonical service is the only domain entry point: `ILibraryService`
(books and collections) receives every command — REST endpoints, the Angular
UI, and Ask Nostos all forward to it, and the endpoint layer holds no domain
rules (thin connector doctrine). Agents on the same machine can use the REST
API (the `nostos-library` skill in `.agents/skills/` documents the calls). The
user's own AI apps connect over MCP (`/mcp`, `Nostos.Product/Integrations/Mcp`):
its tools wrap Ask Nostos capabilities through `AssistantCapabilityRegistry`,
so the registry is the one contract, and the endpoint is mapped only for a
host that registers the `NostosMcp` authorization policy.

The library domain is exact-once on `(clientId, idempotencyKey)` receipts and a
state version: `LibraryCommandReceipt` keys on `(ClientId, IdempotencyKey)`
(client id ≤ 64 chars, key ≤ 128 chars, enforced by the
`CK_LibraryCommandReceipts_Bounds` CHECK constraint), and the `LibraryState` singleton carries the version string
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
