import { computed, inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Subject } from 'rxjs';
import {
  ImportActivity,
  isImportInFlight,
  isImportTerminal,
} from '../dtos/import.dtos';
import { Book } from '../dtos/book.dtos';
import { BooksService } from './books.service';
import { ProvidersService } from './providers.service';
import { ToastService } from './toast.service';

/** What the feed's connection is doing, so the UI can say "reconnecting" rather than just stop. */
export type ImportConnectionState = 'idle' | 'connecting' | 'live' | 'reconnecting';

/**
 * Owns the background-import feed.
 *
 * Why this is a service and not part of the library query: an import in flight is
 * not a search result. It has to stay visible whatever the user sorts or filters
 * by, and watching it must not mean refetching the library — a refetch every
 * second would also re-order and re-render the page under the reader's hands.
 *
 * The connection is deliberately on demand:
 *   - it is opened only when there is work in flight, and closed as soon as the
 *     last import reaches a terminal state (an SSE stream left open 24/7 is a
 *     connection, a proxy slot and a wake-up every heartbeat, for nothing);
 *   - `onerror` surfaces 'reconnecting' instead of hanging silently, and every
 *     (re)connect re-reads GET /api/imports/active rather than assuming the
 *     events missed while it was down were buffered — they were not;
 *   - a terminal event fetches ONLY that one book and hands it to the library to
 *     patch in place.
 */
@Injectable({ providedIn: 'root' })
export class ImportService {
  private readonly http = inject(HttpClient);
  private readonly books = inject(BooksService);
  private readonly providers = inject(ProvidersService);
  private readonly toast = inject(ToastService);

  private readonly entries = signal<ImportActivity[]>([]);
  private readonly connection = signal<ImportConnectionState>('idle');
  private readonly dismissedIds = signal<ReadonlySet<string>>(new Set());

  /** Failures already announced, so a repeated terminal frame cannot say it twice. */
  private readonly announcedFailures = new Set<string>();

  /** Everything worth showing, minus what the user dismissed. */
  readonly imports = computed(() =>
    this.entries().filter((entry) => !this.dismissedIds().has(entry.id)),
  );

  /** Work that can still change — the work the feed is watching. */
  readonly activeImports = computed(() => this.imports().filter(isImportInFlight));

  /** Work that ended badly and is waiting for the user to retry or dismiss it. */
  readonly failedImports = computed(() =>
    this.imports().filter((entry) => isImportTerminal(entry) && entry.state !== 'succeeded'),
  );

  readonly hasActiveImports = computed(() => this.activeImports().length > 0);

  /** True while anything at all should be on screen, including failures. */
  readonly hasImports = computed(() => this.imports().length > 0);

  /**
   * The library rows the feed is currently importing, as a stable list.
   *
   * Only a BOOK ROW can change the list, so only book rows are in here. The feed
   * announces a job before its row exists (the backend plans the item first, ~1-3s),
   * and keying on the entry id as well made that first moment look like a change the
   * list had to catch up with: one wasted request, and one visible reload, for an
   * import that could not be in the results yet. Percentages are not in here either:
   * they tick several times a second and change nothing about which books the page
   * holds.
   */
  readonly inFlightBookIds = computed(() =>
    this.imports()
      .filter((entry) => isImportInFlight(entry))
      .map((entry) => entry.bookId)
      .filter((bookId): bookId is string => !!bookId)
      .sort(),
  );

  /**
   * The in-flight entry driving a given book's card/row, or undefined.
   *
   * This is the whole point of the feed now: the library renders its own items,
   * and an item that is being imported simply knows its own progress. There is no
   * second list to reconcile with the first.
   */
  readonly progressByBookId = computed(() => {
    const byBook = new Map<string, ImportActivity>();

    for (const entry of this.imports()) {
      if (entry.bookId) byBook.set(entry.bookId, entry);
    }

    return byBook;
  });

  readonly connectionState = this.connection.asReadonly();

  /**
   * A book the server just reported as finished (or failed). The library patches
   * this single book into the list it already has — it is never refetched as a
   * whole, because the rest of it did not change.
   */
  readonly bookPatched = new Subject<Book>();

  private source: EventSource | null = null;
  private hasOpened = false;

  /**
   * Start watching, if there is anything to watch.
   *
   * Called when the library mounts and right after an import is queued: the feed
   * is opened by demand, not by the app merely existing.
   */
  ensureConnected(): void {
    if (this.source) return;

    this.connection.set('connecting');

    // Re-read first: the stream only reports CHANGE, so deciding whether it is
    // worth opening at all (and what the current state is) has to come from the
    // list.
    this.http.get<ImportActivity[]>('/api/imports/active').subscribe({
      next: (entries) => {
        this.absorb(entries);
        this.openIfWorthwhile();
      },
      error: () => {
        // The server is unreachable. Report it rather than pretending the import
        // finished; the stream is not opened, and the next ensureConnected tries
        // again.
        this.connection.set('reconnecting');
      },
    });
  }

  /** Stop watching and drop the connection (used on sign-out / teardown paths). */
  disconnect(): void {
    this.closeSource();
    this.connection.set('idle');
  }

  /** Hide a finished entry. Client-side only — hiding a notice must not touch the book. */
  dismiss(activity: ImportActivity): void {
    this.dismissedIds.update((current) => new Set(current).add(activity.id));
    this.entries.update((current) => current.filter((entry) => entry.id !== activity.id));
  }

  /** Start the same import again, when the source is known. */
  retry(activity: ImportActivity): void {
    if (!activity.canRetry || !activity.providerId || !activity.externalId) return;

    this.providers
      .acquire(activity.providerId, {
        externalId: activity.externalId,
        assetId: activity.assetId,
        includeCover: true,
      })
      .subscribe({
        next: () => {
          // The new job's id and book are the server's to report; drop the failed
          // entry so it cannot be shown twice, then re-read.
          this.dismiss(activity);
          this.ensureConnected();
        },
        // Left on screen on purpose: a retry that could not even be queued is
        // still the user's to see, and the message says why.
        error: () => undefined,
      });
  }

  /** Cancel a job that is still running. */
  cancel(activity: ImportActivity): void {
    if (activity.source !== 'job') return;

    this.providers.cancel(activity.id).subscribe({
      next: () => this.entries.update((current) => current.filter((e) => e.id !== activity.id)),
      error: () => undefined,
    });
  }

  /**
   * The EventSource is created through this seam so a unit test never opens a
   * real connection to a real server.
   */
  protected createEventSource(url: string): EventSource {
    return new EventSource(url);
  }

  private openIfWorthwhile(): void {
    if (this.source) return;

    if (!this.hasActiveImports()) {
      // Nothing in flight: everything on screen is already terminal, so there is
      // no change to wait for.
      this.connection.set('idle');
      return;
    }

    let source: EventSource;
    try {
      source = this.createEventSource('/api/imports/stream');
    } catch {
      // A browser (or a test environment) without EventSource is not a crash:
      // stop asking for one rather than throwing out of a mount hook.
      this.connection.set('idle');
      return;
    }

    this.source = source;

    source.onopen = () => {
      this.connection.set('live');

      // A RE-connect may have missed events while it was down — they are not
      // buffered anywhere — so the list is re-read rather than trusted. The first
      // open needs no re-read: ensureConnected just did one.
      if (this.hasOpened) this.resync();
      this.hasOpened = true;
    };

    source.onerror = () => {
      // The browser retries the connection itself; the point here is to SAY so.
      // Swallowing this is how a frozen bar looks like a working import.
      if (!this.source) return;
      this.connection.set('reconnecting');
    };

    source.addEventListener('progress', (event) => {
      const entries = this.parse<ImportActivity[]>((event as MessageEvent).data);
      if (!entries) return;

      this.absorb(entries);
      // A snapshot with nothing in flight means there is nothing left to watch:
      // the terminal frame for whatever just ended was sent before this one, so
      // it has already been handled. Without this the connection would sit open
      // until the server's idle timeout closed it.
      this.closeIfSettled();
    });

    for (const terminal of ['done', 'failed']) {
      source.addEventListener(terminal, (event) => {
        const payload = this.parse<ImportActivity[]>((event as MessageEvent).data);
        const activity = payload?.[0];
        if (activity) this.onFinished(activity);
      });
    }
  }

  private resync(): void {
    this.http.get<ImportActivity[]>('/api/imports/active').subscribe({
      next: (entries) => {
        this.absorb(entries);
        this.closeIfSettled();
      },
      error: () => this.connection.set('reconnecting'),
    });
  }

  /**
   * Replace the feed's contents with the server's answer.
   *
   * The server's list wins for every id it knows. A locally-known FINISHED entry
   * it does not know about is kept: it just ended, the user has not seen it, and
   * a reconnect in that window must not make the outcome vanish. An entry that
   * was in flight and is now absent has resolved while the feed was down, so its
   * book is fetched — that one book, never the library.
   */
  private absorb(serverEntries: ImportActivity[]): void {
    const previous = this.entries();
    const incoming = new Map(serverEntries.map((entry) => [entry.id, entry]));

    const vanished = previous.filter(
      (entry) => isImportInFlight(entry) && !incoming.has(entry.id),
    );

    const kept = previous.filter(
      (entry) => isImportTerminal(entry) && !incoming.has(entry.id),
    );

    this.entries.set([...serverEntries, ...kept]);

    for (const entry of vanished) this.fetchBook(entry);

    // A failure the user missed while the page was closed still has to be said out
    // loud — this list is where a restart-interrupted import reappears, and the feed
    // is not delivering events for it. Announced once per entry per session.
    for (const entry of serverEntries) {
      if (!isImportInFlight(entry) && entry.state !== 'succeeded') this.announceFailure(entry);
    }
  }

  private onFinished(activity: ImportActivity): void {
    this.fetchBook(activity);

    if (activity.state === 'succeeded') {
      // Done means the book is in the library and its row was just patched in, so
      // a "Done" notice would only ask the user to dismiss a success.
      this.entries.update((current) => current.filter((entry) => entry.id !== activity.id));
    } else {
      const existing = this.entries().find((entry) => entry.id === activity.id);
      // A terminal event is the authoritative outcome for that id, and it may
      // arrive after a snapshot already described the entry as running.
      this.entries.update((current) => [
        ...current.filter((entry) => entry.id !== activity.id),
        existing ? { ...existing, ...activity } : activity,
      ]);

      this.announceFailure(existing ? { ...existing, ...activity } : activity);
    }

    this.closeIfSettled();
  }

  /**
   * A failed import says so, because nothing else can.
   *
   * A running import is visible as its own card; a failed one is not guaranteed to
   * be — the row may not exist yet (the item never got as far as a book), it may be
   * on another page, or the user's filter may exclude it — so the feed, which is
   * app-wide and knows the outcome whatever the user is looking at, raises a toast.
   * The failure also stays on the book's own card where one exists, with Retry and
   * Dismiss; this is the notice, not the record.
   */
  private announceFailure(activity: ImportActivity): void {
    if (this.announcedFailures.has(activity.id)) return;

    this.announcedFailures.add(activity.id);

    const title = activity.title?.trim();
    const head = title ? `Could not import “${title}”` : 'Could not finish an import';
    const reason = activity.message?.trim().replace(/\.$/, '');
    this.toast.error(reason ? `${head}: ${reason}` : head);
  }

  /**
   * Fetch the ONE book an event named and hand it to the library to patch in
   * place. Never a list request: the rest of the library did not change.
   */
  private fetchBook(activity: ImportActivity): void {
    if (!activity.bookId) return;

    this.books.get(activity.bookId).subscribe({
      next: (book) => this.bookPatched.next(book),
      // The library keeps whatever it already had. This is a cosmetic refresh of
      // one row, not a state the user is waiting on.
      error: () => undefined,
    });
  }

  /**
   * The feed has a lifetime, not a subscription: once nothing can change again,
   * the connection is closed and the section shows only outcomes the user has to
   * act on.
   */
  private closeIfSettled(): void {
    if (this.hasActiveImports()) return;
    if (!this.source) return;

    this.closeSource();
    this.connection.set('idle');
  }

  private closeSource(): void {
    const source = this.source;
    // Cleared BEFORE closing, so the close's own event cannot be read as a
    // connection error.
    this.source = null;
    this.hasOpened = false;
    source?.close();
  }

  private parse<T>(data: string): T | null {
    try {
      return JSON.parse(data) as T;
    } catch {
      // A malformed frame is not worth tearing the feed down for; the next one
      // will very likely be fine.
      return null;
    }
  }
}
