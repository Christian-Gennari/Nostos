# External media: use books that are already on your server

SelfHosted only. Nostos can read audiobooks and ebooks from a folder you already
keep on the server, without copying them. You scan the folder, review what was
found, import it, and scan again later when you add more.

**Your original files stay where they are and are never modified or deleted by
Nostos.**

Nostos Cloud does not have this feature.

## Set it up

The web interface cannot choose a folder. The person who runs the server makes
one folder visible to Nostos and names it in the configuration.

### Docker Compose

Mount the folder read-only and tell Nostos where it is inside the container:

```yaml
services:
  nostos:
    image: ghcr.io/christian-gennari/nostos:latest
    ports:
      - "5099:8080"
    volumes:
      - nostos-data:/data
      - /mnt/audiobooks:/external/audiobooks:ro
    environment:
      ExternalMedia__Root: /external/audiobooks
    restart: unless-stopped

volumes:
  nostos-data:
```

- `/mnt/audiobooks` is the folder on your machine. Change it to yours.
- `:ro` mounts it read-only. Nostos never writes to the folder, and with `:ro`
  the operating system would refuse even if it tried.
- Keep the `/data` volume as it is. Nostos stores its database, notes, covers
  and backups there, and needs to write to it.

Restart the container. **Settings → This server → External media** appears once
`ExternalMedia__Root` is set.

### Without Docker

Set `ExternalMedia__Root` to the absolute path of the folder (or
`ExternalMedia:Root` in `appsettings.json`) and restart Nostos.

### File permissions on Linux

The container runs as a non-root user. That user must be able to read the files
and list the folders:

```bash
chmod -R o+rX /mnt/audiobooks
```

A folder Nostos is not allowed to read is skipped, and the review screen says
how many were skipped.

### One folder

This release links one folder. To link several, mount them side by side under
one parent and point `ExternalMedia__Root` at the parent.

If you later mount the same folder at a different path, linked books keep
working as long as the files are at the same place below it. Books follow the
folder's identity (`ExternalMedia__RootId`, default `external`), not its path.
Leave that setting alone once you have linked books.

## How the folder is read

| In the folder | Becomes |
| --- | --- |
| An `.epub` or `.pdf` file | One ebook |
| An `.m4b` file | One audiobook |
| A folder with one `.mp3` or `.m4a` file | One audiobook |
| A folder with several `.mp3` or `.m4a` files | One audiobook with one track per file |

A layout like this works well:

```text
audiobooks/
  Ursula K. Le Guin/
    A Wizard of Earthsea/
      01 - Warriors in the Mist.mp3
      02 - The Shadow.mp3
      cover.jpg
  Bram Stoker/
    Dracula.m4b
ebooks/
  Middlemarch.epub
```

- **Track order** is the file name order, with numbers read as numbers, so
  `2 - Intro` comes before `10 - End`.
- **Title and author** come from the file's own tags, then from the file or
  folder name. Nostos does not guess an author from a folder higher up.
- **Covers** come from `cover.jpg`, `cover.png` or `folder.jpg` in a track
  folder, or from artwork embedded in the audio file. Nostos stores its own copy
  of the cover. EPUB and PDF covers are not read yet.
- **Symbolic links and hidden files** (names starting with a dot) are skipped
  and never followed.
- **Other audio and ebook formats** (FLAC, OGG, MOBI and others) cannot be
  linked yet. They are counted on the review screen and left alone.

### Books that need a decision

Some layouts can be read two ways. These are shown under **Check these** and are
left out unless you tick them:

- a book with the same title and author as one already in your library (it is
  added as a separate book, never merged);
- audio files in a folder that also holds an `.m4b`;
- several audio files directly in the linked folder;
- disc folders such as `CD 1` and `CD 2` (each is proposed as its own book);
- a folder with its own audio files and subfolders that also hold audio.

A book cannot be imported when its file cannot be read, when a folder holds more
than 2,000 tracks, or when new files appear in a folder that is already a linked
book. For the last case, remove the book from Nostos and import the folder again.

## Scan, review, import

1. **Scan folder.** Nostos lists the folder and reads tags. Nothing is added.
2. **Review and import.** Untick anything you do not want. Tick the books under
   **Check these** that you do want.
3. **Import.** The import runs on the server. You can close the window or the
   page; reopening Settings shows the progress.

Importing reads each audio track once to measure it. No media file is copied or
converted. A large library takes time to read, and nothing else.

Importing twice is safe. A file belongs to one book only, so a book that is
already linked is reported as such and nothing is duplicated.

### Scan again

Run **Scan again** after you add files. New books appear as ready. Nostos does
not watch the folder and does not scan on a schedule.

## Removing a linked book

**Remove from Nostos** deletes the book, its notes and its reading progress from
Nostos, and the cover Nostos stored. The original files are not touched. Scan
again and the book is offered for import once more.

Uploading a file to a linked book stores that file in Nostos and stops using the
linked one. The original is not replaced.

## When files go missing

If a file is moved, renamed or deleted, or the folder is not mounted:

- the book stays in your library with its notes and progress;
- its page shows **External file unavailable**;
- the next scan lists it under **Files no longer in the folder**.

Put the file back at the same place and the book works again. Nostos never
removes a book because its file disappeared. A renamed file is seen as a new
book, so the old entry has to be removed by hand.

## Backups and moving your library

| | What it contains for a linked book |
| --- | --- |
| **Backup** (Settings → Backups & data) | The book, its notes, its progress, and the reference to the file. Not the media. |
| **Portable export** (`.nostos`) | Everything, including the media itself. |

- **Restoring a backup** brings linked books back as linked books. They play
  once the same folder is mounted again. Back up the media folder yourself.
- **A portable export** reads every linked file into the archive, so it can be
  as large as the linked library. The Nostos it is imported into stores those
  files itself and needs no folder. Server paths are never written into the
  archive.
- If a linked file cannot be read during an export, the export stops with an
  error instead of producing an archive that lacks it. Mount the folder, or
  remove the book, and export again.

## Streaming and offline listening

Linked books are streamed from the server like any other book. Nostos does not
download them to a phone for offline listening.
