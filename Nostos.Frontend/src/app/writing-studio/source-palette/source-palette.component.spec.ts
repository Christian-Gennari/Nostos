import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { BooksService } from '../../core/services/books.service';
import { NotesService } from '../../core/services/notes.service';
import { SourcePaletteComponent } from './source-palette.component';
import { PaletteChoice } from './source-palette.helpers';

const hits = [
  {
    id: 'q',
    bookId: 'b',
    bookTitle: 'The World as Will and Representation',
    bookAuthor: 'Arthur Schopenhauer',
    content: 'Will and body.',
    selectedText: 'Every true act of his will is a movement of his body.',
    snippet: null,
    topicNames: [],
    createdAt: '',
  },
  {
    id: 'p',
    bookId: 'b2',
    bookTitle: 'Studies in Pessimism',
    bookAuthor: 'Arthur Schopenhauer',
    content: 'Kept to reread.',
    selectedText: 'This vanity finds expression…',
    snippet: null,
    topicNames: [],
    createdAt: '',
    kind: 'passage' as const,
    passageTitle: 'On the Vanity of Existence',
  },
];

describe('SourcePaletteComponent (issue #1039)', () => {
  let fixture: ComponentFixture<SourcePaletteComponent>;
  let component: SourcePaletteComponent;
  let search: ReturnType<typeof vi.fn>;
  let chosen: PaletteChoice[];

  beforeEach(async () => {
    vi.useFakeTimers();
    localStorage.clear();
    search = vi.fn((query: string) => of(query.includes('schopenhauer') ? hits : []));
    await TestBed.configureTestingModule({
      imports: [SourcePaletteComponent],
      providers: [
        { provide: NotesService, useValue: { search } },
        {
          provide: BooksService,
          useValue: { list: vi.fn(() => of({ items: [{ title: 'Parerga', author: 'Arthur Schopenhauer' }] })) },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(SourcePaletteComponent);
    component = fixture.componentInstance;
    chosen = [];
    component.choose.subscribe((c) => chosen.push(c));
  });

  afterEach(() => vi.useRealTimers());

  async function typeQuery(query: string | null) {
    fixture.componentRef.setInput('query', query);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(200);
    fixture.detectChanges();
  }

  const key = (key: string, init: KeyboardEventInit = {}) => new KeyboardEvent('keydown', { key, ...init });

  it('is closed until an @ query opens', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="source-palette"]')).toBeNull();
    expect(component.handleKey(key('Enter'))).toBe(false);
  });

  it('searches as the query is typed and lists the hits', async () => {
    await typeQuery('schopenhauer will');
    expect(search).toHaveBeenCalledWith('schopenhauer will', 20);
    const rows = fixture.nativeElement.querySelectorAll('.sp-row');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Every true act');
    expect(rows[1].textContent).toContain('Passage');
  });

  it('retries a query that matched nothing with the nearest author spelling', async () => {
    await typeQuery('schopenhaur');
    expect(search).toHaveBeenCalledWith('schopenhaur', 20);
    expect(search).toHaveBeenCalledWith('schopenhauer', 20);
    expect(component.items().length).toBe(2);
    expect(component.heading()).toContain('schopenhauer');
  });

  it('chooses with Enter as a quote, Shift+Enter as a note, Ctrl+Enter as a reference', async () => {
    await typeQuery('schopenhauer');
    expect(component.handleKey(key('Enter'))).toBe(true);
    expect(component.handleKey(key('Enter', { shiftKey: true }))).toBe(true);
    expect(component.handleKey(key('Enter', { ctrlKey: true }))).toBe(true);
    expect(chosen.map((c) => c.mode)).toEqual(['quote', 'note', 'reference']);
  });

  it('moves with the arrow keys and wraps', async () => {
    await typeQuery('schopenhauer');
    component.handleKey(key('ArrowDown'));
    expect(component.active()).toBe(1);
    component.handleKey(key('ArrowDown'));
    expect(component.active()).toBe(0);
    component.handleKey(key('ArrowUp'));
    component.handleKey(key('Enter'));
    expect(chosen[0].source.id).toBe('p');
  });

  it('offers the kept sources for an empty query, without searching', async () => {
    fixture.componentRef.setInput('keptSources', [
      { id: 'k', bookId: 'b', bookTitle: 'Book', content: 'Kept', selectedText: 'Kept quote', createdAt: '', addedAt: '' },
    ]);
    await typeQuery('');
    expect(search).not.toHaveBeenCalled();
    expect(component.items().map((i) => i.id)).toEqual(['k']);
    expect(component.heading()).toContain('Kept with this writing');
  });

  it('says so when nothing matches, and offers the Reference rail', async () => {
    let browsed = '';
    component.browse.subscribe((q) => (browsed = q));
    await typeQuery('zzz unknown');
    expect(fixture.nativeElement.textContent).toContain('No sources match “zzz unknown”');
    (fixture.nativeElement.querySelector('.sp-browse') as HTMLButtonElement).click();
    expect(browsed).toBe('zzz unknown');
  });

  it('inserts a note with no quotation as a note even on Enter', async () => {
    search.mockReturnValue(of([{ ...hits[0], id: 'thought', selectedText: null }]));
    await typeQuery('schopenhauer');
    component.handleKey(key('Enter'));
    expect(chosen[0].mode).toBe('note');
  });
});
