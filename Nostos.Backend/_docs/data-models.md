# Backend — Data Models

## Overview

The backend uses Entity Framework Core 10 with SQLite. All models live in `Nostos.Backend.Data.Models`. The `NostosDbContext` configures relationships, discriminators, and indexes.

## Entity Relationship Diagram

```
┌──────────────┐     ┌──────────────────┐     ┌──────────────┐
│  Collection   │────<│     BookModel      │────<│   NoteModel   │
│              │  M:M │  (TPH inheritance) │  1:M │              │
└──────────────┘     └──────────────────┘     └──────┬───────┘
      │ self-ref                                       │ M:M
      │ ParentId                                       │
      ↓                                          ┌─────┴──────┐
┌──────────────┐                                │NoteConceptModel│
│  Collection   │                                └─────┬──────┘
│  (children)   │                                       │ M:M
└──────────────┘                                 ┌─────┴──────┐
                                                  │ConceptModel │
┌──────────────┐                                 └────────────┘
│ WritingModel  │ self-referencing
│  (tree)       │ ParentId → WritingModel
└──────────────┘
```

## BookModel (Abstract Base — TPH Inheritance)

Table-per-hierarchy (TPH) with discriminator column `BookType`.

| Property       | Type              | Description                            |
| -------------- | ----------------- | -------------------------------------- |
| `Id`           | `Guid`            | Primary key (auto-generated)           |
| `Title`        | `string`          | **Required**                           |
| `Author`       | `string?`         | Author name(s)                         |
| `Metadata`     | `BookMetadata`    | Owned type — detailed metadata         |
| `Progress`     | `ReadingProgress` | Owned type — reading state             |
| `FileDetails`  | `FileInfoDetails` | Owned type — file references           |
| `CreatedAt`    | `DateTime`        | UTC creation timestamp                 |
| `BookCollections` | `ICollection<BookCollectionModel>` | Membership in any number of collections |

### Discriminator Values

| Subclass            | `BookType`    | Extra Fields                   |
| ------------------- | ------------- | ------------------------------ |
| `PhysicalBookModel` | `"physical"`  | `Isbn`, `PageCount`            |
| `EBookModel`        | `"ebook"`     | `Isbn`, `PageCount`            |
| `AudioBookModel`    | `"audiobook"` | `Asin`, `Duration`, `Narrator` |

### BookMetadata (Owned)

| Property             | Type      | Description                  |
| -------------------- | --------- | ---------------------------- |
| `Subtitle`           | `string?` | Book subtitle                |
| `Description`        | `string?` | Synopsis/description         |
| `Editor`             | `string?` | Editor name                  |
| `Translator`         | `string?` | Translator name              |
| `Publisher`          | `string?` | Publisher                    |
| `PlaceOfPublication` | `string?` | City of publication          |
| `PublishedDate`      | `string?` | Publication date (free-form) |
| `Language`           | `string?` | Language code                |
| `Categories`         | `string?` | Comma-separated categories   |
| `Edition`            | `string?` | Edition info                 |
| `Series`             | `string?` | Series name                  |
| `VolumeNumber`       | `string?` | Volume within series         |

### ReadingProgress (Owned)

| Property          | Type        | Description                    |
| ----------------- | ----------- | ------------------------------ |
| `LastLocation`    | `string?`   | Epub CFI or page reference     |
| `ProgressPercent` | `int`       | 0–100                          |
| `Rating`          | `int`       | This edition's own rating, an exception to the book's (`WorkModel.Rating`); 0 = none |
| `PersonalReview`  | `string?`   | This edition's own review, an exception to the book's; null = none |
| `LastReadAt`      | `DateTime?` | Last reading session timestamp |
| `FinishedAt`      | `DateTime?` | Completion timestamp           |

### FileInfoDetails (Owned)

| Property        | Type      | Description                         |
| --------------- | --------- | ----------------------------------- |
| `HasFile`       | `bool`    | Whether a file is uploaded          |
| `FileName`      | `string?` | Stored filename (e.g., `book.epub`) |
| `CoverFileName` | `string?` | Cover filename (e.g., `cover.jpg`)  |
| `ChaptersJson`  | `string?` | JSON array of chapters (audio)      |
| `LocationsJson` | `string?` | Cached epub locations for progress  |

## CollectionModel

Hierarchical folders for organizing books. Self-referencing parent-child.

| Property   | Type                           | Description                     |
| ---------- | ------------------------------ | ------------------------------- |
| `Id`       | `Guid`                         | Primary key                     |
| `Name`     | `string`                       | Collection name                 |
| `ParentId` | `Guid?`                        | Parent collection (null = root) |
| `Parent`   | `CollectionModel?`             | Navigation property             |
| `Children` | `ICollection<CollectionModel>` | Child collections               |

**Index:** `ParentId`

## NoteModel

User annotations with wiki-link concept support.

| Property       | Type                            | Description                                           |
| -------------- | ------------------------------- | ----------------------------------------------------- |
| `Id`           | `Guid`                          | Primary key                                           |
| `Content`      | `string`                        | **Required** — Note text (may contain `[[concepts]]`) |
| `CfiRange`     | `string?`                       | Epub CFI range (for highlight positioning)            |
| `SelectedText` | `string?`                       | Highlighted text from the book                        |
| `HighlightColour` | `string?`                     | Highlighter pen by name (`amber`, `sage`, `blue`, `rose`); `null` = none recorded, drawn amber |
| `CreatedAt`    | `DateTime`                      | UTC creation timestamp                                |
| `BookId`       | `Guid`                          | FK to Book                                            |
| `Book`         | `BookModel?`                    | Navigation property                                   |
| `NoteConcepts` | `ICollection<NoteConceptModel>` | Linked concepts                                       |

**Index:** `BookId`

## ConceptModel

Auto-created from `[[brackets]]` in notes.

| Property       | Type                            | Description           |
| -------------- | ------------------------------- | --------------------- |
| `Id`           | `Guid`                          | Primary key           |
| `Concept`      | `string`                        | Concept name (unique) |
| `NoteConcepts` | `ICollection<NoteConceptModel>` | Linked notes          |

**Index:** `Concept` (unique)

## NoteConceptModel (Join Table)

Many-to-many between Notes and Concepts.

| Property    | Type   | Description                  |
| ----------- | ------ | ---------------------------- |
| `NoteId`    | `Guid` | FK to Note (composite PK)    |
| `ConceptId` | `Guid` | FK to Concept (composite PK) |

## BookCollectionModel (Join Table)

Many-to-many between Books and Collections — **the only record of membership**.
`Books.CollectionId` was dropped, so a book being in several collections is
representable and no mirror column needs keeping in sync.

| Property       | Type       | Description                                     |
| -------------- | ---------- | ----------------------------------------------- |
| `BookId`       | `Guid`     | FK to Book (composite PK), **Cascade** on delete |
| `CollectionId` | `Guid`     | FK to Collection (composite PK), **Restrict**    |
| `AddedAt`      | `DateTime` | When the membership was created                  |

Delete behaviour is deliberately asymmetric: `Cascade` from Book so deleting a
book cannot strand membership rows, `Restrict` from Collection so a non-empty
collection cannot be removed by raw SQL behind the service's back (the service
deletes membership rows itself, then the collection). Anything that serialises a
book must `Include(b => b.BookCollections)`.

## WritingModel

Hierarchical file system for the writing studio.

| Property    | Type                        | Description                       |
| ----------- | --------------------------- | --------------------------------- |
| `Id`        | `Guid`                      | Primary key                       |
| `Name`      | `string`                    | **Required** — File/folder name   |
| `Type`      | `WritingType`               | `Folder` or `Document` (enum)     |
| `Content`   | `string?`                   | Markdown content (documents only) |
| `ParentId`  | `Guid?`                     | Parent folder (null = root)       |
| `Parent`    | `WritingModel?`             | Navigation property               |
| `Children`  | `ICollection<WritingModel>` | Children (cascade delete)         |
| `CreatedAt` | `DateTime`                  | UTC creation timestamp            |
| `UpdatedAt` | `DateTime`                  | UTC last modification timestamp   |

**Index:** `ParentId`  
**Delete behavior:** Cascade (deleting a folder deletes all descendants)

## Database Indexes

| Table         | Column(s)      | Type       |
| ------------- | -------------- | ---------- |
| `Books`       | `Title`        | Non-unique |
| `Books`       | `Author`       | Non-unique |
| `BookCollections` | `CollectionId` | Non-unique |
| `Notes`       | `BookId`       | Non-unique |
| `Collections` | `ParentId`     | Non-unique |
| `Writings`    | `ParentId`     | Non-unique |
| `Concepts`    | `Concept`      | **Unique** |
