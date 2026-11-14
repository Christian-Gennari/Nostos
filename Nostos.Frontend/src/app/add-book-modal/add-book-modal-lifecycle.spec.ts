import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';

import { AddBookModal } from './add-book-modal.component';
import { Book, BooksService } from '../core/services/books.service';
import { BookUploadService } from '../core/services/book-upload.service';
import { ProvidersService } from '../core/services/providers.service';
import { ProviderDiscoverySearchResult } from '../core/dtos/provider.dtos';

/**
 * What a closed Add book sheet must leave behind: nothing (#1639).
 *
 * The step components and the upload session own long-running work, so the
 * interesting case is not the happy path — it is destroying the sheet while a
 * catalogue is still answering, a file is still moving, or the upload view is
 * waiting out its short settle delay. Every one of those must end with the
 * request cancelled and no timer left to fire onto a dead component.
 */
describe('AddBookModal — closing', () => {
  let component: AddBookModal;
  let fixture: ComponentFixture<AddBookModal>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [AddBookModal] }).compileComponents();
    fixture = TestBed.createComponent(AddBookModal);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('collections', []);
    await fixture.whenStable();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('cancels an in-flight catalogue search and its slow-catalogue timer', async () => {
    vi.useFakeTimers();
    const providers = TestBed.inject(ProvidersService);
    vi.spyOn(providers, 'list').mockReturnValue(of([]));
    const pending = new Subject<ProviderDiscoverySearchResult>();
    vi.spyOn(providers, 'searchAll').mockReturnValue(pending.asObservable());
    const onSetTimer = vi.spyOn(globalThis, 'setTimeout');
    const onClearTimer = vi.spyOn(globalThis, 'clearTimeout');

    component.enterSourceMode();
    fixture.detectChanges();
    // The step's own focus timer is not what this asserts; let it run first.
    await vi.advanceTimersByTimeAsync(0);

    component.sourceQuery.set('pride');
    component.searchSource();
    expect(pending.observed).toBe(true);
    // The search schedules the slow-catalogue notice, and that is the timer a
    // closed sheet must not leave behind.
    const slowTimer = onSetTimer.mock.results.at(-1)?.value;
    expect(slowTimer).toBeDefined();

    fixture.destroy();

    expect(pending.observed).toBe(false);
    expect(onClearTimer).toHaveBeenCalledWith(slowTimer);
  });

  it('stops the file request and drops the beforeunload guard on destroy', () => {
    const books = TestBed.inject(BooksService);
    vi.spyOn(books, 'createOrMatch').mockReturnValue(
      of({ book: { id: 'new-book' } as unknown as Book, created: true }),
    );
    const upload = new Subject<unknown>();
    vi.spyOn(books, 'uploadFile').mockReturnValue(upload.asObservable() as never);
    vi.spyOn(TestBed.inject(BookUploadService), 'supportsResumable').mockReturnValue(false);
    const onAdd = vi.spyOn(window, 'addEventListener');
    const onRemove = vi.spyOn(window, 'removeEventListener');

    component.form.title = 'The Magic Mountain';
    component.form.type = 'ebook';
    component.selectedFile.set(
      new File(['ebook'], 'magic-mountain.epub', { type: 'application/epub+zip' }),
    );
    fixture.detectChanges();
    component.submit();

    expect(upload.observed).toBe(true);
    const guard = onAdd.mock.calls.find(([type]) => type === 'beforeunload')?.[1];

    fixture.destroy();

    expect(upload.observed).toBe(false);
    expect(
      onRemove.mock.calls.some(([type, listener]) => type === 'beforeunload' && listener === guard),
    ).toBe(true);
  });

  it('drops the delayed finish when the modal is destroyed first', async () => {
    vi.useFakeTimers();
    const books = TestBed.inject(BooksService);
    vi.spyOn(books, 'createOrMatch').mockReturnValue(
      of({ book: { id: 'new-book' } as unknown as Book, created: true }),
    );
    const uploads = TestBed.inject(BookUploadService);
    vi.spyOn(uploads, 'supportsResumable').mockReturnValue(true);
    vi.spyOn(uploads, 'upload').mockResolvedValue({} as never);
    const uploaded = vi.spyOn(books, 'uploadCover');
    const closed = vi.fn();
    component.closeModal.subscribe(closed);

    component.form.title = 'The Magic Mountain';
    component.form.type = 'ebook';
    component.selectedFile.set(
      new File(['ebook'], 'magic-mountain.epub', { type: 'application/epub+zip' }),
    );
    component.submit();
    // Let the resolved promise reach the 1.2 s settle delay, which keeps the
    // upload view up until the cover step has run.
    await vi.advanceTimersByTimeAsync(0);
    const timersBeforeDestroy = vi.getTimerCount();
    expect(timersBeforeDestroy).toBeGreaterThan(0);

    fixture.destroy();
    await vi.advanceTimersByTimeAsync(5_000);

    expect(uploaded).not.toHaveBeenCalled();
    expect(closed).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });
});
