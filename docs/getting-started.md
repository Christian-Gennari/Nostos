# Nostos — Getting Started

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/) with npm
- Git

## Clone & Install

```bash
git clone <repository-url>
cd Nostos
```

### Backend Dependencies

```bash
cd Nostos.Backend
dotnet restore
```

### Frontend Dependencies

```bash
cd Nostos.Frontend
npm install
```

## Development Mode

You need **two terminals** running simultaneously:

### Terminal 1 — Backend API

```bash
cd Nostos.Backend
dotnet run
```

The API starts on `http://localhost:5099`. The database (`nostos.db`) is auto-created and migrated on first run; `Persistence:DatabasePath` moves it (see [Configuration](#configuration)).

### Terminal 2 — Frontend Dev Server

```bash
cd Nostos.Frontend
ng serve
```

The Angular dev server starts on `http://localhost:4200` and proxies `/api` and `/opds` requests to the backend at `:5099` (configured in `src/proxy.conf.json`).

**Open** `http://localhost:4200` in your browser.

## Production Build

A single command builds both frontend and backend:

```bash
cd Nostos.Backend
dotnet build -c Release
```

This triggers the MSBuild `BuildFrontend` target which:

1. Runs `npm install` (if `node_modules/` doesn't exist)
2. Runs `npm run build` (Angular production build)
3. Copies the built files from `Nostos.Frontend/dist/Nostos.Frontend/browser/` to `Nostos.Backend/wwwroot/`

The resulting binary serves both the API and the SPA.

### Run the Production Build

```bash
cd Nostos.Backend
dotnet run -c Release
```

Or publish for deployment:

```bash
dotnet publish -c Release -o ./publish
cd publish
./Nostos.Backend
```

## Configuration

### Backend (`appsettings.json`)

| Setting                    | Default       | Description       |
| -------------------------- | ------------- | ----------------- |
| `Logging:LogLevel:Default` | `Information` | Minimum log level |
| `ExternalMedia:Root`       | not set (`/external/media` in the Docker image) | Absolute path of a folder of existing books to read in place. See [External media](external-media.md) |
| `Calibre:LibraryRoot`      | not set (`/external/calibre` in the Docker image) | Absolute path of a Calibre library folder, read-only. See [Calibre import](calibre-import.md) |
| `CalibreUpload:RootPath`   | `<transfer root>/calibre-uploads` | Scratch for a zipped Calibre library received through the resumable upload. Absolute, or relative to the content root. See [Calibre import](calibre-import.md) |
| `ReadAloud:Enabled`, `ReadAloud:BaseUrl` | off, not set | Connects a speech server so books are read aloud with a natural voice. Also settable in Settings → AI connections. See [Read aloud](read-aloud.md) |

The SQLite file is resolved by `Nostos.Backend/Configuration/PersistenceRegistration.cs`:
`Persistence:DatabasePath` (environment variable `Persistence__DatabasePath`)
wins when set — absolute for a dedicated volume such as `/data/nostos.db`,
relative to the content root otherwise — and unset falls back to
`<content root>/nostos.db`. The database's parent directory is created on
startup, and the same path is used by backup settings, backup restore and the
library import/replace flows.

### Upload Limits

Kestrel's maximum request body and the multipart form limit are both **4 GB**
(`4L * 1024 * 1024 * 1024`, `Nostos.Backend/Composition/SelfHostedHttpPipeline.cs`).
That is the ceiling for one upload; larger books use the resumable part upload,
whose fixed part size the server owns (`BookUploadLimits`). The cap is
deliberately one declaration, because a second `ConfigureKestrel` call would
silently replace the first.

### Frontend Proxy (`src/proxy.conf.json`)

```json
{
  "/api": { "target": "http://localhost:5099", "secure": false },
  "/opds": { "target": "http://localhost:5099", "secure": false }
}
```

## Project Structure Quick Reference

| Path                            | Purpose                                  |
| ------------------------------- | ---------------------------------------- |
| `Nostos.Product/`               | Shared product: DbContext and model, endpoints, services |
| `Nostos.Backend/`               | .NET 10 SelfHosted host executable       |
| `Nostos.Backend.Tests/`         | xUnit suite for the backend and product  |
| `Nostos.Frontend/`              | Angular 22 SPA                           |
| `Nostos.Shared/`                | Shared DTOs/Enums between backend layers |
| `Nostos.Backend/Storage/books/` | Book file storage (default `Storage:BooksRoot`, not committed) |
| `Nostos.Backend/nostos.db`      | SQLite database (default `Persistence:DatabasePath`, auto-created) |

## Database Migrations

The schema is created and migrated automatically on startup by
`DatabaseBootstrapService`: a truly empty database gets the complete current
schema plus an accurate migration-history baseline in one transaction, and an
existing database goes through the ordinary EF migration path. No manual step is
needed for deployment.

To create a new migration, run it from the backend project (which is also the
startup project):

```bash
dotnet ef migrations add <MigrationName> \
  --project Nostos.Backend --startup-project Nostos.Backend
```

Let `dotnet ef` create the file; never invent a migration filename. To apply
migrations manually:

```bash
dotnet ef database update \
  --project Nostos.Backend --startup-project Nostos.Backend
```

Note that the test suite uses `EnsureCreated` and never executes migrations, so
a hand-written backfill or guard is only proven on a migrated database copy.
