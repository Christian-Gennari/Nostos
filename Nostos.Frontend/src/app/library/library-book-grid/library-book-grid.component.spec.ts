import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { LibraryBookGrid } from './library-book-grid.component';
import { ImportService } from '../../core/services/import.service';
import type { Book } from '../../core/dtos/book.dtos';

@Component({
  standalone: true,
  imports: [LibraryBookGrid],
  template: `
    <app-library-book-grid
      class="book-grid"
      [books]="books()"
      [selecting]="selecting()"
      [selected]="selected()"
      [searchQuery]="search()"
      [hasFilters]="hasFilters()"
      (favoriteToggled)="favorites.push($event)"
      (finishedToggled)="finished.push($event)"
      (editRequested)="edits.push($event)"
      (deleteRequested)="deletes.push($event)"
      (bookPressed)="presses.push($event)"
      (selectionChanged)="selections.push($event)"
      (hoverStarted)="hovers.push($event)"
      (clearSearch)="clearedSearch.push(true)"
      (addBook)="adds.push(true)"
    />
  `,
})
class GridHost {
  readonly books = signal<Book[]>([]);
  readonly selecting = signal(false);
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly search = signal('');
  readonly hasFilters = signal(false);
  readonly favorites: Book[] = [];
  readonly finished: Book[] = [];
  readonly edits: Book[] = [];
  readonly deletes: Book[] = [];
  readonly presses: Book[] = [];
  readonly selections: { id: string; on: boolean }[] = [];
  readonly hovers: Book[] = [];
  readonly clearedSearch: boolean[] = [];
  readonly adds: boolean[] = [];
}

function book(overrides: Partial<Book> = {}): Book {
  return {
    id: 'b1',
    title: 'Dune',
    subtitle: null,
    author: 'Frank Herbert',
    type: 'ebook',
    coverUrl: '/api/books/b1/cover',
    rating: 4,
    isFavorite: false,
    finishedAt: null,
    progressPercent: 0,
    createdAt: '2026-01-01T00:00:00Z',
    otherEditions: [],
    status: 0,
    statusMessage: null,
    ...overrides,
  } as unknown as Book;
}

describe('LibraryBookGrid', () => {
  let fixture: ComponentFixture<GridHost>;
  let host: GridHost;
  const el = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLElement | null;
  const all = (selector: string) => Array.from(fixture.nativeElement.querySelectorAll(selector)) as HTMLElement[];

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [GridHost],
      providers: [
        provideRouter([]),
        {
          provide: ImportService,
          useValue: {
            progressByBookId: () => new Map(),
            retry: vi.fn(),
            dismiss: vi.fn(),
            imports: () => [],
            inFlightBookIds: () => [],
            bookPatched: of(null),
          },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(GridHost);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('is the grid container itself, so the shelf styles target the host', () => {
    const grid = el('app-library-book-grid')!;
    expect(grid.classList.contains('book-grid')).toBe(true);
  });

  it('draws a card as cover, title and author only: no reading state on the shelf', () => {
    host.books.set([book({ finishedAt: '2026-02-01T00:00:00Z', isFavorite: true })]);
    fixture.detectChanges();

    const card = el('.book-card')!;
    expect(card.querySelector('app-star-rating')).toBeNull();
    expect(card.querySelector('.meta-status')).toBeNull();
    const author = card.querySelector('.meta-author') as HTMLElement;
    expect(author.querySelector('.meta-author-name')?.textContent?.trim()).toBe('Frank Herbert');

    // The toggles exist for the hover reveal, but the visible surface is title +
    // author: reading state belongs to the book page.
    expect(card.querySelectorAll('.fav-btn-grid, .finished-btn-grid')).toHaveLength(2);
    expect(card.querySelectorAll('.meta-author nostos-icon')).toHaveLength(0);
  });

  it('gives each card one real link named by the title', () => {
    host.books.set([book()]);
    fixture.detectChanges();

    const links = all('.book-card a.card-link');
    expect(links).toHaveLength(1);
    expect(links[0].textContent?.trim()).toBe('Dune');
    expect(links[0].getAttribute('href')).toBe('/library/b1');
  });

  it('draws the multi-format badge with one glyph per format the work is available in', () => {
    host.books.set([
      book({ otherEditions: [{ id: 'b2', type: 'audiobook' }] as unknown as Book[] }),
    ]);
    fixture.detectChanges();

    const badge = el('.format-badge.micro-glyphs')!;
    expect(badge.querySelectorAll('nostos-icon')).toHaveLength(2);
    expect(badge.getAttribute('title')).toBe('Available in Audiobook, Ebook');
  });

  it('names the single format in capitals, with its glyph', () => {
    host.books.set([book()]);
    fixture.detectChanges();

    const badge = el('.format-badge.single')!;
    expect(badge.textContent?.trim()).toBe('EBOOK');
    expect(badge.querySelector('nostos-icon')).not.toBeNull();
  });

  it('reports a tap instead of acting on the book itself', () => {
    host.books.set([book()]);
    fixture.detectChanges();

    const card = el('.book-card')!;
    (card.querySelector('.fav-btn-grid') as HTMLButtonElement).click();
    (card.querySelector('.finished-btn-grid') as HTMLButtonElement).click();
    (card.querySelector('.actions-overlay .action-circle') as HTMLButtonElement).click();
    (card.querySelector('.actions-overlay .delete') as HTMLButtonElement).click();
    card.click();

    expect(host.favorites.map((b) => b.id)).toEqual(['b1']);
    expect(host.finished.map((b) => b.id)).toEqual(['b1']);
    expect(host.edits.map((b) => b.id)).toEqual(['b1']);
    expect(host.deletes.map((b) => b.id)).toEqual(['b1']);
    expect(host.presses.map((b) => b.id)).toEqual(['b1']);
  });

  it('reports hover as intent, so the shelf can warm the hero art', () => {
    host.books.set([book()]);
    fixture.detectChanges();

    el('.book-card')!.dispatchEvent(new Event('mouseenter'));

    expect(host.hovers.map((b) => b.id)).toEqual(['b1']);
  });

  it('ticks a book in Select mode instead of linking it, and says which ticked', () => {
    host.books.set([book()]);
    host.selecting.set(true);
    fixture.detectChanges();

    expect(el('.book-card a.card-link')!.getAttribute('href')).toBeNull();

    const checkbox = el('.select-check') as HTMLInputElement;
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event('change'));

    expect(host.selections).toEqual([{ id: 'b1', on: true }]);
  });

  it('offers no checkbox for a book that is still importing', () => {
    host.books.set([book({ status: 1 })]);
    host.selecting.set(true);
    fixture.detectChanges();

    expect(el('.select-check')).toBeNull();
    expect(el('.book-card')!.getAttribute('draggable')).toBe('false');
  });

  it('explains a failed import on the card, with the feed actions reachable', () => {
    host.books.set([book({ status: 3, statusMessage: 'Disk full.' })]);
    fixture.detectChanges();

    expect(el('.item-failure')!.textContent).toContain('Disk full.');
  });

  it('says which of search, filters or an empty library emptied the shelf', () => {
    host.search.set('zzz');
    fixture.detectChanges();
    expect(el('app-library-empty-state')!.textContent).toContain('No books match "zzz"');

    host.search.set('');
    host.hasFilters.set(true);
    fixture.detectChanges();
    expect(el('app-library-empty-state')!.textContent).toContain('Nothing matches these filters');

    host.hasFilters.set(false);
    fixture.detectChanges();
    expect(el('app-library-empty-state')!.textContent).toContain('Your library is empty');
  });
});
