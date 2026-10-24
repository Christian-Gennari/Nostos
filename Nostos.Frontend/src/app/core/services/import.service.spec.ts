import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ImportService } from './import.service';
import { ToastService } from './toast.service';
import { ImportActivity } from '../dtos/import.dtos';
import { Book } from '../dtos/book.dtos';

/**
 * The import feed's connection rules, driven through a fake EventSource.
 *
 * Every assertion here is about a decision the UI would otherwise get wrong in
 * a way nobody notices until an import of theirs hangs: opening a stream when
 * there is nothing to watch, keeping one open after the last import finished,
 * trusting stale state across a reconnect, or hiding a reconnecting feed behind
 * a bar that has simply stopped moving.
 */
describe('ImportService', () => {
  let service: ImportService;
  let http: HttpTestingController;

  const BOOK_ID = '11111111-1111-4111-8111-111111111111';

  function activity(overrides: Partial<ImportActivity> = {}): ImportActivity {
    return {
      id: 'job-1',
      source: 'job',
      state: 'running',
      stage: 'downloading',
      percent: 40,
      detail: '12/34 files',
      providerId: 'gutenberg',
      externalId: '201',
      assetId: 'epub3-images',
      bookId: BOOK_ID,
      title: 'Flatland',
      author: 'Edwin Abbott Abbott',
      coverUrl: null,
      errorCode: null,
      message: null,
      createdAt: '2026-09-18T10:00:00Z',
      updatedAt: '2026-09-18T10:00:05Z',
      canRetry: true,
      ...overrides,
    };
  }

  /** A stand-in for the browser's EventSource, with the frames under test control. */
  class FakeEventSource {
    static instances: FakeEventSource[] = [];

    onopen: (() => void) | null = null;
    onerror: (() => void) | null = null;
    closed = false;

    private readonly listeners = new Map<string, ((event: MessageEvent) => void)[]>();

    constructor(readonly url: string) {
      FakeEventSource.instances.push(this);
    }

    addEventListener(type: string, listener: (event: MessageEvent) => void): void {
      const current = this.listeners.get(type) ?? [];
      current.push(listener);
      this.listeners.set(type, current);
    }

    close(): void {
      this.closed = true;
    }

    emit(type: string, payload: ImportActivity[]): void {
      for (const listener of this.listeners.get(type) ?? []) {
        listener({ data: JSON.stringify(payload) } as MessageEvent);
      }
    }

    open(): void {
      this.onopen?.();
    }

    fail(): void {
      this.onerror?.();
    }
  }

  beforeEach(() => {
    FakeEventSource.instances = [];

    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(ImportService);
    http = TestBed.inject(HttpTestingController);

    vi.spyOn(
      service as unknown as { createEventSource: (url: string) => EventSource },
      'createEventSource',
    ).mockImplementation((url: string) => new FakeEventSource(url) as unknown as EventSource);
  });

  afterEach(() => {
    http.verify();
  });

  const activeRequest = () => http.expectOne('/api/imports/active');
  const stream = () => FakeEventSource.instances[0];

  it('opens no connection at all when nothing is in flight', () => {
    service.ensureConnected();
    activeRequest().flush([]);

    expect(FakeEventSource.instances).toHaveLength(0);
    expect(service.connectionState()).toBe('idle');
    expect(service.hasImports()).toBe(false);
  });

  it('opens the stream when the list reports work in flight', () => {
    service.ensureConnected();
    activeRequest().flush([activity()]);

    expect(FakeEventSource.instances).toHaveLength(1);
    expect(stream().url).toBe('/api/imports/stream');
    expect(service.hasActiveImports()).toBe(true);
    expect(service.activeImports()[0].title).toBe('Flatland');

    stream().open();
    expect(service.connectionState()).toBe('live');
  });

  it('follows progress frames', () => {
    service.ensureConnected();
    activeRequest().flush([activity({ percent: 10 })]);
    stream().open();

    stream().emit('progress', [activity({ percent: 74, stage: 'importing' })]);

    expect(service.activeImports()[0].percent).toBe(74);
    expect(service.activeImports()[0].stage).toBe('importing');
  });

  it('signals a change when an import gets its book row, but not on progress ticks', () => {
    service.ensureConnected();
    activeRequest().flush([activity({ bookId: null })]);
    stream().open();

    const beforeRow = service.inFlightBookIds();

    // A job with no row yet is not something a list could catch up with.
    expect(beforeRow).toEqual([]);

    // The same import, but its book row now exists. The library's page is a snapshot
    // taken before that row, so it has to be re-read for the book to appear in it.
    stream().emit('progress', [activity({ bookId: 'book-1' })]);
    const withRow = service.inFlightBookIds();
    expect(withRow).toEqual(['book-1']);

    // A number moving several times a second is not a change to the rows.
    stream().emit('progress', [activity({ bookId: 'book-1', percent: 84 })]);
    stream().emit('progress', [activity({ bookId: 'book-1', percent: 91 })]);
    expect(service.inFlightBookIds()).toEqual(withRow);
  });

  it('fetches only the finished book on a done event, then closes the stream', () => {
    service.ensureConnected();
    activeRequest().flush([activity()]);
    stream().open();

    const patched: Book[] = [];
    service.bookPatched.subscribe((book) => patched.push(book));

    stream().emit('done', [activity({ state: 'succeeded', stage: 'done', percent: 100 })]);

    // One book, by id — never the library list, whose remaining rows did not
    // change and would re-order the page under the reader.
    const bookRequest = http.expectOne(`/api/books/${BOOK_ID}`);
    bookRequest.flush({ id: BOOK_ID, title: 'Flatland' } as Book);

    expect(patched).toHaveLength(1);
    expect(patched[0].id).toBe(BOOK_ID);

    // A success needs no notice: the book is in the library now.
    expect(service.hasImports()).toBe(false);
    expect(stream().closed).toBe(true);
    expect(service.connectionState()).toBe('idle');
  });

  it('keeps a failed import on screen with its message, and stays put', () => {
    service.ensureConnected();
    activeRequest().flush([activity()]);
    stream().open();

    stream().emit(
      'failed',
      [activity({ state: 'failed', stage: 'failed', message: 'The source refused the download.' })],
    );
    http.expectOne(`/api/books/${BOOK_ID}`).flush({ id: BOOK_ID } as Book);

    expect(service.hasImports()).toBe(true);
    expect(service.hasActiveImports()).toBe(false);
    expect(service.failedImports()[0].message).toBe('The source refused the download.');
    // Nothing left to watch, so the connection is not kept open for it.
    expect(stream().closed).toBe(true);
  });

  it('toasts a failure, because a failed import has no guaranteed card to live on', () => {
    const toast = TestBed.inject(ToastService);
    const errorSpy = vi.spyOn(toast, 'error');

    service.ensureConnected();
    activeRequest().flush([activity()]);
    stream().open();

    stream().emit(
      'failed',
      [activity({ state: 'failed', stage: 'failed', title: 'Flatland', message: 'The source refused the download.' })],
    );
    http.expectOne(`/api/books/${BOOK_ID}`).flush({ id: BOOK_ID } as Book);

    expect(errorSpy).toHaveBeenCalledWith(
      expect.stringContaining('Could not import “Flatland”: The source refused the download'),
    );

    // A repeated terminal frame must not say it twice (it does re-read the one book,
    // which is idempotent).
    stream().emit(
      'failed',
      [activity({ state: 'failed', stage: 'failed', title: 'Flatland', message: 'The source refused the download.' })],
    );
    http.expectOne(`/api/books/${BOOK_ID}`).flush({ id: BOOK_ID } as Book);
    expect(errorSpy).toHaveBeenCalledTimes(1);
  });

  it('announces a failure it finds in the feed, not only one it watches happen', () => {
    const toast = TestBed.inject(ToastService);
    const errorSpy = vi.spyOn(toast, 'error');

    // The list is where a restart-interrupted import reappears after a page load, and
    // no event is coming for it. Without this, such a failure would be silent.
    service.ensureConnected();
    activeRequest().flush([
      activity({
        id: 'job-restart',
        bookId: null,
        state: 'failed',
        stage: 'failed',
        title: 'Meditations',
        message: 'Import interrupted by server restart.',
      }),
    ]);

    expect(errorSpy).toHaveBeenCalledWith(
      expect.stringContaining('Could not import “Meditations”: Import interrupted by server restart'),
    );
  });

  it('reports a dropped connection instead of looking like a stalled import', () => {
    service.ensureConnected();
    activeRequest().flush([activity()]);
    stream().open();

    stream().fail();

    expect(service.connectionState()).toBe('reconnecting');
  });

  it('re-reads the list on a RE-connect rather than trusting what it missed', () => {
    service.ensureConnected();
    activeRequest().flush([activity({ percent: 10 })]);
    stream().open();

    stream().fail();
    // The browser reconnects on its own; events sent while it was down are gone
    // for good, so the only honest answer is to ask again.
    stream().open();

    activeRequest().flush([activity({ percent: 63 })]);
    expect(service.activeImports()[0].percent).toBe(63);
  });

  it('treats an entry that vanished during a reconnect as finished', () => {
    service.ensureConnected();
    activeRequest().flush([activity()]);
    stream().open();

    const patched: Book[] = [];
    service.bookPatched.subscribe((book) => patched.push(book));

    stream().fail();
    stream().open();
    // The import completed while the feed was down: it is simply absent now.
    activeRequest().flush([]);

    http.expectOne(`/api/books/${BOOK_ID}`).flush({ id: BOOK_ID } as Book);
    expect(patched).toHaveLength(1);
    expect(service.hasImports()).toBe(false);
  });

  it('dismisses a finished entry without touching the server or the book', () => {
    service.ensureConnected();
    activeRequest().flush([activity()]);
    stream().open();

    stream().emit('failed', [activity({ state: 'failed', stage: 'failed', message: 'Refused.' })]);
    http.expectOne(`/api/books/${BOOK_ID}`).flush({ id: BOOK_ID } as Book);
    expect(service.hasImports()).toBe(true);

    service.dismiss(service.failedImports()[0]);

    expect(service.hasImports()).toBe(false);
    // No request of any kind: hiding a notice is a client-side decision, and the
    // book row (which is genuinely Failed) is not something a dismissal edits.
    http.expectNone(() => true);
  });

  it('re-queues the same source on retry and re-reads the feed', () => {
    service.ensureConnected();
    activeRequest().flush([]);

    service.retry(activity({ state: 'failed', source: 'reconciled' }));

    const acquire = http.expectOne('/api/providers/gutenberg/acquire');
    expect(acquire.request.method).toBe('POST');
    expect(acquire.request.body).toEqual({
      externalId: '201',
      assetId: 'epub3-images',
      includeCover: true,
    });
    acquire.flush({ jobId: 'job-2', state: 'queued' });

    // The failed entry is gone and the feed has been re-read for the new job.
    const resync = http.expectOne('/api/imports/active');
    resync.flush([]);
    expect(service.hasImports()).toBe(false);
  });

  it('does not offer a retry the source cannot support', () => {
    service.ensureConnected();
    activeRequest().flush([]);

    service.retry(activity({ state: 'failed', canRetry: false }));

    http.expectNone('/api/providers/gutenberg/acquire');
  });
});
