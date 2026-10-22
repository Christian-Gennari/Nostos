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
import { toSignal } from '@angular/core/rxjs-interop';
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
import { NOSTOS_MENU } from '../ui/menu/menu.component';
import { NoteItemComponent } from '../ui/note-item/note-item.component';
import { NoticeComponent } from '../ui/notice/notice.component';
import { TopicPickerPopoverComponent } from '../ui/topic-picker-popover/topic-picker-popover.component';
import { BrainTopicRenameComponent } from './topic-rename/brain-topic-rename.component';
import { BrainTopicUsedInComponent, USED_IN_FETCHED } from './topic-used-in/brain-topic-used-in.component';
import { BrainListCacheService } from './brain-list-cache.service';
import { BrainNoteActionsService } from './brain-note-actions.service';
import { BrainNoteDialogsComponent } from './brain-note-dialogs.component';
import { BrainStateService } from './brain-state.service';
import { BrainTopicActionsService } from './brain-topic-actions.service';
import { BrainTopicWordsComponent } from './brain-topic-words.component';
import { BrainTopicSuggestionsService } from './brain-topic-suggestions.service';
import {
  NOTE_ORDER_KEY,
  ORDERS,
  ORDER_LABELS,
  TOPIC_ORDER_KEY,
  countLabel,
  defaultOrder,
  isOrder,
  placeTitle,
  plural,
  readStored,
  stillBelongs,
  topicsMatching,
  writeStored,
  type BrainPlace,
} from './brain.helpers';

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
    BrainTopicRenameComponent,
    BrainNoteDialogsComponent,
    BrainTopicWordsComponent,
    BrainTopicUsedInComponent,
  ],
  providers: [BrainNoteActionsService, BrainTopicSuggestionsService],
  templateUrl: './brain-list.page.html',
  styleUrls: ['./brain-list.page.css', './brain-touch.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrainListPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly notesService = inject(NotesService);
  private readonly topicsService = inject(TopicsService);
  private readonly cache = inject(BrainListCacheService);
  private readonly injector = inject(Injector);

  readonly state = inject(BrainStateService);
  readonly topicActions = inject(BrainTopicActionsService);
  readonly noteActions = inject(BrainNoteActionsService);
  readonly suggestions = inject(BrainTopicSuggestionsService);

  private readonly data = toSignal(this.route.data, { initialValue: this.route.snapshot.data });
  private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
  private readonly queryParams = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });

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
    ...this.books().map((book) => ({ value: book.bookId, label: `${book.bookTitle || 'Unknown book'} (${book.noteCount})` })),
  ]);
  readonly bookTitle = computed(() => {
    const id = this.bookId();
    if (!id) return null;
    return this.books().find((book) => book.bookId === id)?.bookTitle
      ?? this.notes().find((note) => note.bookId === id)?.bookTitle
      ?? 'This book';
  });
  readonly matchingTopics = computed(() => topicsMatching(this.state.topics(), this.q()));
  readonly filtersActive = computed(() => !!this.q() || !!this.bookId());

  // Selection.
  readonly selecting = signal(false);
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly selectionPickerOpen = signal(false);
  readonly selectionPickerPositions = SELECTION_PICKER_POSITIONS;

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
  readonly bookCounts = computed(() => new Map(this.books().map((book) => [book.bookId, book.noteCount])));
  readonly plural = plural;
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
  private readonly sentinel = viewChild<ElementRef<HTMLElement>>('sentinel');
  private seq = 0;
  private booksSeq = 0;
  private booksKey: string | null = null;
  /** Where notes the list let go were, so Undo can put them back there. */
  private readonly letGo = new Map<string, number>();
  private lastScrollTop = 0;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private currentKey: string | null = null;

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

    // A topic was renamed, merged or deleted: the server rewrote the notes'
    // references, so read the notes on screen again.
    let rewritten = this.state.topicsRewritten();
    effect(() => {
      const version = this.state.topicsRewritten();
      if (version === rewritten) return;
      rewritten = version;
      untracked(() => this.refill());
    });

    // Load the next page when the end of the list comes into view.
    effect((onCleanup) => {
      const sentinel = this.sentinel()?.nativeElement;
      if (!sentinel || typeof IntersectionObserver === 'undefined') return;
      const observer = new IntersectionObserver((entries) => {
        if (entries.some((entry) => entry.isIntersecting)) this.loadMore();
      }, { root: this.scroller()?.nativeElement ?? null, rootMargin: '600px 0px' });
      observer.observe(sentinel);
      onCleanup(() => observer.disconnect());
    });

    inject(DestroyRef).onDestroy(() => {
      if (this.searchTimer !== null) clearTimeout(this.searchTimer);
      this.saveSnapshot();
    });

  }

  /** The filters as the API reads them. */
  private filter(): NoteBrowseFilter {
    const place = this.place();
    return {
      query: this.q() || undefined,
      bookId: this.bookId() ?? undefined,
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
      afterNextRender(() => this.scroller()?.nativeElement.scrollTo({ top: restored.scrollTop }), {
        injector: this.injector,
      });
      return;
    }

    this.notes.set([]);
    this.total.set(0);
    this.loaded.set(false);
    this.failed.set(false);
    this.lastScrollTop = 0;
    this.scroller()?.nativeElement.scrollTo({ top: 0 });
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
    }).subscribe({
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
    }).subscribe({
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
    this.notesService.books(this.filter()).subscribe({
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
    this.topicsService.getWritingUsage(topicId, USED_IN_FETCHED).subscribe({
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
    this.topicsService.getRelated(topicId).subscribe({
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
    return index === 0 || notes[index - 1].bookId !== notes[index].bookId;
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
    this.topicsService.get(topic.id).subscribe({
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
