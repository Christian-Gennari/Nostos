# Backend — Endpoints

## Overview

The backend uses ASP.NET Core **Minimal API** pattern. Endpoints are organized as static extension methods that register route groups. The product endpoint groups are mapped by `MapNostosProductEndpoints` in `Nostos.Product/Composition/NostosProductComposition.cs`; `Nostos.Backend/Program.cs` maps that product surface and adds only the host-specific Backup, ExternalMedia and MCP groups:

```csharp
// Nostos.Product/Composition/NostosProductComposition.cs — MapNostosProductEndpoints
api.MapBooksEndpoints(policies);
api.MapNotesEndpoints();
api.MapCollectionsEndpoints();
api.MapTopicsEndpoints();
api.MapWritingsEndpoints();
api.MapOpdsEndpoints(opds, policies.OpdsAuthorizationPolicy,
                     policies.LargeTransferRateLimitPolicy);

// Nostos.Backend/Program.cs — host-specific groups
app.MapNostosProductEndpoints(
    opdsOptions,
    NostosProductEndpointPolicies.None with { MapResumableBookUploadEndpoints = true });
app.MapBackupEndpoints();
app.MapExternalMediaEndpoints();
app.MapMcpAccessTokenEndpoints();
app.MapMcpOAuthEndpoints();
```

## Endpoint Groups

### BooksEndpoints (`/api/books`)

All book routes forward to the canonical `ILibraryService` (issue #34): the
endpoint layer performs no repository writes and no title-matching logic.
Metadata routes use the service; the `locations`, `file`, and `cover` routes
keep their repository-backed implementations.

| Method   | Route             | Description                                        | Dependencies                                                     |
| -------- | ----------------- | -------------------------------------------------- | ---------------------------------------------------------------- |
| `GET`    | `/`               | List books (paginated, filtered, sorted, searched) | `ILibraryService`                                                |
| `GET`    | `/{id}`           | Get single book                                    | `ILibraryService`                                                |
| `POST`   | `/`               | Create-or-match book (legacy permissive)           | `ILibraryService`                                                |
| `PUT`    | `/{id}`           | Update book metadata                               | `ILibraryService`                                                |
| `PUT`    | `/{id}/progress`  | Update reading progress (validated 0–100)          | `ILibraryService`                                                |
| `GET`    | `/{id}/locations` | Get cached epub locations                          | `IBookRepository`                                                |
| `POST`   | `/{id}/locations` | Save epub locations                                | `IBookRepository`                                                |
| `DELETE` | `/{id}`           | Delete book + files (row first, then storage)      | `ILibraryService`, `IFileStorageService`                         |
| `POST`   | `/{id}/file`      | Upload book file (409 `book_has_file` unless `?replace=true`) | `IBookRepository`, `IFileStorageService`, `MediaMetadataService` |
| `GET`    | `/{id}/file`      | Download/stream book file                          | `IFileStorageService`                                            |
| `POST`   | `/{id}/cover`     | Upload cover image                                 | `IBookRepository`, `IFileStorageService`                         |
| `GET`    | `/{id}/cover`     | Download cover image                               | `IFileStorageService`                                            |
| `DELETE` | `/{id}/cover`     | Delete cover                                       | `IBookRepository`, `IFileStorageService`                         |
| `GET`    | `/lookup/{isbn}`  | Lookup metadata by ISBN (15s timeout)              | `BookLookupService`                                              |

`POST /` is the legacy-permissive create-or-match: an exact normalized
identity match returns the existing book (`200 OK`, `outcome: matched`),
otherwise a book is created (`201 Created` with `Location: /api/books/{id}`,
`outcome: created`). Ambiguity creates rather than asks — the strict
confirmation flow is used only by Ask Nostos. `PUT /{id}/progress` validates the
percentage 0–100 and aligns `FinishedAt` with the finished state. `DELETE
/{id}` returns `409 book_in_use` when the book is referenced by a training
assignment or a note, and storage files are removed only after the database
row is gone.

Errors are mapped by `LibraryHttpMapper` to Problem Details: `invalid_*` →
400, `*_not_found` → 404, the conflict family (`identity_conflict`,
`duplicate_identifier`, `confirmation_required`, `collection_name_conflict`,
`collection_cycle`, `collection_has_children`, `book_in_use`) → 409,
everything else → 422. `GET /lookup/{isbn}` rejects an invalid ISBN with 400
and returns 404 when no metadata is found.

### ProviderEndpoints (`/api/providers`)

Read-only browsing of external catalogues, plus acquisition. Providers are
resolved from the `IProviderRegistry` through `IProviderEnablementService`, so a
disabled provider is answered exactly like an unknown one; the endpoint layer
never contacts a source directly and performs no library writes of its own
(issues #166/#167/#168, enablement in #774).

A client can only ever name **a provider, an item and an asset** — never a URL.
The provider resolves the actual download locations server-side, which is what
stops this surface from becoming an arbitrary-URL downloader.

| Method   | Route                            | Description                                                   | Dependencies                    |
| -------- | -------------------------------- | ------------------------------------------------------------- | ------------------------------- |
| `GET`    | `/`                              | List enabled providers with capabilities and rights notice     | `IProviderEnablementService`    |
| `GET`    | `/{providerId}/search`           | Search a source (`query`, `limit`, `offset`)                   | `IProviderSearch`               |
| `GET`    | `/{providerId}/items/{externalId}` | Normalized item detail                                      | `IProviderCatalog`              |
| `GET`    | `/{providerId}/items/{externalId}/cover` | Proxy a cover image (the browser never contacts the source) | `IProviderCatalog`, `IProviderContentDownloader` |
| `POST`   | `/{providerId}/acquire`          | Start an acquisition job; `202` with a job id                 | `IAcquisitionService`, `IAcquisitionJobManager` |
| `GET`    | `/acquisitions/{jobId}`          | Poll job state/stage/percent, resulting `bookId`, error code  | `IAcquisitionJobManager`        |
| `DELETE` | `/acquisitions/{jobId}`          | Cancel an in-flight job                                       | `IAcquisitionJobManager`        |

Provider failure codes are stable and carried through to the client, e.g.
`provider_unknown`, `provider_item_not_found`, `provider_asset_unavailable`,
`provider_response_invalid`, `provider_unavailable`, `download_failed`,
`media_tool_missing`, `assembly_failed`, `acquisition_conflict`. A successful
job that finds the work already present returns the **existing** book id rather
than creating a duplicate.

### ProviderSettingsEndpoints (`/api/settings/providers`)

The management surface for provider enablement (issue #774). Unlike every other
provider route, this one lists disabled sources so they can be turned back on.
`GET /api/providers` and provider-specific operations remain enabled-only.

| Method | Route            | Description                                                                | Dependencies               |
| ------ | ---------------- | -------------------------------------------------------------------------- | -------------------------- |
| `GET`  | `/`              | Every registered source with description, capabilities and effective state | `IProviderEnablementService` |
| `PUT`  | `/{providerId}`  | Store `{ "enabled": bool }`; missing field → 400 `invalid_provider_preference`, unregistered id → 404 `provider_unknown` | `IProviderEnablementService` |

### ManagedBackupEndpoints (`/api/managed-backups`)

Authenticated, read-only managed-backup history. A host without a provider
returns `501 Not Implemented`; capable hosts derive tenant scope from the
authenticated server-side identity. See `docs/managed-backups-contract.md` for
the response fields and retention semantics.

| Method | Route                  | Description                                                   | Dependencies          |
| ------ | ---------------------- | ------------------------------------------------------------- | --------------------- |
| `GET`  | `/api/managed-backups` | List the current customer's completed managed nightly backups | `IManagedBackupCatalog` |

### NotesEndpoints (`/api`)

| Method   | Route                   | Description                           | Dependencies                                                 |
| -------- | ----------------------- | ------------------------------------- | ------------------------------------------------------------ |
| `GET`    | `/books/{bookId}/notes` | List notes for a book                 | `INoteRepository`                                            |
| `POST`   | `/books/{bookId}/notes` | Create note (+ topic processing)      | `IBookRepository`, `INoteRepository`, `NoteProcessorService` |
| `PUT`    | `/notes/{id}`           | Update note (+ topic re-processing)   | `INoteRepository`, `NoteProcessorService`                    |
| `DELETE` | `/notes/{id}`           | Delete note + topic links             | `INoteRepository`                                            |

### CollectionsEndpoints (`/api/collections`)

Routed through the canonical `ILibraryService` (issue #34).

| Method   | Route   | Description                   | Dependencies            |
| -------- | ------- | ----------------------------- | ----------------------- |
| `GET`    | `/`     | List all collections (flat)   | `ILibraryService`       |
| `GET`    | `/{id}` | Get single collection         | `ILibraryService`       |
| `POST`   | `/`     | Create collection             | `ILibraryService`       |
| `PUT`    | `/{id}` | Rename and/or move            | `ILibraryService`       |
| `DELETE` | `/{id}` | Delete (unlinks books)        | `ILibraryService`       |

`POST /` always returns `201 Created`; a sibling with the same normalized name
under the same parent returns the existing collection instead of a duplicate.
`PUT /{id}` performs the move and rename through the canonical service: a
move into its own descendant returns `409 collection_cycle` and a sibling
name collision at the destination returns `409 collection_name_conflict`.
`DELETE /{id}` unlinks the books in the collection (never deletes them) and
returns `409 collection_has_children` while the collection still has child
collections.

### TopicsEndpoints (`/api/topics`)

`/api/topics` is the canonical route. `/api/concepts` is a transition alias: it is mapped to the same handlers and returns the same topic field names. It still works; do not remove it from clients without a migration.

| Method | Route   | Description                                | Dependencies         |
| ------ | ------- | ------------------------------------------ | -------------------- |
| `GET`  | `/`     | List all topics (with usage counts)        | `ITopicRepository`   |
| `GET`  | `/{id}` | Get topic detail (with all linked notes)   | `ITopicRepository`   |

### WritingsEndpoints (`/api/writings`)

| Method   | Route        | Description                               | Dependencies         |
| -------- | ------------ | ----------------------------------------- | -------------------- |
| `GET`    | `/`          | List all writings (flat)                  | `IWritingRepository` |
| `GET`    | `/{id}`      | Get document content                      | `IWritingRepository` |
| `POST`   | `/`          | Create folder or document                 | `IWritingRepository` |
| `PUT`    | `/{id}`      | Update name/content                       | `IWritingRepository` |
| `PUT`    | `/{id}/move` | Move to new parent (with cycle detection) | `IWritingRepository` |
| `DELETE` | `/{id}`      | Delete (cascade children)                 | `IWritingRepository` |

### OpdsEndpoints (`/opds`)

Unauthenticated by design (see the OPDS section of `docs/api-reference.md`);
mapped only when `Opds:Enabled` is true, which is the default.

| Method | Route             | Description                                      | Dependencies      |
| ------ | ----------------- | ------------------------------------------------ | ----------------- |
| `GET`  | `/`               | OPDS 1.2 acquisition feed, one page (`?page=N`)  | `IBookRepository` |
| `GET`  | `/api/opds/info`  | Catalog state + address for Settings (always mapped) | —             |

### MCP (`/mcp`)

Model Context Protocol, Streamable HTTP, mapped by `MapNostosProductEndpoints` only when the host registers the `NostosMcp` authorization policy. Tools and citations: [`docs/api-reference.md`](../../docs/api-reference.md#mcp--mcp-issue-1467).

### BackupEndpoints (`/api/backup`)

| Method     | Route            | Description                                | Dependencies                   |
| ---------- | ---------------- | ------------------------------------------ | ------------------------------ |
| `GET`      | `/status`        | Get last backup time and scheduled status   | `IBackupService`               |
| `GET`      | `/settings`      | Get current retention and interval settings | `IBackupService`               |
| `PUT`      | `/settings`      | Update backup configuration                | `IBackupService`               |
| `POST`     | `/trigger`       | Manually start a backup immediately        | `IBackupService`               |
| `POST`     | `/restore/{id}`  | Restore library from a specific archive     | `IBackupService`               |
| `GET`      | `/history`       | List all backup records                    | `IBackupService`               |
| `DELETE`   | `/history/{id}`  | Delete a backup record and its archive file | `IBackupService`               |
| `GET`      | `/download/{id}` | Stream `.nostos` archive to browser        | `IBackupService`               |
| `POST`     | `/import`        | Scan `/backups` folder for untracked files | `IBackupService`               |
| `GET`      | `/progress`      | Real-time step-by-step progress tracking   | `BackupSettingsProvider`       |

## Maintenance Mode Middleware

`LibraryMaintenanceMiddleware` takes shared operation leases for `/api`, `/opds`, `/mcp`, and database readiness. It holds them through complete response streaming and request-scope disposal. New operations are refused while the single coordinator drains or holds exclusive maintenance. GET `/api/backup/progress`, process liveness and static UI remain available without opening the library. The backup restore endpoint delegates admission to `BackupService` for its shared-to-exclusive handoff.

- **Status Code:** 503 Service Unavailable
- **Response:** `{"code": "migration_activation_busy", "error": "The library is in maintenance. Try again later."}`; `Retry-After: 5`.
- **Purpose:** Drains existing DB/file operations before restore or later migration cutover; new requests cannot straddle the exclusive window.

## Cycle Detection

`WritingsEndpoints` implements cycle detection when moving items, and the
canonical library service does the same for collection moves:

```
1. Check: target parent != self
2. Walk ancestors: starting from target parent, follow ParentId chain
3. If any ancestor == moving item's ID → reject
```

`WritingsEndpoints` rejects with 400; collection moves return
`409 collection_cycle` (Problem Details via `LibraryHttpMapper`). This
prevents a folder or collection from being moved into its own descendant
(which would create an infinite loop).

## Mapping

DTO ↔ Model conversion is handled by `MappingExtensions` (extension methods):

| Direction   | Methods                                     |
| ----------- | ------------------------------------------- |
| Model → DTO | `ToDto()`, `ToContentDto()`                 |
| DTO → Model | `ToModel()`, `ToModel(bookId)`              |
| Update      | `Apply(dto)` — patches only non-null fields |

The `Apply` pattern supports partial updates: only fields present in the DTO are applied, preserving existing values for omitted fields.
