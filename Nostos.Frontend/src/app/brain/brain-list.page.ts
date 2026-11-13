import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { TopicChipComponent } from '../ui/topic-chip/topic-chip.component';
import { toSignal, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { CdkConnectedOverlay, CdkOverlayOrigin, type ConnectedPosition } from '@angular/cdk/overlay';

import type { NoteBookFacet, NoteOrder, NoteSearchHit } from '../core/dtos/note.dtos';
import { NotesService, type NoteBrowseFilter } from '../core/services/notes.service';
import {
  TopicsService,
  type RelatedTopicDto,
  type TopicDto,
  type TopicWritingDto,
  type TopicWritingUsageDto,
} from '../core/services/topics.service';
import { ButtonComponent } from '../ui/button/button.component';
import { ChipComponent } from '../ui/chip/chip.component';
import { DropdownComponent, type DropdownOption } from '../ui/dropdown/dropdown.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { EmptyStateComponent } from '../ui/empty-state/empty-state.component';
import { PagingStatusComponent } from '../ui/paging-status/paging-status.component';
import { SelectionBarComponent } from '../ui/selection-bar/selection-bar.component';
import { NOSTOS_MENU } from '../ui/menu/menu.component';
import { NoteItemComponent } from '../ui/note-item/note-item.component';
import { NoticeComponent } from '../ui/notice/notice.component';
import { TopicPickerPopoverComponent } from '../ui/topic-picker-popover/topic-picker-popover.component';
import { InlineRenameComponent } from '../ui/inline-rename/inline-rename.component';
import { BrainTopicUsedInComponent, USED_IN_FETCHED } from './topic-used-in/brain-topic-used-in.component';
import { BrainListCacheService } from './brain-list-cache.service';
import { BrainNoteActionsService } from './brain-note-actions.service';
import { BrainNoteDetailComponent } from './brain-note-detail.component';
import { BrainNoteDialogsComponent } from './brain-note-dialogs.component';
import { BrainNoteRowComponent } from './brain-note-row.component';
import { BrainStateService } from './brain-state.service';
import { BrainTopicActionsService } from './brain-topic-actions.service';
import { BrainTopicWordsComponent } from './brain-topic-words.component';
import { BrainTopicSuggestionsService } from './brain-topic-suggestions.service';
import {
  NOTE_ORDER_KEY,
  ORDERS,
  ORDER_LABELS,
  TOPIC_ORDER_KEY,
  bookKey,
  countLabel,
  defaultOrder,
  facetKey,
  isOrder,
  noteDateGroups,
  placeTitle,
  plural,
  readStored,
  stillBelongs,
  topicsMatching,
  writeStored,
  type BrainPlace,
} from './brain.helpers';
import { SearchFieldComponent } from '../ui/search-field/search-field.component';
import { UNKNOWN_BOOK } from '../core/book-format';
import { BRAIN_WIDE_QUERY, isBrainWideViewport } from '../ui/breakpoints';

const PAGE_SIZE = 25;
const SEARCH_DEBOUNCE_MS = 250;
const RELATED_SHOWN = 6;


const SELECTION_PICKER_POSITIONS: ConnectedPosition[] = [
  { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -8 },
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 8 },
];

/**
 * One list of notes: All notes, Passages, Unsorted, or a topic. Every place
 * has the same toolbar (search, book, order, select) and the same card; a
 * topic adds its own header. What is shown is in the URL (`q`, `book`,
 * `order`, `with`), so Back and a shared link reopen the same view.
 */
@Component({
  selector: 'app-brain-list-page',
  standalone: true,
  imports: [
    SearchFieldComponent,
    RouterLink,
    CdkConnectedOverlay,
    CdkOverlayOrigin,
    ...NOSTOS_MENU,
    ButtonComponent,
    ChipComponent,
    DropdownComponent,
    IconButtonComponent,
    NostosIconComponent,
    InputDirective,
    LoadingIndicatorComponent,
    NoteItemComponent,
    NoticeComponent,
    TopicPickerPopoverComponent,
    InlineRenameComponent,
    BrainNoteDialogsComponent,
    BrainNoteDetailComponent,
    BrainNoteRowComponent,
    BrainTopicWordsComponent,
    BrainTopicUsedInComponent,
    EmptyStateComponent,
    PagingStatusComponent,
    SelectionBarComponent,
    TopicChipComponent,
  ],
  providers: [BrainNoteActionsService, BrainTopicSuggestionsService],
  templateUrl: './brain-list.page.html',
  styleUrls: ['./brain-list.page.css', './brain-touch.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrainListPage {
  protected readonly unknownBook = UNKNOWN_BOOK;
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly notesService = inject(NotesService);
  private readonly topicsService = inject(TopicsService);
  private readonly cache = inject(BrainListCacheService);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);

  readonly state = inject(BrainStateService);
  readonly topicActions = inject(BrainTopicActionsService);
  readonly noteActions = inject(BrainNoteActionsService);
  readonly suggestions = inject(BrainTopicSuggestionsService);

  private readonly data = toSignal(this.route.data, { initialValue: this.route.snapshot.data });
  private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
  private readonly queryParams = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });

  /** The wide Brain splits into the list and the selected note (issue #1531). */
  private readonly wideMedia = typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia(BRAIN_WIDE_QUERY)
    : null;
  readonly wide = signal(isBrainWideViewport());

  readonly place = computed<BrainPlace>(() => (this.data()['place'] as BrainPlace | undefined) ?? 'all');
  readonly topicId = computed(() => (this.place() === 'topic' ? this.params().get('topicId') : null));
  readonly q = computed(() => this.queryParams().get('q')?.trim() ?? '');
  readonly bookId = computed(() => this.queryParams().get('book'));
  readonly withId = computed(() => (this.place() === 'topic' ? this.queryParams().get('with') : null));
  private readonly storedOrders = signal({
    notes: readStored(NOTE_ORDER_KEY, isOrder, 'newest'),
    topic: readStored(TOPIC_ORDER_KEY, isOrder, defaultOrder('topic')),
  });
  readonly order = computed<NoteOrder>(() => {
    const fromUrl = this.queryParams().get('order');
    if (isOrder(fromUrl)) return fromUrl;
    return this.place() === 'topic' ? this.storedOrders().topic : this.storedOrders().notes;
  });

  readonly topic = computed(() => this.state.topicById(this.topicId()));
  readonly withTopic = computed(() => this.state.topicById(this.withId()));
  readonly title = computed(() => placeTitle(this.place(), this.topic()));
  /** A topic id the Brain no longer knows: deleted, or merged away. */
  readonly topicMissing = computed(() =>
    this.place() === 'topic' && this.state.topicsLoaded() && !this.topic());

  // The list.
  readonly notes = signal<NoteSearchHit[]>([]);
  readonly total = signal(0);
  readonly loading = signal(false);
  readonly loaded = signal(false);
  readonly failed = signal(false);
  readonly books = signal<NoteBookFacet[]>([]);
  readonly related = signal<RelatedTopicDto[]>([]);
  readonly relatedExpanded = signal(false);
  /** Where this topic is used in writing, from what writings cite; read-only (issue #1162). */
  readonly writingUsage = signal<TopicWritingUsageDto | null>(null);
  /** The writings that cite each of this topic's notes, newest edited first. */
  readonly citedIn = computed(() => {
    const usage = this.writingUsage();
    const byId = new Map((usage?.writings ?? []).map((w) => [w.id.toLowerCase(), w]));
    return new Map((usage?.noteCitations ?? []).map((c) => [
      c.noteId.toLowerCase(),
      (c.writingIds ?? []).map((id) => byId.get(id.toLowerCase())).filter((w): w is TopicWritingDto => !!w),
    ]));
  });
  readonly noWritings: readonly TopicWritingDto[] = [];

  // The toolbar.
  readonly searchDraft = signal('');
  readonly orderOptions: DropdownOption[] = ORDERS.map((value) => ({ value, label: ORDER_LABELS[value] }));
  readonly bookOptions = computed<DropdownOption[]>(() => [
    { value: '', label: 'All books' },
    ...this.books().map((book) => ({ value: facetKey(book), label: `${book.bookTitle || UNKNOWN_BOOK} (${book.noteCount})` })),
  ]);
  readonly bookTitle = computed(() => {
    const id = this.bookId();
    if (!id) return null;
    return this.books().find((book) => facetKey(book) === id)?.bookTitle
      ?? this.notes().find((note) => bookKey(note) === id)?.bookTitle
      ?? 'This book';
  });
  readonly matchingTopics = computed(() => topicsMatching(this.state.topics(), this.q()));
  readonly filtersActive = computed(() => !!this.q() || !!this.bookId());

  // Selection.
  readonly selecting = signal(false);
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly selectionPickerOpen = signal(false);
  readonly selectionPickerPositions = SELECTION_PICKER_POSITIONS;

  // The selected note in the wide pane. It is in the URL (`?note=`), so a
  // reload, Back and a shared link restore the same note in the same list.
  readonly selectedNoteId = computed(() => this.queryParams().get('note'));
  readonly selectedNote = signal<NoteSearchHit | null>(null);
  readonly noteLoading = signal(false);
  readonly noteFailed = signal(false);
  /** The row Tab lands on: the selection, or the first note when nothing is chosen. */
  readonly keyboardAnchorId = computed(() => {
    const id = this.selectedNoteId();
    if (id && this.notes().some((note) => note.id === id)) return id;
    return this.notes()[0]?.id ?? null;
  });

  readonly visibleRelated = computed(() =>
    this.relatedExpanded() ? this.related() : this.related().slice(0, RELATED_SHOWN));
  readonly hiddenRelated = computed(() => Math.max(0, this.related().length - RELATED_SHOWN));

  readonly countText = computed(() => countLabel(this.place(), this.total()));
  readonly topicSummary = computed(() => {
    const topic = this.topic();
    if (!topic) return '';
    const books = this.books().length;
    return books ? `${plural(topic.usageCount, 'note')} from ${plural(books, 'book')}` : plural(topic.usageCount, 'note');
  });
  readonly bookCounts = computed(() => new Map(this.books().map((book) => [facetKey(book), book.noteCount])));
  /** Date groups for Newest first / Oldest first, one entry per loaded note. */
  readonly dateGroups = computed(() => noteDateGroups(this.notes()));
  readonly plural = plural;
  readonly bookKey = bookKey;
  /** The topic whose title is being renamed in place, if it is this page's. */
  readonly renamingTitle = computed(() => {
    const topic = this.topic();
    return topic && this.topicActions.renameId() === topic.id && this.topicActions.renameSurface() === 'title' ? topic : null;
  });

  readonly emptyText = computed(() => {
    if (this.filtersActive()) return 'No notes match.';
    switch (this.place()) {
      case 'passages':
        return 'No passages yet. In a book, select where one starts and choose Mark passage.';
      case 'unsorted':
        return 'Every note has a topic.';
      case 'topic':
        return this.withTopic() ? 'These topics share no notes.' : 'No notes carry this topic any more.';
      default:
        return 'Your notes will appear here as you read: highlight a line or write a thought in any book.';
    }
  });

  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private readonly listScroller = viewChild<ElementRef<HTMLElement>>('list');
  private readonly detailPane = viewChild<ElementRef<HTMLElement>>('detailPane');
  private seq = 0;
  private booksSeq = 0;
  private booksKey: string | null = null;
  /** Where notes the list let go were, so Undo can put them back there. */
  private readonly letGo = new Map<string, number>();
  private lastScrollTop = 0;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private currentKey: string | null = null;
  /** The note id + topics version whose detail is loaded; avoids rereading it. */
  private selectedLoadKey: string | null = null;
  /** Guards the phone redirect from asking twice before the route changes. */
  private openingNoteId: string | null = null;

  private readonly listKey = computed(() =>
    JSON.stringify([this.place(), this.topicId(), this.q(), this.bookId(), this.withId(), this.order()]));

  constructor() {
    this.state.ensureLoaded();
    this.noteActions.attach({
      replace: (note) => this.replaceNote(note),
      remove: (id) => this.removeNote(id),
    });

    // Any change of place, filter or order starts the list again (or restores
    // it, coming Back to it).
    effect(() => {
      const key = this.listKey();
      untracked(() => this.open(key));
    });

    // The topic's neighbours, for "Often with".
    effect(() => {
      const id = this.topicId();
      untracked(() => this.loadRelated(id));
    });

    // Where the topic is used in writing. It follows what writings cite, so it
    // is read again whenever the list is (a note deleted, a topic merged).
    effect(() => {
      const [, , id] = [this.listKey(), this.state.topicsRewritten(), this.topicId()];
      untracked(() => this.loadWritingUsage(id));
    });

    // The field follows the URL when the search changes from elsewhere (Back, a chip).
    effect(() => {
      const q = this.q();
      untracked(() => {
        // Only a change from elsewhere: the reader's own trailing space stays.
        if (this.searchTimer === null && this.searchDraft().trim() !== q) this.searchDraft.set(q);
      });
    });

    // The wide layout is a viewport fact, not a routing one: crossing the
    // breakpoint turns the pane on or off without rebuilding the list.
    if (this.wideMedia) {
      const onChange = (event: MediaQueryListEvent) => this.wide.set(event.matches);
      this.wideMedia.addEventListener('change', onChange);
      this.destroyRef.onDestroy(() => this.wideMedia?.removeEventListener('change', onChange));
    }

    // The URL says which note is open; the pane shows it (or the list notices
    // that this width needs the note's own page).
    effect(() => {
      const id = this.selectedNoteId();
      const wide = this.wide();
      const rewritten = this.state.topicsRewritten();
      const notes = this.notes();
      untracked(() => this.syncSelectedNote(id, wide, rewritten, notes));
    });

    // A new note starts at its top, however far the last one was scrolled, and
    // its row comes into view when the selection came from the URL.
    effect(() => {
      const id = this.selectedNoteId();
      untracked(() => {
        this.detailPane()?.nativeElement.scrollTo({ top: 0 });
        afterNextRender(() => {
          if (!id) return;
          this.rowElements()
            .find((row) => row.dataset['noteId'] === id)
            ?.scrollIntoView({ block: 'nearest' });
        }, { injector: this.injector });
      });
    });

    // A topic was renamed, merged or deleted: the server rewrote the notes'
    // references, so read the notes on screen again.
    let rewritten = this.state.topicsRewritten();
    effect(() => {
      const version = this.state.topicsRewritten();
      if (version === rewritten) return;
      rewritten = version;
      untracked(() => this.refill());
    });

    this.destroyRef.onDestroy(() => {
      if (this.searchTimer !== null) clearTimeout(this.searchTimer);
      this.saveSnapshot();
    });

  }

  /** The filters as the API reads them. */
  private filter(): NoteBrowseFilter {
    const place = this.place();
    return {
      query: this.q() || undefined,
      // One book, every edition of it.
      workId: this.bookId() ?? undefined,
      withoutTopics: place === 'unsorted',
      kind: place === 'passages' ? 'passage' : undefined,
      topicId: this.topicId() ?? undefined,
      withTopicId: this.withId() ?? undefined,
    };
  }

  private open(key: string): void {
    this.saveSnapshot();
    this.currentKey = key;
    this.letGo.clear();
    this.leaveSelection();

    // Whatever was loading belongs to the list being left.
    this.seq++;
    this.booksSeq++;
    this.loading.set(false);

    const restored = this.cache.restore(key, this.state.dataVersion());
    if (restored) {
      this.notes.set(restored.notes);
      this.total.set(restored.total);
      this.books.set(restored.books);
      this.booksKey = JSON.stringify(this.filter());
      this.loaded.set(true);
      this.failed.set(false);
      this.lastScrollTop = restored.scrollTop;
      afterNextRender(() => this.scrollContainer()?.scrollTo({ top: restored.scrollTop }), {
        injector: this.injector,
      });
      return;
    }

    this.notes.set([]);
    this.total.set(0);
    this.loaded.set(false);
    this.failed.set(false);
    this.lastScrollTop = 0;
    this.scrollContainer()?.scrollTo({ top: 0 });
    this.loadPage();
    this.loadBooks();
  }

  private saveSnapshot(): void {
    if (!this.currentKey || !this.loaded()) return;
    this.cache.save(this.currentKey, {
      notes: this.notes(),
      total: this.total(),
      books: this.books(),
      // Read from the last scroll event: by the time a destroyed page saves,
      // its element is detached and reports 0.
      scrollTop: this.lastScrollTop,
      version: this.state.dataVersion(),
    });
  }

  onScroll(event: Event): void {
    this.lastScrollTop = (event.target as HTMLElement).scrollTop;
  }

  /** The element that scrolls the list: the whole page narrow, the list wide. */
  private scrollContainer(): HTMLElement | undefined {
    return (this.wide() ? this.listScroller() : this.scroller())?.nativeElement;
  }

  // --- The note open in the wide pane ------------------------------------

  /**
   * The URL is the selection: `?note=` shows its note in the pane. A narrow
   * viewport has no pane, so the same id opens the note's own page (replacing
   * the list entry, so Back does not land on a URL that redirects again).
   */
  private syncSelectedNote(
    id: string | null,
    wide: boolean,
    topicsVersion: number,
    notes: readonly NoteSearchHit[],
  ): void {
    if (!id) {
      this.selectedNote.set(null);
      this.noteLoading.set(false);
      this.noteFailed.set(false);
      this.selectedLoadKey = null;
      return;
    }
    if (!wide) {
      this.selectedNote.set(null);
      this.noteLoading.set(false);
      this.noteFailed.set(false);
      if (this.openingNoteId !== id) {
        this.openingNoteId = id;
        void this.router.navigate(['/brain/notes', id], { replaceUrl: true });
      }
      return;
    }
    this.openingNoteId = null;
    const key = `${id}:${topicsVersion}`;
    const fromList = notes.find((note) => note.id === id) ?? null;
    if (fromList) {
      this.selectedNote.set(fromList);
      this.noteLoading.set(false);
      this.noteFailed.set(false);
      this.selectedLoadKey = key;
      return;
    }
    if (this.selectedLoadKey === key) return;
    this.selectedLoadKey = key;
    this.noteLoading.set(true);
    this.noteFailed.set(false);
    this.notesService.get(id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (note) => {
        if (this.selectedNoteId() !== id) return;
        this.selectedNote.set(note);
        this.noteLoading.set(false);
      },
      error: () => {
        if (this.selectedNoteId() !== id) return;
        this.selectedNote.set(null);
        this.noteLoading.set(false);
        this.noteFailed.set(true);
      },
    });
  }

  /** The calm state when a wide Brain has no note open. */
  readonly noNoteText = 'Select a note from the list, or press ↑ and ↓ to move through it.';

  chooseNote(note: NoteSearchHit): void {
    this.selectNote(note.id);
  }

  /** A wide row press: ticks the note in Select mode, opens it otherwise. */
  onRowClick(note: NoteSearchHit): void {
    if (!this.selecting()) {
      this.chooseNote(note);
      return;
    }
    this.setSelected(note.id, !this.selected().has(note.id));
  }

  /**
   * A related note, the keyboard, or a row: open it in the same place. A click
   * pushes a history entry (so Back returns to the note read before it); the
   * arrow keys replace it, so walking the list does not flood history.
   */
  selectNote(id: string, replace = false): void {
    if (!this.wide()) {
      void this.router.navigate(['/brain/notes', id]);
      return;
    }
    if (this.selectedNoteId() === id) return;
    this.setQuery({ note: id }, replace);
  }

  clearNoteSelection(): void {
    this.selectedNote.set(null);
    this.selectedLoadKey = null;
    if (this.selectedNoteId()) this.setQuery({ note: null }, true);
  }

  /** Roving tabindex: one stop per list, on the chosen note or the first one. */
  rowTabIndex(noteId: string): number {
    return this.keyboardAnchorId() === noteId ? 0 : -1;
  }

  private rowElements(): HTMLElement[] {
    const root = this.scroller()?.nativeElement;
    return root ? Array.from(root.querySelectorAll<HTMLElement>('[data-note-row]')) : [];
  }

  /**
   * ↑/↓ walk the list, Enter hands focus to the pane, Space chooses the note —
   * or, while notes are being ticked, ticks the focused one.
   */
  onListKeydown(event: KeyboardEvent): void {
    if (!this.wide()) return;
    const target = (event.target as HTMLElement | null)?.closest<HTMLElement>('[data-note-row]');
    // Only a note row is a listbox option. The paging status below the list
    // keeps its own keyboard behaviour.
    if (!target) return;
    const rows = this.rowElements();
    if (!rows.length) return;
    const currentId = target.dataset['noteId'];
    const index = rows.findIndex((row) => row.dataset['noteId'] === currentId);

    if (event.key === 'ArrowDown' || event.key === 'ArrowUp' || event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      const next = event.key === 'Home'
        ? 0
        : event.key === 'End'
          ? rows.length - 1
          : index < 0
            ? (event.key === 'ArrowDown' ? 0 : rows.length - 1)
            : Math.min(rows.length - 1, Math.max(0, index + (event.key === 'ArrowDown' ? 1 : -1)));
      const row = rows[next];
      const id = row?.dataset['noteId'];
      if (!id) return;
      this.selectNote(id, true);
      row.focus();
      return;
    }

    if (event.key === 'Enter') {
      event.preventDefault();
      if (currentId && this.selectedNoteId() !== currentId) this.selectNote(currentId);
      this.focusDetail();
      return;
    }

    if (event.key === ' ') {
      if (!currentId) return;
      // A checkbox inside the row keeps its own Space and reports the tick itself.
      if (event.target !== target) return;
      event.preventDefault();
      if (this.selecting()) {
        this.setSelected(currentId, !this.selected().has(currentId));
        return;
      }
      this.selectNote(currentId, true);
    }
  }

  /** Enter from the list: the note itself takes the keyboard. */
  focusDetail(): void {
    this.detailPane()?.nativeElement.focus();
  }

  /** Esc from the pane: back to the list row the reader came from. */
  focusList(): void {
    const id = this.selectedNoteId();
    const rows = this.rowElements();
    const row = rows.find((candidate) => candidate.dataset['noteId'] === id) ?? rows[0];
    row?.focus();
  }

  /**
   * Reads the notes on screen again, as many as are loaded, keeping the
   * reader's place: after a topic rename, or when Undo brought back a note
   * this list had let go.
   */
  private refill(): void {
    const seq = ++this.seq;
    this.loading.set(true);
    this.notesService.browse({
      ...this.filter(),
      order: this.order(),
      limit: Math.min(200, Math.max(PAGE_SIZE, this.notes().length + 1)),
      offset: 0,
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (page) => {
        if (seq !== this.seq) return;
        this.notes.set(page.items);
        this.total.set(page.totalCount);
        this.loading.set(false);
        this.loaded.set(true);
      },
      error: () => {
        if (seq === this.seq) this.loading.set(false);
      },
    });
    this.loadBooks(true);
  }

  loadPage(): void {
    if (this.loading()) return;
    const seq = ++this.seq;
    this.loading.set(true);
    this.failed.set(false);
    this.notesService.browse({
      ...this.filter(),
      order: this.order(),
      limit: PAGE_SIZE,
      offset: this.notes().length,
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (page) => {
        if (seq !== this.seq) return;
        const held = new Set(this.notes().map((note) => note.id));
        this.notes.update((notes) => [...notes, ...page.items.filter((note) => !held.has(note.id))]);
        this.total.set(page.totalCount);
        this.loading.set(false);
        this.loaded.set(true);
      },
      error: () => {
        if (seq !== this.seq) return;
        this.loading.set(false);
        this.failed.set(true);
      },
    });
  }

  loadMore(): void {
    if (this.loaded() && !this.loading() && !this.failed() && this.notes().length < this.total()) this.loadPage();
  }

  /** The book list depends on the place and filters, not the order: reload only when those change. */
  private loadBooks(force = false): void {
    const key = JSON.stringify(this.filter());
    if (!force && key === this.booksKey) return;
    this.booksKey = key;
    const seq = ++this.booksSeq;
    this.notesService.books(this.filter()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (books) => {
        if (seq === this.booksSeq) this.books.set(books);
      },
      error: () => {
        if (seq === this.booksSeq) this.booksKey = null;
      },
    });
  }

  private writingUsageSeq = 0;

  private loadWritingUsage(topicId: string | null): void {
    const seq = ++this.writingUsageSeq;
    if (!topicId) {
      this.writingUsage.set(null);
      return;
    }
    if (this.writingUsage() && this.topicId() !== topicId) this.writingUsage.set(null);
    this.topicsService.getWritingUsage(topicId, USED_IN_FETCHED).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (usage) => {
        if (seq === this.writingUsageSeq) this.writingUsage.set(usage);
      },
      error: () => {
        if (seq === this.writingUsageSeq) this.writingUsage.set(null);
      },
    });
  }

  /** The writings that cite a note, for its "Cited in N writings" menu. */
  citedWritings(noteId: string): readonly TopicWritingDto[] {
    return this.citedIn().get(noteId.toLowerCase()) ?? this.noWritings;
  }

  private loadRelated(topicId: string | null): void {
    this.related.set([]);
    this.relatedExpanded.set(false);
    if (!topicId) return;
    this.topicsService.getRelated(topicId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (related) => {
        if (this.topicId() !== topicId) return;
        this.related.set([...related].sort((a, b) => b.sharedNotes - a.sharedNotes || a.name.localeCompare(b.name)));
      },
      error: () => undefined,
    });
  }

  // --- The URL is the state ---------------------------------------------

  private setQuery(params: Record<string, string | null>, replaceUrl = false): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: params, queryParamsHandling: 'merge', replaceUrl });
  }

  onSearchInput(value: string): void {
    this.searchDraft.set(value);
    if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.searchTimer = null;
      this.setQuery({ q: value.trim() || null }, true);
    }, SEARCH_DEBOUNCE_MS);
  }

  clearSearch(): void {
    if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    this.searchTimer = null;
    this.searchDraft.set('');
    this.setQuery({ q: null });
  }

  setBook(bookId: string | null): void {
    this.setQuery({ book: bookId || null });
  }

  /** "More from this book": the book as a whole, not the one edition the note is from. */
  moreFromBook(note: NoteSearchHit): void {
    this.setBook(bookKey(note));
  }

  setOrder(value: string): void {
    if (!isOrder(value)) return;
    const topic = this.place() === 'topic';
    this.storedOrders.update((orders) => (topic ? { ...orders, topic: value } : { ...orders, notes: value }));
    writeStored(topic ? TOPIC_ORDER_KEY : NOTE_ORDER_KEY, value);
    this.setQuery({ order: value });
  }

  narrowTo(topicId: string | null): void {
    this.setQuery({ with: topicId });
  }

  clearFilters(): void {
    this.searchDraft.set('');
    this.setQuery({ q: null, book: null });
  }

  // --- The notes on screen ----------------------------------------------

  private replaceNote(note: NoteSearchHit): void {
    if (this.selectedNote()?.id === note.id) this.selectedNote.set(note);
    const belongs = stillBelongs(note, this.place(), this.topic(), this.withTopic());
    if (!this.notes().some((item) => item.id === note.id)) {
      // A note this list had let go belongs again (Undo): back in its place.
      const at = this.letGo.get(note.id);
      if (belongs && at !== undefined) {
        this.letGo.delete(note.id);
        this.notes.update((notes) => [...notes.slice(0, at), note, ...notes.slice(at)]);
        this.total.update((total) => total + 1);
      }
      return;
    }
    if (!belongs) {
      this.removeNote(note.id);
      return;
    }
    this.notes.update((notes) => notes.map((item) => (item.id === note.id ? note : item)));
  }

  private removeNote(id: string): void {
    if (this.selectedNoteId() === id) this.clearNoteSelection();
    const at = this.notes().findIndex((note) => note.id === id);
    if (at < 0) return;
    this.letGo.set(id, at);
    this.notes.update((notes) => notes.filter((note) => note.id !== id));
    this.total.update((total) => Math.max(0, total - 1));
    if (!this.selected().has(id)) return;
    const next = new Set(this.selected());
    next.delete(id);
    this.selected.set(next);
    // A deleted selection leaves nothing to act on.
    if (next.size === 0 && this.noteActions.deleting()) this.leaveSelection();
  }

  /** Where a book's group starts in the "by book" order. */
  startsBook(index: number): boolean {
    const notes = this.notes();
    return index === 0 || bookKey(notes[index - 1]) !== bookKey(notes[index]);
  }

  // --- Selection ----------------------------------------------------------

  toggleSelecting(): void {
    if (this.selecting()) this.leaveSelection();
    else this.selecting.set(true);
  }

  startSelecting(noteId: string): void {
    this.selecting.set(true);
    this.selected.set(new Set([noteId]));
  }

  setSelected(noteId: string, on: boolean): void {
    this.selected.update((selected) => {
      const next = new Set(selected);
      if (on) next.add(noteId);
      else next.delete(noteId);
      return next;
    });
  }

  leaveSelection(): void {
    this.selecting.set(false);
    this.selected.set(new Set());
    this.selectionPickerOpen.set(false);
  }

  addTopicToSelection(name: string): void {
    this.selectionPickerOpen.set(false);
    this.noteActions.addTopicToMany([...this.selected()], name);
  }

  keepSelectionWithWriting(): void {
    this.noteActions.keepWithWriting([...this.selected()]);
  }

  deleteSelection(): void {
    this.noteActions.askDelete([...this.selected()]);
  }

  /** Keep the topic's notes with a writing: every note it has, not only the ones loaded. */
  keepTopicWithWriting(topic: TopicDto): void {
    this.topicsService.get(topic.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (detail) => this.noteActions.keepWithWriting(detail.notes.map((note) => note.noteId)),
      error: () => undefined,
    });
  }

  openDrawer(): void {
    this.state.drawerOpen.set(true);
  }

  showOnMap(topicId: string): void {
    void this.router.navigate(['/brain/map'], { queryParams: { focus: topicId } });
  }
}
