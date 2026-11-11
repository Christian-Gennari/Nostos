# Import a Calibre library

SelfHosted only, for now. Nostos reads the Calibre library folder the server can
see, shows what it found, and brings in the books you choose. Your Calibre
library is never changed or deleted.

Nostos Cloud cannot read a folder, so it does not offer this yet: a Cloud
import needs the library uploaded (a zipped Calibre folder through the library
transfer). That is a follow-up, not part of this release.

## Set it up

### Docker Compose

Add one line under `volumes` for the `nostos` service. The
[Compose file in this repository](../compose.yml) has it ready to uncomment.

```yaml
services:
  nostos:
    image: ghcr.io/christian-gennari/nostos:latest
    ports:
      - "5099:8080"
    volumes:
      - nostos-data:/data
      - /mnt/calibre:/external/calibre:ro      # add this line
    restart: unless-stopped

volumes:
  nostos-data:
```

- Replace `/mnt/calibre` with the folder that holds `metadata.db` — the Calibre
  library itself, not a folder of loose books.
- Keep `/external/calibre` exactly as written: that is where the Nostos image
  looks.
- `:ro` mounts the library read-only. Nostos opens `metadata.db` in SQLite's
  read-only mode and copies the book files it imports; it never writes to the
  library, and with `:ro` the operating system would refuse even if it tried.

Apply it:

```bash
docker compose up -d
```

### docker run

```bash
docker run -d \
  --name nostos \
  -p 5099:8080 \
  -v nostos-data:/data \
  -v /mnt/calibre:/external/calibre:ro \
  --restart unless-stopped \
  ghcr.io/christian-gennari/nostos:latest
```

### Without Docker

Set the environment variable `Calibre__LibraryRoot` (two underscores) to the
absolute path of the Calibre library folder, or add this to `appsettings.json`,
and restart Nostos:

```json
{
  "Calibre": { "LibraryRoot": "/mnt/calibre" }
}
```

The path must be absolute. A relative path stops the server at start-up with a
message naming `Calibre:LibraryRoot`.

The server's user must be able to read `metadata.db` and the book folders. In
the container that is user id 1654:

```bash
chmod -R o+rX /mnt/calibre
```

## What is imported

| Calibre | Nostos |
| --- | --- |
| Title, authors | The book's title and author. Several Calibre authors are joined with `&` |
| Series and series index | Series and volume number |
| Tags | Categories on the book; collections only when you tick the box |
| Publisher, publication date | Publisher and published date |
| Identifiers (ISBN) | ISBN, when it is a valid ISBN-10 or ISBN-13 |
| Rating (0–10) | 0–5 stars (`10` → `5`) |
| Comments (HTML) | The book's description, as text |
| Cover (`cover.jpg`) | Nostos stores its own copy |
| EPUB, PDF, M4B, M4A, MP3 | The chosen file is copied into Nostos |
| `#read`, `#date_read` | Read date (the finish date). A read flag alone cannot become a date |
| Other custom columns | Listed on the review screen as left alone |

A Calibre book can hold several formats. The review screen offers each one as
its own choice, with the best one ticked (EPUB, then PDF, then M4B, M4A, MP3).
Ticking two formats brings both in as editions of the same work. Nostos holds
one file per book, so a second format of the same book is a second edition, not
a second file on the same row.

Calibre formats Nostos cannot read (AZW3, MOBI, TXT and others) are listed on
the review screen with the reason; they are never silently ignored. A book with
only unsupported formats appears under **Not imported**.

## Match against what you already have

Every Calibre book is matched with your library the same way the other imports
match: ISBN first, then the same title and author. A book that matches an
edition with no file receives the file and keeps its metadata; a format that is
already in the library is reported as already here and is not duplicated. A
Resemblance that is not certain appears under **Check these** and is left out
until you answer it.

## Review, import, undo

1. **Settings → Import → Calibre library → Import…**. Nostos reads
   `metadata.db` and the book folders. Nothing is written.
2. **Review.** The screen shows the counts, every book, the formats it can
   bring, the tags and read date it found, and the custom columns it left alone.
   Untick anything you do not want. *Create collections from Calibre tags* is
   off by default: tags already become categories, and no collection is created
   unless you tick it.
3. **Import.** The import runs on the server. You can close the window or the
   page; reopening Settings shows the progress.
4. **Undo.** The result screen takes the whole import back: the files it copied,
   the covers it added, the memberships and collections it created, and the
   books it added when you have not read or written in them since. A book you
   have started reading, or put a note on, stays.

Importing the same library again is safe: a format already here is reported as
already here and nothing is duplicated.

## Removing an imported book

Deleting a book in Nostos removes Nostos's copy. The file in the Calibre
library stays where it is, and the next import offers it again.
