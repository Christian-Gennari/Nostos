# Repair Library rows left by duplicate or incomplete uploads

Use this runbook for accounts affected by the pre-#620 duplicate-submit bug or by local uploads abandoned before explicit `UploadPending` lifecycle state existed.

The repair is intentionally **dry-run first**. Never bulk-delete rows based only on `HasFile=false` or `FileName IS NULL`: legitimate physical and metadata-only digital books use that shape.

## 1. Take a backup and record the baseline

Create the normal Nostos backup/export for the account before changing anything. Record:

- `GET /api/books/status-counts`;
- the affected collection badge counts;
- the grouped Library result for the suspected work(s);
- the candidate book ids, `WorkId`, type, title/author, `CreatedAt`, `Status`, `HasFile`, `FileName`, and collection memberships.

For a self-hosted SQLite database, read-only SQL is acceptable for discovery. Do **not** use SQL `DELETE` for cleanup.

A useful dry-run query is:

```sql
SELECT
    b.Id,
    b.WorkId,
    b.BookType,
    b.Title,
    b.Author,
    b.CreatedAt,
    b.Status,
    b.HasFile,
    b.FileName
FROM Books AS b
ORDER BY b.WorkId, b.CreatedAt, b.Id;
```

If the deployed schema names owned file columns differently, inspect the schema and adjust only the read-only projection.

## 2. Classify candidates conservatively

### Rows created after this fix

`Status = UploadPending` is explicit evidence that the row was created for a required local file that never completed. Confirm that no retry is still intended before deletion.

### Legacy rows created before this fix

A legacy abandoned upload can look exactly like a legitimate `Ready + HasFile=false + FileName=null` book. There is no safe automatic predicate for those rows.

Use incident evidence instead: the #620 event window, repeated near-simultaneous rows, the affected user's report, matching `WorkId`, and which edition the user intends to keep. Similar title/author alone is not enough. Preserve legitimate separate editions.

Produce a written candidate list before deletion:

```text
DELETE candidate: <book-id>  work=<work-id>  reason=<duplicate-submit|confirmed-abandoned-upload>
KEEP survivor:    <book-id>  work=<work-id>  reason=<canonical edition/user confirmed>
```

If classification is uncertain, stop with that row untouched.

## 3. Delete only through the canonical book endpoint

For each approved candidate, call the normal authenticated endpoint:

```http
DELETE /api/books/{bookId}
```

Do not delete directly from `Books`, `BookCollections`, notes, text-index tables, or object storage.

The canonical endpoint removes the book through `ILibraryService.DeleteBookAsync`, then invokes the book-text lifecycle cleanup and `IBookAssetStorage.DeleteBookFilesAsync`. That is the supported path for dependent database data and stored objects.

Delete one candidate at a time and record the HTTP result. A successful deletion returns `204 No Content`. Treat any other status as a failed repair step and investigate before continuing.

## 4. Verify after each work

Re-run the baseline reads:

- the grouped Library should show the intended work/card exactly once;
- `All Books`, status counts, and collection badges should agree with the grouped Library semantics;
- intended editions should still be present under the work;
- deleted ids should return not found;
- no remaining candidate should be removed merely because it has no file.

Keep the before/after ids and counts with the repair record. The procedure is complete only when the observable read model is correct and every deletion went through the canonical endpoint.
