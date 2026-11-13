# Backend — Shared DTOs & Enums

## Overview

The `Nostos.Shared` project contains record types and enums shared across the backend. These define the API contract and are used for both request bodies and response shapes.

All DTOs are C# `record` types (immutable by default, with `with` expression support).

The canonical definition of every contract is the file under `Nostos.Shared/Dtos/`
or `Nostos.Shared/Enums/`. This page explains the shapes a reviewer or agent is
most likely to touch and names the rest; where the two disagree, the source wins.
Most contracts are **positional** records, so a new field is only ever appended
(inserting one renumbers the JSON of every existing client).

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
| `CollectionIds`                                               | `IEnumerable<Guid>?`           | Every collection the book is in (the only membership record) |
| `LastLocation`                                                | `string?`                      | Reading position                       |
| `ProgressPercent`                                             | `int`                          | 0–100                                  |
| `Rating`                                                      | `int`                          | 0–5, the BOOK's (shared by every edition) |
| `IsFavorite`                                                  | `bool`                         | the book's                             |
| `PersonalReview`                                              | `string?`                      | the book's                             |
| `EditionRating`, `EditionReview`                              | `int`, `string?`               | this edition's own exception (0 / null = none) |
| `LastReadAt`, `FinishedAt`                                    | `DateTime?`                    |                                        |
| `Chapters`                                                    | `IEnumerable<BookChapterDto>?` | Audio chapters                         |
| `WorkId`                                                      | `Guid?`                        | The work (book) every edition shares   |
| `EditionCount`                                                | `int`                          | Editions in the work, `1` when alone   |
| `OtherEditions`                                               | `IEnumerable<EditionSummaryDto>?` | Sibling editions, for the edition switcher |
| `Source`                                                      | `BookSourceDto?`               | Provider provenance for an acquired file |
| `Status`                                                      | `BookStatus`                   | Life-cycle state (`Ready`, `Downloading`, `Transcoding`, `Failed`, `UploadPending`) |
| `StatusMessage`                                               | `string?`                      | Human-readable reason for `Status`     |
| `Tracks`                                                      | `IEnumerable<BookTrackDto>?`   | Ordered tracks of a multi-track audiobook; the book then reports `HasFile = true` with no `FileName` |
| `CoverVersion`                                                | `string?`                      | Changes when the cover's bytes change; append as `?v=` to the cover URL |

`EditionSummaryDto` is the compact per-edition form used by `OtherEditions`
(`Id`, `Type`, `Format`, `ProgressPercent`, `FinishedAt`, `LastReadAt`,
`HasFile`, `FileName`, `Narrator`, `Duration`, `Edition`, plus the appended
`Title`, `Author`, `Publisher`, `PublishedDate`, `Rating`, `PersonalReview`
and `HasPlace`). `BookTrackDto` is `(Number, Title, Duration, Bytes,
ContentType)`, with duration in seconds. `BookSourceDto` is the source's own
provenance (`ProviderId`, `ProviderDisplayName`, `ExternalId`, `SourceUrl`,
`AssetFormat`, `RightsStatement`, `AcquiredAt`); the rights field carries the
source's wording, never a Nostos claim.

### CreateBookDto (Request)

All fields for creating a new book. `Type` determines the subclass (`physical`, `ebook`, `audiobook`). `CollectionIds` is the membership set (`CollectionId` is the inbound single-value compatibility form); `FileUploadExpected` says a local file is still coming, and `FileAttached` says one accompanies this create on any upload path — either flag stops the create from matching an edition that already has its own file.

### UpdateBookDto (Request)

All fields optional (`?`). Only non-null fields are applied. Includes `IsFinished` boolean for toggling finished state. `Rating`, `IsFavorite` and `PersonalReview` write the book's values; `EditionRating` (0 clears) and `EditionReview` (empty string clears) write this edition's own exceptions.

### UpdateProgressDto (Request)

```csharp
record UpdateProgressDto(string Location, int Percentage, double? PlaybackRate = null);
```

`PlaybackRate` is the audiobook speed saved with the position, so another device
opens at it; null leaves the stored rate unchanged.

### BookChapterDto

```csharp
record BookChapterDto(string Title, double StartTime);
```

### BookLocationsDto

```csharp
record BookLocationsDto(string Locations);
```

### Other book request/response types

```csharp
record LibraryStatusCountsDto(int All, int NotStarted, int Reading, int Favorites,
                              int Finished, int Unsorted, int Audiobooks = 0,
                              int Ebooks = 0, int Pdfs = 0);
record BookFileVersionDto(string Version, long Bytes);       // which bytes the file holds now
sealed record UpdateBookCollectionsDto(IReadOnlyList<Guid> CollectionIds); // full replacement set
sealed record LinkWorkDto(Guid TargetBookId);                // "link as one book"
sealed record EditionSuggestionDto(Guid Id, string Title, string? Author,
                                   Guid WorkId, int EditionCount, string Type, string? FileName);
sealed record BookFileMetadataDto(string? Title, string? Author, string? Language, int? PageCount);
```

---

## Collection DTOs (`Nostos.Shared.Dtos.CollectionDto`)

```csharp
record CollectionDto(Guid Id, string Name, Guid? ParentId);
record CreateCollectionDto(string Name, Guid? ParentId);
sealed record UpdateCollectionDto([property: JsonRequired] string Name,
                                  [property: JsonRequired] Guid? ParentId);
sealed record CollectionCountDto(Guid CollectionId, int BookCount);
```

`UpdateCollectionDto` requires both properties on the wire (a missing `parentId`
is a 400; an explicit `null` means "move to root"). `CollectionCountDto` is the
sidebar's descendant-inclusive book count and is deliberately separate from
`CollectionDto`.

---

## Topic DTOs (`Nostos.Shared.Dtos.TopicDto`)

```csharp
// List and detail
record TopicDto(Guid Id, string Name, int UsageCount, int NoteMatchCount = 0,
                string? NoteMatchSnippet = null, string? Description = null);
record TopicDetailDto(Guid Id, string Name, List<NoteContextDto> Notes);
record TopicStatsDto(int TotalTopics, int TotalReferences, int SingleNoteTopics,
                     string? MostUsedName, int MostUsedCount);
record RelatedTopicDto(Guid Id, string Name, int SharedNotes, List<Guid> SharedNoteIds);

// Whole-brain knowledge graph
record TopicGraphNodeDto(Guid Id, string Name, int UsageCount);
record TopicGraphEdgeDto(Guid SourceId, Guid TargetId, int SharedNotes);
record TopicGraphDto(List<TopicGraphNodeDto> Nodes, List<TopicGraphEdgeDto> Edges);

// Requests
record CreateTopicDto(string Topic);
record UpdateTopicDto(string Topic);
record MergeTopicDto(Guid TargetId);
record UpdateTopicDescriptionDto(string? Description);
record TopicLinkRequestDto(IReadOnlyList<string> Topics, IReadOnlyList<Guid> NoteIds);
record TopicLinkResultDto(IReadOnlyList<Guid> ChangedNoteIds, IReadOnlyList<NoteSearchHitDto> Notes);

// Where a topic is used in writing (worked out at read time)
record TopicWritingUsageDto(int Total, List<TopicWritingDto> Writings,
                            List<NoteCitationCountDto> NoteCitations);
record TopicWritingDto(Guid Id, string Name, DateTime UpdatedAt);
record NoteCitationCountDto(Guid NoteId, int Writings, List<Guid>? WritingIds = null);
```

A topic detail's notes:

```csharp
record NoteContextDto(Guid NoteId, string Content, string? SelectedText,
                      string? CfiRange, Guid BookId, string BookTitle, DateTime CreatedAt,
                      string SourceAnchorKind = "unknown", string? SourceAnchorValue = null,
                      bool AnchorVerified = false, string? HighlightColour = null,
                      string Kind = "note", string? PassageTitle = null, string? PassageEnd = null,
                      int? PassageWordCount = null, string? BookAuthor = null);
```

---

## Note DTOs (`Nostos.Shared.Dtos.NoteDto`)

```csharp
record NoteDto(Guid Id, Guid BookId, string Content, string? CfiRange,
               string? SelectedText, DateTime CreatedAt, string? BookTitle,
               // APPENDED: assistant capture provenance, the pen, passages, the
               // book's author, and the note's version.
               string? RawContent = null, string CaptureSource = "text",
               string ProcessingMode = "verbatim", string SourceAnchorKind = "unknown",
               string? SourceAnchorValue = null, bool AnchorVerified = false,
               string? HighlightColour = null, string Kind = "note",
               string? PassageTitle = null, string? PassageEnd = null,
               int? PassageWordCount = null, string? BookAuthor = null,
               DateTime? UpdatedAt = null);
```

- `Kind` is `"note"` or `"passage"`. A passage starts at `SourceAnchorValue`
  and ends at `PassageEnd`; `SelectedText` is only its opening, and the full
  text comes from `GET /api/notes/{id}/passage`.
- `HighlightColour` is the pen by name (`amber | sage | blue | rose`); null
  means none recorded and a reader draws amber.
- `UpdatedAt` is the note's version. A client echoes it as `BaseUpdatedAt` to
  get a `409` instead of overwriting a note that changed underneath it.

```csharp
record CreateNoteDto(string Content, string? CfiRange = null, string? SelectedText = null,
                     string? RawContent = null, string CaptureSource = "text",
                     string ProcessingMode = "verbatim", string SourceAnchorKind = "unknown",
                     string? SourceAnchorValue = null, bool AnchorVerified = false,
                     string? HighlightColour = null, string Kind = "note",
                     string? PassageTitle = null, string? PassageEnd = null,
                     string? PassageHtml = null, IReadOnlyList<string>? Topics = null);

record UpdateNoteDto(string Content, string? SelectedText = null,
                     string? HighlightColour = null, string? PassageTitle = null,
                     IReadOnlyList<string>? Topics = null,
                     DateTime? BaseUpdatedAt = null);
```

`Topics` are the pills added where the note was written: each one the `Content`
does not already declare is written into it as its own `[[Name]]` paragraph
before processing. On update, `Content` is then the note's own words only, so a
pill left out of `Topics` is removed.

The same file also defines the shapes the Brain and the passage viewer use:
`NoteSearchHitDto` (a search/list row: snippet, topic names, provenance, book
format, edition count, `WorkId`), `NoteSearchPageDto` (one bounded page plus the
total still waiting), `PassageDto` (one passage in full, fetched on open),
`NoteSummaryDto` (counts for the Brain's fixed places), `NoteBookFacetDto`,
`NoteLocationDto`, `ReprocessNoteDto` and `NoteRawTranscriptDto`.

---

## Writing DTOs (`Nostos.Shared.Dtos.WritingDto`)

```csharp
record WritingDto(Guid Id, string Name, string Type, Guid? ParentId, DateTime UpdatedAt);
record WritingContentDto(Guid Id, string Name, string Content, DateTime UpdatedAt);
record CreateWritingDto(string Name, string Type, Guid? ParentId);
record UpdateWritingDto(string Name, string? Content, DateTime? BaseUpdatedAt = null);
record MoveWritingDto(Guid? NewParentId);
sealed record WritingPageSetupDto(string Paper, double MarginMm, string Font,
                                  double LineSpacing, string CitationStyle);
```

`BaseUpdatedAt` is the `UpdatedAt` the client last loaded: when sent and the
document changed since, the save is refused with `409` and the current content.
`WritingPageSetupDto` is the saved export layout (paper, margin, font, line
spacing, citation style) for a writing or folder.

---

## Enums

### BookFilter (`Nostos.Shared/Enums/BookFilter.cs`)

```csharp
enum BookFilter { All, Favorites, Finished, Reading, Unsorted, NotStarted }
```

`NotStarted` is its own filter: a work counts as not started only when no
edition has progress, a saved place or a finish (`WorkReadingStatus`). Reading
status is always derived from the editions and never stored on the book.

### BookSort (`Nostos.Shared/Enums/BookSort.cs`)

```csharp
enum BookSort { Recent, Title, Rating, LastRead, RecentlyFinished }
```

### BookStatus (`Nostos.Shared/Enums/BookStatus.cs`)

```csharp
enum BookStatus { Ready = 0, Downloading = 1, Transcoding = 2, Failed = 3, UploadPending = 4 }
```

`UploadPending` is explicit life-cycle state for a create-on-confirm import
whose file has not finished uploading; `HasFile = false` alone is also valid
for a metadata-only digital or physical book.

---

## Other `Nostos.Shared` contracts

These files are part of the same API contract. Their exact positional order
lives in the source; this table is the map to it.

| File | Types | What it is |
| --- | --- | --- |
| `Dtos/AssistantDtos.cs` | `AssistantTurnRequest`/`Response`, `AssistantContextDto`, `AssistantHistoryMessageDto`, `AssistantSuggestionDto`, `AssistantChangesetDto` and its requests, `AssistantTurnEventDto`, `AssistantTurnCancelRequest`/`Response`, `AssistantAnchorPromptDto`, `AssistantSourceReferenceDto`, `AssistantEvidenceHandleDto`/`ReferenceDto` | Ask Nostos wire contract: one turn in, reply + suggestions + pending changeset out; streamed turns; source/evidence handles |
| `Dtos/ProviderDtos.cs` | `ProviderSummaryDto`, `ProviderAssetDto`, `ProviderItemDto`, `ProviderSearchResultDto`, `ProviderDiscoverySearchResultDto`, `ProviderMetadataOverridesDto`, `ProviderAcquireRequestDto`, `ProviderAcquisitionDto`, `ProviderSettingsItemDto`, `ProviderSettingsResponseDto`, `ProviderPreferenceUpdateDto` | External content providers and acquisition; provider-neutral by design (no provider-specific field) |
| `Dtos/ImportDtos.cs` | `ImportActivityDto` | "Imports in progress" feed: a live job or a book row whose import a restart interrupted |
| `Dtos/LibraryCommandDtos.cs` | `LibraryCommandResultDto`, `LibraryCreateBookRequest`, `LibraryUpdateBookRequest`, `LibraryLinkWorkRequest`, `LibraryUnlinkWorkRequest`, `LibraryWorkMembershipResultDto`, `LibraryResolveBookRequest`/`Result`, `LibraryCandidate`, `LibraryResolution`, `LibraryAttachAcquiredAssetRequest`, `LibraryAcquiredTrack`, `LibraryCreateOrMatchResultDto`, collection requests/results | The canonical library command envelopes and their typed request bodies |
| `Dtos/LibraryChangesetDtos.cs` | `LibraryChangesetDiffDto`, `LibraryCollectionChangeDto`, `LibraryMembershipChangeDto`, `LibraryBookEditDto`, `LibraryFieldChangeDto`, `LibraryChangesetCountsDto` | What a set of library changes does, as the review card shows it |
| `Dtos/ReadingListDto.cs` | `ReadingListEntryDto`, `SaveReadingListEntryDto`, `SetReadingListNextDto`, `ReadingListAcquiredDto` | "Next up" shelf entries for books the user does not own yet |
| `Dtos/OpdsDto.cs` | `OpdsInfoDto` | Whether the OPDS catalogue is served, the URL to give a reader, and whether it is loopback-only |
| `Dtos/WritingSourceDto.cs` | `AddWritingSourceDto`, `WritingSourceDto` | A note kept as a source for a writing document; every note field is read live |
| `Dtos/BackupDto.cs` | `BackupStatusDto`, `BackupSettingsDto`, `UpdateBackupSettingsDto`, `BackupHistoryDto`, `TriggerBackupResultDto`, `RestoreResultDto`, `BackupManifestDto`, `BackupProgressDto` | Backup/restore settings, history and progress |

`Dtos/CollectionDto.cs`, `Dtos/TopicDto.cs` and `Dtos/WritingDto.cs` hold the
collection, topic and writing Studio contracts named in the sections above.
