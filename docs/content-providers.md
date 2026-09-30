# Content Providers & Acquisition

Nostos can import books from external public-domain/open-content catalogues
(Project Gutenberg, Wikisource, LibriVox, and future sources) without any of them leaking
into the library, storage, reader, notes, work-grouping or backup layers.

This document describes the provider/acquisition architecture introduced by
[#166](https://github.com/Christian-Gennari/Nostos/issues/166), how to
add a new built-in provider, and what a provider must not do.

## The two concerns

The feature is split in two, and almost every later decision follows from that
split:

| Concern | Question it answers | Where it lives |
| --- | --- | --- |
| **Provider** | *What content exists outside Nostos?* | `Nostos.Backend/Providers/Contracts/` + `Nostos.Backend/Providers/<Name>/` |
| **Acquisition** | *How does a chosen external item become an ordinary local book?* | `Nostos.Backend/Providers/Acquisition/` |

A provider **describes** remote content. It returns data and nothing else: no
database writes, no storage paths, no reader behaviour, no knowledge of
collections, works or progress. The acquisition layer owns every side effect.

The payoff is that a completed import is *just a local book*. Reading, playback,
notes, progress, work grouping and `.nostos` archives never consult a provider,
and nothing about an imported book fails when the source goes offline.

## Contracts

All of these are Nostos-owned. No provider's own JSON/XML types cross this
boundary — a Gutenberg Atom entry, a LibriVox API record and a future OPDS entry
all normalize into the same records.

**Identity** — `IContentProvider`: a stable `Id` (lowercase, persisted in
provenance, never renamed once shipped), a `DisplayName`, declared
`ProviderCapabilities`, and an optional `RightsNotice`.

**Capabilities are opt-in interfaces**, not one large interface every provider
has to stub out:

| Interface | Purpose |
| --- | --- |
| `IProviderSearch` | Find items by a normal user query. |
| `IProviderCatalog` | Full detail for one external item id. |
| `IProviderAcquisitionPlanner` | Resolve item + asset into concrete download parts and an output format. |
| `IProviderDownloadPolicy` | Which hosts may be downloaded from, and within what bounds. |
| `IAcquisitionAssembler` | Combine downloaded parts into one file (multi-track audiobooks only). |

`ProviderRegistry` resolves each provider into a `ProviderRegistration` bundle
(provider + the capability implementations it actually supplies), so no caller
ever casts. It is **fail-fast**: a provider whose declared `ProviderCapabilities`
disagree with the interfaces it implements stops the app at startup, because the
alternative is a confusing runtime failure in the middle of a download.

`ProviderCapabilities` is also what the UI reads, exposed as lower-case strings
by `GET /api/providers`, so a source that cannot do something is never offered
for it.

### Normalized models

`ProviderMetadata` (title/author/language/publisher/dates/categories/narrator/
duration), `ProviderAsset` (one downloadable representation, **with no URL**),
`ProviderItem` (item identity + metadata + assets + cover + source/rights),
`ProviderSearchQuery`/`ProviderSearchPage`, `ProviderDownloadPart`,
`ProviderOutput`, `ProviderAcquisitionPlan`.

`ProviderDownloadPart.FileExtension` is a validated plain extension, never a
filename: the acquisition layer generates every path it writes, so no remote
string can influence a filesystem path.

Chapters in a plan are the canonical `BookChapterDto` from
`Nostos.Shared`, not a provider-specific chapter type — the audio reader gets
usable navigation without knowing the source was multi-track.

## Acquisition flow

```
AcquisitionRequest { ProviderId, ExternalId, AssetId?, CollectionIds? }
  1. resolve provider                                  -> provider_unknown
  2. cheap duplicate check, only when AssetId was given
  3. plan the acquisition (the only catalogue call)     -> provider_item_not_found
  4. authoritative duplicate check on the RESOLVED asset id
  5. read-only probe: does the library already hold this work with a file?
                                                        -> already_in_library
  ---------- staging: no database writes at all ----------
  6. free-space pre-flight
  7. download every part (bounded concurrency, caps, retries)
  8. validate the parts
  9. assemble (provider assembler, or the single part is the artifact)
 10. cover artwork (best effort, image-validated)
  ---------- commit: library writes ----------
 11. create/match the book via ILibraryService
 12. adopt the staged file into storage (a rename on the same volume)
 13. AttachAcquiredAssetAsync: file details + provenance in ONE transaction
 14. cover (best effort)
 15. staging directory removed (finally)
```

### Why two phases

An earlier design created the book *before* downloading, reasoning that storage
is keyed by a book id. That was wrong on its own terms — the id is a `Guid` we
can generate without touching the database — and it was wrong in practice: a
whole audiobook takes tens of minutes, so a file-less book row sat visible in
the library (Kobo, OPDS, MCP, the UI) for that entire window, and a restart
mid-download leaked it permanently. Phase 1 therefore has **zero** database
side effects; if anything fails or the process dies, the staging directory is
simply removed.

The residual window is the space between step 12 and step 13, which is two
local operations with no network involved. If it is interrupted, the worst case
is a file-less book row (a normal state in Nostos) plus a file on disk, and
retrying the import is safe because step 4 recognises the provenance row.

### Duplicate and retry semantics

`BookAcquisitions` is unique on `(ProviderId, ExternalId, AssetId)`. That is the
whole mechanism:

- Re-importing the same item+asset is a no-op: it returns the existing book
  without downloading anything (step 2/4, and the database index as the final
  backstop).
- The check happens **after** the plan resolves the asset, because when the
  caller names no asset the resolved id is not known earlier — checking a `null`
  asset id would either miss a duplicate or point at the wrong asset.
- A retry after any failure starts from a clean staging directory and finds no
  provenance row, so it simply re-runs.

### Failure semantics

| Failure | What survives |
| --- | --- |
| Download/validation/assembly | Nothing. No book, no files, no staging directory. |
| Storage (`AdoptBookFileAsync`) | No book (rolled back), no files, no staging directory. |
| Provenance write | The adopted file is deleted, and a book this run created is deleted. Staging directory removed. |

A stored file is deleted *only* on the file it belongs to (`DeleteBookFile`), so
a rollback cannot take a matched book's pre-existing cover with it. Only books
this acquisition created are ever deleted.

## Provenance

`BookAcquisitions` — a separate table with generic columns:

`ProviderId`, `ProviderDisplayName`, `ExternalId`, `AssetId`, `AssetFormat`,
`ImportedExtension`, `SourceUrl`, `RightsStatement`, `AcquiredAt`, `BookId`.

There is deliberately **no** `GutenbergId`/`LibriVoxId` column anywhere on
`Books`: provider identity is not bibliographic identity, and a column per
provider would put provider knowledge into the core library domain and grow one
column per source. Provenance is also *not* a second identity system — matching
and work grouping remain `ILibraryService`'s job.

`BookModel` carries one generic `Acquisition` navigation, `BookDto` appends a
compact `BookSourceDto? Source`, and anything that serialises a book includes it
(`BackupService` does, or backups would silently drop where imported books came
from).

**Rights are a quoted statement, never a verdict.** `RightsStatement` stores the
source's own wording ("Public domain in the USA."), because Nostos is in no
position to declare a work unrestricted in every jurisdiction — and a boolean
claiming "free" would have to be un-invented the first time a real licensing
question arrived. Providers supply it via `ProviderSourceInfo.RightsStatement`
and a short `RightsNotice` for the source picker.

## Storage

`IFileStorageService` has one lower-level primitive, and browser uploads go
through it too:

- `SaveBookFileAsync(bookId, Stream, fileName, ct)` — writes to a
  `book.<ext>.partial` sibling and **renames** it into place only once the
  stream is fully consumed, so a failed, aborted or cancelled transfer can never
  leave a half-written file where a reader would find it.
- `SaveBookFileAsync(bookId, IFormFile)` — a thin wrapper over the above, so
  manual uploads and remote acquisitions cannot drift apart.
- `AdoptBookFileAsync(bookId, sourcePath, fileName, ct)` — moves an
  already-materialised file into place. Acquisition downloads and assembles
  hundreds of megabytes into staging, and must not keep a second copy of it.
  The destination is derived entirely from the book id and a validated
  extension; only the source is a path.
- `DeleteBookFile(bookId)` — removes only the primary file, leaving covers
  alone, so a rollback is precise.

The storage root is configurable via `Storage:BooksRoot` (default
`Storage/books` under the content root). This exists because a test must never
be able to write into a real library, and because a deployment may want a
dedicated media volume. `BackupService` reads
`IFileStorageService.StorageRoot` rather than rebuilding the path, so the
archive and the storage service cannot disagree about where the library is.

## HTTP surface

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/providers` | Registered sources and their capabilities. |
| GET | `/api/providers/search?query=&kind=&limit=` | Unified discovery across every eligible provider. `kind` is optional (`ebook` / `audiobook`). |
| GET | `/api/providers/{providerId}/search?query=&limit=&offset=` | Provider-specific normalized search (kept for compatibility/testing). |
| GET | `/api/providers/{providerId}/items/{externalId}` | Normalized item detail + assets. |
| GET | `/api/providers/{providerId}/items/{externalId}/cover` | Proxied cover artwork. |
| POST | `/api/providers/{providerId}/acquire` | Start an import; returns a job. |
| GET | `/api/providers/acquisitions/{jobId}` | Poll progress. |
| DELETE | `/api/providers/acquisitions/{jobId}` | Cancel. |

**No route accepts a URL.** A client names a provider, an item and an asset, and
the provider resolves the actual location server-side. Combined with the
download policy, that is what stops the acquisition endpoint from becoming an
arbitrary-URL downloader.

### Unified discovery

The Add Book flow does not ask the reader to choose a provider first. The
aggregate search endpoint selects providers by capability:

- all material: `Search` plus either ebook or audiobook acquisition;
- e-books: `Search + EbookAcquisition`;
- audiobooks: `Search + AudiobookAcquisition`.

Eligible searches run concurrently. One source failing does not hide sibling
results: the response includes a status for every participating provider, with
its own `Notice` or stable error code. Successful result sets are sorted by
provider id and interleaved round-robin while preserving each provider's own
result order. There is deliberately no invented cross-provider relevance score.

Aggregate discovery also applies a hard per-provider deadline through
`ProviderDiscovery:SearchTimeout` (default `00:00:12`). This boundary is
separate from each provider's own `HttpClient` timeout: a provider task that
never completes, even if it ignores cancellation, stops blocking the aggregate
once the discovery deadline expires. Provider-local timeout/cancellation is
reported as `provider_timeout`; genuine HTTP request cancellation still
propagates instead of being converted into a normal search response.

The whole search is additionally bounded by an interactive budget,
`ProviderDiscovery:AggregateSearchTimeout` (default `00:00:04`). Every eligible
provider starts at the same moment and each waits at most the smaller of the
two deadlines, so one unhealthy catalogue costs the Add Book picker a few
seconds instead of the full per-provider deadline. Providers still pending at
the budget are cancelled and reported as `provider_timeout`; siblings that
answered in time contribute their results, which are merged once. The default
is a conservative starting point, not a measured percentile; tune it once real
provider timings are available.

Wikisource has no server-side search: its provider filters WS Export's
export-ready OPDS catalogue locally. The parsed catalogue is cached per process
for six hours and refreshed single-flight, so concurrent searches share one
download. The refresh runs on its own bounded lifetime (the client timeout),
so a search that gives up at the aggregate budget still warms the cache for the
next one. A failed refresh is not cached, and an expired catalogue is refetched
rather than served.

Search items are thin and carry no assets. They *do* carry normalized
`MediaKind`, so the client can show "E-book" or "Audiobook" without knowing
that Gutenberg is an ebook source or LibriVox is an audiobook source. Result
identity is always `ProviderId + ExternalId`; external ids are not globally
unique.


Imports run as **jobs** because a whole audiobook takes far longer than any
sensible HTTP request: `POST .../acquire` returns as soon as the job is queued
and the client polls. Completion is only reported once the final file is stored
and the library row is attached.

## Security and resource limits

- **https only.** A plaintext download could be rewritten in transit, and an
  imported library file is exactly what an attacker would want to substitute.
- **Every redirect hop is re-validated** against the provider's host allow-list.
  `HttpClient`'s auto-redirect is deliberately off; letting it follow them would
  mean the policy only ever saw the first URL.
- **Host matching is hierarchical suffix matching.** `archive.org` allows
  `ia801600.us.archive.org`; a single-label wildcard would not, yet that is where
  archive.org's own download redirects land.
- **Streaming with hard caps** — per part, per acquisition, and a part count —
  enforced *while* reading, because `Content-Length` is the source's claim, not
  a fact. Nothing is buffered whole; only cover art is held in memory, bounded.
- **Retries only what is worth retrying** (429, 503, 408, 5xx, transient IO), with
  exponential backoff. A 404 is answered immediately.
- **Free-space pre-flight** before downloading, from the parts' declared sizes.
- **Staging is never `Path.GetTempPath()`.** On Linux `/tmp` is frequently a
  RAM-backed tmpfs; a few gigabytes of audiobook there would exhaust host memory
  and get the app OOM-killed. Staging lives beside the books directory, which
  also makes committing the result a rename rather than a copy.
- **Concurrency is bounded.** `Acquisition:DownloadConcurrency` (default 3) per
  job, and `Acquisition:MaxConcurrentTranscodes` (default 1) process-wide —
  encoding a feature-length audiobook already saturates the machine, and a
  second concurrent encode makes the whole server unresponsive rather than
  merely slow.
- **Cancellation propagates** through planning, download, assembly and cleanup.

## Configuration

```jsonc
{
  "Storage": {
    // Optional. Absolute, or relative to the content root. Default: Storage/books
    "BooksRoot": "/srv/nostos/books"
  },
  "ProviderDiscovery": {
    // Hard deadline for one provider in unified search.
    "SearchTimeout": "00:00:12",
    // Interactive ceiling for the whole search across providers.
    "AggregateSearchTimeout": "00:00:04"
  },
  "Acquisition": {
    // Optional. Defaults to a sibling of Storage/books, NOT /tmp.
    "WorkingRoot": "/srv/nostos/staging",
    "DownloadConcurrency": 3,        // parts fetched at once, 1..8
    "DownloadAttempts": 3,           // per part, 1..6
    "DownloadTimeoutSeconds": 900,   // per part
    "AcquisitionTimeoutMinutes": 240,// whole job
    "MaxRedirects": 5,
    "MaxConcurrentTranscodes": 1,    // process-wide, 1..4
    "FreeSpaceFactor": 2.5,          // required free space vs expected bytes
    "MinimumFreeSpaceBytes": 536870912,
    "JobRetentionMinutes": 180,      // how long a finished job stays pollable
    "MaxRetainedJobs": 50
  }
}
```

## Adding a built-in provider

1. Create `Nostos.Backend/Providers/<Name>/<Name>Provider.cs`.
2. Implement `IContentProvider` (`Id` lowercase and hyphenated, e.g.
   `gutenberg`) plus whichever capability interfaces the source supports.
3. Normalize the source's own catalogue into the contracts. Keep its DTOs
   `private`/`internal` to that folder, and do the source's *quirks* here —
   including normalizing metadata into the shape the rest of Nostos expects
   (e.g. `"Austen, Jane"` → `"Jane Austen"`, stripping life years), because
   `BookIdentityNormalizer` deliberately does not rewrite author strings and a
   differently-formatted author would never group with the same work.
4. Implement `IProviderDownloadPolicy`: the allowed hosts (suffix-matched), and
   the caps. Only ever emit `https` URLs.
5. Register it as a singleton `IContentProvider` in product composition. If it
   declares `Search` plus an acquisition capability and normalizes
   `ProviderItem.MediaKind` + assets, it automatically participates in unified
   discovery — no Angular provider allow-list or provider button is added.
   Nothing in the library, storage or reader layers changes. There is no dynamic
   assembly loading and no third-party plugin marketplace: providers are
   built-in and compiled in.
6. Add tests with a stubbed HTTP layer; never depend on the live source for
   automated tests.

### Provider rules

- Never write to the database, never touch `Storage`, never create a book.
- Never return a URL to a client, and never accept one from a client.
- Never implement reader behaviour. If the source's representation does not
  match how Nostos models a book (a multi-track audiobook, say), implement
  `IAcquisitionAssembler` and normalize it during acquisition instead.
- Declare capabilities honestly; the registry enforces the correspondence with
  the interfaces at startup.

## Project Gutenberg (built-in provider)

Registered as `gutenberg`. The first real provider, and the worked example of
the contract above.

**Protocol.** Gutenberg's machine-readable OPDS (Atom) feeds, not the
human-facing website:

- search — `https://www.gutenberg.org/ebooks/search.opds/?query=<escaped>`
- detail — `https://www.gutenberg.org/ebooks/<id>.opds`
- cover — `https://www.gutenberg.org/cache/epub/<id>/pg<id>.cover.medium.jpg`

`start_index` is added only for a non-zero offset, and is 1-based where the
provider's `Offset` is 0-based. Every request carries an identifying
User-Agent. All OPDS/XML knowledge lives in `GutenbergCatalog` so a change to
Gutenberg's protocol is a change to one file.

**What the provider maps**

- `ExternalId` — Gutenberg's numeric book id.
- `Author` — inverted from catalogue order (`Austen, Jane` → `Jane Austen`),
  with life dates stripped and suffixes kept (`King, Martin Luther, Jr.` →
  `Martin Luther King, Jr.`). The detail feed lists the same person once per
  entry, so names are de-duplicated *after* normalization — otherwise Pride and
  Prejudice imports with an author of "Jane Austen, Jane Austen". The feed's own
  `<author>Project Gutenberg</author>` is the catalogue, not an author, and is
  never read.
- `Language` — the `dcterms:language` code widened to a name (`en` →
  `English`).
- `Categories` — LCSH subjects only; the `DCMIType`/`LCC` classification codes
  are not subjects and are dropped.
- `Rights` — the feed's public-domain statement, quoted verbatim.
- `Assets` — EPUB representations plus PDF **only when the OPDS detail entry
  actually advertises a PDF acquisition link**. `epub3-images` remains the
  preferred representation when available. EPUB and PDF are both ebook assets;
  PDF is not a `ProviderMediaKind`. MOBI/Kindle variants are deliberately *not*
  exposed because Nostos has no reader for them.
- `Cover` — derived from the id, so search results carry a cover without a
  per-item request.

**Search and results.** The search feed carries only title, author and id;
language, subjects, rights and formats come from the detail request made when a
result is opened or planned. That keeps a search to a single request.

**Safety.** The external id is validated as digits before any URL is built, so a
client cannot steer the request path; a non-numeric id is rejected without a
request being made at all. Cover and file downloads go through the host policy
(`*.gutenberg.org`) and are proxied by Nostos, so the browser never contacts
Gutenberg directly.

**Failure behaviour.** A 404 on a detail feed returns "no such item" rather than
an error. A response that is not a feed (an HTML error page, say) surfaces as
`provider_response_invalid`. An asset the item does not offer surfaces as
`provider_asset_unavailable`.

**Etiquette.** OPDS feeds are small and cache-friendly; Nostos makes one request
per search, one per detail, and downloads each asset once. There is no crawl,
no bulk harvesting and no scheduled polling of the catalogue.

## Wikisource (built-in provider)

Registered as `wikisource`, backed by Wikimedia's WS Export service rather
than by scraping normal Wikisource pages.

Search uses the English WS Export OPDS catalogue and detail uses its Atom export
for the selected page. The detail response normalizes two ebook format families
when resolvable:

- EPUB — `application/epub+zip`, preferred by default;
- PDF — `application/pdf`.

WS Export's machine export endpoint accepts `page` and `format`; its current
generator contract supports the `pdf` alias (mapped by WS Export to its PDF
generator). Nostos therefore resolves PDF independently as
`/?lang=en&format=pdf&page=<page>` rather than deriving a PDF URL from the EPUB
link or scraping the human Wikisource site. A requested PDF never falls back to
EPUB if its export fails.

Both representations remain `ProviderMediaKind.Ebook`. The normalized asset
MIME is what lets the Add Book UI present the human-level decision "EPUB or
PDF" without a Wikisource-specific branch. Rights and source URLs remain the
work's own Atom metadata, and downloads stay behind the WS Export/Wikimedia host
allow-list.

## Standard Ebooks (built-in provider)

Registered as `standard-ebooks`. Standard Ebooks approved Nostos for access to
its official OPDS feeds on **September 25, 2026** by whitelisting the project
User-Agent:

```
Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos-Rebirth)
```

The repository has since been renamed to `Christian-Gennari/Nostos`, but this
historical URL remains intentionally in the User-Agent because it is the exact
identity Standard Ebooks approved. Change it only after Standard Ebooks approves
a replacement.

That access model carries **no shared credential or feed secret**. The same
provider identity is therefore used in SelfHosted and Cloud; nothing sensitive
is committed to the repository or sent to a browser. The User-Agent is part of
the approved integration contract and must not be changed casually — an
unapproved/default client is expected to receive `401 Unauthorized`.

**Protocol.** Nostos uses Standard Ebooks' official OPDS search endpoint, never
the human-facing website:

```
https://standardebooks.org/feeds/opds/all?query=<text>&per-page=<n>&page=<n>
```

Standard Ebooks supports both OPDS 1.x Atom and OPDS 2.0 JSON. Nostos
deliberately requests the Atom acquisition representation. At the time this
provider was implemented, the Atom entries include the source's complete
per-title `<rights>` statement while the OPDS 2.0 publication generator does
not emit an equivalent rights field. Preserving that source wording is more
important than preferring the newer wire format, and the choice remains entirely
inside the provider boundary.

**Identity and detail.** Standard Ebooks' canonical identifier is the ebook page
URL, for example:

```
https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice
```

Nostos persists a route-safe provider id derived from that canonical path
(`jane-austen~pride-and-prejudice`). Some editions add another path segment
for a translator/editor, and that segment remains part of the id. The feed does
not expose a separate per-publication OPDS endpoint, so item detail is
re-resolved through the official search template and then **exact-matched
against the canonical identifier**. A malformed external id is rejected before
any network request.

**What the provider maps**

- title, author(s), language, publisher, publication date, summary and subjects;
- the canonical Standard Ebooks page as source provenance;
- the source's complete rights statement, including its jurisdiction warning
  and CC0 statement for original Standard Ebooks content;
- the source-provided cover artwork;
- EPUB acquisition links and advertised sizes.

Only the EPUB representations that Nostos's existing ebook reader can consume
are exposed:

1. **Recommended compatible epub** — preferred by default;
2. **Advanced epub** — retained as an explicit alternative.

Kobo Kepub, AZW3/Kindle and XHTML links may appear in the feed but are not
offered as import assets here. The provider does not add new reader formats just
because the source publishes them.

The selected EPUB is handed to the common acquisition pipeline. Downloads are
restricted to `standardebooks.org` (including its subdomains through the
provider host policy), use the same approved Nostos User-Agent, retain source
provenance, and inherit the generic duplicate/import semantics. No
Standard-Ebooks-specific behavior is added to the library or EPUB reader.

## LibriVox (built-in provider)

Registered as `librivox`. The worked example of a provider whose material is
**not** a single file — the case `RequiresAssembly` exists for.

**Protocol.** LibriVox's JSON API, not the website:

- search — `https://librivox.org/api/feed/audiobooks/?title=^<text>&format=json&extended=1&limit=&offset=`
- detail — `https://librivox.org/api/feed/audiobooks/?id=<n>&format=json&extended=1`
- cover — `https://archive.org/services/img/<archive-identifier>`
- sections — archive.org MP3 URLs, as published in the record

`extended=1` is what includes the `sections` array. All JSON knowledge lives in
`LibriVoxCatalog`.

**Search is genuinely limited, and the provider says so.** The feed has no
free-text search: a title query matches only titles that *start with* the text
(the `^` operator), and there is no substring matching. So the provider searches
titles first and, when that finds nothing, searches authors — and returns a
`Notice` explaining which happened, so a thin result set reads as a catalogue
limitation rather than a broken search. A query shorter than two characters is
not sent at all.

**What the provider maps**

- `ExternalId` — the LibriVox recording id.
- `Author` — built from the feed's structured `first_name`/`last_name` fields.
- `Narrator` — the distinct readers across all sections. A solo reading yields
  one name; a full-cast dramatic reading is summarised ("Denny Sayers (d. 2015)
  and 8 others") rather than pasted in as a cast list.
- `Language`, `Categories` (the feed's genres), `PublishedDate`
  (`copyright_year`), `Duration` (`totaltime`).
- `Description` — the feed's HTML is stripped and entities decoded, so markup
  never reaches the reader.
- `Cover` — archive.org's thumbnail service, derived from the item identifier,
  so search results carry a cover without a second request each.
- `PartCount` — the section count, which is what the user needs to judge a
  recording's size.

**One asset, deliberately.** A LibriVox item offers exactly one asset, `m4b`.
There is no per-track asset: the multi-track form is an implementation detail of
the source, not something a user should be able to import. Asking for any other
asset fails with `provider_asset_unavailable` rather than quietly importing the
M4B anyway.

### The M4B assembly

```
ordered MP3 sections -> download -> validate -> ffprobe each -> concat -> AAC -> single .m4b -> chapters
```

The audio reader plays one stream, so the track set is absorbed here, once. What
lands in the library is one local `.m4b`; nothing downstream knows LibriVox
exists.

Four details are load-bearing, and each was verified against ffmpeg before being
relied on:

1. **`-ar 44100` is required.** Sections are recorded by different volunteers
   over years. The first section of a real recording measured here was
   **22.05 kHz**; without an explicit rate the AAC encoder adopts the first
   input's rate and silently downsamples the entire book.
2. **Chapters are measured, not read from the API.** The feed's per-section
   `playtime` is rounded and drifts; across forty sections that accumulates into
   tens of seconds of misalignment. Each downloaded file is probed with ffprobe
   and the chapter spans are cumulative real durations. (On the recording used
   for the smoke test the API and the measurement agreed to the second — which is
   luck, not a guarantee.)
3. **The ffmetadata chapter key is `title`, with `TIMEBASE=1/1000`.** The
   plausible-looking `CHAPTERTITLE` is not a key ffmpeg recognises and fails
   *silently*, producing chapters with blank names. Values are escaped for
   `=`, `;`, `#` and `\`, because chapter titles come from volunteers and
   contain those characters.
4. **`-map_chapters 1` is required alongside `-map_metadata 1`.** By default
   ffmpeg copies chapters from the first input that has any, so an ID3 chapter
   tag on section one would win over the generated table.

The effective command is:

```
ffmpeg -f concat -safe 0 -i concat.txt -i chapters.txt \
       -map 0:a -map_metadata 1 -map_chapters 1 \
       -c:a aac -b:a 64k -ac 1 -ar 44100 -movflags +faststart -f ipod out.m4b
```

The concat *demuxer* is used rather than the concat filter: these recordings run
to tens of sections, and a filter graph would need an input and a pad per
section. `-movflags +faststart` matters because the file is hundreds of
megabytes and the reader streams it with Range requests — without it the index
atom sits at the end of the file and seeking stalls. `-ac 1`/`-b:a 64k` matches
the sources (mono 64 kbps speech); a higher bitrate cannot restore detail that
was never recorded.

The finished file is then verified before it is accepted: the chapter count
ffprobe can see must equal the number generated, and the duration must match the
measured total within a small tolerance. A chapter table that silently failed to
apply is exactly the failure this step exists to catch, so the pipeline does not
report success merely because the tracks downloaded.

### Runtime prerequisite: ffmpeg and ffprobe

Importing a LibriVox recording **requires `ffmpeg` and `ffprobe` on the server**.
This is a real dependency and is treated as one:

- it is **detected at startup** by `MediaProcessRunner`, which resolves each tool
  on `PATH` (or from `Media:FfmpegPath` / `Media:FfprobePath`) and checks it is a
  real executable — availability is a fact rather than an assumption;
- it is **reported at the point of use**: with the tools absent, the import fails
  with `media_tool_missing` and the message
  *"Importing a LibriVox recording needs ffmpeg and ffprobe on the server's PATH
  (or Media:FfmpegPath / Media:FfprobePath set to their full paths)."*
- it **degrades partially**, not catastrophically: with the tools absent the app
  starts normally, and LibriVox search and item detail still work — only the
  acquisition step fails.

Gutenberg imports need no external tooling, and a server without ffmpeg is a
perfectly valid Nostos deployment.

**Encoding cost.** Speech AAC encodes at roughly 70–100× real time on one core,
so a 13-hour audiobook takes about 8–10 minutes. Encodes are serialised process-wide
(`AcquisitionOptions:MaxConcurrentTranscodes`, default 1) because one encode
already saturates the box.

### Operational notes

- **archive.org is the actual host of the audio, and it is flaky.** The
  published URLs point at `www.archive.org`, which redirects to a
  `dn<NNN>.ca.archive.org` node; transfers were observed succeeding, then
  returning `503`, within minutes. The host allow-list is suffix-matched so those
  redirect targets are covered, downloads are retried
  (`AcquisitionOptions:DownloadAttempts`, default 3), and a persistent failure
  surfaces as `download_failed` naming the URL and status. Expect occasional
  transient failures on a large import.
- The cover comes from archive.org's thumbnail service, so a network hiccup there
  costs a cover, not the import.

## Documented limitations

- **In-memory job store.** A server restart forgets in-flight jobs; the UI sees
  an unknown job id and says so. Nothing is lost — staging goes with the process
  and no library row exists until the very end.
- **A crash between the file rename and the provenance write** can leave a
  file-less book plus an orphaned file in `Storage/books/<id>/`. The file-less
  book is a valid state the user can delete. There is no automatic orphan
  scanner; the pre-existing manual-upload path has the same property.
- **No automatic asset re-selection.** If a source stops offering the asset an
  import was planned for, the import fails with `provider_asset_unavailable`;
  the user picks another asset.
- **A source whose search is limited** (LibriVox, for example, matches a whole
  title or an author surname only) reports a `Notice` alongside a thin result
  set rather than pretending the catalogue is empty.
- **LibriVox search cannot match inside a title.** The feed only matches titles
  that begin with the query, so "wallpaper" will not find "The Yellow
  Wallpaper" — the user searches for the opening of the title instead. The
  provider falls back to an author search and explains itself via `Notice`, but
  it cannot invent a substring index the source does not have.
- **A LibriVox recording is imported whole or not at all.** There is no way to
  import a single section, by design: per-track import would put the multi-track
  representation into the library, which is the thing this design exists to
  prevent.
- **LibriVox covers come from archive.org's thumbnail service.** The LibriVox
  record itself carries no cover field, so a cover is derived from the archive
  item identifier; if archive.org is unreachable the import still succeeds and
  simply has no artwork.
- **LibriVox downloads depend on archive.org**, which is an external service with
  no uptime guarantee and was observed returning `503` intermittently during
  development. Large imports may need a retry. Nothing local is lost when this
  happens — no book row and no file are created until the whole import succeeds.
