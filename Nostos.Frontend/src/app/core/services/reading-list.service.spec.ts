import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ReadingListEntry } from '../dtos/reading-list.dtos';
import { ReadingListService } from './reading-list.service';

function entry(id: string, nextPosition: number | null, title = id): ReadingListEntry {
  return {
    id,
    title,
    author: null,
    isbn: null,
    publishedDate: null,
    why: null,
    recommendedBy: null,
    nextPosition,
    createdAt: '2026-10-01T00:00:00Z',
  };
}

describe('ReadingListService', () => {
  let service: ReadingListService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ReadingListService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function load(entries: ReadingListEntry[]): void {
    service.load().subscribe();
    http.expectOne('/api/reading-list').flush(entries);
  }

  it('shows no count until the first load has answered', () => {
    expect(service.loaded()).toBe(false);

    load([entry('a', 0), entry('b', null)]);

    expect(service.loaded()).toBe(true);
    expect(service.count()).toBe(2);
    expect(service.next().map((e) => e.id)).toEqual(['a']);
    expect(service.someday().map((e) => e.id)).toEqual(['b']);
  });

  it('puts a new Someday book first in Someday and a new shelf book last on the shelf', () => {
    load([entry('next-1', 0), entry('someday-1', null)]);

    service.add({ title: 'New someday' }).subscribe();
    http.expectOne({ method: 'POST', url: '/api/reading-list' }).flush(entry('someday-2', null));
    expect(service.entries().map((e) => e.id)).toEqual(['next-1', 'someday-2', 'someday-1']);

    service.add({ title: 'New next', readNext: true }).subscribe();
    const request = http.expectOne({ method: 'POST', url: '/api/reading-list' });
    expect(request.request.body).toEqual({ title: 'New next', readNext: true });
    request.flush(entry('next-2', 1));
    expect(service.entries().map((e) => e.id)).toEqual([
      'next-1',
      'next-2',
      'someday-2',
      'someday-1',
    ]);
  });

  it('replaces the shelf with the server order', () => {
    load([entry('a', 0), entry('b', null)]);

    service.setNext(['b', 'a']).subscribe();
    const request = http.expectOne({ method: 'PUT', url: '/api/reading-list/next' });
    expect(request.request.body).toEqual({ ids: ['b', 'a'] });
    request.flush([entry('b', 0), entry('a', 1)]);

    expect(service.next().map((e) => e.id)).toEqual(['b', 'a']);
    expect(service.someday()).toEqual([]);
  });

  it('updates one entry in place', () => {
    load([entry('a', 0), entry('b', null)]);

    service.update('b', { title: 'Renamed' }).subscribe();
    http
      .expectOne({ method: 'PUT', url: '/api/reading-list/b' })
      .flush(entry('b', null, 'Renamed'));

    expect(service.entries().map((e) => e.title)).toEqual(['a', 'Renamed']);
  });

  it('drops an entry when it is removed or becomes a library book', () => {
    load([entry('a', 0), entry('b', null), entry('c', null)]);

    service.remove('b').subscribe();
    http.expectOne({ method: 'DELETE', url: '/api/reading-list/b' }).flush(null);
    expect(service.entries().map((e) => e.id)).toEqual(['a', 'c']);

    service.markAcquired('a', 'book-1').subscribe();
    const request = http.expectOne({ method: 'POST', url: '/api/reading-list/a/acquired' });
    expect(request.request.body).toEqual({ bookId: 'book-1' });
    request.flush(null);
    expect(service.entries().map((e) => e.id)).toEqual(['c']);
  });

  it('keeps the entry when the server refuses the change', () => {
    load([entry('a', 0)]);

    service.remove('a').subscribe({ error: () => undefined });
    http
      .expectOne({ method: 'DELETE', url: '/api/reading-list/a' })
      .flush(null, { status: 500, statusText: 'Server Error' });

    expect(service.entries().map((e) => e.id)).toEqual(['a']);
  });
});
