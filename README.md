<div align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/logo-dark.svg" />
    <source media="(prefers-color-scheme: light)" srcset="docs/brand/logo-light.svg" />
    <img alt="Nostos" src="docs/brand/logo-light.svg" width="276" />
  </picture>

  <p><strong>Open-source, self-hosted commonplace book for readers and writers.</strong></p>
  <p>Bring your books together, keep thoughts with their sources, and turn what you read into your own writing.</p>

  <p>
    <a href="https://nostos.page">Website</a> •
    <a href="https://docs.nostos.page">Docs</a> •
    <a href="#features">Features</a> •
    <a href="#getting-started">Getting started</a> •
    <a href="#license--trademark">License</a>
  </p>
</div>

<hr>

<div align="center">
  <a href="https://nostos.page/#film">
    <img
      width="2880"
      height="1620"
      alt="Nostos Launch Film — A home for your reading life"
      src="docs/screenshots/promo-poster.png"
    />
  </a>
  <p><em>Watch the launch film on <a href="https://nostos.page/#film">nostos.page</a></em></p>
</div>

## What is Nostos?
 
 Nostos is an open-source, self-hosted commonplace book for people who read and write. It brings physical books, EPUBs, PDFs and audiobooks into one quiet library, keeps saved passages and notes attached to the exact page they came from, and lets you bring quotations into your drafts with verified citations.
 
 It connects the whole journey in one place: **Read → Notice → Capture → Return → Write.**
 
 - **Read & listen:** Read EPUBs and PDFs with adjustable typography, or listen to audiobooks (multi-track files play as one book).
 - **Capture & return:** Highlights and notes stay attached to their source. Topics connect ideas across books in the Brain.
 - **Write:** Type `@` in the Writing Studio to pull a kept passage into your draft with attribution attached. Export with real footnotes to Word, PDF, Markdown or rich text for Substack.
 - **Own your data:** Runs locally as a single Docker container with SQLite. Export notes and highlights as plain Markdown or full `.nostos` backups anytime.
 
 It is deliberately quiet. There are no social feeds, streaks or engagement mechanics. The optional assistant stays out of the way until you ask for it.
 
 You can use the hosted service, [Nostos Cloud](https://nostos.page), or run it yourself. This repository is the complete, open-source **SelfHosted** app.

---

## Features

### Library

- **Every kind of book in one place:** physical books, EPUB, PDF, TXT, MOBI and audiobooks (MP3, M4A, M4B). Audiobooks split into many files play as one book.
- **Free books, built in:** search Project Gutenberg, Standard Ebooks, Wikisource and LibriVox from **Add a book**. Litteraturbanken (Swedish literature) is available to turn on.
- **Collections and editions:** nested collections, drag and drop, and editions of the same work grouped together.
- **A To read list:** keep the books you want but don't own yet, with a short **Next up** shelf.
- **Search everything:** press <kbd>Ctrl</kbd>/<kbd>⌘</kbd>+<kbd>K</kbd> to find books, topics, notes and documents. Search forgives typos.

<div align="center">
  <img width="2880" height="1800" alt="Nostos Library with collections and book covers" src="docs/screenshots/library.png" />
</div>

### Reading & listening

- **Readers for every format:** an EPUB reader with adjustable typography, a PDF reader, and an audiobook player with chapters, speed control and a sleep timer.
- **Highlights and notes on the page:** select text to highlight it, and click a highlight to open its note.
- **Passages:** save a long stretch of a book, a range of PDF pages or a clip of an audiobook, and read or play it again on its own.
- **Switch between ebook and audiobook:** if you have both editions, continue in the other one near where you are.
- **Read aloud:** listen to any EPUB or PDF with your device's voice or a natural voice from a speech server.
- **Bring your highlights:** import highlights and notes from Kobo, KOReader, a Kindle's `My Clippings.txt` or a Readwise CSV export, review them, then add them to the right books.

<div align="center">
  <img width="2880" height="1800" alt="Nostos EPUB reader open at the first chapter of Moby-Dick" src="docs/screenshots/reader.png" />
</div>

### Brain

The Brain is where notes from different books meet.

- **Topics:** link ideas with `[[Topic]]` in any note or with a tap on its card, and sort new notes one by one.
- **Places, like the Library:** all notes, passages, unsorted notes, the topic map, and every topic, in one sidebar.
- **Back to the source:** every topic leads back to the notes, passages and books behind it, and can carry your own words.

<div align="center">
  <img width="3800" height="1820" alt="Nostos Brain topic map with a selected topic" src="docs/screenshots/brain-graph.png" />
</div>

### Writing Studio

- **Opens on the page:** a blank sheet with the caret in it; your files and references slide in when you want them, and the chrome fades while you type.
- **Type `@` to quote:** find a note, quote or passage by its words, book or author (`@schopenhauer will`) and put it on the page with its attribution. A long passage opens so you can pick the sentences you want.
- **`/` for blocks:** headings, lists, a divider, an image, a table or code from an empty line.
- **Make the page yours:** Studio themes, typefaces, size, spacing and width, a typewriter mode that keeps the current line centred, and optional typewriter sounds.
- **Saves as you type,** as plain Markdown.

<div align="center">
  <img width="2880" height="1800" alt="Nostos Writing Studio" src="docs/screenshots/writing-studio.png" />
</div>

<details>
  <summary><strong>More screenshots</strong></summary>
  <br />
  <table>
    <tr>
      <td width="50%">
        <strong>Book page</strong><br /><br />
        <img width="2880" height="1800" alt="Book Details" src="docs/screenshots/book-details.png" />
      </td>
      <td width="50%">
        <strong>Add a book</strong><br /><br />
        <img width="2880" height="1800" alt="Add Book Modal" src="docs/screenshots/add-book-modal.png" />
      </td>
    </tr>
    <tr>
      <td colspan="2">
        <strong>Brain topics and notes</strong><br /><br />
        <img width="2880" height="1800" alt="Nostos Brain: a topic and its notes, with the sidebar of places and topics" src="docs/screenshots/brain-list.png" />
      </td>
    </tr>
  </table>
</details>

### Ask Nostos (optional)

Ask Nostos is an AI assistant for your own library. It can find what you have read, help you capture a thought, or reorganise your collections. When it changes your library, it shows one set of changes you can undo, or review before anything happens. Nostos works fully without it.

On SelfHosted, you connect your own AI provider in **Settings → AI connections**. Nostos Cloud includes a managed allowance.

### Your library stays yours

- **Portable:** export your whole library as a `.nostos` archive and import it into any Nostos, Nostos Cloud or self-hosted.
- **Your notes in plain Markdown:** download your notes, highlights, passages and topics as a .zip of Markdown files, one per book and one per topic, with `[[Topic]]` links kept. Open it in Obsidian or any text editor.
- **Use the files you already have (SelfHosted):** point Nostos at a folder of books on your server. It reads them in place and never changes them.
- **Backups:** SelfHosted has local backup and restore. Nostos Cloud makes daily managed backups, which you can see in Settings.
- **OPDS:** browse and download your library from compatible e-reader apps.

---

## Getting started

### Nostos Cloud

The easiest way to start: **[get started at nostos.page](https://nostos.page/start)** and see [plans and pricing](https://nostos.page/pricing).

### Self-host with Docker
 
 ```bash
 docker volume create nostos-data
 docker run -d \
   --name nostos \
   -p 5099:8080 \
   -v nostos-data:/data \
   --restart unless-stopped \
   ghcr.io/christian-gennari/nostos:latest
 ```
 
 Or use the included Compose file: `docker compose up -d`. Then open **http://localhost:5099**. No account or subscription needed.
 
 App-store packages are also available for **Unraid**, **CasaOS**, **Umbrel**, **Runtipi** and **selfh.st**.
 
 Everything Nostos keeps lives in the `/data` volume: the database, your books, local backups and the key ring in `/data/keys` that protects AI keys saved in Settings. Back up the whole volume, and keep it when you replace the container.

- **Hardware:** the image runs on 64-bit Intel and AMD machines (`linux/amd64`) and on 64-bit Arm (`linux/arm64`): a Raspberry Pi 4 or 5 with a 64-bit OS, an Apple Silicon Mac, or an Arm NAS. Docker picks the right one for you. 32-bit systems are not supported.
- **Memory:** Nostos uses about 110–125 MB when idle and about 220–330 MB with a small library (50 ebooks with covers and 5 audiobooks). Give it at least 512 MB; 1 GB leaves room for large imports and backups.
- **Update:** `docker compose pull && docker compose up -d`. `latest` is published only after the full CI passes on `main` and that exact image has passed its start-up and persistence check; it never moves back to an older revision. To pin a build, set `NOSTOS_IMAGE_TAG=selfhosted-sha-<commit>`; a commit that only changes documentation shares its parent's image. Older `sha-*` tags are not SelfHosted builds.
- **Books already on your server:** mount the folder at `/external/media` (read-only), then open **Settings → This server → External media**. See the [External media guide](docs/external-media.md).
- **Coming from an older image?** Older images kept the key ring inside the container. Copy it into `/data/keys` before you replace the container, or re-enter your AI keys afterwards.

The [self-hosting docs](https://docs.nostos.page/get-started/quickstart/) cover requirements, reverse proxies, backups and updates.

### Run from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js LTS](https://nodejs.org/).

```bash
git clone https://github.com/Christian-Gennari/Nostos.git
cd Nostos
npm install
npm --prefix Nostos.Frontend install
npm start        # development: http://localhost:4200
npm run prod     # production build: http://localhost:5099
```

Outside Docker, the database defaults to `<content root>/nostos.db`. Override paths with `Persistence__DatabasePath`, `Storage__BooksRoot`, `Storage__BackupsRoot` and `DataProtection__KeysPath`. See [Getting started (development)](docs/getting-started.md) for details.

---

## How it's built

Nostos is one product with two deployments. **SelfHosted** (this repository) runs on its own with SQLite, local files, local backups and your own AI provider. **Nostos Cloud** adds private hosting pieces (sign-in, managed storage, billing, managed AI) around the same product and the same app. See the [public/private boundary](docs/adr/cloud-public-private-boundary.md).

```text
Nostos/
├── Nostos.Product/    # the product: domain, application and API behaviour
├── Nostos.Backend/    # the SelfHosted host: SQLite, local files, backups, your own AI keys
├── Nostos.Shared/     # shared DTOs and contracts
├── Nostos.Frontend/   # the Angular app, shared by SelfHosted and Cloud
└── docs/              # product, architecture and operations docs
```

**Stack:** [.NET 10](https://dotnet.microsoft.com/) Minimal APIs, EF Core 10 and SQLite · [Angular 22](https://angular.dev/) with Signals · epub.js · ngx-extended-pdf-viewer · Howler.js · Sigma.js and Graphology · TipTap with marked and Turndown · Phosphor icons · Newsreader and Hanken Grotesk.

**Design:** warm paper surfaces, a restrained forest green, literary type and quiet controls. The mark is a doorway and a lowercase `n`: a Forest Green (`#293E32`) tile with a White (`#FFFFFF`) arch, the same in light and dark themes. Read the [Design Manifesto](docs/design-manifesto.md) or browse the [brand assets](_brand-assets/README.md).

---

## Documentation

- **[docs.nostos.page](https://docs.nostos.page):** guides for Nostos Cloud and self-hosting.
- **[Releases](https://github.com/Christian-Gennari/Nostos/releases):** what's new. How releases are made: [docs/releases.md](docs/releases.md).
- **Guides in this repo:** [External media](docs/external-media.md) · [Read aloud](docs/read-aloud.md) · [Content providers](docs/content-providers.md) · [Library portability](docs/cloud/portability.md) · [SelfHosted activation & recovery](docs/selfhosted-activation-recovery.md)
- **For developers and agents:** [REST API](Nostos.Backend/_docs/endpoints.md) · [Library skill for agents](.agents/skills/nostos-library/SKILL.md) · [Deployment capabilities](docs/cloud/deployment-modes.md) · [PostgreSQL compatibility](docs/cloud/postgresql-compatibility-spike.md)
- **[Security policy](SECURITY.md):** how to report a vulnerability.

Work in progress is tracked in [GitHub Issues](https://github.com/Christian-Gennari/Nostos/issues). Before your first pull request is merged, a bot asks you to accept the [Contributor License Agreement](CLA.md) with one comment.

---

## License & Trademark

Nostos is licensed under the **[GNU Affero General Public License v3.0 or later](./LICENSE)**. You may run, study, modify, and redistribute the software under those terms. If you run a modified version as a network service, you must offer its source code to the people who use it.

The Nostos name, related word marks, and official visual identity are not granted by the AGPL. Distributed forks and derivative builds must use independent naming and branding. See [TRADEMARK.md](./TRADEMARK.md) for the full policy.

The EPUB reader bundles the **[Libron](https://github.com/nicoverbruggen/libron)** typeface (© Nico Verbruggen, derived from Readerly and Newsreader), licensed separately under the [SIL Open Font License 1.1](./Nostos.Frontend/public/fonts/libron/OFL.txt).
