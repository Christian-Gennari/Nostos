# Frontend — Services

All HTTP services are `providedIn: 'root'` singletons using Angular `HttpClient`. The API base URL is `/api`.

---

## BooksService

**File:** `src/app/core/services/books.service.ts`

| Method           | Signature                                            | Return                                | Purpose                                                             |
| ---------------- | ---------------------------------------------------- | ------------------------------------- | ------------------------------------------------------------------- |
| `list`           | `(options: BookListOptions)`                         | `Observable<PaginatedResponse<Book>>` | Paginated book list with filter / sort / search / collection params |
| `get`            | `(id: string)`                                       | `Observable<Book>`                    | Single book by ID                                                   |
| `create`         | `(dto: CreateBookDto)`                               | `Observable<Book>`                    | Create a new book                                                   |
| `update`         | `(id: string, dto: UpdateBookDto)`                   | `Observable<Book>`                    | Update metadata                                                     |
| `delete`         | `(id: string)`                                       | `Observable<void>`                    | Delete a book                                                       |
| `updateProgress` | `(id: string, location: string, percentage: number)` | `Observable<any>`                     | Save reading location + percentage                                  |
| `getLocations`   | `(id: string)`                                       | `Observable<BookLocationsDto>`        | Get cached epub locations JSON                                      |
| `saveLocations`  | `(id: string, locations: string)`                    | `Observable<void>`                    | Persist epub locations for instant progress                         |
| `uploadFile`     | `(bookId: string, file: File)`                       | `Observable<HttpEvent>`               | Upload file (FormData, `reportProgress: true`)                      |
| `uploadCover`    | `(bookId: string, file: File)`                       | `Observable<HttpEvent>`               | Upload cover image                                                  |
| `deleteCover`    | `(bookId: string)`                                   | `Observable<void>`                    | Remove cover                                                        |
| `lookup`         | `(isbn: string)`                                     | `Observable<CreateBookDto>`           | ISBN metadata lookup                                                |

**Helper type:** `BookListOptions = { filter?, sort?, search?, page?, pageSize? }`

---

## CollectionsService

**File:** `src/app/core/services/collections.service.ts`

| Method   | Signature                                | Return                     | Purpose                                        |
| -------- | ---------------------------------------- | -------------------------- | ---------------------------------------------- |
| `list`   | `()`                                     | `Observable<Collection[]>` | All collections                                |
| `create` | `(dto: CreateCollectionDto)`             | `Observable<Collection>`   | Create collection                              |
| `update` | `(id: string, dto: UpdateCollectionDto)` | `Observable<Collection>`   | Update collection                              |
| `delete` | `(id: string)`                           | `Observable<void>`         | Delete (`SidebarCollections` clears `LibraryFilterService.collectionId` if it was selected) |

**Signal state (global singletons):**

- `collectionId = signal<string | null>(null)` — the selected collections *filter* (`null` = "All Books"). It lives on `LibraryFilterService`, not on the HTTP service, so the URL/localStorage owns it rather than the API client.
- `sidebarExpanded = signal(true)` — sidebar toggle state

---

## NotesService

**File:** `src/app/core/services/notes.service.ts`

| Method   | Signature                              | Return               | Purpose          |
| -------- | -------------------------------------- | -------------------- | ---------------- |
| `list`   | `(bookId: string)`                     | `Observable<Note[]>` | Notes for a book |
| `create` | `(bookId: string, dto: CreateNoteDto)` | `Observable<Note>`   | Create note      |
| `update` | `(id: string, dto: UpdateNoteDto)`     | `Observable<Note>`   | Update note      |
| `delete` | `(id: string)`                         | `Observable<void>`   | Delete note      |

---

## TopicsService

**File:** `src/app/core/services/topics.service.ts`

| Method | Signature      | Return                         | Purpose                                  |
| ------ | -------------- | ------------------------------ | ---------------------------------------- |
| `list` | `()`           | `Observable<TopicDto[]>`     | All topics with `usageCount`           |
| `get`  | `(id: string)` | `Observable<TopicDetailDto>` | Topic detail with all associated notes |

**Inline DTOs:**

```ts
interface TopicDto {
  id: string;
  name: string;
  usageCount: number;
}
interface NoteContextDto {
  noteId;
  content;
  selectedText?;
  cfiRange?;
  bookId;
  bookTitle;
}
interface TopicDetailDto {
  id;
  name;
  notes: NoteContextDto[];
}
```

---

## WritingsService

**File:** `src/app/core/services/writings.service.ts`

| Method   | Signature                                   | Return                          | Purpose                   |
| -------- | ------------------------------------------- | ------------------------------- | ------------------------- |
| `list`   | `()`                                        | `Observable<WritingDto[]>`      | Entire file tree          |
| `get`    | `(id: string)`                              | `Observable<WritingContentDto>` | Single document content   |
| `create` | `(dto: CreateWritingDto)`                   | `Observable<WritingDto>`        | Create folder or document |
| `update` | `(id: string, dto: UpdateWritingDto)`       | `Observable<WritingContentDto>` | Update name / content     |
| `move`   | `(id: string, newParentId: string \| null)` | `Observable<WritingDto>`        | Reparent (drag & drop)    |
| `delete` | `(id: string)`                              | `Observable<void>`              | Delete + children         |

---

## ToastService

**File:** `src/app/core/services/toast.service.ts`

| Method    | Signature                                                | Purpose                 |
| --------- | -------------------------------------------------------- | ----------------------- |
| `show`    | `(message, type: ToastType = 'info', durationMs = 4000)` | Show auto-dismiss toast |
| `success` | `(message)`                                              | Green toast             |
| `error`   | `(message)`                                              | Red toast (5 s)         |
| `info`    | `(message)`                                              | Purple toast            |
| `dismiss` | `(id: number)`                                           | Manual dismiss          |

**Signal:** `toasts = signal<Toast[]>([])`  
**Types:** `ToastType = 'success' | 'error' | 'info'`

---

## NavigationHistoryService

**File:** `src/app/core/services/navigation-history.service.ts`

| Method       | Signature                        | Purpose                               |
| ------------ | -------------------------------- | ------------------------------------- |
| `getLastUrl` | `(appPrefix: string)` → `string` | Returns last visited URL in a section |

Subscribes to `NavigationEnd` router events. Maintains a `Record<string, string>` mapping section prefixes (`/library`, `/second-brain`, `/studio`) to their last-visited URLs. Used by `AppDockComponent` to deep-link back into the last page within each section.

---

## TopicAutocompleteService

**File:** `src/app/ui/topic-autocomplete-panel/topic-autocomplete.service.ts`

| Method                | Signature                   | Purpose                               |
| --------------------- | --------------------------- | ------------------------------------- |
| `setTopics`         | `(list: TopicDto[])`      | Set master list                       |
| `update`              | `(text, cursorPos)`         | Parse `[[` prefix, filter suggestions |
| `moveUp` / `moveDown` | `()`                        | Keyboard navigation                   |
| `choose`              | `()` → `TopicDto \| null` | Return selected topic               |
| `clear`               | `()`                        | Reset                                 |

---

## BackupService

**File:** `src/app/core/services/backup.service.ts`

| Method           | Return                       | Purpose                                      |
| ---------------- | ---------------------------- | -------------------------------------------- |
| `getStatus`      | `Observable<BackupStatus>`   | Get last backup time and scheduled status     |
| `getSettings`    | `Observable<BackupSettings>` | Get retention and interval config            |
| `updateSettings` | `Observable<BackupSettings>` | Save new backup preferences                  |
| `triggerBackup`  | `Observable<any>`            | Manually start a backup                      |
| `restore`        | `Observable<RestoreResult>`  | Trigger restoration from ID                  |
| `getHistory`     | `Observable<HistoryItem[]>`  | List all known backup archives               |
| `deleteBackup`   | `Observable<void>`           | Remove record and archive from server        |
| `getDownloadUrl` | `string`                     | Returns direct URL for archive download      |
| `importExisting` | `Observable<imported[]>`     | Scan server disk for untracked archives      |
| `getProgress`    | `Observable<BackupProgress>` | Poll current step/percentage (every 1s)      |

**Signals:** `suggestions = signal<TopicDto[]>([])`, `activeIndex = signal<number>(0)`

> **Note:** Declared `providedIn: 'root'` but `TopicInputComponent` provides it at the component level, so each textarea gets independent autocomplete state.
