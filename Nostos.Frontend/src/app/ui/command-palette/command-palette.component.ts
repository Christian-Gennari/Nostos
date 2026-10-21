import {
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  afterRenderEffect,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
  ChangeDetectionStrategy,
} from '@angular/core';
import { Router } from '@angular/router';
import { Observable, Subject, catchError, debounceTime, forkJoin, map, of, switchMap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { BooksService } from '../../core/services/books.service';
import { CollectionsService } from '../../core/services/collections.service';
import { HighlightImportService } from '../../core/services/highlight-import.service';
import { NotesService } from '../../core/services/notes.service';
import { ThemeService, themeOption } from '../../core/services/theme.service';
import { TopicDto, TopicsService } from '../../core/services/topics.service';
import { WritingsService } from '../../core/services/writings.service';
import { ToastService } from '../../core/services/toast.service';
import { LibraryFilterService } from '../../library/library-filter.service';
import { Book } from '../../core/dtos/book.dtos';
import { Collection } from '../../core/dtos/collection.dtos';
import { NoteSearchHit } from '../../core/dtos/note.dtos';
import { WritingDto } from '../../core/dtos/writing.dtos';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import type { NostosIconName } from '../icon/nostos-icons';
import { LoadingIndicatorComponent } from '../loading-indicator/loading-indicator.component';
import { AssistantService } from '../assistant/assistant.service';
import { AssistantLauncherService } from '../assistant/assistant-launcher.service';
import { CommandPaletteService } from './command-palette.service';
import { RecentItem, RecentItemsService, notesLabel } from './recent-items.service';
import { coverImageUrl } from '../../core/cover-url';

export type PaletteEntryKind =
  | 'book'
  | 'topic'
  | 'note'
  | 'document'
  | 'collection'
  | 'action'
  | 'ask';

/** A run of a label, marked when it is the part the query matched. */
export interface LabelPart {
  text: string;
  hit: boolean;
}

/** What the query is restricted to: everything, or one kind via a prefix. */
export type PaletteScope = 'all' | 'actions' | 'topics' | 'books';

const SCOPE_PREFIXES: Record<string, PaletteScope> = { '>': 'actions', '#': 'topics', '@': 'books' };

/** Splits a typed query into its scope prefix (if any) and the words to match. */
export function parseQuery(raw: string): { scope: PaletteScope; term: string } {
  const trimmed = raw.trim();
  const scope = SCOPE_PREFIXES[trimmed.charAt(0)];
  return scope ? { scope, term: trimmed.slice(1).trim() } : { scope: 'all', term: trimmed };
}

/** Cuts a label into runs so every case-insensitive occurrence of the term can be marked. */
export function highlightParts(text: string, term: string): LabelPart[] {
  const needle = term.trim().toLowerCase();
  if (!needle) return [{ text, hit: false }];
  const parts: LabelPart[] = [];
  const haystack = text.toLowerCase();
  let from = 0;
  for (let at = haystack.indexOf(needle); at !== -1; at = haystack.indexOf(needle, from)) {
    if (at > from) parts.push({ text: text.slice(from, at), hit: false });
    parts.push({ text: text.slice(at, at + needle.length), hit: true });
    from = at + needle.length;
  }
  if (from < text.length) parts.push({ text: text.slice(from), hit: false });
  return parts;
}

export interface PaletteEntry {
  kind: PaletteEntryKind;
  id: string;
  label: string;
  /** The label cut into runs with the matched text marked; absent means unmarked. */
  labelParts?: LabelPart[];
  sub: string;
  icon: NostosIconName;
  /** A book's cover thumbnail; the icon stands in when there is none. */
  coverUrl?: string | null;
  /** Extra words an action answers to, beyond its label. */
  keywords?: string;
}

export interface PaletteSection {
  id: string;
  title: string;
  entries: PaletteEntry[];
}

/** A section's entries with their position in the flat keyboard order. */
interface IndexedSection extends PaletteSection {
  start: number;
}

const LIMITS = { books: 5, topics: 4, notes: 4, documents: 4, collections: 4, scoped: 8 };

interface ServerResults {
  books: Book[];
  notes: NoteSearchHit[];
}

const NO_SERVER_RESULTS: ServerResults = { books: [], notes: [] };

/**
 * Global command palette (Cmd/Ctrl+K, or Search everything in the More sheet
 * on a phone).
 *
 * One search across the library: books and notes are matched on the server;
 * topics, documents and collections are small lists matched here by name.
 * Empty, it offers the places opened most recently and the quick actions.
 * When Ask Nostos is on, the last row turns the query into a question.
 */
@Component({
  selector: 'app-command-palette',
  standalone: true,
  imports: [LoadingIndicatorComponent, NostosIconComponent],
  templateUrl: './command-palette.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './command-palette.component.css',
})
export class CommandPalette {
  private router = inject(Router);
  private booksService = inject(BooksService);
  private collectionsService = inject(CollectionsService);
  private notesService = inject(NotesService);
  private topicsService = inject(TopicsService);
  private writingsService = inject(WritingsService);
  private filters = inject(LibraryFilterService);
  private highlightImport = inject(HighlightImportService);
  private theme = inject(ThemeService);
  private toast = inject(ToastService);
  private assistant = inject(AssistantService);
  private launcher = inject(AssistantLauncherService);
  private recent = inject(RecentItemsService);
  private palette = inject(CommandPaletteService);
  private host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly isOpen = this.palette.isOpen;
  readonly query = signal('');
  readonly searching = signal(false);
  readonly activeIndex = signal(0);

  private readonly inputRef = viewChild<ElementRef<HTMLInputElement>>('paletteInput');
  private readonly resultsRef = viewChild<ElementRef<HTMLElement>>('results');

  private readonly server = signal<ServerResults>(NO_SERVER_RESULTS);
  private readonly collections = signal<Collection[]>([]);
  private readonly topics = signal<TopicDto[]>([]);
  private readonly documents = signal<WritingDto[]>([]);
  private readonly searchSubject = new Subject<string>();
  private previouslyFocused: HTMLElement | null = null;

  private readonly actions = computed<PaletteEntry[]>(() => {
    const dark = this.theme.mode() === 'dark';
    // The other side of light and dark: the person's own day or night theme.
    const target = themeOption(dark ? this.theme.dayTheme() : this.theme.nightTheme());
    const targetName = target.value === 'light' || target.value === 'dark' ? target.value : target.label;
    const assistantOn = this.launcher.available();
    const out: PaletteEntry[] = [
      { kind: 'action', id: 'add-book', label: 'Add a book', sub: 'Search for it or enter it by hand', icon: 'plus', keywords: 'new book import' },
      { kind: 'action', id: 'new-document', label: 'New document', sub: 'Start writing in the Studio', icon: 'file-plus', keywords: 'write draft studio create' },
    ];
    if (assistantOn) {
      out.push({ kind: 'action', id: 'capture', label: 'Capture a thought', sub: 'Opens Ask Nostos', icon: 'sparkle', keywords: 'note ask assistant ai' });
    }
    out.push(
      { kind: 'action', id: 'import-highlights', label: 'Import e-reader highlights', sub: 'Kobo and KOReader notes', icon: 'download-simple', keywords: 'kobo koreader' },
      {
        kind: 'action',
        id: 'toggle-theme',
        label: `Switch to ${targetName} theme`,
        sub: 'Appearance',
        icon: dark ? 'sun' : 'moon',
        keywords: 'theme dark light mode appearance sepia mist study',
      },
    );
    return out;
  });

  private readonly destinations: PaletteEntry[] = [
    { kind: 'action', id: 'go-library', label: 'Library', sub: 'Your books', icon: 'books', keywords: 'go home' },
    { kind: 'action', id: 'go-brain', label: 'Brain', sub: 'Topics and notes', icon: 'brain', keywords: 'go second brain topics' },
    { kind: 'action', id: 'go-studio', label: 'Writing Studio', sub: 'Your documents', icon: 'pen-nib', keywords: 'go writing drafts' },
    { kind: 'action', id: 'go-settings', label: 'Settings', sub: 'Library, assistant, backups and appearance', icon: 'gear-six', keywords: 'go preferences' },
  ];

  readonly parsed = computed(() => parseQuery(this.query()));

  readonly sections = computed<IndexedSection[]>(() => {
    const { scope, term } = this.parsed();
    const q = term.toLowerCase();
    const raw =
      scope !== 'all' ? this.scopedSections(scope, q) : q ? this.searchSections(q) : this.emptySections();
    let start = 0;
    return raw
      .filter((section) => section.entries.length > 0)
      .map((section) => {
        const entries = section.entries.map((entry) =>
          entry.kind === 'ask' ? entry : { ...entry, labelParts: highlightParts(entry.label, term) },
        );
        const indexed = { ...section, entries, start };
        start += entries.length;
        return indexed;
      });
  });

  /** What the empty state says, so a scoped search that finds nothing says what it looked for. */
  readonly emptyMessage = computed(() => {
    const { scope, term } = this.parsed();
    const kind = scope === 'actions' ? 'actions' : scope === 'topics' ? 'topics' : scope === 'books' ? 'books' : null;
    if (!term) return kind ? `No ${kind} yet.` : '';
    return kind ? `No ${kind} match “${term}”.` : `Nothing matches “${term}”.`;
  });

  readonly entries = computed<PaletteEntry[]>(() => this.sections().flatMap((s) => s.entries));

  readonly activeId = computed(() =>
    this.entries().length > 0 ? this.optionId(this.activeIndex()) : null,
  );

  constructor() {
    // The app root starts this at boot so visits are recorded before the
    // palette has ever loaded; the call here keeps the component whole on its own.
    this.recent.watch(this.router);
    this.palette.setDismissHandler(() => this.dismiss());
    inject(DestroyRef).onDestroy(() => this.palette.setDismissHandler(null));

    this.searchSubject
      .pipe(
        debounceTime(200),
        switchMap((raw) => {
          const { scope, term: q } = parseQuery(raw);
          // Topics and actions are matched here; only books and notes go to the server.
          const wantsBooks = scope === 'all' || scope === 'books';
          const wantsNotes = scope === 'all';
          if (!q || (!wantsBooks && !wantsNotes)) {
            this.searching.set(false);
            return of(NO_SERVER_RESULTS);
          }
          this.searching.set(true);
          return forkJoin({
            books: wantsBooks
              ? this.booksService
                  .list({ search: q, page: 1, pageSize: LIMITS.books })
                  .pipe(map((res) => res.items ?? []), catchError(() => of([] as Book[])))
              : of([] as Book[]),
            notes: wantsNotes
              ? this.notesService.search(q, LIMITS.notes).pipe(catchError(() => of([] as NoteSearchHit[])))
              : of([] as NoteSearchHit[]),
          });
        }),
        takeUntilDestroyed(),
      )
      .subscribe((results) => {
        this.server.set(results);
        this.searching.set(false);
        // New results reorder the list; keep the highlight on a real row.
        if (this.activeIndex() >= this.entries().length) this.activeIndex.set(0);
      });

    // Opened by the shortcut or by a visible control through the service.
    effect(() => {
      if (this.isOpen()) untracked(() => this.onOpened());
    });

    // Keep the highlighted row in view as the arrow keys move it.
    afterRenderEffect(() => {
      const id = this.activeId();
      const container = this.resultsRef()?.nativeElement;
      if (!id || !container) return;
      container.querySelector<HTMLElement>(`#${id}`)?.scrollIntoView?.({ block: 'nearest' });
    });
  }

  /**
   * The palette is modal: while it is open, focus stays in it. A page that
   * moves focus on its own after a navigation (the Brain opening a deep-linked
   * note, say) would otherwise take the keystrokes meant for the search field.
   */
  @HostListener('document:focusin', ['$event'])
  onFocusIn(event: FocusEvent): void {
    if (!this.isOpen()) return;
    const target = event.target as Node | null;
    const palette = this.host.nativeElement.querySelector('.palette');
    if (!palette || (target && palette.contains(target))) return;
    this.inputRef()?.nativeElement.focus();
  }

  /** Moves through the open palette. Cmd/Ctrl+K itself belongs to CommandPaletteService. */
  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (!this.isOpen()) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      this.dismiss();
    } else if (event.key === 'ArrowDown') {
      event.preventDefault();
      this.moveActive(1);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.moveActive(-1);
    } else if (event.key === 'Home' && this.entries().length) {
      event.preventDefault();
      this.activeIndex.set(0);
    } else if (event.key === 'End' && this.entries().length) {
      event.preventDefault();
      this.activeIndex.set(this.entries().length - 1);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      const entry = this.entries()[this.activeIndex()];
      if (entry) this.run(entry);
    }
  }

  open(): void {
    this.palette.open();
  }

  /** Closes after running an entry: focus belongs to wherever it led. */
  close(): void {
    this.previouslyFocused = null;
    this.palette.close();
  }

  /** Closes without acting (Escape, the backdrop): focus goes back. */
  dismiss(): void {
    const previous = this.previouslyFocused;
    this.close();
    if (previous?.isConnected) previous.focus();
  }

  onQueryChange(value: string): void {
    this.query.set(value);
    this.activeIndex.set(0);
    // Results stay until the next answer arrives, so typing does not make the
    // Books and Notes sections blink; an emptied query clears them at once.
    if (!value.trim()) this.server.set(NO_SERVER_RESULTS);
    this.searchSubject.next(value);
  }

  moveActive(delta: number): void {
    const count = this.entries().length;
    if (count === 0) return;
    this.activeIndex.set((this.activeIndex() + delta + count) % count);
  }

  /** Pointer movement, not entry: a list that reflows under a still pointer must not steal the highlight. */
  onPointerMove(index: number): void {
    if (this.activeIndex() !== index) this.activeIndex.set(index);
  }

  optionId(index: number): string {
    return `palette-option-${index}`;
  }

  run(entry: PaletteEntry): void {
    this.close();
    switch (entry.kind) {
      case 'book':
        this.remember(entry);
        this.router.navigate(['/library', entry.id]);
        return;
      case 'topic':
        this.remember(entry);
        this.router.navigate(['/brain/topics', entry.id]);
        return;
      case 'note':
        this.router.navigate(['/brain/notes', entry.id]);
        return;
      case 'document':
        this.remember(entry);
        this.router.navigate(['/studio'], { queryParams: { writingId: entry.id } });
        return;
      case 'collection':
        this.filters.collectionId.set(entry.id);
        this.router.navigate(['/library']);
        return;
      case 'ask':
        this.assistant.updateDraft(this.parsed().term);
        this.assistant.requestSurfaceOpen();
        return;
      case 'action':
        this.runAction(entry.id);
        return;
    }
  }

  private runAction(id: string): void {
    switch (id) {
      case 'go-library':
        this.router.navigate(['/library']);
        return;
      case 'go-brain':
        this.router.navigate(['/brain']);
        return;
      case 'go-studio':
        this.router.navigate(['/studio']);
        return;
      case 'go-settings':
        this.router.navigate(['/settings']);
        return;
      case 'add-book':
        this.router.navigate(['/library'], { queryParams: { add: 'book' } });
        return;
      case 'new-document':
        this.writingsService.create({ name: 'Untitled document', type: 'Document', parentId: null }).subscribe({
          next: (doc) => this.router.navigate(['/studio'], { queryParams: { writingId: doc.id } }),
          error: () => this.toast.error('Could not create a document'),
        });
        return;
      case 'capture':
        this.assistant.requestSurfaceOpen();
        return;
      case 'import-highlights':
        this.highlightImport.open();
        return;
      case 'toggle-theme':
        this.theme.toggle();
        return;
    }
  }

  private remember(entry: PaletteEntry): void {
    if (entry.kind === 'book' || entry.kind === 'topic' || entry.kind === 'document') {
      this.recent.record({ kind: entry.kind, id: entry.id, label: entry.label, sub: entry.sub });
    }
  }

  private onOpened(): void {
    const active = document.activeElement;
    this.previouslyFocused = active instanceof HTMLElement && active !== document.body ? active : null;
    this.query.set('');
    this.server.set(NO_SERVER_RESULTS);
    this.activeIndex.set(0);
    // The input only exists after the @if renders; focus it on the next
    // macrotask so typing starts in the palette, not the page behind it.
    setTimeout(() => this.inputRef()?.nativeElement.focus(), 0);

    // Small lists are fetched fresh on every open, so a topic or document made
    // since the last open is findable; each failure only empties its section.
    this.loadList(this.collectionsService.list(), this.collections);
    this.loadList(this.topicsService.list(), this.topics, (topics) =>
      this.pruneRecent('topic', new Set(topics.map((t) => t.id))),
    );
    this.loadList(
      this.writingsService.list().pipe(map((all) => all.filter((w) => w.type === 'Document'))),
      this.documents,
      (docs) => this.pruneRecent('document', new Set(docs.map((d) => d.id))),
    );
  }

  /**
   * Fetches a short list into its section. A failure only empties that
   * section; `onLoaded` runs on success alone, because an empty list from a
   * failed request says nothing about what exists.
   */
  private loadList<T>(
    source: Observable<T[]>,
    target: { set(value: T[]): void },
    onLoaded?: (items: T[]) => void,
  ): void {
    source.subscribe({
      next: (items) => {
        target.set(items ?? []);
        onLoaded?.(items ?? []);
      },
      error: () => target.set([]),
    });
  }

  /** Drops recent topics or documents that have since been deleted. */
  private pruneRecent(kind: 'topic' | 'document', existing: Set<string>): void {
    for (const item of this.recent.items()) {
      if (item.kind === kind && !existing.has(item.id)) this.recent.forget(kind, item.id);
    }
  }

  private emptySections(): PaletteSection[] {
    return [
      { id: 'recent', title: 'Recent', entries: this.recent.items().slice(0, 5).map(recentEntry) },
      { id: 'actions', title: 'Quick actions', entries: this.actions() },
      { id: 'go', title: 'Go to', entries: this.destinations },
    ];
  }

  /** A prefixed query: one kind of thing, listed in full when nothing follows the prefix. */
  private scopedSections(scope: PaletteScope, q: string): PaletteSection[] {
    switch (scope) {
      case 'actions':
        return [
          {
            id: 'actions',
            title: 'Actions',
            entries: [...this.actions(), ...this.destinations].filter(
              (a) => !q || `${a.label} ${a.sub} ${a.keywords ?? ''}`.toLowerCase().includes(q),
            ),
          },
        ];
      case 'topics': {
        const topics = this.topics()
          .filter((t) => !q || t.name.toLowerCase().includes(q))
          .sort((a, b) => b.usageCount - a.usageCount)
          .slice(0, LIMITS.scoped);
        return [{ id: 'topics', title: 'Topics', entries: topics.map(topicEntry) }];
      }
      case 'books':
        return q
          ? [{ id: 'books', title: 'Books', entries: this.server().books.slice(0, LIMITS.books).map(bookEntry) }]
          : [
              {
                id: 'recent-books',
                title: 'Recent books',
                entries: this.recent.items().filter((i) => i.kind === 'book').map(recentEntry),
              },
            ];
      default:
        return [];
    }
  }

  private searchSections(q: string): PaletteSection[] {
    const { books, notes } = this.server();
    const sections: PaletteSection[] = [
      {
        id: 'books',
        title: 'Books',
        entries: books.slice(0, LIMITS.books).map(bookEntry),
      },
      {
        id: 'topics',
        title: 'Topics',
        entries: this.topics()
          .filter((t) => t.name.toLowerCase().includes(q))
          .slice(0, LIMITS.topics)
          .map(topicEntry),
      },
      {
        id: 'notes',
        title: 'Notes',
        entries: notes.slice(0, LIMITS.notes).map((n) => ({
          kind: 'note' as const,
          id: n.id,
          label: noteExcerpt(n, q),
          sub: n.bookTitle ?? 'Note',
          icon: 'note' as const,
        })),
      },
      {
        id: 'documents',
        title: 'Documents',
        entries: this.documents()
          .filter((d) => d.name.toLowerCase().includes(q))
          .slice(0, LIMITS.documents)
          .map((d) => ({ kind: 'document' as const, id: d.id, label: d.name, sub: 'Document', icon: 'file-text' as const })),
      },
      {
        id: 'collections',
        title: 'Collections',
        entries: this.collections()
          .filter((c) => c.name.toLowerCase().includes(q))
          .slice(0, LIMITS.collections)
          .map((c) => ({ kind: 'collection' as const, id: c.id, label: c.name, sub: 'Collection', icon: 'folder' as const })),
      },
      {
        id: 'actions',
        title: 'Actions',
        entries: [...this.actions(), ...this.destinations].filter((a) =>
          `${a.label} ${a.sub} ${a.keywords ?? ''}`.toLowerCase().includes(q),
        ),
      },
    ];
    if (this.launcher.available()) {
      sections.push({
        id: 'ask',
        title: 'Ask Nostos',
        entries: [{ kind: 'ask', id: 'ask', label: this.parsed().term, sub: 'Ask Nostos', icon: 'sparkle' }],
      });
    }
    return sections;
  }
}

function bookEntry(b: Book): PaletteEntry {
  // A 22px-wide chip: the smallest thumbnail, not the full-size cover.
  return { kind: 'book', id: b.id, label: b.title, sub: b.author || 'Unknown author', icon: 'book', coverUrl: coverImageUrl(b, 120) };
}

function topicEntry(t: TopicDto): PaletteEntry {
  return { kind: 'topic', id: t.id, label: t.name, sub: notesLabel(t.usageCount), icon: 'hash' };
}

function recentEntry(item: RecentItem): PaletteEntry {
  const icon: NostosIconName = item.kind === 'book' ? 'book' : item.kind === 'topic' ? 'hash' : 'file-text';
  return { kind: item.kind, id: item.id, label: item.label, sub: item.sub, icon };
}

const EXCERPT_MAX = 120;
/** How much of the note to keep before the match when the match sits deep in it. */
const EXCERPT_LEAD = 32;

/**
 * One readable line of a note: the server's snippet, else its opening words.
 * When the matched word sits beyond the first line's worth of text, the line
 * starts shortly before it, so the row shows why the note matched.
 */
export function noteExcerpt(hit: NoteSearchHit, term = ''): string {
  let text = (hit.snippet || hit.content || hit.selectedText || '')
    // Topic links at the end are tags, not prose: drop them. Links inside a
    // sentence are words in it: keep the word, lose the brackets.
    .replace(/(\s*\[\[[^\]]+\]\])+\s*$/, '')
    .replace(/\[\[([^\]]+)\]\]/g, '$1')
    .replace(/\s+/g, ' ')
    .trim();
  if (!text) return 'Untitled note';

  const at = term ? text.toLowerCase().indexOf(term.toLowerCase()) : -1;
  if (at > EXCERPT_LEAD + 16) {
    // Start at a word boundary a little before the match.
    const from = text.lastIndexOf(' ', at - EXCERPT_LEAD) + 1;
    text = `…${text.slice(from)}`;
  }
  return text.length > EXCERPT_MAX ? `${text.slice(0, EXCERPT_MAX - 3)}…` : text;
}
