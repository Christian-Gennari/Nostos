<div align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/logo-dark.svg" />
    <source media="(prefers-color-scheme: light)" srcset="docs/brand/logo-light.svg" />
    <img alt="Nostos" src="docs/brand/logo-light.svg" width="276" />
  </picture>

  <p><strong>A home for reading, thinking, and writing.</strong></p>
  <p>Bring books, notes, ideas, and drafts together in one connected place.</p>

  <p>
    <a href="#what-is-nostos">About</a> •
    <a href="#features">Features</a> •
    <a href="#getting-started">Getting Started</a> •
    <a href="#architecture--tech-stack">Tech Stack</a> •
    <a href="#documentation">Docs</a> •
    <a href="#license--trademark">License</a>
  </p>
</div>

<hr>

<div align="center">
  <img
    width="2880"
    height="1800"
    alt="Nostos Library Interface"
    src="docs/screenshots/library.png"
  />
</div>

## What is Nostos?

Nostos is a personal reading and writing environment built around a simple idea: what you read should be able to stay connected to what you think and what you eventually write.

Instead of splitting that process across a library app, an e-book reader, a notes system, and a writing tool, Nostos keeps the whole path together. You can collect physical books, EPUBs, PDFs, and audiobooks; read or listen inside the app; capture notes and highlights; connect ideas through concepts; and bring that material into the Writing Studio when it is time to turn reading into your own work.

The interface is deliberately restrained. There are no social feeds, reading streaks, engagement mechanics, or productivity scores competing for attention. The emphasis stays on the library and the work you are doing with it.

This repository contains the canonical Nostos product and the complete public **SelfHosted** application. The official hosted Nostos Cloud service uses the same product model and Angular frontend through a separate private hosting composition.

---

## Features

### Library

Keep different kinds of books in one collection without flattening them into the same experience.

- **Physical books:** Add books with metadata and ISBN lookup while keeping them alongside digital titles.
- **EPUB and PDF:** Store and read files directly in Nostos.
- **Audiobooks:** Import M4B, M4A, and MP3 audio with chapter-aware playback.
- **Collections and filtering:** Organize books into nested collections, move them with drag and drop, and filter by reading state, rating, recency, or collection.
- **Works and editions:** Nostos can group matching editions using normalized ISBNs or title and author identity.
- **Free-source acquisition:** Search and import supported public-domain material from Project Gutenberg and LibriVox. Imported books become ordinary Nostos library items rather than remaining dependent on the source.

<details>
  <summary><strong>View Library Screenshots</strong></summary>
  <br />
  <table>
    <tr>
      <td width="50%">
        <strong>Book Overview & Progress</strong><br /><br />
        <img width="2880" height="1800" alt="Book Details" src="docs/screenshots/book-details.png" />
      </td>
      <td width="50%">
        <strong>Cataloging & Import</strong><br /><br />
        <img width="2880" height="1800" alt="Add Book Modal" src="docs/screenshots/add-book-modal.png" />
      </td>
    </tr>
  </table>
</details>

---

### Reading & Listening

Nostos includes readers for the formats it stores, so notes and progress can stay close to the source.

- **EPUB reader:** Read EPUB files in the browser and capture text from the book as you go.
- **PDF reader:** Read PDFs, select passages, and create highlights without leaving Nostos.
- **Audiobook player:** Listen to chaptered audiobooks with saved playback progress.
- **Contextual notes:** Keep thoughts attached to the book they came from instead of moving them into a disconnected notes folder.

---

### Brain & Concepts

The Brain is where notes from individual books start to connect.

- **Wiki-links:** Use `[[Concept]]` links in notes to connect recurring ideas.
- **Concept views:** Browse concepts as a list or explore their relationships in the interactive graph.
- **Evidence-first navigation:** Move from an idea back to the notes and reading material that support it.
- **Automatic cleanup:** Concepts with no remaining references can be cleaned up automatically.

<div align="center">
  <img
    width="2880"
    height="1800"
    alt="Nostos Brain concept index and linked notes"
    src="docs/screenshots/brain-list.png"
  />
</div>

<div align="center">
  <img
    width="3800"
    height="1820"
    alt="Nostos Brain interactive concept graph"
    src="docs/screenshots/brain-graph.png"
  />
</div>

---

### Writing Studio

The Writing Studio brings drafting and source material into the same workspace.

- **Three-panel workspace:** Keep a project and chapter tree on the left, the draft in the center, and relevant source material on the right.
- **Notes and quotations at hand:** Browse material from the library while writing and insert useful passages into the draft.
- **Rich editing with Markdown round-tripping:** TinyMCE provides the editing surface while Turndown keeps Markdown conversion available.
- **Automatic saving:** Draft changes are saved in the background.

<div align="center">
  <img
    width="2880"
    height="1800"
    alt="Nostos Writing Studio"
    src="docs/screenshots/writing-studio.png"
  />
</div>

---

### Optional AI Assistance

Nostos can use configured AI services for features such as **Ask Nostos** and speech-to-text, but AI is not required for the core product.

In the public SelfHosted application, AI provider configuration remains under the operator's control. The hosted service can provide managed AI through its own private infrastructure. The product-level interfaces stay provider-neutral in this repository.

---

### Portability, Backup & Integrations

- **Portable archives:** Export and import `.nostos` archives so library data is not trapped in one installation.
- **Local backup and restore:** SelfHosted installations include local backup and restore workflows with integrity checks and restore safeguards.
- **OPDS export:** A configurable OPDS catalogue can expose a self-hosted library to compatible readers on networks you control.
- **MCP:** An optional bearer-protected Model Context Protocol endpoint lets compatible tools work with Nostos through the documented library contracts.

---

## SelfHosted and Nostos Cloud

Nostos is one product with two deployment modes.

### SelfHosted

The public executable in this repository is complete on its own. It uses:

- SQLite for relational data
- the local filesystem for book media
- local backup and restore
- operator-configured AI providers when AI features are enabled

Your library database and stored media stay on the machine or server where you run Nostos. Features that contact external services, such as public catalogue imports, metadata lookup, or optional AI, naturally require network access when you use them.

### Nostos Cloud

The official hosted service composes the public Nostos product with private hosting infrastructure for authentication, tenant provisioning, managed storage, billing, managed AI, and operations.

Reusable product behavior, the domain model, capability contracts, and the customer-facing Angular application live here first. Hosted provider and operator implementations remain private.

See the [public/private boundary ADR](docs/adr/cloud-public-private-boundary.md) and [deployment capabilities](docs/cloud/deployment-modes.md) for the architectural contract.

---

## Architecture & Tech Stack

Nostos is designed as a single product with a reusable product layer, a public SelfHosted host, and one shared frontend.

```text
Nostos-Rebirth/
├── Nostos.Product/       # canonical product, domain, application and API behavior
├── Nostos.Backend/       # public SelfHosted host: SQLite, local files, backup, BYOK
├── Nostos.Shared/        # shared DTOs and product contracts
├── Nostos.Frontend/      # Angular application shared by SelfHosted and Cloud
└── docs/                 # product, architecture and operational documentation
```

### Core stack

- **Backend:** [.NET 10](https://dotnet.microsoft.com/) with ASP.NET Core Minimal APIs
- **Database:** SQLite with Entity Framework Core 10 in the public SelfHosted host
- **Frontend:** [Angular 21](https://angular.dev/) with standalone components and Signals
- **EPUB:** `epub.js`
- **PDF:** `ngx-extended-pdf-viewer`
- **Audio:** `Howler.js`
- **Concept graph:** Graphology, Sigma.js, and `d3-force`
- **Editor:** TinyMCE with Turndown for Markdown conversion
- **Icons:** Phosphor
- **Typography:** Newsreader and Hanken Grotesk
- **MCP:** Model Context Protocol support through the ASP.NET Core SDK

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js LTS](https://nodejs.org/) with npm
- Optional: `ffmpeg` and `ffprobe` for LibriVox audiobook imports, which are assembled into chaptered `.m4b` files

### Development

Clone the repository, install the root tooling and frontend dependencies, then start the backend and frontend together.

```bash
git clone https://github.com/Christian-Gennari/Nostos-Rebirth.git
cd Nostos-Rebirth

npm install
npm --prefix Nostos.Frontend install
npm start
```

The Angular development server runs at **http://localhost:4200** and proxies API requests to the backend on port **5099**.

### Production

```bash
npm run prod
```

This builds the Angular frontend and serves the application through the .NET host. Open **http://localhost:5099** in your browser.

### Docker / Compose

The container keeps application binaries under `/app` and all durable SelfHosted data under **`/data`**. Do not relocate ASP.NET's content root to persist data: the frontend is served from the application's `wwwroot`, while the SQLite database and library storage have their own configurable paths.

Build and run directly:

```bash
docker build -t nostos:selfhosted .
docker volume create nostos-data
docker run -d \
  --name nostos \
  -p 5099:8080 \
  -v nostos-data:/data \
  --restart unless-stopped \
  nostos:selfhosted
```

Or use the included Compose file:

```bash
docker compose up -d --build
```

Then open **http://localhost:5099**. The named `nostos-data` volume contains `nostos.db`, stored books, and local backup archives, so replacing the container does not replace your library data.

To rebuild after pulling a newer revision:

```bash
docker compose build --pull
docker compose up -d
```

Compose keeps the existing named volume. Use Nostos' portable `.nostos` export/backup features as your user-owned backup and migration path; a Docker volume is persistent storage, not a substitute for backups.

For a non-container install, the historical database location remains `<content root>/nostos.db` unless `Persistence__DatabasePath` is configured explicitly. `Storage__BooksRoot` and `Storage__BackupsRoot` can likewise override their local paths.

A stable prebuilt SelfHosted registry tag is being handled separately; the checked-in Compose path deliberately builds the exact source revision you have cloned instead of depending on an ambiguous registry tag.

For provider-specific behavior and requirements, see [Content Providers & Acquisition](docs/content-providers.md).

---

## Design

Nostos uses an editorial visual language built around warm paper-like surfaces, restrained forest green, literary typography, and low-chrome controls. The goal is to make the application feel closer to a considered reading environment than a conventional SaaS dashboard.

The Nostos mark is a doorway and a subtle lowercase `n`: a Forest Green (`#293E32`) rounded tile with a White (`#FFFFFF`) arch cut from it. The mark remains the same in light and dark themes.

Read the [Design Manifesto](docs/design-manifesto.md) for the product's visual and interaction principles, or browse the [brand assets](_brand-assets/README.md).

---

## Documentation

- **[Design Manifesto](docs/design-manifesto.md):** Product, visual, and interaction principles.
- **[Content Providers & Acquisition](docs/content-providers.md):** Provider architecture and import behavior for external catalogues.
- **[MCP Library Contracts](docs/library-mcp-contracts.md):** Stable Model Context Protocol contracts for agents and tools.
- **[Backend Endpoints](Nostos.Backend/_docs/endpoints.md):** REST API reference.
- **[Public/private boundary ADR](docs/adr/cloud-public-private-boundary.md):** Ownership boundary between the public product and official hosted composition.
- **[Deployment Capabilities](docs/cloud/deployment-modes.md):** SelfHosted and Cloud capability contract.
- **[Portable Archives](docs/cloud/portability.md):** Provider-neutral `.nostos` export and import contract.
- **[PostgreSQL Compatibility](docs/cloud/postgresql-compatibility-spike.md):** Evidence for the shared relational product model.

Current development work is tracked in [GitHub Issues](https://github.com/Christian-Gennari/Nostos-Rebirth/issues).

---

## License & Trademark

Nostos is licensed under the **[GNU General Public License v3.0 or later](./LICENSE)**. You may run, study, modify, and redistribute the software under those terms.

The Nostos name, related word marks, and official visual identity are not granted by the GPL. Distributed forks and derivative builds must use independent naming and branding. See [TRADEMARK.md](./TRADEMARK.md) for the full policy.
