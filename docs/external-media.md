# External media: use books that are already on your server

SelfHosted only. Nostos can read audiobooks and ebooks from a folder you already
keep on the server, without copying them. You scan the folder, review what was
found, import it, and scan again later when you add more.

**Your original files stay where they are and are never modified or deleted by
Nostos.**

Nostos Cloud does not have this feature.

## Set it up

The web interface cannot choose a folder. Whoever runs the server makes one
folder visible to Nostos and names it in the configuration. It takes three
steps, and step 1 depends on how you run Nostos.

### Step 1: give Nostos the folder

#### Docker Compose

Add the two marked lines to the `nostos` service in your `compose.yml`. The
[Compose file in this repository](../compose.yml) has them ready to uncomment.

```yaml
services:
  nostos:
    image: ghcr.io/christian-gennari/nostos:latest
    ports:
      - "5099:8080"
    volumes:
      - nostos-data:/data
      - /mnt/audiobooks:/external/media:ro      # add: your folder, read-only
    environment:
      ExternalMedia__Root: /external/media      # add: where Nostos finds it
    restart: unless-stopped

volumes:
  nostos-data:
```

- Replace `/mnt/audiobooks` with the folder on your machine. Leave
  `/external/media` as it is: that is the folder's name inside the container,
  and the two lines must agree on it.
- `:ro` mounts the folder read-only. Nostos never writes to it, and with `:ro`
  the operating system would refuse even if it tried.
- Keep the `nostos-data:/data` line. Nostos stores its database, notes, covers
  and backups there and needs to write to it.
- `ExternalMedia__Root` has two underscores.

Apply it:

```bash
docker compose up -d
```

#### docker run

Add one `-v` and one `-e` to the command you already use:

```bash
docker run -d \
  --name nostos \
  -p 5099:8080 \
  -v nostos-data:/data \
  -v /mnt/audiobooks:/external/media:ro \
  -e ExternalMedia__Root=/external/media \
  --restart unless-stopped \
  ghcr.io/christian-gennari/nostos:latest
```

A running container cannot be given a new folder. Remove it
(`docker rm -f nostos`) and run the command again; your library is in the
`nostos-data` volume and is kept.

#### Without Docker

Set the environment variable `ExternalMedia__Root` to the absolute path of the
folder, or add this to `appsettings.json`, and restart Nostos:

```json
{
  "ExternalMedia": { "Root": "/mnt/audiobooks" }
}
```

### Step 2: let Nostos read the files (Linux)

The container runs as a user that is not root (user id 1654). That user must be
able to read the files and open the folders:

```bash
chmod -R o+rX /mnt/audiobooks
```

This changes permissions only. It does not change the files.

### Step 3: check it worked

Open Nostos and go to **Settings → This server → External media**. It should say
**Available**. Press **Scan folder**.

| What you see | What it means | What to do |
| --- | --- | --- |
| No **External media** entry in Settings | `ExternalMedia__Root` is not set in the running server | Check the spelling (two underscores) and that the container was recreated, not only restarted |
| **Not available** | The setting is there, but nothing is mounted at that path | Make the path after the colon in the volume line and the `ExternalMedia__Root` value the same |
| The server does not start | `ExternalMedia__Root` is not an absolute path | Use a path that starts with `/`. The log says `ExternalMedia:Root must be an absolute path` |
| The scan finds 0 books and says folders could not be read | The server's user is not allowed to read the folder | Run the `chmod` command in step 2 |
| The scan finds 0 books and no folders were skipped | There is nothing Nostos can link in the folder | Check the mounted folder is the right one and see [How the folder is read](#how-the-folder-is-read) |

To see the folder the way Nostos sees it:

```bash
docker exec nostos ls /external/media
```

### One folder

This release links one folder. To link several, mount them side by side under
one parent and point `ExternalMedia__Root` at the parent:

```yaml
    volumes:
      - /mnt/audiobooks:/external/media/audiobooks:ro
      - /mnt/ebooks:/external/media/ebooks:ro
    environment:
      ExternalMedia__Root: /external/media
```

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
