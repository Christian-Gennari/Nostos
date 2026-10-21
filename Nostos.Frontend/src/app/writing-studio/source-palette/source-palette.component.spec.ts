import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { Subject } from 'rxjs';

import { BooksService } from '../../core/services/books.service';
import { CloudAuthService } from '../../core/services/cloud-auth.service';
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
        { provide: CloudAuthService, useValue: { sessionChanges$: new Subject() } },
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

  it('waits for the answer to the query on the page before Enter picks', async () => {
    fixture.componentRef.setInput('keptSources', [
      { id: 'k', bookId: 'b', bookTitle: 'Book', content: 'Kept', selectedText: 'Kept quote', createdAt: '', addedAt: '' },
    ]);
    await typeQuery('');
    fixture.componentRef.setInput('query', 'schopenhauer');
    fixture.detectChanges();
    // Still debouncing: the kept list is on screen, but it is not the answer.
    expect(component.handleKey(key('Enter'))).toBe(true);
    expect(chosen).toEqual([]);
    await vi.advanceTimersByTimeAsync(200);
    expect(chosen.map((c) => c.source.id)).toEqual(['q']);
  });

  it('drops an answer that arrives after the query was deleted back to @', async () => {
    const late = new Subject<typeof hits>();
    search.mockReturnValue(late);
    fixture.componentRef.setInput('keptSources', [
      { id: 'k', bookId: 'b', bookTitle: 'Book', content: 'Kept', selectedText: 'Kept quote', createdAt: '', addedAt: '' },
    ]);
    await typeQuery('kant');
    await typeQuery('');
    late.next(hits);
    expect(component.items().map((i) => i.id)).toEqual(['k']);
  });

  it('keeps the highlighted row when the kept list changes under an open query', async () => {
    await typeQuery('schopenhauer');
    component.handleKey(key('ArrowDown'));
    const calls = search.mock.calls.length;
    fixture.componentRef.setInput('keptSources', [
      { id: 'q', bookId: 'b', bookTitle: 'Book', content: '', selectedText: 'x', createdAt: '', addedAt: '' },
    ]);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(200);
    expect(component.active()).toBe(1);
    expect(search.mock.calls.length).toBe(calls);
  });

  it('lets Tab leave the page instead of swallowing it', async () => {
    await typeQuery('schopenhauer');
    expect(component.handleKey(key('Tab'))).toBe(false);
  });

  it('inserts a note with no quotation as a note even on Enter', async () => {
    search.mockReturnValue(of([{ ...hits[0], id: 'thought', selectedText: null }]));
    await typeQuery('schopenhauer');
    component.handleKey(key('Enter'));
    expect(chosen[0].mode).toBe('note');
  });

  describe('a steady palette (issue #1111)', () => {
    const longQuote = 'It is a long quotation. '.repeat(120);

    function results() {
      return [
        { ...hits[0], id: 'short', selectedText: 'Short.' },
        { ...hits[0], id: 'long', selectedText: longQuote, content: 'My own note on it.' },
        hits[1],
      ];
    }

    it('keeps the rows as they are when the highlight moves, and reads the active one in the preview', async () => {
      search.mockReturnValue(of(results()));
      await typeQuery('schopenhauer');
      const rowHtml = () =>
        Array.from(fixture.nativeElement.querySelectorAll('.sp-row-main') as NodeListOf<HTMLElement>).map((r) => r.innerHTML);
      const before = rowHtml();

      component.handleKey(key('ArrowDown'));
      fixture.detectChanges();

      expect(rowHtml()).toEqual(before);
      const preview = fixture.nativeElement.querySelector('[data-testid="source-preview"]') as HTMLElement;
      expect(preview.textContent).toContain(longQuote.trim());
      expect(preview.textContent).toContain('My own note on it.');
      expect(fixture.nativeElement.querySelector('.sp-list .sp-note')).toBeNull();
    });

    it('offers the other insert options in the preview, and ⋯ highlights a row without inserting it', async () => {
      search.mockReturnValue(of(results()));
      await typeQuery('schopenhauer');
      const more = fixture.nativeElement.querySelectorAll('.sp-more')[2] as HTMLButtonElement;
      more.click();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();

      expect(component.active()).toBe(2);
      expect(chosen).toEqual([]);
      const actions = Array.from(
        fixture.nativeElement.querySelectorAll('[data-testid="source-preview"] .sp-more-action') as NodeListOf<HTMLButtonElement>,
      );
      expect(actions.map((a) => a.textContent?.trim())).toEqual(['Insert as note', 'Insert as reference']);
      actions[1].click();
      expect(chosen.map((c) => [c.source.id, c.mode])).toEqual([['p', 'reference']]);
    });

    it('keeps the old results on screen while a new query is answered', async () => {
      search.mockReturnValue(of(results()));
      await typeQuery('schopenhauer');
      const late = new Subject<typeof hits>();
      search.mockReturnValue(late);
      fixture.componentRef.setInput('query', 'schopenhauer will');
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(200);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelectorAll('.sp-row').length).toBe(3);
      expect(fixture.nativeElement.querySelector('.sp-searching')?.textContent).toContain('Searching');
      late.next([hits[0]]);
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.sp-searching')).toBeNull();
    });

    it('decides its place once per open, and again only when the caret changes line', async () => {
      const anchor = { top: 300, bottom: 320, left: 200, right: 210 };
      fixture.componentRef.setInput('anchor', anchor);
      await typeQuery('schopenhauer');
      const first = component.position();

      fixture.componentRef.setInput('anchor', { ...anchor, left: 260, right: 270 });
      search.mockReturnValue(of([]));
      await typeQuery('schopenhauer zzz');
      expect(component.position()).toBe(first);

      fixture.componentRef.setInput('anchor', { top: 340, bottom: 360, left: 40, right: 50 });
      fixture.detectChanges();
      expect(component.position()).not.toBe(first);
      expect(component.position().style['top']).toBe('366px');
    });

    it('gives the palette a fixed height while it is open', async () => {
      fixture.componentRef.setInput('anchor', { top: 100, bottom: 120, left: 200, right: 210 });
      await typeQuery('zzz unknown');
      const palette = fixture.nativeElement.querySelector('[data-testid="source-palette"]') as HTMLElement;
      const emptyHeight = palette.style.height;
      expect(emptyHeight).toMatch(/px$/);

      search.mockReturnValue(of(results()));
      await typeQuery('schopenhauer');
      expect(palette.style.height).toBe(emptyHeight);
    });
  });
});
