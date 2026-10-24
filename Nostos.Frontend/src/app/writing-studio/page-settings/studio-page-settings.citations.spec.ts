import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import { Book } from '../../core/dtos/book.dtos';
import { BooksService } from '../../core/services/books.service';
import { ToastService } from '../../core/services/toast.service';
import { CitationService } from '../citations/citation.service';
import { StudioPageSettingsComponent } from './studio-page-settings.component';
import { StudioPreferencesService } from './studio-preferences.service';

const BOOKS: Record<string, Partial<Book>> = {
  b1: { id: 'b1', title: 'Candide', author: 'Voltaire', publisher: null, publishedDate: null },
  b2: { id: 'b2', title: 'Gravity and Grace', author: 'Simone Weil', publisher: 'Routledge', publishedDate: '1947' },
  b3: { id: 'b3', title: 'Anonymous Tract', author: null, publisher: 'Dover', publishedDate: '1801' },
};

/** Missing details, filled in from the Citations list (issue #1157). */
describe('StudioPageSettingsComponent: missing citation details (#1157)', () => {
  let fixture: ComponentFixture<StudioPageSettingsComponent>;
  let prefs: StudioPreferencesService;
  let update: ReturnType<typeof vi.fn>;
  let toastError: ReturnType<typeof vi.fn>;
  let updatedBooks: Book[];

  async function open(focusBook: string | null = 'b1', cited = ['b1', 'b2', 'b3']): Promise<void> {
    fixture = TestBed.createComponent(StudioPageSettingsComponent);
    updatedBooks = [];
    fixture.componentInstance.bookUpdated.subscribe((book) => updatedBooks.push(book));
    fixture.componentRef.setInput('startView', 'citations');
    fixture.componentRef.setInput('focusBook', focusBook);
    fixture.componentRef.setInput('citedBookIds', cited);
    fixture.detectChanges();
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(root().querySelector('[data-testid="ps-citation-sources"]')).not.toBeNull();
    });
  }

  /** The panel renders in the overlay (the shared settings popover), not in the host. */
  function root(): HTMLElement {
    return document.querySelector('[data-testid="page-settings"]') as HTMLElement;
  }

  function sources(): { book: string | null; title: string; fields: string[] }[] {
    return [...root().querySelectorAll('.ps-cite-source')].map((form) => ({
      book: form.getAttribute('data-book'),
      title: form.querySelector('.ps-cite-source-title')!.textContent!.trim(),
      fields: [...form.querySelectorAll('input')].map((input) => input.name),
    }));
  }

  function type(book: string, field: string, value: string): void {
    const input = root().querySelector(`.ps-cite-source[data-book="${book}"] input[name="${field}"]`) as HTMLInputElement;
    input.value = value;
  }

  function submit(book: string): void {
    (root().querySelector(`.ps-cite-source[data-book="${book}"]`) as HTMLFormElement).requestSubmit();
    fixture.detectChanges();
  }

  beforeEach(async () => {
    localStorage.clear();
    update = vi.fn((id: string, dto: Partial<Book>) => of({ ...BOOKS[id], ...dto } as Book));
    toastError = vi.fn();
    await TestBed.configureTestingModule({
      imports: [StudioPageSettingsComponent],
      providers: [
        {
          provide: BooksService,
          useValue: {
            get: vi.fn((id: string) => (BOOKS[id] ? of(BOOKS[id] as Book) : throwError(() => new Error('gone')))),
            list: vi.fn(() => of({ items: [] })),
            update,
          },
        },
        { provide: ToastService, useValue: { error: toastError, show: vi.fn(), info: vi.fn(), success: vi.fn() } },
      ],
    }).compileComponents();
    prefs = TestBed.inject(StudioPreferencesService);
    prefs.reset();
    prefs.update({ citationStyle: 'apa' });
  });

  afterEach(() => fixture?.destroy());

  it('opens on the Citations list with the named book first, asking only for what the style lacks', async () => {
    await open('b3');

    expect(root().querySelector('.ps-subhead-title')?.textContent).toContain('Citation style');
    expect(sources()).toEqual([
      { book: 'b3', title: 'Anonymous Tract', fields: ['author'] },
      { book: 'b1', title: 'Candide', fields: ['year', 'publisher'] },
    ]);
    await vi.waitFor(() => expect(document.activeElement).toBe(root().querySelector('.ps-cite-source[data-book="b3"] input')));
  });

  it('asks only for the author in the house format', async () => {
    prefs.update({ citationStyle: 'nostos' });
    await open();
    expect(sources()).toEqual([{ book: 'b3', title: 'Anonymous Tract', fields: ['author'] }]);
  });

  it('saves the details typed, and every citation of the book follows', async () => {
    const citations = TestBed.inject(CitationService);
    const revision = citations.revision();
    await open();

    type('b1', 'year', '1759');
    type('b1', 'publisher', 'Cramer');
    submit('b1');

    await vi.waitFor(() => expect(updatedBooks.length).toBe(1));
    expect(update).toHaveBeenCalledWith('b1', { publishedDate: '1759', publisher: 'Cramer' });
    expect(citations.revision()).toBeGreaterThan(revision);
    expect((await citations.book('b1'))?.publishedDate).toBe('1759');
    fixture.detectChanges();
    expect(sources().map((s) => s.book)).toEqual(['b3']);
  });

  it('saves only what was filled in, and says when a year cannot be read', async () => {
    await open();

    type('b1', 'year', 'last spring');
    submit('b1');
    expect(update).not.toHaveBeenCalled();
    expect(root().querySelector('.ps-cite-error')?.textContent).toContain('four digits');

    type('b1', 'year', '');
    type('b1', 'publisher', 'Cramer');
    submit('b1');
    await vi.waitFor(() => expect(update).toHaveBeenCalledWith('b1', { publisher: 'Cramer' }));
  });

  it('says so when the details cannot be saved', async () => {
    update.mockReturnValueOnce(throwError(() => new Error('offline')));
    await open();

    type('b3', 'author', 'A. Nonymous');
    submit('b3');
    await vi.waitFor(() => expect(toastError).toHaveBeenCalledWith('Could not save the book’s details'));
    expect(updatedBooks).toEqual([]);
  });
});
