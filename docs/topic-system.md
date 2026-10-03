# Nostos — Topic System

## Overview

Nostos implements a **Zettelkasten-inspired topic linking system** that connects notes across books using wiki-style `[[double bracket]]` syntax. This creates an emergent knowledge graph from your reading.

## How It Works

### 1. Writing Notes with Topics

When creating or editing a note (either from the reader or the Second Brain), wrap any topic name in double brackets:

```
This passage explores [[Stoicism]] and its relationship to [[Virtue Ethics]].
The author draws on [[Marcus Aurelius]] throughout.
```

### 2. Automatic Processing

When a note is saved, the `NoteProcessorService` on the backend:

1. **Parses** the content using regex `\[\[(.*?)\]\]` to extract topic names
2. **Clears** existing topic links for that note (for re-processing on edit)
3. **Matches** found names against existing topics (case-insensitive)
4. **Creates** new `TopicModel` entries for any topics that don't exist yet
5. **Links** the note to all matched/created topics via the `NoteTopics` join table

### 3. Exploring Topics

The **Second Brain** page (`/second-brain`) provides a master-detail view:

- **Left pane:** All topics sorted by usage count (most-referenced first)
- **Right pane:** All notes containing the selected topic, with book context

Topic names in notes are rendered as clickable tags (via `NoteFormatPipe`) that navigate to that topic's detail view.

### 4. Writing Studio Integration

The Writing Studio's right sidebar provides access to both:

- **Topics tab:** Browse topics and their linked notes
- **Books tab:** Browse books and their notes

Clicking a note in either tab inserts its highlighted text as a blockquote into the editor, enabling research-driven writing.

## Data Model

```
TopicModel (1) ←→ (M) NoteTopicModel (M) ←→ (1) NoteModel
                                                        │
                                                        ↓
                                                    BookModel
```

| Table          | Fields                                                             |
| -------------- | ------------------------------------------------------------------ |
| `Topics`     | `Id`, `Topic` (unique, indexed)                                  |
| `NoteTopics` | `NoteId`, `TopicId` (composite PK)                               |
| `Notes`        | `Id`, `BookId`, `Content`, `CfiRange`, `SelectedText`, `CreatedAt` |

## Autocomplete

Both the Note Card inline editor and the Topic Input component support autocomplete:

1. User types `[[`
2. `TopicAutocompleteService` activates and filters existing topics
3. A suggestion panel appears below the cursor position
4. Arrow keys navigate, Enter selects
5. Selected topic name is inserted as `[[TopicName]] `

## Orphan Cleanup

The `TopicCleanupWorker` (background service) runs every **1 hour** and deletes topics that have zero linked notes. This prevents accumulation of unused topics from edited/deleted notes.

## Frontend Rendering

The `NoteFormatPipe` transforms note content for display:

```
Input:  "This relates to [[Stoicism]] and [[Ethics]]"
Output: "This relates to <span class="topic-tag" data-topic-id="...">Stoicism</span>
         and <span class="topic-tag" data-topic-id="...">Ethics</span>"
```

These tags are styled as clickable chips and emit navigation events when clicked.
