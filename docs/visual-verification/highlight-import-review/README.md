# Highlight-import review verification

Verified on 2026-10-02 in Chromium at device scale 1, with a 1440 × 900 desktop
viewport and a 390 × 844 mobile viewport. Screenshots were inspected at 1:1.

The backend ran on 5320 with `Persistence__DatabasePath` pointing to a fresh
SQLite backup under `/tmp/highlight-review-verification/`. `Storage__BooksRoot`
and `Storage__BackupsRoot` also pointed into that disposable directory. The
frontend ran on 5321 with the worktree proxy. No mutating request went to the
shared app.

## Source evidence

The four test sidecars were written by the official KOReader v2026.07.1
AppImage during the earlier agent's UI capture of public-domain test books.
This continuation reused those files through the Nostos dialog. Raw sidecars
and the original KOReader screenshots remain local and are not published.

The local synthetic `KoboReader.sqlite` uses the schema built from firmware 4.38.23828
CREATE/ALTER statements. Its annotation rows were inserted for testing; this
is **not** a database written by a physical Kobo. That remains unverified.

Before this change, the dialog imported immediately and left missing books
unplaced; the previous result is recorded in PR #673.
The new review step provides **Add to library** and **Choose a book** before
writing anything.

## Exercised flow

| Step | Books | Notes | Batches | Result |
| --- | ---: | ---: | ---: | --- |
| Fresh backup after migration | 76 | 62 | 0 | `20261002151347_AddNoteImportTables` applied; three empty import tables. |
| Preview and review choices | 76 | 62 | 0 | Preview and choosing Add to library wrote nothing. |
| Commit five selected files | 77 | 71 | 1 | Nine highlights, four destination books, six remembered source mappings. |
| Import the same files again | 77 | 71 | 1 | Zero new notes, nine duplicates; confirmed mappings remembered. |
| Undo from Recent imports | 77 | 62 | 0 | All nine imported notes removed; new book and mappings retained. |
| Import after Undo | 77 | 71 | 1 | All nine notes could be imported again. |
| Undo from the result | 77 | 62 | 0 | All nine imported notes removed again. |

- Confirmed *Candide, ou l'optimisme* → *Candide*.
- Searched the library and chose *Devils* for *The possessed*.
- Chose Add to library for *The Time Machine*, which created one physical book.
- Left *A Book Not In Nostos* out; its one annotation was not stored.
- Checked `SelectedText` and `Content` exactly for the Candide note
  “War made regular, typed in KOReader.” and the Time Machine note
  “Why only three, asked in KOReader.”
- Asserted that every pre-existing note row remained unchanged at each step.
- One batch contained all nine notes across the five files, with four distinct
  destination book IDs. Both Undo entry points exercised the real backend.
- No browser console errors or page errors. No mobile horizontal overflow.
- The shared database remained at 76 books and 62 notes; a read-only SHA-256
  comparison of all note rows before/after also matched.

Generated-demo dialog text and final counters are in
[browser-results.json](browser-results.json); the private-copy counters above
were verified locally and are reported only as aggregate counts.

## Screenshots

Published screenshots were captured separately using a new, empty database
seeded with only three generated sample books: Candide, Devils and Fictions.
The same browser flow passed there (books 3 → 4, notes 0 → 9 → 0). These PNGs
capture only the dialog at device scale 1. They contain no shared-library
records, background account information, raw annotations or device database.
The published `browser-results.json` is from that generated demo dataset.

- [First review](review-light.png), [mobile review](review-mobile.png).
- [Library book picker](book-picker.png), [choices before commit](review-decided.png).
- [Import result](import-result.png), [one recent batch](recent-import.png).
- [Remembered review](review-remembered.png), [persisted dark theme](review-dark.png).
- [Re-import result](reimport-result.png), [Undo result](import-undone.png).

## Automated checks

Commands ran in the isolated worktree, with frontend commands in
`Nostos.Frontend/`:

```text
dotnet build Nostos.sln --no-restore
Build succeeded. 0 Warning(s), 0 Error(s).

dotnet test Nostos.Backend.Tests/Nostos.Backend.Tests.csproj --no-restore
Passed! - Failed: 0, Passed: 1111, Skipped: 0, Total: 1111

npm run check
34 stylesheets parse cleanly.
Every colour token has a dark counterpart.
Design-language drift: no findings across 46 stylesheets.

npm run build
Application bundle generation complete.

npm test
Test Files 3 failed | 72 passed (75)
Tests 1135 passed (1135)
```

The three frontend suites fail to load with `No such built-in module: node:`:
`reader-shell.component.spec.ts`, `pdf-reader.component.spec.ts`, and
`motion-contract.spec.ts`. A disposable `git archive origin/main` snapshot at
`a39e9652f650092ae2b74b62ef49fc9b560b7d78` reproduced the same three failures
with 1122 passing tests. They are unchanged by this branch.

Regression checks cover multi-file batching and Undo, keeping added books
and mappings after Undo, counting each library book once in the result,
retrying failed Undo requests, and searching again after a failed request.
