import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, map, tap } from 'rxjs';
import { ReadingListEntry, SaveReadingListEntry } from '../dtos/reading-list.dtos';

/**
 * The reading list: books the user wants and does not have yet.
 *
 * The list is small and returned whole, so this service holds it as one signal
 * that the page and the library sidebar's count both read. Every write answers
 * with the server's version of what changed and the signal follows it.
 */
@Injectable({ providedIn: 'root' })
export class ReadingListService {
  private http = inject(HttpClient);

  private readonly entriesState = signal<ReadingListEntry[]>([]);
  private readonly loadedState = signal(false);

  /** "Next up" in shelf order, then "Someday" newest first, as the server sorts it. */
  readonly entries = this.entriesState.asReadonly();
  /** False until the first load has answered, so a count is never shown as a guess. */
  readonly loaded = this.loadedState.asReadonly();

  readonly next = computed(() => this.entriesState().filter((e) => e.nextPosition !== null));
  readonly someday = computed(() => this.entriesState().filter((e) => e.nextPosition === null));
  readonly count = computed(() => this.entriesState().length);

  load(): Observable<ReadingListEntry[]> {
    return this.http.get<ReadingListEntry[]>('/api/reading-list').pipe(
      tap((entries) => {
        this.entriesState.set(entries);
        this.loadedState.set(true);
      }),
    );
  }

  add(entry: SaveReadingListEntry): Observable<ReadingListEntry> {
    return this.http.post<ReadingListEntry>('/api/reading-list', entry).pipe(
      tap((created) => {
        const next = this.next();
        const someday = this.someday();
        // The server's order: a new shelf entry goes last on the shelf, a new
        // Someday entry first in Someday.
        this.entriesState.set([...next, created, ...someday]);
      }),
    );
  }

  update(id: string, entry: SaveReadingListEntry): Observable<ReadingListEntry> {
    return this.http.put<ReadingListEntry>(`/api/reading-list/${id}`, entry).pipe(
      tap((updated) =>
        this.entriesState.update((entries) => entries.map((e) => (e.id === id ? updated : e))),
      ),
    );
  }

  remove(id: string): Observable<void> {
    return this.http
      .delete<void>(`/api/reading-list/${id}`)
      .pipe(tap(() => this.entriesState.update((entries) => entries.filter((e) => e.id !== id))));
  }

  /** Replaces the whole "Next up" shelf; every entry not named becomes "Someday". */
  setNext(ids: string[]): Observable<ReadingListEntry[]> {
    return this.http
      .put<ReadingListEntry[]>('/api/reading-list/next', { ids })
      .pipe(tap((entries) => this.entriesState.set(entries)));
  }

  /** The entry became `bookId`: its reason moves onto the book and it leaves the list. */
  markAcquired(id: string, bookId: string): Observable<void> {
    return this.http.post<void>(`/api/reading-list/${id}/acquired`, { bookId }).pipe(
      map(() => undefined),
      tap(() => this.entriesState.update((entries) => entries.filter((e) => e.id !== id))),
    );
  }
}
