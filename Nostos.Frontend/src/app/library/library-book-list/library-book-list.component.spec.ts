import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { LibraryBookList } from './library-book-list.component';
import { ImportService } from '../../core/services/import.service';
import type { Book } from '../../core/dtos/book.dtos';

@Component({
  standalone: true,
  imports: [LibraryBookList],
  template: `
    <app-library-book-list
      class="table-view"
      [books]="books()"
      [selecting]="selecting()"
      [selected]="selected()"
      [searchQuery]="search()"
      [hasFilters]="hasFilters()"
      (favoriteToggled)="favorites.push($event)"
      (finishedToggled)="finished.push($event)"
      (ratingChanged)="ratings.push($event)"
      (editRequested)="edits.push($event)"
      (deleteRequested)="deletes.push($event)"
      (bookPressed)="presses.push($event)"
      (selectionChanged)="selections.push($event)"
    />
  `,
})
class ListHost {
  readonly books = signal<Book[]>([]);
  readonly selecting = signal(false);
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly search = signal('');
  readonly hasFilters = signal(false);
  readonly favorites: Book[] = [];
  readonly finished: Book[] = [];
  readonly ratings: { book: Book; rating: number }[] = [];
  readonly edits: Book[] = [];
  readonly deletes: Book[] = [];
  readonly presses: Book[] = [];
  readonly selections: { id: string; on: boolean }[] = [];
}

function book(overrides: Partial<Book> = {}): Book {
  return {
    id: 'b1',
    title: 'Dune',
    subtitle: 'A Subtitle',
    author: 'Frank Herbert',
    type: 'ebook',
    coverUrl: '/api/books/b1/cover',
    rating: 0,
    isFavorite: true,
    finishedAt: null,
    progressPercent: 0,
    createdAt: '2026-01-01T00:00:00Z',
    otherEditions: [],
    status: 0,
    statusMessage: null,
    ...overrides,
  } as unknown as Book;
}

describe('LibraryBookList', () => {
  let fixture: ComponentFixture<ListHost>;
  let host: ListHost;
  const el = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLElement | null;
  const all = (selector: string) => Array.from(fixture.nativeElement.querySelectorAll(selector)) as HTMLElement[];

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [ListHost],
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
    fixture = TestBed.createComponent(ListHost);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('is the table itself, with its column headers', () => {
    expect(el('app-library-book-list')!.classList.contains('table-view')).toBe(true);
    const headers = all('.table-header > div').map((cell) => cell.textContent?.trim());
    expect(headers).toEqual(['Title', 'Author', 'Rating', 'Format', 'Added']);
  });

  it('orders the title cell as cover, title, then the status toggles', () => {
    host.books.set([book()]);
    fixture.detectChanges();

    const cell = el('.col.title')!;
    const ordered = Array.from(cell.children)
      .filter((child) => !child.classList.contains('select-check'))
      .map((child) =>
        child.classList.contains('list-cover-frame') ? 'list-cover-frame' : child.className.split(' ')[0],
      );
    expect(ordered).toEqual(['list-cover-frame', 'list-title-text', 'list-row-status']);
    expect(cell.querySelector('nostos-book-cover.list-cover-frame.nostos-book-cover--sm')).toBeTruthy();
    expect(cell.querySelectorAll('.list-row-status .fav-btn-list, .list-row-status .finished-btn-list')).toHaveLength(2);
  });

  it('writes the list format as plain text, with the status cluster as a sibling', () => {
    host.books.set([book({ type: 'audiobook' })]);
    fixture.detectChanges();

    const row = el('.table-row')!;
    const format = row.querySelector('.col.format .list-format') as HTMLElement;
    expect(format.textContent?.trim()).toBe('Audiobook');
    expect(row.querySelector('.format-badge')).toBeNull();
    expect(row.querySelector('.list-row-status')!.parentElement).toBe(row.querySelector('.col.title'));
  });

  it('leaves an unrated cell empty and shows the stars when rated', () => {
    host.books.set([book({ rating: 0 })]);
    fixture.detectChanges();
    expect(el('.table-row .col.rating app-star-rating')).toBeNull();

    host.books.set([book({ rating: 3 })]);
    fixture.detectChanges();
    expect(el('.table-row .col.rating app-star-rating')).not.toBeNull();
  });

  it('draws the row progress inside the row it belongs to', () => {
    host.books.set([book({ status: 1 })]);
    fixture.detectChanges();

    const bar = el('.table-row .list-progress')!;
    expect(bar.getAttribute('role')).toBe('progressbar');
    expect(bar.querySelector('.list-progress-caption')?.textContent).toContain('Downloading');
  });

  it('reports a tap instead of acting on the book itself', () => {
    host.books.set([book()]);
    fixture.detectChanges();

    (el('.fav-btn-list') as HTMLButtonElement).click();
    (el('.finished-btn-list') as HTMLButtonElement).click();
    (el('.actions-cell .edit') as HTMLButtonElement).click();
    (el('.actions-cell .delete') as HTMLButtonElement).click();
    el('.table-row')!.click();

    expect(host.favorites.map((b) => b.id)).toEqual(['b1']);
    expect(host.finished.map((b) => b.id)).toEqual(['b1']);
    expect(host.edits.map((b) => b.id)).toEqual(['b1']);
    expect(host.deletes.map((b) => b.id)).toEqual(['b1']);
    expect(host.presses.map((b) => b.id)).toEqual(['b1']);
  });

  it('ticks a book in Select mode instead of linking it', () => {
    host.books.set([book()]);
    host.selecting.set(true);
    fixture.detectChanges();

    expect(el('.list-book-title')!.getAttribute('href')).toBeNull();

    const checkbox = el('.select-check') as HTMLInputElement;
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event('change'));

    expect(host.selections).toEqual([{ id: 'b1', on: true }]);
  });
});
