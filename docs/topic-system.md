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

Text that is not a valid `[[Name]]` is plain text, in the processor and in the
note renderer alike: a `[[` with no `]]` after it on the same line, `[[]]`, a
stray `]]`, and `[[a|b]]`. There is no alias syntax, so `|` never splits a
reference. A topic that an older version made from such text stays until no
note uses it; the orphan cleanup then removes it.

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
| `PUT /api/topics/kind` | Set or clear a kind on one or many topics (max 500, source `reader`) |
| `DELETE /api/topics/{id}` | Delete, turning references into plain words |
| `GET /api/topic-kinds` | List kinds in sort order with topic counts |
| `POST /api/topic-kinds` | Create a kind (`409` duplicate name/colour, `422` at 12-kind cap) |
| `PUT /api/topic-kinds/{id}` | Update name, colour (`swapColor` swaps with another kind), or sort order |
| `DELETE /api/topic-kinds/{id}` | Delete a kind; its topics become untyped |
| `POST /api/books/{bookId}/notes`, `PUT /api/notes/{id}` | Create or edit a note; optional `topics` are its pill topics (see "Writing a note") |
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

## Topic kinds

A topic can carry at most one kind (or none), stored on `TopicModel.KindId`
with its provenance in `TopicModel.KindSource` (`reader`, `assistant`, or
`connected-app`; null when untyped).

- **Defaults and limit:** Each library starts with six built-in seeded kinds —
  `Person`, `Idea`, `Place`, `Work`, `Event`, `Field` (`kind-1` … `kind-6`) —
  which the reader can rename, recolour, reorder, or delete like custom kinds.
  At most **12** kinds may exist, each with a unique folded `NormalizedName` and
  palette key (`kind-1` … `kind-12`).
- **Delete:** Deleting a kind sets both `KindId` and `KindSource` to `null` on
  its topics without deleting topics.
- **Merge:** Merging (or renaming onto an existing topic) keeps the survivor's
  kind and source; if the survivor has none, it takes the merged topic's `KindId`
  and `KindSource`.
- **Who may replace a kind:** A kind whose source is `reader` is never
  overwritten by Ask Nostos or a connected app unless the caller explicitly asks
  (`overwriteReaderKinds: true`); it is reported as skipped instead. The rule
  lives in `ITopicRepository.SetTopicKindsAsync`, which applies a mixed batch in
  one transaction under the library write lock. Over MCP, `set_topic_kinds`
  writes source `connected-app`; the reader can review or clear those kinds at
  **Brain → Kinds → "Set by a connected app"**.
- **Portability and backup:** Backups and portable archives carry `topicKinds`
  and each topic's `kindId`/`kindSource`. Archives without `topicKinds` import
  with default kinds and untyped topics; imports match existing kinds by
  normalized name and honour the 12-kind limit (extra kinds are skipped and
  reported, with their topics imported untyped).

## Data model

```
TopicKindModel (1) ←── (M) TopicModel (1) ←→ (M) NoteTopicModel (M) ←→ (1) NoteModel ──→ BookModel
```

| Table | Fields |
| --- | --- |
| `TopicKinds` | `Id`, `Name`, `NormalizedName` (unique), `ColorKey` (`kind-1`…`kind-12`, unique), `SortOrder`, `CreatedAt` |
| `Topics` | `Id`, `Topic`, `NormalizedName` (unique), `Description` (nullable, ≤ 4,000), `KindId` (nullable FK → `TopicKinds`, `OnDelete: SetNull`), `KindSource` (nullable, `reader` \| `assistant` \| `connected-app`) |
| `NoteTopics` | `NoteId`, `TopicId` (composite key) |
| `Notes` | `Id`, `BookId`, `Content`, `SelectedText`, `Kind`, … |

## Orphan cleanup

`TopicCleanupWorker` runs every hour and deletes topics that no note carries,
**unless the reader described them**: a topic's own words are theirs to
delete.

## Writing a note

There are two ways to give a note a topic, and every place a note is written
offers both:

- **Inline**, for a topic that is part of the sentence:
  `[[Attention]] is a spiritual journey.` Typing `[[` opens the topic
  autocomplete.
- **A pill**, for a topic that is a label: write the words, then **+ Topic**
  under them.

The rules that make the two one system (issue #1445):

1. Every topic on a note shows as a pill under its words, typed or added.
2. Every place a note is written is the note composer
   (`ui/note-composer`): the words with `[[` autocomplete, and the topic row
   (`ui/topic-row`) under them. That is the Brain's card editor, the reader's
   selection note, highlight card, quick note and passage sheet, the book
   page's new note and the photo quote. A guard spec
   (`note-fields.guard.spec.ts`) fails on a new `<textarea>` until it is the
   composer or listed with what it holds.
3. The `[[Name]]` lines a pill becomes in the stored text are never shown:
   not on a card (`thoughtForDisplay`) and not in the editor (the composer
   opens a note's words and shows those lines as pills).
4. Removing the pill of an inline topic turns its word plain; removing an
   added pill drops its line.
5. The placeholder is "Write your thought, or link a [[Topic]]", which teaches
   the inline way; a field that asks its own question (the passage sheet's
   "Why keep this passage?") ends with "Link a [[Topic]]".

The composer sends the words as `content` and the pills as `topics`; the
server writes each pill as its own `[[Name]]` paragraph
(`TopicReferences.AppendAll`). An edit that sends `topics` sends all of them:
a pill topic left out is removed, and so is an older link the text never
declared. The client's copy of the text rules (`ui/note-composer/topic-text.ts`)
and the server's run the same cases,
`Nostos.Shared/Fixtures/topic-references.json`.

A note of only a topic is a note: a highlight tagged **Faith** with no words
saves.

## Rendering and autocomplete

`NoteFormatPipe` turns a reference into a link to `/brain/topics/:id` (or an
"unresolved" tag when no such topic exists); a plain click opens it in the app
without a page load. `[[` in a note's words opens the topic autocomplete
(`TopicAutocompleteService`); **+ Topic** under them opens the topic picker,
which is a sheet over the keyboard on a phone.

## Writing Studio

A note kept with a writing (Keep with writing…, from any Brain card, the
selection bar, or a whole topic's ⋯ menu) is referenced as a source of that
writing, never copied into its text.
