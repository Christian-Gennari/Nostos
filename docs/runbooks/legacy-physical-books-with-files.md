# Legacy physical books that carry a file

Before #332 a physical book could own a digital file: Add Book defaulted to
"Physical book" and an upload was accepted on any row. A book added by ISBN and
then given a scanned PDF ended up `BookType = 'physical'` **and**
`FileDetails_HasFile = 1`. Its book page showed paper controls beside a working
Read button, the Library labelled it "Physical", and its page count and ISBN sat
in the physical columns (`PhysicalBookModel_PageCount`,
`PhysicalBookModel_Isbn`) while its reading position was saved in the PDF
reader's format (`{"pageNumber":N}`), not the paper one (`{"page":N}`).

The migration `ConvertLegacyPhysicalBooksWithFiles` fixes these rows in place on
upgrade. Nothing needs doing by hand in the normal case; this runbook is for
understanding what changed, checking the result, and handling the rows it
deliberately left alone.

## What the migration does

For every row with `BookType = 'physical'` and `FileDetails_HasFile = 1`:

| File extension (case-insensitive)      | Becomes     | Columns                                                                                     |
| -------------------------------------- | ----------- | ------------------------------------------------------------------------------------------- |
| `.pdf`, `.epub`, `.txt`, `.mobi`, `.azw3` | `ebook`     | `PhysicalBookModel_Isbn` → `Isbn`, `PhysicalBookModel_PageCount` → `PageCount`, physical columns nulled |
| `.mp3`, `.m4a`, `.m4b`                 | `audiobook` | physical columns nulled; `NormalizedIsbn` cleared (an audiobook has no ISBN identity)        |

Book ids, reading progress, notes, ratings, quotes and collection membership are
not touched. Nothing is split into a paper edition plus a digital one: the
"physical" label came from the Add Book default, not from anyone saying they own
a paper copy.

**A row is left physical** when

- its saved position is the paper format `{"page":N}` — someone read it as paper,
  and moving that position is not the migration's guess to make; or
- its stored file name has no book extension this product accepts.

Those rows keep the old shape. Both the shape and its limitation are documented
on the book page in the PR that introduced the migration.

## 1. Take a backup first

Create the normal backup/export before upgrading, as for any migration.

## 2. Dry-run the classification (read-only)

On a **copy** of the database, count what would convert and what would be left
alone. Never run a mutating statement against the live file.

```bash
cp nostos.db /tmp/nostos-check.db
```

```sql
-- What the migration will convert, by target type.
SELECT
    CASE
        WHEN lower(FileName) LIKE '%.mp3' OR lower(FileName) LIKE '%.m4a' OR lower(FileName) LIKE '%.m4b'
            THEN 'audiobook'
        WHEN lower(FileName) LIKE '%.pdf' OR lower(FileName) LIKE '%.epub' OR lower(FileName) LIKE '%.txt'
          OR lower(FileName) LIKE '%.mobi' OR lower(FileName) LIKE '%.azw3'
            THEN 'ebook'
        ELSE 'left physical'
    END AS target,
    count(*) AS rows
FROM (
    SELECT ("FileDetails_FileName") AS FileName
    FROM Books
    WHERE "BookType" = 'physical'
      AND "FileDetails_HasFile" = 1
      AND replace(trim(coalesce("Progress_LastLocation", '')), ' ', '') NOT LIKE '{"page":%'
) AS candidate
GROUP BY target;
```

```sql
-- The rows the migration leaves alone, and why.
SELECT "Id", "Title", "FileDetails_FileName", "Progress_LastLocation"
FROM Books
WHERE "BookType" = 'physical'
  AND "FileDetails_HasFile" = 1
  AND (
      replace(trim(coalesce("Progress_LastLocation", '')), ' ', '') LIKE '{"page":%'
      OR lower(coalesce("FileDetails_FileName", '')) NOT LIKE '%.pdf'
         AND lower(coalesce("FileDetails_FileName", '')) NOT LIKE '%.epub'
         AND lower(coalesce("FileDetails_FileName", '')) NOT LIKE '%.txt'
         AND lower(coalesce("FileDetails_FileName", '')) NOT LIKE '%.mobi'
         AND lower(coalesce("FileDetails_FileName", '')) NOT LIKE '%.azw3'
         AND lower(coalesce("FileDetails_FileName", '')) NOT LIKE '%.mp3'
         AND lower(coalesce("FileDetails_FileName", '')) NOT LIKE '%.m4a'
         AND lower(coalesce("FileDetails_FileName", '')) NOT LIKE '%.m4b'
  );
```

## 3. Check after upgrading

```sql
-- Should be 0.
SELECT count(*) FROM Books
WHERE "BookType" = 'physical' AND "FileDetails_HasFile" = 1
  AND replace(trim(coalesce("Progress_LastLocation", '')), ' ', '') NOT LIKE '{"page":%';
```

For a converted book, the Library should now say "E-book"/"Audiobook", the read
button should open straight into the reader, and the page count should show the
number that used to be in the physical column.

## Known limits

- **A converted row is not reversible.** `Down` does nothing: the old shape is
  the defect, and a reverse pass could not tell a converted book from one that
  was always an ebook.
- **An audiobook loses the print ISBN and page count** the physical row carried.
  The audio model has no field for them, so they are cleared rather than left in
  columns nothing reads. This only affects a row whose file is audio; a database
  whose physical rows hold PDFs or EPUBs is unaffected.
- **Normalized identity.** The startup backfill recomputes `NormalizedIsbn` from
  the book's type after the migration. If a converted book's ISBN now equals
  another book's, startup refuses with an actionable duplicate-identifier error
  instead of silently reusing it — resolve the duplicate, then start again.
