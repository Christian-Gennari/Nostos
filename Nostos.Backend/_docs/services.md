# Backend — Services

## Overview

Business logic services handle operations that go beyond simple CRUD. They are registered as scoped or singleton services in `Program.cs`.

---

## BookLookupService

**Registration:** `builder.Services.AddScoped<BookLookupService>()`  
**Dependencies:** `IHttpClientFactory`, `ILogger<BookLookupService>`

Provides ISBN-based metadata lookup by querying external APIs in parallel.

### `LookupCombinedAsync(isbn: string) → CreateBookDto?`

1. Cleans the ISBN (strips non-alphanumeric characters)
2. Queries **Open Library** and **Google Books API** concurrently
3. Merges results: Open Library data is preferred, Google fills any gaps
4. Returns a pre-populated `CreateBookDto` or `null` if both APIs return nothing

### External API Calls

| API          | URL Pattern                                                                    | Returns                                                                                  |
| ------------ | ------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------- |
| Google Books | `https://www.googleapis.com/books/v1/volumes?q=isbn:{isbn}`                    | Title, subtitle, authors, description, publisher, date, page count, language, categories |
| Open Library | `https://openlibrary.org/api/books?bibkeys=ISBN:{isbn}&jscmd=data&format=json` | Title, subtitle, authors, publishers, publish places, publish date, page count           |

Both calls are fire-and-forget on failure — individual API errors are caught and logged as warnings.

---

## FileStorageService

**Registration:** `builder.Services.AddSingleton<IFileStorageService, FileStorageService>()`  
**Storage Root:** `{ContentRootPath}/Storage/books/`

Manages on-disk file storage for book files and covers.

### Directory Structure

```
Storage/
└── books/
    └── {bookId}/           # GUID folder per book
        ├── book.epub       # Book file (any supported extension)
        └── cover.jpg       # Cover image
```

### Methods

| Method                             | Description                                                                       |
| ---------------------------------- | --------------------------------------------------------------------------------- |
| `SaveBookFileAsync(bookId, file)`  | Saves uploaded file as `book.{ext}`. Deletes any previous book file in the folder |
| `GetBookFile(bookId)`              | Returns `FileStream` for the book file, or `null`                                 |
| `GetBookFileName(bookId)`          | Returns full filesystem path, or `null`                                           |
| `DeleteBookFiles(bookId)`          | Deletes entire book folder recursively                                            |
| `SaveBookCoverAsync(bookId, file)` | Saves cover as `cover.{ext}`. Deletes any previous cover                          |
| `GetBookCoverPath(bookId)`         | Returns full path to cover file, or `null`                                        |
| `DeleteCover(bookId)`              | Deletes just the cover file                                                       |

### Supported File Types

**Books:**
| Extension | MIME Type |
|---|---|
| `.epub` | `application/epub+zip` |
| `.pdf` | `application/pdf` |
| `.txt` | `text/plain` |
| `.mobi` | `application/x-mobipocket-ebook` |
| `.azw3` | `application/x-mobipocket-ebook` |
| `.mp3` | `audio/mpeg` |
| `.m4a` | `audio/mp4` |
| `.m4b` | `audio/mp4` |

**Covers:** `.png`, `.jpg`, `.jpeg`

### Static Helpers

| Method                                   | Description                                         |
| ---------------------------------------- | --------------------------------------------------- |
| `IsAllowedUpload(contentType, fileName)` | Validates upload by MIME type or extension fallback |
| `GetContentType(filePath)`               | Maps file extension to MIME type for HTTP responses |

---

## MediaMetadataService

**Registration:** `builder.Services.AddScoped<MediaMetadataService>()`  
**Dependencies:** `ILogger<MediaMetadataService>`

Extracts metadata from audio files using the **ATL.NET** library (`z440.atl.core`).

### `EnrichBookMetadata(book: BookModel, filePath: string) → bool`

Called after a book file upload. For audio files:

1. Opens the file with `ATL.Track`
2. Extracts **chapters** (title + start time in seconds) → serialized to JSON and stored in `FileDetails.ChaptersJson`
3. For `AudioBookModel`: extracts **duration** if not already set → stored as `hh:mm:ss`
4. Returns `true` on success, `false` on failure (logged as error)

---

## NoteProcessorService

**Registration:** `builder.Services.AddScoped<NoteProcessorService>()`  
**Dependencies:** `IConceptRepository`

Processes wiki-link syntax in note content.

### `ProcessNoteAsync(note: NoteModel)`

1. Extracts all `[[Concept Name]]` patterns via regex `\[\[(.*?)\]\]`
2. Deduplicates names (case-insensitive)
3. Clears existing concept links for the note
4. Looks up existing concepts by name
5. Creates new `ConceptModel` entries for any unrecognized names
6. Creates `NoteConceptModel` links for all matched concepts

This method modifies the DbContext but does **not** call `SaveChangesAsync()` — the caller is responsible for saving (to allow batching with the note itself).

---

## ConceptCleanupWorker

**Registration:** `builder.Services.AddHostedService<ConceptCleanupWorker>()`  
**Type:** `BackgroundService`

A background worker that runs every **1 hour** and removes orphaned concepts (concepts with zero linked notes).

### Behavior

1. Starts when the application starts
2. Waits 1 hour between iterations via `PeriodicTimer`
3. Each cycle: calls `IConceptRepository.DeleteOrphanedAsync()` (bulk `ExecuteDeleteAsync`)
4. Logs the count of deleted concepts if any
5. Gracefully handles cancellation on shutdown
6. Catches and logs individual cycle errors without crashing

---

## BackupSettingsProvider

**Registration:** `builder.Services.AddSingleton<BackupSettingsProvider>()`  
**Persistence:** `{ContentRootPath}/backup-settings.json`

A thread-safe singleton that manages the application's backup configuration and runtime state.

### Responsibilities
1. **Settings Persistence:** Handles atomic reads/writes of `BackupSettings` to a local JSON file.
2. **Maintenance Mode:** Projects the singleton `ILibraryMaintenanceCoordinator` through `IsInMaintenanceMode`. Compatibility enter/exit calls reference-count one exclusive coordinator lease; backup restore uses the asynchronous drain barrier directly.
3. **Progress Tracking:** Provides a volatile storage for the current backup/restore step, percentage, and step count.

---

## BackupService

**Registration:** `builder.Services.AddScoped<IBackupService, BackupService>()`  
**Backups Root:** `{ContentRootPath}/Storage/backups/`

The core orchestrator for library preservation and recovery.

### Backup Workflow (5 Steps)
1. **Preparing:** Initializes record and ensures storage directory exists.
2. **Collecting Files:** Copies the database (via SQLite `VACUUM INTO`) and book files to a temporary directory.
3. **Compressing:** Creates a `.nostos` ZIP archive containing the manifest, database, and books.
4. **Verifying:** Calculates SHA256 checksums to ensure archive integrity.
5. **Finalizing:** Moves the archive to permanent storage and updates the database record.

### Restore Workflow (3 Steps)
1. **Verifying:** Validates ZIP structure and checks manifest checksums.
2. **Restoring Data:** Acquires exclusive library maintenance after request/worker scopes drain, takes a safety snapshot of the current DB, and overwrites with backup content. Drain timeout fails before replacement; a fixed delay is not used for drain proof.
3. **Completing:** Cleans up temporary files and exits maintenance mode.

### Storage Scanning
The `ImportExistingBackupsAsync` method allows users to manually copy `.nostos` files into the storage directory and register them in the app history after verification.

---

## BackupWorker

**Registration:** `builder.Services.AddHostedService<BackupWorker>()`  
**Type:** `BackgroundService`

A background worker that polls every **5 minutes** to check if a scheduled backup is due.

1. Queries the database for the last successful backup timestamp.
2. Evaluates `IsEnabled` toggle and `IntervalHours` threshold.
3. Triggers a new backup cycle if requirements are met.
4. Enforces the `MaxBackups` retention policy by deleting the oldest archives.
