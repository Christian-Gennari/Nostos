# Library count semantics

The Library sidebar and collection badges count the same unit the default Library renders: **grouped works**.

- **All Books** counts distinct `WorkId` values, not raw `Books` rows.
- Status and format counts apply their existing book-level predicate first, then count distinct `WorkId` values. If two editions of one work match, the work counts once.
- Collection membership remains edition-specific, but a collection badge counts distinct `WorkId` values across that collection's whole subtree. A work represented by editions in two child collections therefore counts once at their common ancestor.
- Grouping is only by persisted `WorkId`. Nostos does not deduplicate counts by title/author, and separate works remain separate even when their metadata is similar.

## Incomplete local uploads

A missing file is not an error state. Physical books and intentionally metadata-only digital books may be `Ready` with `HasFile=false`.

The local-file flow therefore declares its intent explicitly at create time. A newly-created row whose file is expected starts as `BookStatus.UploadPending`; it becomes `Ready` only after `POST /api/books/{id}/file` has stored the file and persisted its canonical file metadata. An interrupted or abandoned upload remains `UploadPending`.

`UploadPending` rows are retained for retry/repair and direct lookup, but are excluded from the grouped Library list, status/format counts, collection badges, and grouped edition summaries. This is a lifecycle distinction, not a rendering-time deletion.

Provider acquisition states (`Downloading`, `Transcoding`, `Failed`) keep their existing behavior. In-progress provider imports remain visible as the Library's operational progress surface.
