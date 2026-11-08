# Backend — Repositories

## Overview

All data access is abstracted behind repository interfaces, injected as **scoped** services. Repositories encapsulate EF Core queries and mutations.

## Registration

All repositories are registered by `AddNostosProduct` in `Nostos.Product/Composition/NostosProductComposition.cs`:

```csharp
services.AddScoped<IBookRepository, BookRepository>();
services.AddScoped<INoteRepository, NoteRepository>();
services.AddScoped<ITopicRepository, TopicRepository>();
services.AddScoped<IWritingRepository, WritingRepository>();
```

Collections have no repository of their own: `LibraryService` reads and writes `CollectionModel` and its book links through `NostosDbContext`.

---

## IBookRepository / BookRepository

Manages `BookModel` entities (all subtypes via TPH).

| Method                                                | Description                                                                         |
| ----------------------------------------------------- | ----------------------------------------------------------------------------------- |
| `GetBooksAsync(search, filter, sort, page, pageSize)` | Paginated query with full filtering/sorting. Returns `PaginatedResponse<BookModel>` |
| `GetByIdAsync(id)`                                    | Find by primary key                                                                 |
| `CountBooksWithFilesAsync()`                          | Count of books with uploaded files (OPDS total)                                     |
| `GetBooksWithFilesPageAsync(skip, take)`              | One page of books with files, newest first (OPDS feed)                              |
| `AddAsync(book)`                                      | Insert + save                                                                       |
| `UpdateAsync(book)`                                   | Update + save                                                                       |
| `DeleteAsync(book)`                                   | Remove + save                                                                       |

### Query Filters (`BookFilter` enum)

| Filter      | Condition                                   |
| ----------- | ------------------------------------------- |
| `Favorites` | `Work.IsFavorite == true` (the book's)      |
| `Finished`  | grouped: any edition of the work has `FinishedAt`; flat: this edition's |
| `Reading`   | grouped: an unfinished edition has `ProgressPercent > 0` and was read after the work's latest finish (or none is finished); flat: `FinishedAt == null && ProgressPercent > 0` |
| `NotStarted`| grouped: no edition has progress or `FinishedAt`; flat: `ProgressPercent == 0` |
| `Unsorted`  | no `BookCollections` row for the book       |

### Sort Options (`BookSort` enum)

| Sort       | Order                          |
| ---------- | ------------------------------ |
| `Recent`   | `CreatedAt DESC` (default)     |
| `Title`    | `Title ASC`                    |
| `Rating`   | `Work.Rating DESC` (the book's) |
| `LastRead` | `LastReadAt DESC` (nulls last) |

---

## INoteRepository / NoteRepository

Manages `NoteModel` entities with eager loading support.

| Method                          | Description                                                             |
| ------------------------------- | ----------------------------------------------------------------------- |
| `GetByBookIdAsync(bookId)`      | All notes for a book (includes `Book` nav property)                     |
| `GetByIdAsync(id)`              | Simple find by PK                                                       |
| `GetByIdWithTopicsAsync(id)`    | Includes `NoteTopics` for an edit (its `Book` is not loaded)            |
| `GetByIdWithBookAsync(id)`      | No-tracking query with `Book` (for read-only DTO mapping)               |
| `AddAsync(note)`                | Add to context (no immediate save — allows batch with topic processing) |
| `SaveChangesAsync()`            | Explicit save (called after `NoteProcessorService` finishes)            |
| `DeleteAsync(note)`             | Remove + save                                                           |
| `DeleteTopicLinksAsync(noteId)` | Bulk delete from `NoteTopics` join table                                |

**Note:** `AddAsync` does NOT call `SaveChangesAsync()` — this is intentional. The note and its topic links are saved together in one transaction after `NoteProcessorService.ProcessNoteAsync()` completes.

---

## ITopicRepository / TopicRepository

Manages `TopicModel` entities and the `NoteTopics` join table.

| Method                                 | Description                                                                                        |
| -------------------------------------- | -------------------------------------------------------------------------------------------------- |
| `GetAllWithUsageCountAsync()`          | Returns `List<TopicDto>` directly (projection query) — sorted by usage count DESC, then name       |
| `SearchByNoteTextAsync(term)`          | Topics whose linked notes match the term in content, selection or book title                       |
| `GetStatsAsync()`                      | `TopicStatsDto`: aggregate topic/reference counts, single-note topics, most used topic             |
| `GetRelatedAsync(id)`                  | `List<RelatedTopicDto>` — topics that share notes with this one                                    |
| `GetGraphAsync()`                      | `TopicGraphDto` — all topics as nodes, one edge per co-occurring pair                              |
| `GetWritingUsageAsync(id, limit, ct)`  | `TopicWritingUsageDto?` — writings citing the topic's notes, newest first                          |
| `GetByIdWithNotesAsync(id)`            | Deep include: `NoteTopics → Note → Book`                                                           |
| `RenameAsync(id, name)`                | Renames (and rewrites the notes' `[[ ]]` references); merges into an existing topic of that name   |
| `MergeAsync(sourceId, targetId)`       | Merges the source into the target and returns the survivor                                         |
| `SetDescriptionAsync(id, description)` | Sets or clears the topic's description                                                             |
| `DeleteAsync(id)`                      | Deletes the topic and its note links, turning its `[[ ]]` references into plain text               |
| `GetByNamesAsync(names)`               | Find topics by normalized name (for matching `[[brackets]]`)                                       |
| `AddRange(topics)`                     | Batch add new topics to context                                                                    |
| `ClearNoteLinksAsync(note)`            | Remove all `NoteTopics` rows for a note; returns link-only names the stored text never declared    |
| `AddNoteLink(link)`                    | Add single `NoteTopicModel` to context                                                             |
| `DeleteOrphanedAsync(ct)`              | `ExecuteDeleteAsync` — topics with zero note links and no description. Returns count deleted       |
| `MergeCaseVariantsAsync(ct)`           | Merges topics whose key is not their name's folded form, then sets every key. Returns count merged |

---

## IWritingRepository / WritingRepository

Manages hierarchical `WritingModel` entities (folders & documents).

| Method                 | Description                                         |
| ---------------------- | --------------------------------------------------- |
| `GetAllAsync()`        | Flat list of all writings                           |
| `GetByIdAsync(id)`     | Find by primary key                                 |
| `AddAsync(writing)`    | Insert + save                                       |
| `UpdateAsync(writing)` | Update + save                                       |
| `DeleteAsync(writing)` | Remove + save (cascade deletes children)            |
| `GetParentIdAsync(id)` | Returns parent ID (for cycle detection during move) |
