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
} from '@angular/core';
import { Router } from '@angular/router';
import { Observable, Subject, catchError, debounceTime, forkJoin, map, of, switchMap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { BooksService } from '../../core/services/books.service';
import { CollectionsService } from '../../core/services/collections.service';
import { HighlightImportService } from '../../core/services/highlight-import.service';
import { NotesService } from '../../core/services/notes.service';
import { ThemeService } from '../../core/services/theme.service';
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

export type PaletteEntryKind =
  | 'book'
  | 'topic'
  | 'note'
  | 'document'
  | 'collection'
  | 'action'
  | 'ask';

export interface PaletteEntry {
  kind: PaletteEntryKind;
  id: string;
  label: string;
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

const LIMITS = { books: 5, topics: 4, notes: 4, documents: 4, collections: 4 };

interface ServerResults {
  books: Book[];
  notes: NoteSearchHit[];
}

const NO_SERVER_RESULTS: ServerResults = { books: [], notes: [] };

/**
 * Global command palette (Cmd/Ctrl+K, or the search-everything controls on a
 * phone).
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

  readonly isOpen = this.palette.isOpen;
  readonly query = signal('');
  readonly searching = signal(false);
  readonly activeIndex = signal(0);
  readonly shortcutLabel = this.launcher.shortcutLabel.replace('J', 'K');

  private readonly inputRef = viewChild<ElementRef<HTMLInputElement>>('paletteInput');
  private readonly resultsRef = viewChild<ElementRef<HTMLElement>>('results');

  private readonly server = signal<ServerResults>(NO_SERVER_RESULTS);
  private readonly collections = signal<Collection[]>([]);
  private readonly topics = signal<TopicDto[]>([]);
  private readonly documents = signal<WritingDto[]>([]);
  private readonly searchSubject = new Subject<string>();
  private previouslyFocused: HTMLElement | null = null;

  private readonly actions = computed<PaletteEntry[]>(() => {
    const dark = this.theme.theme() === 'dark';
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
        label: dark ? 'Switch to light theme' : 'Switch to dark theme',
        sub: 'Appearance',
        icon: dark ? 'sun' : 'moon',
        keywords: 'theme dark light mode appearance',
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

  readonly sections = computed<IndexedSection[]>(() => {
    const q = this.query().trim().toLowerCase();
    const raw = q ? this.searchSections(q) : this.emptySections();
    let start = 0;
    return raw
      .filter((section) => section.entries.length > 0)
      .map((section) => {
        const indexed = { ...section, start };
        start += section.entries.length;
        return indexed;
      });
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
          const q = raw.trim();
          if (!q) {
            this.searching.set(false);
            return of(NO_SERVER_RESULTS);
          }
          this.searching.set(true);
          return forkJoin({
            books: this.booksService
              .list({ search: q, page: 1, pageSize: LIMITS.books })
              .pipe(map((res) => res.items ?? []), catchError(() => of([] as Book[]))),
            notes: this.notesService
              .search(q, LIMITS.notes)
              .pipe(catchError(() => of([] as NoteSearchHit[]))),
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
        this.router.navigate(['/second-brain'], { queryParams: { topicId: entry.id } });
        return;
      case 'note':
        this.router.navigate(['/second-brain'], { queryParams: { noteId: entry.id } });
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
        this.assistant.updateDraft(this.query().trim());
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
        this.router.navigate(['/second-brain']);
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
    this.loadList(this.topicsService.list(), this.topics);
    this.loadList(
      this.writingsService.list().pipe(map((all) => all.filter((w) => w.type === 'Document'))),
      this.documents,
    );
  }

  private loadList<T>(source: Observable<T[]>, target: { set(value: T[]): void }): void {
    source.pipe(catchError(() => of([] as T[]))).subscribe((items) => target.set(items ?? []));
  }

  private emptySections(): PaletteSection[] {
    return [
      { id: 'recent', title: 'Recent', entries: this.recent.items().slice(0, 5).map(recentEntry) },
      { id: 'actions', title: 'Quick actions', entries: this.actions() },
      { id: 'go', title: 'Go to', entries: this.destinations },
    ];
  }

  private searchSections(q: string): PaletteSection[] {
    const { books, notes } = this.server();
    const sections: PaletteSection[] = [
      {
        id: 'books',
        title: 'Books',
        entries: books.slice(0, LIMITS.books).map((b) => ({
          kind: 'book' as const,
          id: b.id,
          label: b.title,
          sub: b.author || 'Unknown author',
          icon: 'book' as const,
          coverUrl: b.coverUrl,
        })),
      },
      {
        id: 'topics',
        title: 'Topics',
        entries: this.topics()
          .filter((t) => t.name.toLowerCase().includes(q))
          .slice(0, LIMITS.topics)
          .map((t) => ({ kind: 'topic' as const, id: t.id, label: t.name, sub: notesLabel(t.usageCount), icon: 'hash' as const })),
      },
      {
        id: 'notes',
        title: 'Notes',
        entries: notes.slice(0, LIMITS.notes).map((n) => ({
          kind: 'note' as const,
          id: n.id,
          label: noteExcerpt(n),
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
        entries: [{ kind: 'ask', id: 'ask', label: this.query().trim(), sub: 'Ask Nostos', icon: 'sparkle' }],
      });
    }
    return sections;
  }
}

function recentEntry(item: RecentItem): PaletteEntry {
  const icon: NostosIconName = item.kind === 'book' ? 'book' : item.kind === 'topic' ? 'hash' : 'file-text';
  return { kind: item.kind, id: item.id, label: item.label, sub: item.sub, icon };
}

/** One readable line of a note: the server's snippet, else its opening words. */
export function noteExcerpt(hit: NoteSearchHit): string {
  const text = (hit.snippet || hit.content || hit.selectedText || '')
    // Topic links at the end are tags, not prose: drop them. Links inside a
    // sentence are words in it: keep the word, lose the brackets.
    .replace(/(\s*\[\[[^\]]+\]\])+\s*$/, '')
    .replace(/\[\[([^\]]+)\]\]/g, '$1')
    .replace(/\s+/g, ' ')
    .trim();
  return text.length > 120 ? `${text.slice(0, 117)}…` : text || 'Untitled note';
}
