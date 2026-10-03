# Frontend — DTOs & Interfaces

All TypeScript interfaces used for API communication and internal data transfer.

---

## Book Types

**File:** `src/app/core/dtos/book.dtos.ts`

### `BookType`

```ts
type BookType = 'physical' | 'ebook' | 'audiobook';
```

### `Book`

```ts
interface Book {
  id: string;
  title: string;
  subtitle?: string;
  author?: string;
  editor?: string;
  translator?: string;
  narrator?: string;
  description?: string;
  type: BookType;
  isbn?: string;
  publisher?: string;
  placeOfPublication?: string;
  publishedDate?: string;
  pageCount?: number;
  language?: string;
  categories?: string;
  series?: string;
  volumeNumber?: number;
  hasFile: boolean;
  fileName?: string;
  coverUrl?: string;
  collectionIds: string[];
  lastLocation?: string;
  progressPercent: number;
  lastReadAt?: string;
  rating: number;
  isFavorite: boolean;
  personalReview?: string;
  finishedAt?: string;
  chapters?: BookChapter[];
  edition?: string;
  asin?: string;
  duration?: number;
}
```

### `BookChapter`

```ts
interface BookChapter {
  title: string;
  startTime: number; // seconds
}
```

### `PaginatedResponse<T>`

```ts
interface PaginatedResponse<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}
```

### `CreateBookDto`

All creation fields: `type`, `title`, `author`, `subtitle`, `editor`, `translator`, `narrator`, `description`, `isbn`, `publisher`, `placeOfPublication`, `publishedDate`, `pageCount`, `language`, `categories`, `series`, `volumeNumber`, `collectionIds`, `personalReview`, `edition`, `asin`, `duration`.

`collectionIds` is the membership set. The singular `collectionId` is still accepted *inbound* for compatibility and translated into a single-element set by the backend; responses report `collectionIds` only.

### `UpdateBookDto`

All fields optional. Includes `isFinished?: boolean` for finish-toggle.

### `UpdateProgressDto`

```ts
interface UpdateProgressDto {
  location: string;
  percentage: number;
}
```

---

## Book Enums

**File:** `src/app/core/dtos/book.enums.ts`

```ts
enum BookFilter {
  All = '',
  Favorites = 'favorites',
  Finished = 'finished',
  Reading = 'reading',
  Unsorted = 'unsorted',
}

enum BookSort {
  Recent = 'recent',
  Title = 'title',
  Rating = 'rating',
  LastRead = 'lastread',
}
```

---

## Collection Types

**File:** `src/app/core/dtos/collection.dtos.ts`

```ts
interface Collection {
  id: string;
  name: string;
  parentId?: string | null;
}

interface CreateCollectionDto {
  name: string;
  parentId?: string;
}

interface UpdateCollectionDto {
  name: string;
  parentId?: string;
}
```

---

## Note Types

**File:** `src/app/core/dtos/note.dtos.ts`

```ts
interface Note {
  id: string;
  bookId: string;
  content: string;
  cfiRange?: string; // epub CFI for highlights
  selectedText?: string; // quoted passage
  createdAt: string; // ISO date
  bookTitle?: string; // populated in some contexts
}

interface CreateNoteDto {
  content: string;
  cfiRange?: string;
  selectedText?: string;
}

interface UpdateNoteDto {
  content: string;
  selectedText?: string;
}
```

---

## Writing Types

**File:** `src/app/core/dtos/writing.dtos.ts`

```ts
interface WritingDto {
  id: string;
  name: string;
  type: 'Folder' | 'Document';
  parentId?: string;
  updatedAt: string;
}

interface WritingContentDto {
  id: string;
  name: string;
  content: string;
  updatedAt: string;
}

interface CreateWritingDto {
  name: string;
  type: 'Folder' | 'Document';
  parentId?: string;
}

interface UpdateWritingDto {
  name: string;
  content?: string;
}

interface MoveWritingDto {
  newParentId?: string;
}
```

---

## Topic Types (Inline in TopicsService)

```ts
interface TopicDto {
  id: string;
  name: string;
  usageCount: number;
}

interface NoteContextDto {
  noteId: string;
  content: string;
  selectedText?: string;
  cfiRange?: string;
  bookId: string;
  bookTitle: string;
}

interface TopicDetailDto {
  id: string;
  name: string;
  notes: NoteContextDto[];
}
```

---

## Reader Interfaces

**File:** `src/app/reader/reader.interface.ts`

```ts
interface TocItem {
  label: string;
  target: string | number;
  children?: TocItem[];
}

interface ReaderProgress {
  label: string;
  percentage: number;
  tooltip?: string;
  pageNumber?: number;
  pageCount?: number;
}

interface IReader {
  toc: Signal<TocItem[]>;
  progress: Signal<ReaderProgress>;
  next(): void;
  previous(): void;
  goTo(target: string | number): void;
  getCurrentLocation(): string;
  zoomIn(): void;
  zoomOut(): void;
  removeHighlight(identifier: string): void;
}
```

---

## Tree Interfaces

**File:** `src/app/ui/flat-tree/tree-node.interface.ts`

```ts
interface TreeNode {
  id: string;
  name: string;
  type?: string;
  children: TreeNode[];
  [key: string]: any;
}

interface TreeNodeMoveEvent {
  item: TreeNode;
  newParentId: string | null;
}
```

**File:** `src/app/ui/flat-tree/flat-tree.helper.ts`

```ts
interface FlatTreeNode {
  id: string;
  name: string;
  type: string;
  parentId: string | null;
  level: number;
  expandable: boolean;
  isExpanded: boolean;
  originalData: any;
}
```
