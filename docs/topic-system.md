# Nostos — Topic System

## Overview

A topic is a name notes are gathered under. A note declares its topics in its
own words with wiki-style `[[double brackets]]`:

```
This passage explores [[Stoicism]] and its relationship to [[Virtue Ethics]].
```

**The note's text is the record.** Every save re-derives the note's topic
links from its text, so anything that changes a note's topics (adding one from
a chip, removing one, renaming or merging a topic, deleting a topic) changes
the text the same way. Otherwise a link removed in one place comes back on the
next edit.

The Brain (`/brain`, see `docs/brain.md`) is where topics are read, linked and
managed.

## The text rules (`TopicReferences`)

`Nostos.Product/Services/TopicReferences.cs` holds the rules, in one place:

| Change | What happens to the text |
| --- | --- |
| Link a topic | `[[Name]]` is appended as its own paragraph (nothing if already declared) |
| Unlink a topic | A reference standing on a line of references is removed (with the line, when nothing is left); a reference inside a sentence becomes its plain words, so no sentence loses a word |
| Rename a topic | References are rewritten in place; if the note already declares the new name, a reference standing alone is dropped rather than repeated |
| Merge a topic | As rename, into the survivor's name |
| Delete a topic | As unlink, in every note that carried it |

Names compare trimmed and case-insensitively. A valid name is 1–200
characters on one line, without `[[`, `]]` or `|`, and does not start with `[`
or end with `]` (`TopicReferences.IsValidName`), so it reads back as the same
name inside brackets.

## Saving a note (`NoteProcessorService`)

On every save the processor parses the text for references, creates topics
that do not exist yet, and replaces the note's links with exactly the declared
ones. A link that the text never declared (an older link-only topic) is kept,
by writing its reference into the text, unless the caller removed it.

## API

| Route | Does |
| --- | --- |
| `GET /api/topics` | Every topic with its usage count and description |
| `GET /api/topics/graph` | Nodes and shared-note edges for the map |
| `GET /api/topics/{id}/related` | Topics that share notes with this one |
| `PUT /api/topics/{id}` | Rename (merges into an existing topic of that name) |
| `POST /api/topics/{id}/merge` | Merge into `targetId` |
| `PUT /api/topics/{id}/description` | Set the topic's own words; empty clears them |
| `DELETE /api/topics/{id}` | Delete, turning references into plain words |
| `POST /api/topics/link` | Add the named topics to many notes (creates new topics); one unit of work |
| `POST /api/topics/unlink` | Remove the named topics from many notes; how a link is undone |
| `GET /api/notes?topicId=&withTopicId=&withoutTopics=&kind=&bookId=&query=&order=` | Browse notes in any place of the Brain |
| `GET /api/notes/summary` | Counts for the sidebar: all, passages, unsorted |
| `GET /api/notes/books` | The books with notes in one place, with counts |

Link and unlink answer which notes changed and every listed note as it now
is, so Undo touches exactly the notes the first call changed.

## A topic in your own words

A topic can carry a description (at most 4,000 characters), written on its
page. It is kept trimmed (empty is none). A merge keeps the survivor's words
and appends the merged topic's after a blank line. Backups and the portable
archive carry it; an archive written before descriptions imports its topics
without one.

## Data model

```
TopicModel (1) ←→ (M) NoteTopicModel (M) ←→ (1) NoteModel ──→ BookModel
```

| Table | Fields |
| --- | --- |
| `Topics` | `Id`, `Topic`, `Description` (nullable, ≤ 4,000) |
| `NoteTopics` | `NoteId`, `TopicId` (composite key) |
| `Notes` | `Id`, `BookId`, `Content`, `SelectedText`, `Kind`, … |

## Orphan cleanup

`TopicCleanupWorker` runs every hour and deletes topics that no note carries,
**unless the reader described them**: a topic's own words are theirs to
delete.

## Rendering and autocomplete

`NoteFormatPipe` turns a reference into a link to `/brain/topics/:id` (or an
"unresolved" tag when no such topic exists); a plain click opens it in the app
without a page load. `[[` in a note's text field opens the topic autocomplete
(`TopicAutocompleteService`); **+ Topic** on a card opens the topic picker.

## Writing Studio

A note kept with a writing (Keep with writing…, from any Brain card, the
selection bar, or a whole topic's ⋯ menu) is referenced as a source of that
writing, never copied into its text.
