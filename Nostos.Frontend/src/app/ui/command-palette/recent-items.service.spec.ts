import { TestBed } from '@angular/core/testing';
import { NavigationEnd, Router } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';

import { RecentItem, RecentItemsService, parseRecentTarget } from './recent-items.service';
import { BooksService } from '../../core/services/books.service';
import { TopicsService } from '../../core/services/topics.service';
import { WritingsService } from '../../core/services/writings.service';

describe('parseRecentTarget', () => {
  it('reads books from the detail page and the reader', () => {
    expect(parseRecentTarget('/library/b1')).toEqual({ kind: 'book', id: 'b1' });
    expect(parseRecentTarget('/read/b1')).toEqual({ kind: 'book', id: 'b1' });
  });

  it('reads topics and documents from their deep-link parameters', () => {
    expect(parseRecentTarget('/brain/topics/t1?with=x')).toEqual({ kind: 'topic', id: 't1' });
    expect(parseRecentTarget('/studio?writingId=w1#top')).toEqual({ kind: 'document', id: 'w1' });
  });

  it('ignores everything else, including the reading list', () => {
    for (const url of ['/library', '/library/to-read', '/brain', '/brain/notes/n1', '/brain/map?focus=t1', '/studio', '/settings']) {
      expect(parseRecentTarget(url)).toBeNull();
    }
  });
});

describe('RecentItemsService', () => {
  const bookGet = vi.fn();
  const topicGet = vi.fn();
  const writingGet = vi.fn();

  function create(): RecentItemsService {
    TestBed.configureTestingModule({
      providers: [
        { provide: BooksService, useValue: { get: bookGet } },
        { provide: TopicsService, useValue: { get: topicGet } },
        { provide: WritingsService, useValue: { get: writingGet } },
      ],
    });
    return TestBed.inject(RecentItemsService);
  }

  const item = (id: string): RecentItem => ({ kind: 'book', id, label: id, sub: 'x' });

  beforeEach(() => {
    localStorage.clear();
    bookGet.mockReset();
    topicGet.mockReset();
    writingGet.mockReset();
  });

  it('keeps the newest first, without duplicates, capped at eight', () => {
    const recent = create();
    for (let i = 0; i < 10; i++) recent.record(item(`b${i}`));
    recent.record(item('b5'));

    expect(recent.items().map((i) => i.id)).toEqual(['b5', 'b9', 'b8', 'b7', 'b6', 'b4', 'b3', 'b2']);
  });

  it('follows a rename in place, and forgets on request', () => {
    const recent = create();
    recent.record(item('b1'));
    recent.record(item('b2'));

    recent.relabel('book', 'b1', 'Renamed');
    expect(recent.items().map((i) => [i.id, i.label])).toEqual([
      ['b2', 'b2'],
      ['b1', 'Renamed'],
    ]);

    recent.forget('book', 'b2');
    expect(recent.items().map((i) => i.id)).toEqual(['b1']);
    expect(JSON.parse(localStorage.getItem('nostos.palette.recent')!)).toHaveLength(1);
  });

  it('survives a reload through localStorage, and ignores a corrupt entry', () => {
    localStorage.setItem(
      'nostos.palette.recent',
      JSON.stringify([item('b1'), { kind: 'spaceship', id: 1 }, null]),
    );
    expect(create().items()).toEqual([item('b1')]);
  });

  it('treats unreadable storage as empty', () => {
    localStorage.setItem('nostos.palette.recent', '{not json');
    expect(create().items()).toEqual([]);
  });

  it('records a visited book under its title, looked up once', () => {
    bookGet.mockReturnValue(of({ id: 'b1', title: 'Dune', author: 'Frank Herbert' }));
    const recent = create();
    const events = new Subject<unknown>();
    recent.watch({ events } as unknown as Router);

    events.next(new NavigationEnd(1, '/library/b1', '/library/b1'));
    events.next(new NavigationEnd(2, '/library', '/library'));
    events.next(new NavigationEnd(3, '/read/b1', '/read/b1'));

    expect(recent.items()).toEqual([{ kind: 'book', id: 'b1', label: 'Dune', sub: 'Frank Herbert' }]);
    expect(bookGet).toHaveBeenCalledTimes(1);
  });

  it('does not remember an id that no longer resolves', () => {
    topicGet.mockReturnValue(throwError(() => new Error('404')));
    const recent = create();
    const events = new Subject<unknown>();
    recent.watch({ events } as unknown as Router);

    events.next(new NavigationEnd(1, '/brain/topics/gone', '/brain/topics/gone'));
    expect(recent.items()).toEqual([]);
  });

  it('labels topics by note count and documents as documents', () => {
    topicGet.mockReturnValue(of({ id: 't1', name: 'Stoicism', notes: [{}, {}] }));
    writingGet.mockReturnValue(of({ id: 'w1', name: 'Essay', content: '' }));
    const recent = create();
    const events = new Subject<unknown>();
    recent.watch({ events } as unknown as Router);

    events.next(new NavigationEnd(1, '/brain/topics/t1', '/brain/topics/t1'));
    events.next(new NavigationEnd(2, '/studio?writingId=w1', '/studio?writingId=w1'));

    expect(recent.items()).toEqual([
      { kind: 'document', id: 'w1', label: 'Essay', sub: 'Document' },
      { kind: 'topic', id: 't1', label: 'Stoicism', sub: '2 notes' },
    ]);
  });
});
