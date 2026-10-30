# Backend — Shared DTOs & Enums

## Overview

The `Nostos.Shared` project contains record types and enums shared across the backend. These define the API contract and are used for both request bodies and response shapes.

All DTOs are C# `record` types (immutable by default, with `with` expression support).

---

## Book DTOs (`Nostos.Shared.Dtos.BookDto`)

### PaginatedResponse\<T\>

Generic paginated wrapper used by list endpoints.

```csharp
record PaginatedResponse<T>(IEnumerable<T> Items, int TotalCount, int Page, int PageSize);
```

### BookDto (Response)

Full book representation returned from API.

| Field                                                         | Type                           | Notes                                  |
| ------------------------------------------------------------- | ------------------------------ | -------------------------------------- |
| `Type`                                                        | `string`                       | `"physical"`, `"ebook"`, `"audiobook"` |
| `Id`                                                          | `Guid`                         |                                        |
| `Title`                                                       | `string`                       |                                        |
| `Subtitle`, `Author`, `Editor`, `Translator`, `Narrator`      | `string?`                      |                                        |
| `Description`                                                 | `string?`                      | Synopsis                               |
| `Isbn`, `Asin`                                                | `string?`                      | Identifiers                            |
| `Duration`                                                    | `string?`                      | `"hh:mm:ss"` for audiobooks            |
| `PageCount`                                                   | `int?`                         | Physical/ebook only                    |
| `Publisher`, `PlaceOfPublication`, `PublishedDate`, `Edition` | `string?`                      | Publication info                       |
| `Language`, `Categories`                                      | `string?`                      |                                        |
| `Series`, `VolumeNumber`                                      | `string?`                      | Series info                            |
| `CreatedAt`                                                   | `DateTime`                     |                                        |
| `HasFile`                                                     | `bool`                         | Whether a file is uploaded             |
| `FileName`                                                    | `string?`                      | e.g. `"book.epub"`                     |
| `CoverUrl`                                                    | `string?`                      | e.g. `"/api/books/{id}/cover"`         |
| `CollectionIds`                                               | `IEnumerable<Guid>?`           | Every collection the book is in (appended field) |
| `LastLocation`                                                | `string?`                      | Reading position                       |
| `ProgressPercent`                                             | `int`                          | 0–100                                  |
| `Rating`                                                      | `int`                          | 0–5, the BOOK's (shared by every edition) |
| `IsFavorite`                                                  | `bool`                         | the book's                             |
| `PersonalReview`                                              | `string?`                      | the book's                             |
| `EditionRating`, `EditionReview`                              | `int`, `string?`               | this edition's own exception (0 / null = none; appended) |
| `LastReadAt`, `FinishedAt`                                    | `DateTime?`                    |                                        |
| `Chapters`                                                    | `IEnumerable<BookChapterDto>?` | Audio chapters                         |

### CreateBookDto (Request)

All fields for creating a new book. `Type` determines the subclass (`physical`, `ebook`, `audiobook`).

### UpdateBookDto (Request)

All fields optional (`?`). Only non-null fields are applied. Includes `IsFinished` boolean for toggling finished state.

### UpdateProgressDto (Request)

```csharp
record UpdateProgressDto(string Location, int Percentage);
```

### BookChapterDto

```csharp
record BookChapterDto(string Title, double StartTime);
```

### BookLocationsDto

```csharp
record BookLocationsDto(string Locations);
```

---

## Collection DTOs (`Nostos.Shared.Dtos.CollectionDto`)

```csharp
record CollectionDto(Guid Id, string Name, Guid? ParentId);
record CreateCollectionDto(string Name, Guid? ParentId);
record UpdateCollectionDto(string Name, Guid? ParentId);
```

---

## Concept DTOs (`Nostos.Shared.Dtos.ConceptDto`)

```csharp
record ConceptDto(Guid Id, string Name, int UsageCount);
record ConceptDetailDto(Guid Id, string Name, List<NoteContextDto> Notes);
record NoteContextDto(Guid NoteId, string Content, string? SelectedText,
                      string? CfiRange, Guid BookId, string BookTitle);
record CreateConceptDto(string Concept);
record UpdateConceptDto(string Concept);
```

---

## Note DTOs (`Nostos.Shared.Dtos.NoteDto`)

```csharp
record NoteDto(Guid Id, Guid BookId, string Content, string? CfiRange,
               string? SelectedText, DateTime CreatedAt, string? BookTitle);
record CreateNoteDto(string Content, string? CfiRange = null, string? SelectedText = null);
record UpdateNoteDto(string Content, string? SelectedText = null);
```

---

## Writing DTOs (`Nostos.Shared.Dtos.WritingDto`)

```csharp
record WritingDto(Guid Id, string Name, string Type, Guid? ParentId, DateTime UpdatedAt);
record WritingContentDto(Guid Id, string Name, string Content, DateTime UpdatedAt);
record CreateWritingDto(string Name, string Type, Guid? ParentId);
record UpdateWritingDto(string Name, string? Content);
record MoveWritingDto(Guid? NewParentId);
```

---

## Enums

### BookFilter (`Nostos.Shared.Enums`)

```csharp
enum BookFilter { All, Favorites, Finished, Reading, Unsorted }
```

### BookSort (`Nostos.Shared.Enums`)

```csharp
enum BookSort { Recent, Title, Rating, LastRead }
```
