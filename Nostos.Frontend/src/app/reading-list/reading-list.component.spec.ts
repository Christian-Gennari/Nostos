import { Component, computed, input, output, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';

import { AddBookModal, AddBookPrefill } from '../add-book-modal/add-book-modal.component';
import { Collection } from '../core/dtos/collection.dtos';
import { ReadingListEntry, SaveReadingListEntry } from '../core/dtos/reading-list.dtos';
import { BookLookupError, BooksService } from '../core/services/books.service';
import { CollectionsService } from '../core/services/collections.service';
import { ProvidersService } from '../core/services/providers.service';
import { ReadingListService } from '../core/services/reading-list.service';
import { ToastService } from '../core/services/toast.service';
import { ReadingList } from './reading-list.component';

/** Stands in for the real sheet: this suite is about the page around it. */
@Component({ selector: 'app-add-book-modal', standalone: true, template: '' })
class StubAddBookModal {
  isOpen = input.required<boolean>();
  collections = input.required<Collection[]>();
  sourceFirst = input(false);
  prefill = input<AddBookPrefill | null>(null);
  closeModal = output<void>();
  bookCreated = output<string>();
  importQueued = output<string>();
}

function entry(overrides: Partial<ReadingListEntry> & { id: string }): ReadingListEntry {
  return {
    title: overrides.id,
    author: null,
    isbn: null,
    publishedDate: null,
    why: null,
    recommendedBy: null,
    nextPosition: null,
    createdAt: '2026-10-01T00:00:00Z',
    ...overrides,
  };
}

/** The real service's shape with the server replaced by an in-memory list. */
class FakeReadingListService {
  readonly state = signal<ReadingListEntry[]>([]);
  readonly loadedState = signal(false);
  readonly entries = this.state.asReadonly();
  readonly loaded = this.loadedState.asReadonly();
  readonly next = computed(() => this.state().filter((e) => e.nextPosition !== null));
  readonly someday = computed(() => this.state().filter((e) => e.nextPosition === null));
  readonly count = computed(() => this.state().length);

  seed: ReadingListEntry[] = [];
  loadFails = false;

  load = vi.fn((): Observable<ReadingListEntry[]> => {
    if (this.loadFails) return throwError(() => new Error('offline'));
    this.state.set(this.seed);
    this.loadedState.set(true);
    return of(this.seed);
  });

  add = vi.fn((dto: SaveReadingListEntry): Observable<ReadingListEntry> => {
    const created = entry({ id: `new-${this.state().length}`, ...dto, title: dto.title });
    this.state.set([...this.next(), created, ...this.someday()]);
    return of(created);
  });

  update = vi.fn((id: string, dto: SaveReadingListEntry): Observable<ReadingListEntry> => {
    const current = this.state().find((e) => e.id === id)!;
    const updated = { ...current, ...dto } as ReadingListEntry;
    this.state.update((all) => all.map((e) => (e.id === id ? updated : e)));
    return of(updated);
  });

  remove = vi.fn((id: string): Observable<void> => {
    this.state.update((all) => all.filter((e) => e.id !== id));
    return of(undefined);
  });

  setNext = vi.fn((ids: string[]): Observable<ReadingListEntry[]> => {
    const byId = new Map(this.state().map((e) => [e.id, e]));
    const shelf = ids.map((id, slot) => ({ ...byId.get(id)!, nextPosition: slot }));
    const rest = this.state()
      .filter((e) => !ids.includes(e.id))
      .map((e) => ({ ...e, nextPosition: null }));
    this.state.set([...shelf, ...rest]);
    return of(this.state());
  });

  markAcquired = vi.fn((id: string, _bookId: string): Observable<void> => {
    this.state.update((all) => all.filter((e) => e.id !== id));
    return of(undefined);
  });
}

describe('ReadingList', () => {
  let fixture: ComponentFixture<ReadingList>;
  let component: ReadingList;
  let list: FakeReadingListService;
  let books: { lookup: ReturnType<typeof vi.fn> };
  let toast: {
    error: ReturnType<typeof vi.fn>;
    info: ReturnType<typeof vi.fn>;
    success: ReturnType<typeof vi.fn>;
  };

  const host = (): HTMLElement => fixture.nativeElement as HTMLElement;
  const text = (): string => host().textContent ?? '';
  const button = (root: Element, label: string): HTMLButtonElement =>
    Array.from(root.querySelectorAll('button')).find(
      (candidate) => candidate.textContent?.trim() === label,
    ) as HTMLButtonElement;

  /** Opens the row's "More" menu and returns the item with that label. */
  function menuItem(root: Element, label: string): HTMLButtonElement {
    (root.querySelector('button[title="More"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    return Array.from(document.querySelectorAll<HTMLButtonElement>('.nostos-menu-item')).find(
      (item) => item.textContent?.trim() === label,
    ) as HTMLButtonElement;
  }

  async function create(seed: ReadingListEntry[] = []): Promise<void> {
    list.seed = seed;
    fixture = TestBed.createComponent(ReadingList);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function typeAndAdd(title: string, author = ''): Promise<void> {
    component.draftTitle = title;
    component.draftAuthor = author;
    component.add();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(async () => {
    list = new FakeReadingListService();
    books = { lookup: vi.fn() };
    toast = { error: vi.fn(), info: vi.fn(), success: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [ReadingList],
      providers: [
        provideRouter([]),
        { provide: ReadingListService, useValue: list },
        { provide: BooksService, useValue: books },
        { provide: CollectionsService, useValue: { list: vi.fn(() => of([])) } },
        { provide: ProvidersService, useValue: { job: vi.fn() } },
        { provide: ToastService, useValue: toast },
      ],
    })
      .overrideComponent(ReadingList, {
        remove: { imports: [AddBookModal] },
        add: { imports: [StubAddBookModal] },
      })
      .compileComponents();
  });

  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove()));

  it('says what the page is for when the list is empty', async () => {
    await create();

    expect(text()).toContain('Nothing here yet. Add the first book you want to read.');
    expect(host().querySelector('.shelf')).toBeNull();
  });

  it('adds a book from a title alone and clears the field for the next one', async () => {
    await create();

    await typeAndAdd('  Middlemarch  ');

    expect(list.add).toHaveBeenCalledWith({ title: 'Middlemarch', author: null });
    expect(component.draftTitle).toBe('');
    expect(text()).toContain('Middlemarch');
    expect(text()).toContain('1 waiting, newest first');
  });

  it('sends the author when one was typed', async () => {
    await create();

    await typeAndAdd('Stoner', 'John Williams');

    expect(list.add).toHaveBeenCalledWith({ title: 'Stoner', author: 'John Williams' });
  });

  it('does not add a blank title', async () => {
    await create();

    await typeAndAdd('   ');

    expect(list.add).not.toHaveBeenCalled();
  });

  it('looks a typed ISBN up so the entry carries the real title', async () => {
    books.lookup.mockReturnValue(
      of({ title: 'Stoner', author: 'John Williams', publishedDate: '1965' }),
    );
    await create();

    await typeAndAdd('978-1-59017-199-8');

    expect(books.lookup).toHaveBeenCalledWith('9781590171998');
    expect(list.add).toHaveBeenCalledWith({
      title: 'Stoner',
      author: 'John Williams',
      isbn: '9781590171998',
      publishedDate: '1965',
    });
  });

  it('keeps what was typed when an ISBN finds nothing', async () => {
    books.lookup.mockReturnValue(throwError(() => new Error('not found')));
    await create();

    await typeAndAdd('9781590171998');

    expect(list.add).not.toHaveBeenCalled();
    expect(component.draftTitle).toBe('9781590171998');
    expect(toast.error).toHaveBeenCalled();
  });

  it('recognises an ISBN with a label, en dashes or spaces', async () => {
    books.lookup.mockReturnValue(of({ title: 'Stoner', author: 'John Williams' }));
    await create();

    await typeAndAdd('ISBN-13: 978–1–59017‑199 8');

    expect(books.lookup).toHaveBeenCalledWith('9781590171998');
    expect(list.add).toHaveBeenCalledWith(expect.objectContaining({ title: 'Stoner' }));
  });

  it('looks an ISBN up even with an author typed, and keeps that author', async () => {
    books.lookup.mockReturnValue(of({ title: 'Stoner', author: 'J. Williams' }));
    await create();

    await typeAndAdd('9781590171998', 'John Williams');

    expect(books.lookup).toHaveBeenCalledWith('9781590171998');
    expect(list.add).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Stoner', author: 'John Williams', isbn: '9781590171998' }),
    );
  });

  it('never saves the ISBN as the title when the lookup has no title', async () => {
    books.lookup.mockReturnValue(of({ title: '  ', author: 'Someone' }));
    await create();

    await typeAndAdd('9781590171998');

    expect(list.add).not.toHaveBeenCalled();
    expect(component.draftTitle).toBe('9781590171998');
    expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('No book was found'));
  });

  it('says the lookup is down rather than that nothing was found', async () => {
    books.lookup.mockReturnValue(throwError(() => new BookLookupError('unavailable', 503)));
    await create();

    await typeAndAdd('9781590171998');

    expect(list.add).not.toHaveBeenCalled();
    expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('not answering'));
  });

  it('shows Next up in shelf order and Someday below it', async () => {
    await create([
      entry({ id: 'first', title: 'First', nextPosition: 0, why: 'Winter book.' }),
      entry({ id: 'second', title: 'Second', nextPosition: 1, recommendedBy: 'Anna' }),
      entry({ id: 'later', title: 'Later' }),
    ]);

    const cards = Array.from(host().querySelectorAll('.shelf-card'));
    expect(cards.map((card) => card.querySelector('.entry-title')?.textContent)).toEqual([
      'First',
      'Second',
    ]);
    expect(cards[0].textContent).toContain('Reading next');
    expect(cards[0].textContent).toContain('Winter book.');
    expect(cards[1].textContent).toContain('Then');
    expect(cards[1].textContent).toContain('Recommended by Anna');

    const rows = Array.from(host().querySelectorAll('.someday-row'));
    expect(rows.map((row) => row.querySelector('.entry-title')?.textContent)).toEqual(['Later']);
  });

  it('moves a book onto the end of the shelf and back off it', async () => {
    await create([entry({ id: 'a', nextPosition: 0 }), entry({ id: 'b' })]);

    button(host().querySelector('.someday-row')!, 'Next up').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(list.setNext).toHaveBeenLastCalledWith(['a', 'b']);
    expect(host().querySelectorAll('.shelf-card').length).toBe(2);

    button(host().querySelectorAll('.shelf-card')[0], 'Someday').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(list.setNext).toHaveBeenLastCalledWith(['b']);
    expect(host().querySelectorAll('.shelf-card').length).toBe(1);
  });

  it('reorders the shelf one place at a time and stops at the ends', async () => {
    await create([
      entry({ id: 'a', nextPosition: 0 }),
      entry({ id: 'b', nextPosition: 1 }),
      entry({ id: 'c', nextPosition: 2 }),
    ]);

    component.move(list.next()[2], -1);
    expect(list.setNext).toHaveBeenLastCalledWith(['a', 'c', 'b']);

    list.setNext.mockClear();
    component.move(list.next()[0], -1);
    component.move(list.next()[2], 1);
    expect(list.setNext).not.toHaveBeenCalled();

    const cards = host().querySelectorAll('.shelf-card');
    const earlier = cards[0].querySelector('button[title="Earlier"]') as HTMLButtonElement;
    const later = cards[cards.length - 1].querySelector('button[title="Later"]') as HTMLButtonElement;
    expect(earlier.disabled).toBe(true);
    expect(later.disabled).toBe(true);
  });

  it('offers no order to change while the shelf holds one book', async () => {
    await create([entry({ id: 'only', nextPosition: 0 }), entry({ id: 'later' })]);

    const card = host().querySelector('.shelf-card')!;
    expect(card.querySelector('button[title="Earlier"]')).toBeNull();
    expect(card.querySelector('button[title="Later"]')).toBeNull();
    expect(text()).toContain("The one you'll read next");
    expect(text()).not.toContain("in the order you'll read them");
  });

  it('shows a wish with the shared cover placeholder, beside its own title', async () => {
    await create([entry({ id: 'a', title: 'Stoner', nextPosition: 0 })]);

    const cover = host().querySelector('.shelf-card nostos-book-cover.cover') as HTMLElement;
    expect(cover).toBeTruthy();
    expect(cover.classList.contains('nostos-book-cover--lg')).toBe(true);
    expect(cover.querySelector('.nostos-book-cover__placeholder')!.getAttribute('aria-hidden')).toBe('true');
  });

  it('asks before removing, and removes only on confirm', async () => {
    await create([entry({ id: 'a', title: 'Pedro Páramo' })]);

    menuItem(host().querySelector('.someday-row')!, 'Remove from list').click();
    fixture.detectChanges();

    expect(list.remove).not.toHaveBeenCalled();
    expect(component.removeHeading()).toBe('Remove “Pedro Páramo” from your reading list?');

    component.confirmRemove();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(list.remove).toHaveBeenCalledWith('a');
    expect(component.removeTarget()).toBeNull();
    expect(toast.success).toHaveBeenCalledWith('Removed “Pedro Páramo” from your reading list');
  });

  it('keeps the edit dialog open and the Save busy until the server answers', async () => {
    await create([entry({ id: 'a', title: 'Stoner' })]);
    const answer = new Subject<ReadingListEntry>();
    list.update.mockReturnValueOnce(answer.asObservable());

    component.openEdit(list.entries()[0]);
    fixture.detectChanges();
    component.saveEdit();
    fixture.detectChanges();

    const save = document.querySelector('button[type="submit"][form="reading-list-edit-form"]') as HTMLButtonElement;
    expect(save.getAttribute('aria-busy')).toBe('true');
    expect(save.textContent).toContain('Saving…');
    component.closeEdit();
    expect(component.editTarget()).not.toBeNull();

    answer.next(entry({ id: "a", title: "Stoner" }));
    answer.complete();
    expect(component.editTarget()).toBeNull();
  });

  it('saves a reason and a recommender without touching what the entry already knows', async () => {
    await create([entry({ id: 'a', title: 'Stoner', isbn: '9781590171998', publishedDate: '1965' })]);

    component.openEdit(list.entries()[0]);
    component.editForm.why = '  Slow and plain.  ';
    component.editForm.recommendedBy = 'Anna';
    component.saveEdit();
    await fixture.whenStable();

    expect(list.update).toHaveBeenCalledWith('a', {
      title: 'Stoner',
      author: null,
      isbn: '9781590171998',
      publishedDate: '1965',
      why: 'Slow and plain.',
      recommendedBy: 'Anna',
    });
    expect(component.editTarget()).toBeNull();
  });

  it('opens Add Book knowing the book, and the entry leaves once a library book exists', async () => {
    await create([entry({ id: 'a', title: 'Middlemarch', author: 'George Eliot' })]);

    // In Someday the row shows one action; "I got it" is in its menu.
    expect(button(host().querySelector('.someday-row')!, 'I got it')).toBeUndefined();
    menuItem(host().querySelector('.someday-row')!, 'I got it').click();
    fixture.detectChanges();

    expect(component.acquirePrefill()).toEqual({
      title: 'Middlemarch',
      author: 'George Eliot',
      isbn: null,
      publishedDate: null,
    });
    expect(list.markAcquired).not.toHaveBeenCalled();

    component.onBookCreated('book-1');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(list.markAcquired).toHaveBeenCalledWith('a', 'book-1');
    expect(list.entries()).toEqual([]);
    expect(toast.success).toHaveBeenCalledWith('“Middlemarch” is in your library');
  });

  it('keeps the entry when Add Book is closed without making a book', async () => {
    await create([entry({ id: 'a', title: 'Middlemarch' })]);

    component.gotIt(list.entries()[0]);
    component.closeAcquire();
    fixture.detectChanges();

    expect(list.markAcquired).not.toHaveBeenCalled();
    expect(list.entries().length).toBe(1);
  });

  it('keeps a shelf card to its two actions and puts edit and remove in its menu', async () => {
    await create([entry({ id: 'a', title: 'Middlemarch', nextPosition: 0 })]);

    const card = host().querySelector('.shelf-card')!;
    const footer = Array.from(card.querySelectorAll('.shelf-card__foot button[appButton]'));
    expect(footer.map((b) => b.textContent?.trim())).toEqual(['I got it', 'Someday']);

    menuItem(card, 'Edit').click();
    fixture.detectChanges();
    expect(component.editTarget()?.id).toBe('a');
  });

  it('offers to take a book off the list when the library already has it', async () => {
    await create([entry({ id: 'a', title: 'Middlemarch', ownedBookId: 'book-9' })]);

    const row = host().querySelector('.someday-row')!;
    expect(row.textContent).toContain("It's in your library");
    expect(menuItem(row, 'I got it')).toBeUndefined();

    button(row, 'Take off the list').click();
    await fixture.whenStable();

    expect(list.markAcquired).toHaveBeenCalledWith('a', 'book-9');
  });

  it('says so and offers a retry when the list cannot be loaded', async () => {
    list.loadFails = true;
    await create();

    expect(text()).toContain('The reading list could not be loaded.');

    list.loadFails = false;
    list.seed = [entry({ id: 'a', title: 'Back again' })];
    button(host(), 'Try again').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text()).toContain('Back again');
  });
});
