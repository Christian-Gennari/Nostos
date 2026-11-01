import { Injectable, inject, signal } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { Observable, map } from 'rxjs';

import type { Book } from '../../core/dtos/book.dtos';
import { TopicsService } from '../../core/services/topics.service';
import { WritingsService } from '../../core/services/writings.service';
import { UNKNOWN_AUTHOR } from '../../core/book-format';

export type RecentKind = 'book' | 'topic' | 'document';

export interface RecentItem {
  kind: RecentKind;
  id: string;
  label: string;
  sub: string;
  /** A book's work: two editions of one book are one recent item (the last one opened). */
  workId?: string;
}

const STORAGE_KEY = 'nostos.palette.recent';
const MAX_ITEMS = 8;

/**
 * The places the reader opened most recently: books, topics and documents.
 *
 * Recorded from the router, so a book opened from the grid counts as much as
 * one opened from the palette. A URL carries only an id, so the label is looked
 * up once when an item is first recorded and kept with it. Stored per browser
 * (localStorage), not synced: it is a convenience list, not library data.
 */
@Injectable({ providedIn: 'root' })
export class RecentItemsService {
  private readonly topics = inject(TopicsService);
  private readonly writings = inject(WritingsService);

  private readonly _items = signal<RecentItem[]>(readStored());
  readonly items = this._items.asReadonly();

  private watching = false;

  /** Starts listening to navigation; repeated calls are no-ops. */
  watch(router: Router): void {
    if (this.watching) return;
    this.watching = true;
    router.events.subscribe((event) => {
      if (event instanceof NavigationEnd) this.recordUrl(event.urlAfterRedirects);
    });
  }

  /** Records an item whose label is already known (the palette's own results). */
  record(item: RecentItem): void {
    const rest = this._items().filter(
      (i) => !(i.kind === item.kind && (i.id === item.id || (!!item.workId && i.workId === item.workId))),
    );
    this.persist([item, ...rest].slice(0, MAX_ITEMS));
  }

  /**
   * Records a book from the page that has just read it, so the router does not
   * read it a second time. Unchanged, it stays where it is without a write.
   */
  recordBook(book: Pick<Book, 'id' | 'title' | 'author' | 'workId'>): void {
    const item = bookItem(book);
    const [top] = this._items();
    if (top && top.kind === 'book' && top.id === item.id && top.label === item.label
      && top.sub === item.sub && top.workId === item.workId) return;
    this.record(item);
  }

  /** Follows a rename without moving the item up the list. */
  relabel(kind: RecentKind, id: string, label: string): void {
    if (!this._items().some((i) => i.kind === kind && i.id === id && i.label !== label)) return;
    this.persist(this._items().map((i) => (i.kind === kind && i.id === id ? { ...i, label } : i)));
  }

  /** Drops an item that no longer opens (deleted since it was recorded). */
  forget(kind: RecentKind, id: string): void {
    this.persist(this._items().filter((i) => !(i.kind === kind && i.id === id)));
  }

  private persist(next: RecentItem[]): void {
    this._items.set(next);
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
    } catch {
      // Private mode / storage disabled: the list still works for this session.
    }
  }

  private recordUrl(url: string): void {
    const target = parseRecentTarget(url);
    if (!target) return;
    const known = this._items().find((i) => i.kind === target.kind && i.id === target.id);
    if (known) {
      this.record(known);
      return;
    }
    // A book's page and the reader read the book anyway and hand it to
    // `recordBook`; reading it here as well would be a second request.
    if (target.kind === 'book') return;
    this.lookup(target.kind, target.id).subscribe({
      next: (item) => this.record(item),
      // An id that does not resolve is not worth remembering.
      error: () => undefined,
    });
  }

  private lookup(kind: Exclude<RecentKind, 'book'>, id: string): Observable<RecentItem> {
    switch (kind) {
      case 'topic':
        return this.topics
          .get(id)
          .pipe(map((t) => ({ kind, id, label: t.name, sub: notesLabel(t.notes.length) })));
      case 'document':
        return this.writings.get(id).pipe(map((w) => ({ kind, id, label: w.name, sub: 'Document' })));
    }
  }
}

function bookItem(b: Pick<Book, 'id' | 'title' | 'author' | 'workId'>): RecentItem {
  return { kind: 'book', id: b.id, label: b.title, sub: b.author || UNKNOWN_AUTHOR, ...(b.workId ? { workId: b.workId } : {}) };
}

/** Which recent-worthy place a URL opens, if any. */
export function parseRecentTarget(url: string): { kind: RecentKind; id: string } | null {
  const [path, query = ''] = url.split('#', 1)[0].split('?', 2);
  const params = new URLSearchParams(query);

  const book = /^\/(?:library|read)\/([^/]+)$/.exec(path);
  if (book && book[1] !== 'to-read') return { kind: 'book', id: decodeURIComponent(book[1]) };

  const topic = /^\/brain\/topics\/([^/]+)$/.exec(path);
  if (topic) return { kind: 'topic', id: decodeURIComponent(topic[1]) };

  if (path === '/studio') {
    const writingId = params.get('writingId');
    if (writingId) return { kind: 'document', id: writingId };
  }
  return null;
}

export function notesLabel(count: number): string {
  return count === 1 ? '1 note' : `${count} notes`;
}

function readStored(): RecentItem[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    if (!Array.isArray(parsed)) return [];
    return parsed
      .filter(
        (i): i is RecentItem =>
          !!i &&
          typeof i === 'object' &&
          ['book', 'topic', 'document'].includes((i as RecentItem).kind) &&
          typeof (i as RecentItem).id === 'string' &&
          typeof (i as RecentItem).label === 'string' &&
          typeof (i as RecentItem).sub === 'string',
      )
      .slice(0, MAX_ITEMS);
  } catch {
    return [];
  }
}
