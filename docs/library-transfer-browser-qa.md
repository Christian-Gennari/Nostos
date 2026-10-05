# Library transfer — real-browser QA (issue #680, slice B10)

Acceptance coverage for exporting, importing and activating a portable Nostos
library through the real UI against a **real SelfHosted backend**. It is a
Playwright suite with its own multi-instance fixture; the default smoke
configuration (`playwright.config.ts`) ignores it, so `npm run e2e` and the
frontend CI job are unchanged.

## How to run

```bash
cd Nostos.Frontend
npm run e2e:transfer -- --project=lt-chromium
npm run e2e:transfer -- --project=lt-firefox
npm run e2e:transfer -- --project=lt-webkit
npm run e2e:transfer -- --project=lt-mobile-webkit
```

Run **one browser per invocation**: the global setup builds once and starts a
fresh fixture for that invocation, and every mutating scenario launches its own
disposable destination instance and tears it down in `finally`.

### Host requirements

- `@playwright/test` browsers for Chromium, Firefox and WebKit
  (`npx playwright install chromium firefox webkit`).
- WebKit needs its system libraries; on hosts without them,
  `npx playwright install-deps webkit` (or the distro equivalents) is
  required. The suite was also run on a host without root by extracting the
  WebKit libraries locally into the browser bundle; that host needed
  `PLAYWRIGHT_SKIP_VALIDATE_HOST_REQUIREMENTS=1` in the environment. This is an
  environment workaround, not part of the suite.

## Fixture

`e2e/support/library-transfer-fixture.mjs` and
`e2e/support/library-transfer-harness.ts` own the environment:

- one temp directory per instance (fresh SQLite database, private `wwwroot`
  copy of the production Angular build, `backend.log`), one free local port;
- `Storage__ChunkBytes` pinned to the 4 MiB contract minimum and disk safety
  margins zeroed, so an ~17.5 MiB synthetic archive crosses the 5-chunk
  requirement;
- the immutable `source` instance is seeded through the app's real REST APIs
  with four books (generated EPUB, generated PDF, a 17 MiB synthetic audio
  file, and a file-less book), a highlight and two notes, two collections with
  memberships, reading progress and a Writing Studio document. Library B
  (destination) has different books/notes/collections so replacement is
  observable.
- every destination is launched per scenario (`launch-instance`) and killed in
  `finally`; `kill-all` runs in global teardown even after a crash.

No real user data is involved: everything is generated in the fixture and the
data roots are disposable.

## Scenario × browser matrix

`a` export + native download + backend verification · `b` empty destination
import → auto-activation → reload serves the library · `c1` populated
replacement with server counts, sealed overlay, one confirmation · `c2` change
after preparation → updated counts + "changed since the import started" → fresh
confirmation · `d1` reload mid-upload → same-file reselect resumes missing
chunks only · `d2` reload during activation → reattach → outcome · `e1`
non-archive · `e2` corrupted archive · `e3` cancel mid-upload → new import ·
`e4` backend restart mid-upload · `e5` second-tab lease · `f` dialog focus
trap/Escape/sealed overlay.

| Scenario | Chromium | Firefox | WebKit | Mobile WebKit |
| --- | --- | --- | --- | --- |
| a export + download + server validation | pass | pass | pass | pass (main path) |
| b empty import → activation → reload | pass | pass | pass | pass (main path) |
| c1 populated replacement (counts, seal, one confirm) | pass | pass | pass | — |
| c2 change after preparation, fresh confirmation | pass | pass | pass | — |
| d1 reload mid-upload, missing chunks only | pass | pass | pass | — |
| d2 reload during activation, reattach | pass | pass | pass | — |
| e1 non-archive rejected, nothing changed | pass | pass | pass | — |
| e2 corrupted archive rejected, library intact | pass | pass | pass | — |
| e3 cancel mid-upload, new import starts | pass | pass | pass | — |
| e4 backend restart mid-upload recovers | pass | pass | pass | — |
| e5 second tab block, no double upload | pass | pass | pass | — |
| f replacement dialog a11y (focus, Escape, seal) | pass | pass | pass | — |

Stability: three consecutive full runs per browser, all green —
Chromium 12/12 three times (3.6–3.8 min each), Firefox 12/12 three times
(3.8 min), WebKit 12/12 three times (3.9–4.1 min), mobile WebKit 1/1 three
times (~38–40 s). Full desktop run ≈ 4 minutes per engine.

### Measured numbers (final runs)

- Archive: 18,355,577–18,355,582 bytes (17.5 MiB), SHA-256 recorded per run in
  `e2e/test-results/library-transfer/scenario-a-*.json`; server-verified counts
  `books=4, notes=3, collections=2, collectionMemberships=2, mediaEntries=3`.
- Chunking: 4 MiB chunks, `ceil(17.5 MiB / 4 MiB) = 5` chunks; the suite fails
  if fewer than five chunks are requested.
- Empty import end to end (file selection → hashing → 5 chunk uploads →
  preparation → auto-activation → reload): 11.7 s Chromium, 12.6 s Firefox,
  12.8 s WebKit. The per-scenario JSON artifacts record these.
- Resume: after a mid-upload reload the server reported 4/5 chunks received
  (Chromium, Firefox) or 2/5 (WebKit); after reselecting the same file the
  client re-sent exactly the missing chunks and the test asserts no
  already-received chunk index appears in the network log again.

## Bugs found by this QA and fixed here

1. **Populated replacement could never complete** — the recovery capture
   refused the `derived/` book-text artifact directory the app itself writes
   inside a book folder, failing with `migration_activation_recovery_failed`.
   Fixed in `SelfHostedRecoverySnapshots` (skip that directory for manifest
   entries, keep measuring its bytes), with new backend tests.
2. **Mid-upload reload could fail the whole import** — the upload engine
   treated a client-aborted request body (`IOException` with the request token
   cancelled) as `migration_storage_exhausted` and failed the durable job.
   Fixed in `SelfHostedMigrationTransferService`; the aborted chunk stays
   unreceived and resumes.
3. **Replacement dialog showed the wrong facts** — the server's verified
   counts (`preparedImport.counts`) were read under the wrong key, the 409
   parser dropped `changedSinceImportStarted`, and the "empty destination
   became populated" dialog rendered a blank incoming side.
4. **Background conflict re-probe loop** — after a background destination
   conflict re-opened the dialog, the old status poll kept re-probing and the
   counts flickered back to stale preflight values.
5. **Focus never entered the replacement dialog** — CDK's one-shot
   autoCapture ran while the unconfirmed probe kept the cancel action
   disabled; initial focus now applies after render once the dialog is
   interactive.

## Observations reported, not fixed here

- `BookTextIngestionWorker` logs `SqliteException` cycle failures while a
  cutover is closing the live database (1–2 per activation with books). The
  worker retries and scenarios still pass, but it is noise that #681 should
  own.
- SelfHosted deliberately has **no onboarding gate** (`CloudEntryService`
  returns the product directly), so the "import into an empty instance"
  scenario runs through Settings on a fresh instance. It uses the exact same
  `library-import-flow` component the Cloud onboarding host embeds; that host
  identity is covered by `cloud-entry.component.spec.ts` unit tests.

## CI

The suite is **not** wired into CI: three engines plus per-scenario disposable
backends would add roughly 15 minutes and WebKit system dependencies to every
push, while `frontend.yml` currently installs Chromium only. Run it locally
(or from the scheduled audit job) with the commands above; the default
`npm test`/`npm run e2e` and their runtimes are unchanged.
