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

The API starts on `http://localhost:5099`. The database (`nostos.db`) is auto-created and migrated on first run using an absolute path relative to the content root.

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

The SQLite database file path is resolved in `Program.cs` using `Path.Combine(builder.Environment.ContentRootPath, "nostos.db")` to ensure an absolute path is used regardless of the working directory.

### Upload Limits

Kestrel and form options are configured for up to **100 GB** file uploads to support large library archives and audiobooks.

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
| `Nostos.Backend/`               | .NET 10 Web API                          |
| `Nostos.Frontend/`              | Angular 21 SPA                           |
| `Nostos.Shared/`                | Shared DTOs/Enums between backend layers |
| `Nostos.Backend/Storage/books/` | Book file storage (not committed)        |
| `Nostos.Backend/nostos.db`      | SQLite database (auto-created)           |

## Database Migrations

Migrations run automatically on startup. To create a new migration:

```bash
cd Nostos.Backend
dotnet ef migrations add <MigrationName>
```

To apply manually:

```bash
dotnet ef database update
```
