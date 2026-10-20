import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { of, throwError } from 'rxjs';

import { CommandPalette, highlightParts, noteExcerpt, parseQuery } from './command-palette.component';
import { CommandPaletteService } from './command-palette.service';
import { RecentItemsService } from './recent-items.service';
import { BooksService } from '../../core/services/books.service';
import { CollectionsService } from '../../core/services/collections.service';
import { HighlightImportService } from '../../core/services/highlight-import.service';
import { NotesService } from '../../core/services/notes.service';
import { TopicsService } from '../../core/services/topics.service';
import { WritingsService } from '../../core/services/writings.service';
import { ThemeService } from '../../core/services/theme.service';
import { LibraryFilterService } from '../../library/library-filter.service';
import { LibraryPreferencesService } from '../../core/services/library-preferences.service';
import { AssistantService } from '../assistant/assistant.service';
import { AssistantStatusService } from '../assistant/assistant-status.service';

describe('CommandPalette', () => {
  let fixture: ComponentFixture<CommandPalette>;
  let component: CommandPalette;
  let router: Router;

  const booksList = vi.fn();
  const notesSearch = vi.fn();
  const collectionsList = vi.fn();
  const topicsList = vi.fn();
  const writingsList = vi.fn();
  const writingsCreate = vi.fn();
  const openHighlightImport = vi.fn();

  beforeEach(async () => {
    vi.useFakeTimers();
    localStorage.clear();
    for (const mock of [booksList, notesSearch, collectionsList, topicsList, writingsList, writingsCreate, openHighlightImport]) {
      mock.mockReset();
    }
    booksList.mockReturnValue(of({ items: [], totalCount: 0 }));
    notesSearch.mockReturnValue(of([]));
    collectionsList.mockReturnValue(
      of([
        { id: 'c1', name: 'Classics', parentId: null },
        { id: 'c2', name: 'Sci-Fi', parentId: null },
      ]),
    );
    topicsList.mockReturnValue(
      of([
        { id: 't1', name: 'Stoicism', usageCount: 3 },
        { id: 't2', name: 'Attention', usageCount: 1 },
      ]),
    );
    writingsList.mockReturnValue(
      of([
        { id: 'w1', name: 'Essay on attention', type: 'Document', updatedAt: '' },
        { id: 'f1', name: 'Attention folder', type: 'Folder', updatedAt: '' },
      ]),
    );

    await TestBed.configureTestingModule({
      imports: [CommandPalette],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: BooksService, useValue: { list: booksList, get: vi.fn() } },
        { provide: NotesService, useValue: { search: notesSearch } },
        { provide: CollectionsService, useValue: { list: collectionsList } },
        { provide: TopicsService, useValue: { list: topicsList, get: vi.fn() } },
        { provide: WritingsService, useValue: { list: writingsList, create: writingsCreate, get: vi.fn() } },
        { provide: HighlightImportService, useValue: { open: openHighlightImport } },
      ],
    }).compileComponents();

    // The app root starts the shortcut; the deferred component does not own it.
    TestBed.inject(CommandPaletteService).start();
    fixture = TestBed.createComponent(CommandPalette);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigate').mockResolvedValue(true as never);
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    localStorage.clear();
  });

  function openPalette(): void {
    component.open();
    fixture.detectChanges();
  }

  function type(query: string): void {
    component.onQueryChange(query);
    vi.advanceTimersByTime(250);
    fixture.detectChanges();
  }

  function enableAssistant(): void {
    TestBed.inject(LibraryPreferencesService).assistantEnabled.set(true);
    TestBed.inject(AssistantStatusService).available.set(true);
  }

  const titles = () => component.sections().map((s) => s.title);
  const entry = (kind: string) => component.entries().find((e) => e.kind === kind)!;

  it('toggles on Cmd/Ctrl+K, with or without Shift', () => {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', metaKey: true }));
    expect(component.isOpen()).toBe(true);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', metaKey: true }));
    expect(component.isOpen()).toBe(false);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'K', ctrlKey: true, shiftKey: true }));
    expect(component.isOpen()).toBe(true);
  });

  it('opens through the service, the way phone controls do, and loads its lists', () => {
    TestBed.inject(CommandPaletteService).open();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="palette-input"]')).toBeTruthy();
    expect(collectionsList).toHaveBeenCalledTimes(1);
    expect(topicsList).toHaveBeenCalledTimes(1);
    expect(writingsList).toHaveBeenCalledTimes(1);
  });

  it('dismisses on Escape and returns focus to where it was', () => {
    const before = document.createElement('button');
    document.body.appendChild(before);
    before.focus();

    openPalette();
    vi.advanceTimersByTime(1);
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('[data-testid="palette-input"]'));

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(component.isOpen()).toBe(false);
    expect(document.activeElement).toBe(before);
    before.remove();
  });

  it('keeps focus while open, even when the page moves it', () => {
    const elsewhere = document.createElement('button');
    document.body.appendChild(elsewhere);
    openPalette();
    vi.advanceTimersByTime(1);
    const input = fixture.nativeElement.querySelector('[data-testid="palette-input"]');

    elsewhere.focus();
    expect(document.activeElement).toBe(input);

    // Closed, the page may move focus freely again.
    component.close();
    fixture.detectChanges();
    elsewhere.focus();
    expect(document.activeElement).toBe(elsewhere);
    elsewhere.remove();
  });

  describe('with nothing typed', () => {
    it('offers quick actions and destinations, without a Recent section yet', () => {
      openPalette();
      expect(titles()).toEqual(['Quick actions', 'Go to']);
      expect(component.sections()[0].entries.map((e) => e.id)).toEqual([
        'add-book',
        'new-document',
        'import-highlights',
        'toggle-theme',
      ]);
    });

    it('adds Capture a thought only while Ask Nostos is on', () => {
      enableAssistant();
      openPalette();
      expect(component.sections()[0].entries.map((e) => e.id)).toContain('capture');
    });

    it('leads with the places opened most recently', () => {
      TestBed.inject(RecentItemsService).record({ kind: 'topic', id: 't1', label: 'Stoicism', sub: '3 notes' });
      openPalette();

      expect(titles()).toEqual(['Recent', 'Quick actions', 'Go to']);
      expect(component.sections()[0].entries[0]).toMatchObject({ kind: 'topic', label: 'Stoicism' });
    });
  });

  describe('searching', () => {
    beforeEach(() => {
      booksList.mockReturnValue(
        of({ items: [{ id: 'b1', title: 'Attention Merchants', author: 'Tim Wu', coverUrl: '/api/books/b1/cover', coverVersion: 'v1' }], totalCount: 1 }),
      );
      notesSearch.mockReturnValue(
        of([
          {
            id: 'n1',
            bookId: 'b2',
            bookTitle: 'Dune',
            content: 'Fear is the mind-killer. [[Attention]]',
            selectedText: null,
            snippet: null,
            topicNames: ['Attention'],
            createdAt: '',
          },
        ]),
      );
    });

    it('groups results into sections in a fixed order', () => {
      enableAssistant();
      openPalette();
      type('attention');

      expect(titles()).toEqual(['Books', 'Topics', 'Notes', 'Documents', 'Ask Nostos']);
      expect(booksList).toHaveBeenCalledWith({ search: 'attention', page: 1, pageSize: 5 });
      expect(notesSearch).toHaveBeenCalledWith('attention', 4);
      // Topics and documents are matched here, by name; folders are not documents.
      expect(entry('topic')).toMatchObject({ id: 't2', sub: '1 note' });
      expect(component.entries().filter((e) => e.kind === 'document').map((e) => e.id)).toEqual(['w1']);
      expect(entry('note').label).toBe('Fear is the mind-killer.');
      expect(entry('book').coverUrl).toBe('/api/books/b1/cover/thumbnail?width=120&v=v1');
    });

    it('shows a book cover in place of the glyph', () => {
      openPalette();
      type('attention');
      const book = fixture.nativeElement.querySelector('[data-testid="palette-item-book"] img');
      expect(book?.getAttribute('src')).toBe('/api/books/b1/cover/thumbnail?width=120&v=v1');
    });

    it('matches actions by their keywords as well as their label', () => {
      openPalette();
      type('kobo');
      const actions = component.sections().find((s) => s.id === 'actions')!;
      expect(actions.entries.map((e) => e.id)).toEqual(['import-highlights']);
    });

    it('says nothing matched, with no Ask row while Ask Nostos is off', () => {
      booksList.mockReturnValue(of({ items: [], totalCount: 0 }));
      notesSearch.mockReturnValue(of([]));
      openPalette();
      type('zzzz');

      expect(component.entries()).toEqual([]);
      expect(fixture.nativeElement.querySelector('.palette-empty').textContent).toContain('Nothing matches');
    });

    it('keeps a section when the other search fails', () => {
      notesSearch.mockReturnValue(throwError(() => new Error('notes search failed')));
      booksList.mockReturnValue(of({ items: [{ id: 'b1', title: 'Attention', author: null }], totalCount: 1 }));
      openPalette();
      type('attention');
      expect(titles()).toContain('Books');
    });
  });

  describe('with a scope prefix', () => {
    it('> lists only actions, matched by the words after it', () => {
      enableAssistant();
      openPalette();
      type('>');
      expect(titles()).toEqual(['Actions']);
      expect(component.entries().length).toBe(9);

      type('> theme');
      expect(component.entries().map((e) => e.id)).toEqual(['toggle-theme']);
      // Scoped searches never become a question.
      expect(component.entries().some((e) => e.kind === 'ask')).toBe(false);
      expect(booksList).not.toHaveBeenCalled();
      expect(notesSearch).not.toHaveBeenCalled();
    });

    it('# lists topics, the most used first, without asking the server', () => {
      openPalette();
      type('#');
      expect(component.entries().map((e) => e.label)).toEqual(['Stoicism', 'Attention']);

      type('#att');
      expect(component.entries().map((e) => e.label)).toEqual(['Attention']);
      expect(notesSearch).not.toHaveBeenCalled();
      expect(booksList).not.toHaveBeenCalled();
    });

    it('@ searches books only, and lists recent books before anything is typed', () => {
      TestBed.inject(RecentItemsService).record({ kind: 'book', id: 'b7', label: 'Dune', sub: 'Frank Herbert' });
      TestBed.inject(RecentItemsService).record({ kind: 'topic', id: 't1', label: 'Stoicism', sub: '3 notes' });
      openPalette();
      type('@');
      expect(titles()).toEqual(['Recent books']);
      expect(component.entries().map((e) => e.id)).toEqual(['b7']);

      booksList.mockReturnValue(of({ items: [{ id: 'b1', title: 'Dune Messiah', author: 'Frank Herbert' }], totalCount: 1 }));
      type('@dune');
      expect(booksList).toHaveBeenCalledWith({ search: 'dune', page: 1, pageSize: 5 });
      expect(notesSearch).not.toHaveBeenCalled();
      expect(titles()).toEqual(['Books']);
    });

    it('says which kind it looked for when nothing matches', () => {
      openPalette();
      type('#zzz');
      expect(fixture.nativeElement.querySelector('.palette-empty').textContent.trim()).toBe('No topics match “zzz”.');
    });
  });

  it('marks the matched words in each label', () => {
    openPalette();
    type('stoic');
    const marks = Array.from<HTMLElement>(fixture.nativeElement.querySelectorAll('[data-testid="palette-item-topic"] mark'));
    expect(marks.map((m) => m.textContent)).toEqual(['Stoic']);
    expect(fixture.nativeElement.querySelector('[data-testid="palette-item-topic"] .palette-item-label').textContent).toBe(
      'Stoicism',
    );
  });

  describe('Recent upkeep', () => {
    it('forgets topics and documents that no longer exist', () => {
      const recent = TestBed.inject(RecentItemsService);
      recent.record({ kind: 'topic', id: 'gone', label: 'Deleted topic', sub: '' });
      recent.record({ kind: 'document', id: 'w1', label: 'Essay on attention', sub: 'Document' });
      recent.record({ kind: 'book', id: 'b1', label: 'Kept', sub: '' });

      openPalette();
      expect(recent.items().map((i) => i.id)).toEqual(['b1', 'w1']);
    });

    it('forgets nothing when a list fails to load', () => {
      topicsList.mockReturnValue(throwError(() => new Error('offline')));
      const recent = TestBed.inject(RecentItemsService);
      recent.record({ kind: 'topic', id: 't1', label: 'Stoicism', sub: '' });

      openPalette();
      expect(recent.items().map((i) => i.id)).toEqual(['t1']);
    });
  });

  describe('running an entry', () => {
    beforeEach(() => {
      enableAssistant();
      booksList.mockReturnValue(of({ items: [{ id: 'b1', title: 'Attention', author: 'A' }], totalCount: 1 }));
      notesSearch.mockReturnValue(
        of([{ id: 'n1', bookId: 'b1', bookTitle: 'A', content: 'x', selectedText: null, snippet: 'attention', topicNames: [], createdAt: '' }]),
      );
      openPalette();
      type('attention');
    });

    it('opens a book and remembers it', () => {
      component.run(entry('book'));
      expect(router.navigate).toHaveBeenCalledWith(['/library', 'b1']);
      expect(TestBed.inject(RecentItemsService).items()[0]).toMatchObject({ kind: 'book', id: 'b1' });
      expect(component.isOpen()).toBe(false);
    });

    it('deep-links a topic, a note and a document', () => {
      component.run(entry('topic'));
      expect(router.navigate).toHaveBeenCalledWith(['/brain/topics', 't2']);

      component.run(entry('note'));
      expect(router.navigate).toHaveBeenCalledWith(['/brain/notes', 'n1']);

      component.run(entry('document'));
      expect(router.navigate).toHaveBeenCalledWith(['/studio'], { queryParams: { writingId: 'w1' } });
    });

    it('turns the query into an Ask Nostos draft', () => {
      const assistant = TestBed.inject(AssistantService);
      const surface = vi.spyOn(assistant, 'requestSurfaceOpen');

      component.run(entry('ask'));

      expect(assistant.draft()).toBe('attention');
      expect(surface).toHaveBeenCalledTimes(1);
      expect(router.navigate).not.toHaveBeenCalled();
    });

    it('sends Add a book to the Library as an intent', () => {
      type('add');
      component.run(component.entries().find((e) => e.id === 'add-book')!);
      expect(router.navigate).toHaveBeenCalledWith(['/library'], { queryParams: { add: 'book' } });
    });

    it('creates an untitled document and opens it, without a name prompt', () => {
      writingsCreate.mockReturnValue(of({ id: 'w9', name: 'Untitled document', type: 'Document' }));
      type('new doc');
      component.run(component.entries().find((e) => e.id === 'new-document')!);

      expect(writingsCreate).toHaveBeenCalledWith({ name: 'Untitled document', type: 'Document', parentId: null });
      expect(router.navigate).toHaveBeenCalledWith(['/studio'], { queryParams: { writingId: 'w9' } });
    });

    it('switches the theme in place', () => {
      const theme = TestBed.inject(ThemeService);
      theme.setChoice('light');
      type('dark');
      const toggle = component.entries().find((e) => e.id === 'toggle-theme')!;
      // The label names the theme it switches TO.
      expect(toggle.label).toBe('Switch to dark theme');
      component.run(toggle);
      expect(theme.active()).toBe('dark');
      theme.setChoice('system');
    });

    it('switches to the person\'s own night theme by name (#1074)', () => {
      const theme = TestBed.inject(ThemeService);
      theme.setNightTheme('study');
      theme.setChoice('sepia');
      type('theme');
      const toggle = component.entries().find((e) => e.id === 'toggle-theme')!;
      expect(toggle.label).toBe("Switch to Writer's Study theme");
      component.run(toggle);
      expect(theme.active()).toBe('study');
      theme.setNightTheme('dark');
      theme.setChoice('system');
    });

    it('opens the highlight import dialog in place', () => {
      type('kobo');
      component.run(entry('action'));
      expect(openHighlightImport).toHaveBeenCalledTimes(1);
      expect(router.navigate).not.toHaveBeenCalled();
    });

    it('filters the Library by a collection', () => {
      type('sci');
      component.run(entry('collection'));
      expect(router.navigate).toHaveBeenCalledWith(['/library']);
      expect(TestBed.inject(LibraryFilterService).collectionId()).toBe('c2');
    });
  });

  describe('as a combobox', () => {
    it('points the input at the highlighted option as the arrows move it', () => {
      openPalette();
      const input = fixture.nativeElement.querySelector('[data-testid="palette-input"]') as HTMLInputElement;

      expect(input.getAttribute('role')).toBe('combobox');
      expect(input.getAttribute('aria-controls')).toBe('palette-listbox');
      expect(input.getAttribute('aria-activedescendant')).toBe('palette-option-0');

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown' }));
      fixture.detectChanges();
      expect(input.getAttribute('aria-activedescendant')).toBe('palette-option-1');
      const active = fixture.nativeElement.querySelector('#palette-option-1');
      expect(active.getAttribute('aria-selected')).toBe('true');

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'End' }));
      fixture.detectChanges();
      expect(component.activeIndex()).toBe(component.entries().length - 1);

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown' }));
      expect(component.activeIndex()).toBe(0);
    });

    it('labels each section group by its heading', () => {
      openPalette();
      const group = fixture.nativeElement.querySelector('[role="group"]') as HTMLElement;
      const heading = fixture.nativeElement.querySelector(`#${group.getAttribute('aria-labelledby')}`);
      expect(heading.textContent.trim()).toBe('Quick actions');
    });

    it('runs the highlighted option on Enter', () => {
      openPalette();
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'End' }));
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
      expect(router.navigate).toHaveBeenCalledWith(['/settings']);
    });
  });
});

describe('parseQuery', () => {
  it('reads a scope prefix and the words after it', () => {
    expect(parseQuery('> theme')).toEqual({ scope: 'actions', term: 'theme' });
    expect(parseQuery('#sto')).toEqual({ scope: 'topics', term: 'sto' });
    expect(parseQuery('  @ dune ')).toEqual({ scope: 'books', term: 'dune' });
    expect(parseQuery('#')).toEqual({ scope: 'topics', term: '' });
  });

  it('treats anything else as a search across everything', () => {
    expect(parseQuery(' rome ')).toEqual({ scope: 'all', term: 'rome' });
    expect(parseQuery('c#')).toEqual({ scope: 'all', term: 'c#' });
  });
});

describe('highlightParts', () => {
  it('marks every case-insensitive occurrence and keeps the original casing', () => {
    expect(highlightParts('Rome and rome', 'ROME')).toEqual([
      { text: 'Rome', hit: true },
      { text: ' and ', hit: false },
      { text: 'rome', hit: true },
    ]);
  });

  it('leaves a label whole when there is no term or no match', () => {
    expect(highlightParts('Dune', '')).toEqual([{ text: 'Dune', hit: false }]);
    expect(highlightParts('Dune', 'x')).toEqual([{ text: 'Dune', hit: false }]);
  });
});

describe('noteExcerpt', () => {
  const hit = (over: object) =>
    ({ id: 'n', bookId: 'b', bookTitle: null, content: '', selectedText: null, snippet: null, topicNames: [], createdAt: '', ...over }) as never;

  it('prefers the server snippet, unwraps inline topic links and collapses whitespace', () => {
    expect(noteExcerpt(hit({ snippet: 'a  [[Stoicism]]\nb', content: 'ignored' }))).toBe('a Stoicism b');
  });

  it('drops the trailing topic tags', () => {
    expect(noteExcerpt(hit({ content: 'What stands in the way becomes the way. [[Stoicism]] [[Obstacles]]' }))).toBe(
      'What stands in the way becomes the way.',
    );
  });

  it('caps a long note at one line', () => {
    const text = 'x'.repeat(200);
    expect(noteExcerpt(hit({ content: text })).length).toBe(118);
  });

  it('never returns an empty label', () => {
    expect(noteExcerpt(hit({}))).toBe('Untitled note');
  });

  it('starts shortly before a match that sits deep in the note', () => {
    const content = `${'Opening words that carry no match at all, '.repeat(4)}then the lantern appears here.`;
    const line = noteExcerpt(hit({ content }), 'lantern');
    expect(line.startsWith('…')).toBe(true);
    expect(line).toContain('lantern');
    expect(line.length).toBeLessThanOrEqual(120);
  });

  it('keeps the start when the match is already in view', () => {
    expect(noteExcerpt(hit({ content: 'The lantern by the door.' }), 'lantern')).toBe('The lantern by the door.');
  });
});
