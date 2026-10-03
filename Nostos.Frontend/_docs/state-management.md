# Frontend — State Management

## Approach

Nostos uses **Angular Signals** for all reactive state. There are no third-party state libraries (no NgRx, no Akita). The patterns are lightweight and intentional.

## Patterns in Use

### 1. Root-Level Signal State (Global Singletons)

Services that are `providedIn: 'root'` expose `signal()` properties directly.

| Service                      | Signals                                 | Purpose                      |
| ---------------------------- | --------------------------------------- | ---------------------------- |
| `CollectionsService`         | `sidebarExpanded`        | Sidebar toggle (`LibraryFilterService.collectionId` owns the active filter) |
| `ToastService`               | `toasts`                                | Reactive toast notifications |
| `TopicAutocompleteService` | `suggestions`, `activeIndex`            | Autocomplete dropdown state  |

Components read these signals in templates: `@if (service.toasts().length)`.

### 2. Component-Level Store (BookDetailStore)

**File:** `src/app/book-detail/book-detail.store.ts`

`BookDetailStore` is an `@Injectable()` class (NOT `providedIn: 'root'`) — it is provided at the component level by `BookDetailComponent`. Each route activation gets its own instance.

**Signals:**

```
loading    = signal(false)
error      = signal<string | null>(null)
book       = signal<Book | null>(null)
notes      = signal<Note[]>([])
collections = signal<Collection[]>([])
topicMap = signal<Map<string, TopicDto>>(new Map())
```

**Key methods:** `loadAllData(id)`, `toggleFavorite()`, `toggleFinished()`, `rate()`, `addNote()`, `updateNote()`, `deleteNote()`, `uploadCover()`, `deleteCover()`, `uploadFile()`

### 3. Optimistic Updates

`BookDetailStore` uses optimistic updates for UX-critical actions:

```ts
// toggleFavorite() — simplified
const prev = this.book();
this.book.update((b) => ({ ...b, isFavorite: !b.isFavorite })); // instant UI
this.booksService.update(id, { isFavorite: !prev.isFavorite }).subscribe({
  error: () => this.book.set(prev), // revert on failure
});
```

Applies to: `toggleFavorite`, `toggleFinished`, `rate`.

### 4. Computed Signals (Derived State)

```ts
// ReaderShell
fileType = computed(() => detectType(this.book()?.fileName));
activeReader = computed(() => this.resolveReader(this.fileType()));
toc = computed(() => this.activeReader()?.toc() ?? []);
```

Computed signals auto-update when their dependencies change. Used heavily in `ReaderShell`, `FlatTreeComponent`, and `Library`.

### 5. Component-Scoped DI

`TopicInputComponent` provides `TopicAutocompleteService` at the component level:

```ts
@Component({
  providers: [TopicAutocompleteService]
})
```

Each `<app-topic-input>` instance gets its own autocomplete state — independent suggestions, independent keyboard navigation index.

### 6. Component Lifecycle

Components are destroyed and recreated on navigation (standard Angular behavior). State that needs to survive navigation is held in singleton services and signal-based stores, not in the component tree. The router is configured with `scrollPositionRestoration: 'enabled'` to handle scroll position automatically.

## Data Flow Summary

```
Backend API
    ↓  HTTP (Observable)
Service (singleton)
    ↓  method call
Store / Component
    ↓  signal update
Template (reactive)
```

There is no global application store. Each page manages its own data lifecycle:

- **Library** — fetches on init, paginates via infinite scroll
- **BookDetail** — `BookDetailStore.loadAllData(id)` on route change
- **SecondBrain** — fetches topic list on init, detail on selection
- **WritingStudio** — fetches full tree on init, individual documents on selection
- **ReaderShell** — fetches book + notes on init, syncs progress on navigation
