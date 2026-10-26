import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';

import { AddBookModal } from './add-book-modal.component';
import { Book, BookLookupError, BooksService } from '../core/services/books.service';
import {
  BookUploadProgress,
  BookUploadService,
} from '../core/services/book-upload.service';
import { BookUploadStatusDto } from '../core/dtos/book-upload.dtos';
import { ToastService } from '../core/services/toast.service';

const collections = [
  { id: 'root', name: 'Root', parentId: null },
  { id: 'child', name: 'Child', parentId: 'root' },
  { id: 'other', name: 'Other', parentId: null },
];

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

describe('AddBookModal', () => {
  let component: AddBookModal;
  let fixture: ComponentFixture<AddBookModal>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AddBookModal],
    }).compileComponents();

    fixture = TestBed.createComponent(AddBookModal);
    component = fixture.componentInstance;
    // isOpen and collections are required inputs; provide them before the first
    // change detection so the constructor effect and template can read them.
    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('collections', collections);
    await fixture.whenStable();
  });

  /**
   * Starts a resumable file run that stays active until the test resolves it,
   * with one progress report already on screen. Returns the handles the close
   * and stop paths are asserted through.
   */
  function startResumableUpload() {
    const books = TestBed.inject(BooksService);
    const createSpy = vi.spyOn(books, 'createOrMatch').mockReturnValue(
      of({
        book: { id: 'new-book', title: 'The Magic Mountain' } as unknown as Book,
        created: true,
      }),
    );
    const uploads = TestBed.inject(BookUploadService);
    vi.spyOn(uploads, 'supportsResumable').mockReturnValue(true);
    const stopSpy = vi.spyOn(uploads, 'stop').mockResolvedValue();
    const run = deferred<BookUploadStatusDto>();
    let report: ((progress: BookUploadProgress) => void) | null = null;
    const uploadSpy = vi.spyOn(uploads, 'upload').mockImplementation((_id, _file, options) => {
      report = options?.onProgress ?? null;
      report?.({ phase: 'uploading', percent: 22, uploadedBytes: 22, movedBytes: 22, totalBytes: 100 });
      return run.promise;
    });
    const closed = vi.fn();
    const added = vi.fn();
    component.closeModal.subscribe(closed);
    component.bookAdded.subscribe(added);

    component.form.title = 'The Magic Mountain';
    component.form.type = 'ebook';
    component.selectedFile.set(
      new File(['ebook'], 'magic-mountain.epub', { type: 'application/epub+zip' }),
    );
    fixture.detectChanges();
    component.submit();
    fixture.detectChanges();

    return {
      books,
      createSpy,
      uploads,
      stopSpy,
      uploadSpy,
      run,
      closed,
      added,
      report: (progress: BookUploadProgress) => report?.(progress),
    };
  }

  function pressBackdrop(element: HTMLElement, pointerId = 1): void {
    for (const type of ['pointerdown', 'pointerup'] as const) {
      const event = new Event(type, { bubbles: true }) as PointerEvent;
      Object.defineProperty(event, 'pointerId', { value: pointerId });
      element.dispatchEvent(event);
    }
  }

  it('exposes the dialog role', () => {
    fixture.detectChanges();

    // The role now comes from `app-modal-shell`, so this targets the role rather
    // than a class the shell happens to use — and checks the label the shell was
    // handed, which is what makes the migration more than a markup move.
    const dialog = fixture.nativeElement.querySelector('[role="dialog"]');
    expect(dialog).toBeTruthy();
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.getAttribute('aria-label')).toBe('Add a book');
  });

  it('uses DialogActions and canonical native buttons for the ordinary create footer', () => {
    fixture.detectChanges();

    const actions = fixture.nativeElement.querySelector('app-dialog-actions') as HTMLElement;
    const buttons = Array.from(actions.querySelectorAll('button')) as HTMLButtonElement[];
    const cancelButton = buttons.find((button) => button.textContent?.trim() === 'Cancel')!;
    const submitButton = buttons.find((button) => button.textContent?.trim() === 'Add to Library')!;

    expect(actions.classList).toContain('nostos-dialog-actions--footer');
    expect(cancelButton.type).toBe('button');
    expect(cancelButton.classList).toContain('nostos-button--secondary');
    expect(submitButton.type).toBe('submit');
    expect(submitButton.getAttribute('form')).toBe('add-book-form');
    expect(submitButton.classList).toContain('nostos-button--primary');
    expect(submitButton.disabled).toBe(true);
    expect(buttons.some((button) => button.textContent?.includes('Delete book'))).toBe(false);
  });

  it('projects the edit-only destructive action into the leading slot and preserves callbacks', () => {
    const deleteSpy = vi.fn();
    const closeSpy = vi.fn();
    component.deleteBook.subscribe(deleteSpy);
    component.closeModal.subscribe(closeSpy);
    fixture.componentRef.setInput('book', {
      id: 'b1',
      title: 'Meditations',
      type: 'physical',
      collectionIds: [],
    } as unknown as Book);
    fixture.detectChanges();

    const actions = fixture.nativeElement.querySelector('app-dialog-actions') as HTMLElement;
    const start = actions.querySelector('.nostos-dialog-actions__start') as HTMLElement;
    const deleteButton = Array.from(start.querySelectorAll('button')).find((button) =>
      button.textContent?.includes('Delete book'),
    ) as HTMLButtonElement;
    const cancelButton = Array.from(actions.querySelectorAll('button')).find((button) =>
      button.textContent?.includes('Cancel'),
    ) as HTMLButtonElement;

    expect(deleteButton).toBeTruthy();
    expect(deleteButton.type).toBe('button');
    expect(deleteButton.classList).toContain('nostos-button--danger');

    deleteButton.click();
    cancelButton.click();

    expect(deleteSpy).toHaveBeenCalledTimes(1);
    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('emits closeModal on Escape while open', () => {
    const closeSpy = vi.fn();
    component.closeModal.subscribe(closeSpy);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('does not emit closeModal on Escape while closed', () => {
    const closeSpy = vi.fn();
    component.closeModal.subscribe(closeSpy);
    fixture.componentRef.setInput('isOpen', false);
    fixture.detectChanges();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(closeSpy).not.toHaveBeenCalled();
  });

  it('focuses the title input on open', async () => {
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));

    const title = fixture.nativeElement.querySelector('input[name="title"]');
    expect(document.activeElement).toBe(title);
  });

  it('opens the physical path around ISBN identification, without a hidden book upload', () => {
    component.startIntent('physical');
    fixture.detectChanges();

    const digitalFileInput = fixture.nativeElement.querySelector(
      'input[type="file"][accept*=".epub"]',
    );
    expect(digitalFileInput).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Identify by ISBN');
    expect(fixture.nativeElement.querySelector('#book-title')).toBeTruthy();
  });

  it.each([
    ['invalid-isbn', 400, 'Invalid ISBN. Enter a valid ISBN-10 or ISBN-13.'],
    ['not-found', 404, 'No book metadata found for this ISBN.'],
    [
      'unavailable',
      503,
      'Book metadata services are temporarily unavailable. Please try again.',
    ],
  ] as const)('shows a distinct ISBN lookup error for %s', (reason, status, expectedMessage) => {
    const books = TestBed.inject(BooksService);
    const toast = TestBed.inject(ToastService);
    vi.spyOn(books, 'lookup').mockReturnValue(
      throwError(() => new BookLookupError(reason, status)),
    );
    const errorToast = vi.spyOn(toast, 'error').mockImplementation(() => undefined);

    component.form.isbn = '9780141183848';
    component.fetchMetadata();

    expect(errorToast).toHaveBeenCalledWith(expectedMessage);
    expect(component.isFetching()).toBe(false);
  });

  it('makes local upload the first visible task, then derives a compact review from the file', async () => {
    component.startIntent('upload');
    fixture.detectChanges();

    expect(component.awaitingLocalFile()).toBe(true);
    expect(fixture.nativeElement.querySelector('#book-title')).toBeNull();

    const input = fixture.nativeElement.querySelector(
      '.file-drop-zone--primary input[type="file"]',
    ) as HTMLInputElement;
    const file = new File(['ebook'], 'the-magic-mountain.epub', {
      type: 'application/epub+zip',
    });
    Object.defineProperty(input, 'files', { value: [file] });
    input.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.awaitingLocalFile()).toBe(false);
    expect(component.form.type).toBe('ebook');
    expect(component.form.title).toBe('the magic mountain');
    expect(fixture.nativeElement.querySelector('#book-title')).toBeTruthy();
    expect(fixture.nativeElement.textContent).toContain('the-magic-mountain.epub');
  });

  it('infers audiobook format from an uploaded audio file', () => {
    component.startIntent('upload');
    component.onFileSelected({
      target: { files: [new File(['audio'], 'ulysses.m4b', { type: 'audio/mp4' })] },
    } as unknown as Event);

    expect(component.form.type).toBe('audiobook');
    expect(component.form.title).toBe('ulysses');
  });

  it('keeps a provider-first manual draft intact when a cover is selected', () => {
    const books = TestBed.inject(BooksService);
    const createSpy = vi.spyOn(books, 'createOrMatch');
    const closeSpy = vi.fn();
    component.closeModal.subscribe(closeSpy);

    component.providerList.set([
      {
        id: 'gutenberg',
        displayName: 'Project Gutenberg',
        capabilities: ['search', 'ebookAcquisition'],
      } as any,
    ]);
    fixture.componentRef.setInput('sourceFirst', true);
    fixture.detectChanges();

    component.showManualMode();
    component.form.title = 'Unsaved draft';
    const cover = new File(['cover'], 'cover.jpg', { type: 'image/jpeg' });
    const objectUrlSpy = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:cover');

    component.onCoverSelected({
      target: { files: [cover] },
    } as unknown as Event);
    fixture.detectChanges();

    expect(component.sourceMode()).toBe(false);
    expect(component.flowIntent()).toBe('manual');
    expect(component.form.title).toBe('Unsaved draft');
    expect(component.selectedCover()).toBe(cover);
    expect(component.coverPreview()).toBe('blob:cover');
    expect(createSpy).not.toHaveBeenCalled();
    expect(closeSpy).not.toHaveBeenCalled();

    objectUrlSpy.mockRestore();
  });

  it('does not reinitialize a manual draft when provider state changes', () => {
    component.providerList.set([
      {
        id: 'gutenberg',
        displayName: 'Project Gutenberg',
        capabilities: ['search', 'ebookAcquisition'],
      } as any,
    ]);
    fixture.componentRef.setInput('sourceFirst', true);
    fixture.detectChanges();

    component.showManualMode();
    component.form.title = 'Keep this draft';

    component.providersLoading.set(true);
    component.providerList.set([
      {
        id: 'gutenberg',
        displayName: 'Project Gutenberg',
        capabilities: ['search', 'ebookAcquisition'],
      } as any,
      {
        id: 'librivox',
        displayName: 'LibriVox',
        capabilities: ['search', 'audiobookAcquisition'],
      } as any,
    ]);
    fixture.detectChanges();

    expect(component.sourceMode()).toBe(false);
    expect(component.flowIntent()).toBe('manual');
    expect(component.form.title).toBe('Keep this draft');
  });

  it('keeps optional metadata behind one shallow More details disclosure', () => {
    fixture.detectChanges();

    const disclosure = fixture.nativeElement.querySelector(
      'details.metadata-disclosure',
    ) as HTMLDetailsElement;
    expect(disclosure).toBeTruthy();
    expect(disclosure.open).toBe(false);
    expect(disclosure.querySelectorAll('details')).toHaveLength(0);

    const text = disclosure.textContent;
    expect(text).toContain('Identity & contributors');
    expect(text).toContain('Publication');
    expect(text).toContain('Identifiers');
    expect(text).toContain('Library details');
    expect(disclosure.querySelector('#book-publisher')).toBeTruthy();
    expect(disclosure.querySelector('#book-personal-review')).toBeTruthy();
  });

  it('clears a chosen digital file when format changes to physical', () => {
    component.onTypeChange('ebook');
    component.selectedFile.set(
      new File(['ebook'], 'meditations.epub', { type: 'application/epub+zip' }),
    );

    component.onTypeChange('physical');

    expect(component.selectedFile()).toBeNull();
  });

  it('never uploads a stale file when creating a physical book', () => {
    const books = TestBed.inject(BooksService);
    vi.spyOn(books, 'createOrMatch').mockReturnValue(of({ book: { id: 'physical-new' } as unknown as Book, created: true }));
    const uploadSpy = vi.spyOn(books, 'uploadFile');

    component.form.title = 'Physical Meditations';
    component.form.type = 'physical';
    component.selectedFile.set(new File(['stale'], 'stale.epub', { type: 'application/epub+zip' }));
    component.submit();

    expect(uploadSpy).not.toHaveBeenCalled();
  });

  it('allows only one create while the first add request is still in flight', () => {
    const books = TestBed.inject(BooksService);
    const createResult = new Subject<{ book: Book; created: boolean }>();
    const createSpy = vi.spyOn(books, 'createOrMatch').mockReturnValue(createResult);

    component.form.title = 'The Magic Mountain';
    component.form.type = 'physical';

    component.submit();
    component.submit();

    expect(createSpy).toHaveBeenCalledTimes(1);
    expect(component.isSubmitting()).toBe(true);

    createResult.error(new Error('create failed'));
    expect(component.isSubmitting()).toBe(false);
  });

  it('asks before closing while the legacy upload runs, and Stop ends the request', async () => {
    // #1265 stopped the modal from trapping the user for the whole upload. The
    // upload is still allowed to be ended, but closing now asks first (#1275).
    const books = TestBed.inject(BooksService);
    vi.spyOn(books, 'createOrMatch').mockReturnValue(of({ book: { id: 'new-book' } as unknown as Book, created: true }));
    const upload = new Subject<any>();
    vi.spyOn(books, 'uploadFile').mockReturnValue(upload);
    component.form.title = 'The Magic Mountain';
    component.form.type = 'ebook';
    component.selectedFile.set(new File(['ebook'], 'magic-mountain.epub', { type: 'application/epub+zip' }));
    const closed = vi.fn();
    component.closeModal.subscribe(closed);

    fixture.detectChanges();
    component.submit();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="add-book-upload-view"]')).toBeTruthy();
    expect(el.querySelector('input[name="title"]')).toBeNull();

    const close = el.querySelector('[data-testid="modal-close"]') as HTMLButtonElement;
    expect(close.disabled).toBe(false);
    close.click();
    fixture.detectChanges();

    expect(closed).not.toHaveBeenCalled();
    expect(el.querySelector('.confirm-modal-card')?.textContent).toContain('Stop the upload?');
    expect(upload.observed).toBe(true);

    (el.querySelector('.confirm-modal-card .btn-confirm') as HTMLButtonElement).click();
    fixture.detectChanges();

    await vi.waitFor(() => {
      expect(closed).toHaveBeenCalledTimes(1);
      expect(upload.observed).toBe(false);
      expect(el.querySelector('[data-testid="add-book-upload-view"]')).toBeNull();
    });
  });

  it('replaces the form with the focused upload view when the file starts moving', () => {
    startResumableUpload();
    const el = fixture.nativeElement as HTMLElement;
    const view = el.querySelector('[data-testid="add-book-upload-view"]') as HTMLElement;

    expect(view).toBeTruthy();
    expect(view.textContent).toContain('The Magic Mountain');
    expect(view.textContent).toContain('magic-mountain.epub');
    expect(view.querySelector('nostos-progress')).toBeTruthy();
    expect(view.textContent).toContain('Uploading… 22% · 22 B of 100 B');
    expect(el.querySelector('button[data-testid="add-book-upload-stop"]')).toBeTruthy();
    expect(el.querySelector('input[name="title"]')).toBeNull();
    expect(el.querySelector('input[name="author"]')).toBeNull();
  });

  it('asks before close, Escape, backdrop and Cancel, and Keep open keeps the run', () => {
    const { stopSpy, closed } = startResumableUpload();
    const el = fixture.nativeElement as HTMLElement;
    const question = () => el.querySelector('.confirm-modal-card') as HTMLElement | null;
    const keepOpen = () => {
      (el.querySelector('.confirm-modal-card .btn-cancel') as HTMLButtonElement).click();
      fixture.detectChanges();
    };
    const expectQuestion = () => {
      expect(question()?.textContent).toContain('Stop the upload?');
      expect(closed).not.toHaveBeenCalled();
    };

    (el.querySelector('[data-testid="modal-close"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expectQuestion();
    keepOpen();
    expect(component.isUploading()).toBe(true);
    expect(stopSpy).not.toHaveBeenCalled();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    expectQuestion();
    // Escape again answers the question, not the sheet underneath it.
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    expect(question()).toBeNull();
    expect(closed).not.toHaveBeenCalled();

    pressBackdrop(el.querySelector('.modal-backdrop') as HTMLElement);
    fixture.detectChanges();
    expectQuestion();
    keepOpen();
    expect(closed).not.toHaveBeenCalled();

    const cancel = Array.from(el.querySelectorAll('app-dialog-actions button')).find(
      (button) => button.textContent?.trim() === 'Cancel',
    ) as HTMLButtonElement;
    expect(cancel).toBeTruthy();
    cancel.click();
    fixture.detectChanges();
    expectQuestion();
    keepOpen();

    expect(stopSpy).not.toHaveBeenCalled();
    expect(component.uploadProgress()).toBe(22);
  });

  it('Stops the run through the service and closes with the saved book', async () => {
    const { stopSpy, closed, added } = startResumableUpload();
    const el = fixture.nativeElement as HTMLElement;

    (el.querySelector('[data-testid="add-book-upload-stop"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelector('.confirm-modal-card')?.textContent).toContain('Stop the upload?');

    (el.querySelector('.confirm-modal-card .btn-confirm') as HTMLButtonElement).click();
    fixture.detectChanges();

    await vi.waitFor(() => {
      expect(stopSpy).toHaveBeenCalledWith('new-book');
      expect(closed).toHaveBeenCalledTimes(1);
      expect(added).toHaveBeenCalledTimes(1);
    });
    expect(component.uploadProgress()).toBeNull();
    expect(component.isSubmitting()).toBe(false);
    expect(el.querySelector('[data-testid="add-book-upload-view"]')).toBeNull();
  });

  it('a reopened Add book starts clean even when the old run reports late', async () => {
    const { closed, report } = startResumableUpload();
    const el = fixture.nativeElement as HTMLElement;

    (el.querySelector('[data-testid="modal-close"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (el.querySelector('.confirm-modal-card .btn-confirm') as HTMLButtonElement).click();
    fixture.detectChanges();
    await vi.waitFor(() => expect(closed).toHaveBeenCalledTimes(1));

    fixture.componentRef.setInput('isOpen', false);
    fixture.detectChanges();
    fixture.componentRef.setInput('isOpen', true);
    fixture.detectChanges();
    await fixture.whenStable();

    // The stopped run's callback is still alive in the service; it must not
    // paint onto the fresh form.
    report({ phase: 'uploading', percent: 60, uploadedBytes: 60, movedBytes: 60, totalBytes: 100 });
    fixture.detectChanges();

    expect(component.uploadProgress()).toBeNull();
    expect(component.isUploading()).toBe(false);
    expect(el.querySelector('[data-testid="add-book-upload-view"]')).toBeNull();
    expect(el.querySelector('input[name="title"]')).toBeTruthy();
  });

  it('warns before leaving the page only while the file is uploading', async () => {
    const onAdd = vi.spyOn(window, 'addEventListener');
    const onRemove = vi.spyOn(window, 'removeEventListener');
    const { run } = startResumableUpload();

    const attached = onAdd.mock.calls.filter(([type]) => type === 'beforeunload');
    expect(attached).toHaveLength(1);
    const handler = attached[0][1] as (event: BeforeUnloadEvent) => void;

    const event = new Event('beforeunload', { cancelable: true }) as BeforeUnloadEvent;
    handler(event);
    expect(event.defaultPrevented).toBe(true);

    run.resolve({} as BookUploadStatusDto);
    await vi.waitFor(() => {
      expect(
        onRemove.mock.calls.some(
          ([type, listener]) => type === 'beforeunload' && listener === handler,
        ),
      ).toBe(true);
    });
    expect(component.isUploading()).toBe(false);
  });

  it('uses the resumable upload when the host advertises it', async () => {
    const books = TestBed.inject(BooksService);
    const create = vi.spyOn(books, 'createOrMatch').mockReturnValue(
      of({ book: { id: 'new-book' } as unknown as Book, created: true }),
    );
    const legacy = vi.spyOn(books, 'uploadFile');
    const uploads = TestBed.inject(BookUploadService);
    vi.spyOn(uploads, 'supportsResumable').mockReturnValue(true);
    const resumable = vi
      .spyOn(uploads, 'upload')
      .mockReturnValue(Promise.resolve({ state: 'Complete' } as never));

    component.form.title = 'The Magic Mountain';
    component.form.type = 'ebook';
    const file = new File(['ebook'], 'magic-mountain.epub', {
      type: 'application/epub+zip',
    });
    component.selectedFile.set(file);

    component.submit();

    await vi.waitFor(() => expect(resumable).toHaveBeenCalledTimes(1));
    expect(resumable).toHaveBeenCalledWith(
      'new-book',
      file,
      expect.objectContaining({ onProgress: expect.any(Function) }),
    );
    expect(legacy).not.toHaveBeenCalled();
    // The row stays visible while parts arrive; only the legacy whole-request
    // path needs UploadPending to hide an incomplete book.
    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({ fileUploadExpected: false }),
    );
  });

  it('shows resumable failure copy and keeps the created row for a retry', async () => {
    const books = TestBed.inject(BooksService);
    vi.spyOn(books, 'createOrMatch').mockReturnValue(
      of({ book: { id: 'new-book' } as unknown as Book, created: true }),
    );
    const uploads = TestBed.inject(BookUploadService);
    vi.spyOn(uploads, 'supportsResumable').mockReturnValue(true);
    vi.spyOn(uploads, 'upload').mockReturnValue(
      Promise.reject({ code: 'book_upload_expired', status: 410 }),
    );

    component.form.title = 'The Magic Mountain';
    component.form.type = 'ebook';
    component.selectedFile.set(
      new File(['ebook'], 'magic-mountain.epub', { type: 'application/epub+zip' }),
    );

    component.submit();

    await vi.waitFor(() => expect(component.uploadError()).toContain('expired'));
    expect(component.uploadError()).toContain('same file to resume');
    expect(component.hasPendingUpload()).toBe(true);
    expect(component.isSubmitting()).toBe(false);
  });

  it('hides the form while the file runs and never creates or uploads twice', () => {
    const books = TestBed.inject(BooksService);
    const createSpy = vi
      .spyOn(books, 'createOrMatch')
      .mockReturnValue(of({ book: { id: 'new-book' } as unknown as Book, created: true }));
    const uploadResult = new Subject<any>();
    const uploadSpy = vi.spyOn(books, 'uploadFile').mockReturnValue(uploadResult);

    component.form.title = 'The Magic Mountain';
    component.form.type = 'ebook';
    component.selectedFile.set(
      new File(['ebook'], 'magic-mountain.epub', { type: 'application/epub+zip' }),
    );

    fixture.detectChanges();
    component.submit();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(createSpy).toHaveBeenCalledTimes(1);
    expect(uploadSpy).toHaveBeenCalledTimes(1);
    expect(component.isSubmitting()).toBe(true);
    expect(el.querySelector('[data-testid="add-book-upload-view"]')).toBeTruthy();
    expect(el.querySelector('input[name="title"]')).toBeNull();
    expect(el.querySelector('button[type="submit"][form="add-book-form"]')).toBeNull();

    // Enter or a programmatic re-entry is still the same pending upload, never
    // a second create or a second file request.
    component.submit();

    expect(createSpy).toHaveBeenCalledTimes(1);
    expect(uploadSpy).toHaveBeenCalledTimes(1);
  });

  it('holds the new file back when the book already exists with its own file', () => {
    const books = TestBed.inject(BooksService);
    const existing = {
      id: 'existing',
      title: 'Meditations',
      hasFile: true,
      isbn: '9780140449334',
    } as unknown as Book;
    vi.spyOn(books, 'createOrMatch').mockReturnValue(of({ book: existing, created: false }));
    const uploadResult = new Subject<any>();
    const uploadSpy = vi.spyOn(books, 'uploadFile').mockReturnValue(uploadResult);

    component.form.title = 'Meditations (scan)';
    component.form.isbn = '978-0-14-044933-4';
    component.form.type = 'ebook';
    component.selectedFile.set(new File(['pdf'], 'meditations.pdf', { type: 'application/pdf' }));
    fixture.detectChanges();
    component.submit();
    fixture.detectChanges();

    expect(uploadSpy).not.toHaveBeenCalled();
    expect(component.isSubmitting()).toBe(false);
    const notice = fixture.nativeElement.querySelector('[data-testid="add-book-existing"]');
    expect(notice?.textContent).toContain('“Meditations” is already in your library with the same ISBN');

    (
      fixture.nativeElement.querySelector(
        '[data-testid="add-book-replace-existing"]',
      ) as HTMLButtonElement
    ).click();

    expect(uploadSpy).toHaveBeenCalledTimes(1);
    expect(uploadSpy.mock.calls[0][0]).toBe('existing');
  });

  it('closes without touching the existing file when the user keeps it', () => {
    const books = TestBed.inject(BooksService);
    const existing = { id: 'existing', title: 'Meditations', hasFile: true } as unknown as Book;
    vi.spyOn(books, 'createOrMatch').mockReturnValue(of({ book: existing, created: false }));
    const uploadSpy = vi.spyOn(books, 'uploadFile');
    const closeSpy = vi.fn();
    component.closeModal.subscribe(closeSpy);

    component.form.title = 'Meditations';
    component.form.type = 'ebook';
    component.selectedFile.set(new File(['pdf'], 'meditations.pdf', { type: 'application/pdf' }));
    component.submit();
    component.keepExistingFile();

    expect(uploadSpy).not.toHaveBeenCalled();
    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('sends the file to an existing book that was still waiting for one', () => {
    const books = TestBed.inject(BooksService);
    const waiting = { id: 'waiting', title: 'Meditations', hasFile: false } as unknown as Book;
    vi.spyOn(books, 'createOrMatch').mockReturnValue(of({ book: waiting, created: false }));
    const uploadSpy = vi.spyOn(books, 'uploadFile').mockReturnValue(new Subject<any>());

    component.form.title = 'Meditations';
    component.form.type = 'ebook';
    component.selectedFile.set(new File(['epub'], 'm.epub', { type: 'application/epub+zip' }));
    component.submit();

    expect(uploadSpy).toHaveBeenCalledTimes(1);
    expect(component.existingWithFile()).toBeNull();
  });

  it('says so instead of pretending to add a book that is already there', () => {
    const books = TestBed.inject(BooksService);
    const existing = { id: 'existing', title: 'Meditations', hasFile: false } as unknown as Book;
    vi.spyOn(books, 'createOrMatch').mockReturnValue(of({ book: existing, created: false }));
    const coverSpy = vi.spyOn(books, 'uploadCover');
    const infoSpy = vi.spyOn(TestBed.inject(ToastService), 'info');
    const closeSpy = vi.fn();
    component.closeModal.subscribe(closeSpy);

    component.form.title = 'Meditations';
    component.form.type = 'physical';
    component.selectedCover.set(new File(['img'], 'cover.png', { type: 'image/png' }));
    component.submit();

    expect(infoSpy).toHaveBeenCalledWith('“Meditations” is already in your library');
    expect(coverSpy).not.toHaveBeenCalled();
    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('retries a failed upload against the created book instead of creating another row', () => {
    const books = TestBed.inject(BooksService);
    const createdBook = { id: 'existing-row' } as unknown as Book;
    const createSpy = vi.spyOn(books, 'createOrMatch').mockReturnValue(of({ book: createdBook, created: true }));
    const firstUpload = new Subject<any>();
    const secondUpload = new Subject<any>();
    const uploadSpy = vi
      .spyOn(books, 'uploadFile')
      .mockReturnValueOnce(firstUpload)
      .mockReturnValueOnce(secondUpload);
    const file = new File(['ebook'], 'magic-mountain.epub', {
      type: 'application/epub+zip',
    });

    component.form.title = 'The Magic Mountain';
    component.form.type = 'ebook';
    component.selectedFile.set(file);

    component.submit();
    firstUpload.error({ status: 504 });
    fixture.detectChanges();

    expect(component.isSubmitting()).toBe(false);
    expect(component.uploadError()).toContain('timed out');
    expect(fixture.nativeElement.textContent).toContain('Try again');

    // The primary form action is also safe recovery: it must route back to the
    // already-created row rather than POST /api/books again.
    component.submit();

    expect(createSpy).toHaveBeenCalledTimes(1);
    expect(uploadSpy).toHaveBeenCalledTimes(2);
    expect(uploadSpy).toHaveBeenNthCalledWith(2, createdBook.id, file);
    expect(component.isSubmitting()).toBe(true);
    expect(component.uploadError()).toBeNull();
  });

  it("shows the host's own words when an upload meets a plan limit", () => {
    const books = TestBed.inject(BooksService);
    const createdBook = { id: 'limited-row' } as unknown as Book;
    vi.spyOn(books, 'createOrMatch').mockReturnValue(of({ book: createdBook, created: true }));
    const upload = new Subject<any>();
    vi.spyOn(books, 'uploadFile').mockReturnValue(upload);
    component.form.title = 'Middlemarch';
    component.form.type = 'ebook';
    component.selectedFile.set(new File(['ebook'], 'middlemarch.epub', { type: 'application/epub+zip' }));

    component.submit();
    upload.error({
      status: 403,
      error: {
        code: 'trial_upload_limit_reached',
        detail: 'Your free trial includes 25 uploaded files, and you have used them all. Subscribe to keep adding books.',
      },
    });

    expect(component.uploadError()).toBe(
      'Your free trial includes 25 uploaded files, and you have used them all. Subscribe to keep adding books.',
    );
  });

  it('renders the multi-select picker with the hierarchy intact', () => {
    fixture.detectChanges();
    const options = Array.from<Element>(
      fixture.nativeElement.querySelectorAll('app-collection-picker .option'),
    ).map((b) => b.textContent?.trim() ?? '');

    expect(options).toContain('Root');
    expect(options).toContain('Child');
    expect(options).toContain('Other');
  });

  it('preselects the book collection when editing', () => {
    fixture.componentRef.setInput('book', {
      id: 'b1',
      title: 'Meditations',
      collectionIds: ['child'],
    } as unknown as Book);
    fixture.detectChanges();

    expect(component.form.collectionIds).toEqual(['child']);
  });

  it('preselects EVERY membership when the book is in several collections', () => {
    // The defect this replaces: a single <select> could only show one of them.
    fixture.componentRef.setInput('book', {
      id: 'b1',
      title: 'Meditations',
      collectionIds: ['child', 'other'],
    } as unknown as Book);
    fixture.detectChanges();

    expect(component.form.collectionIds).toEqual(['child', 'other']);
  });

  it('unchecking the last collection leaves an empty set, not a stale one', () => {
    fixture.componentRef.setInput('book', {
      id: 'b1',
      title: 'Meditations',
      collectionIds: ['child'],
    } as unknown as Book);
    fixture.detectChanges();
    expect(component.form.collectionIds).toEqual(['child']);

    // Click the option that is actually selected (the picker sorts by name, so
    // index 0 is not it) and confirm the set empties rather than keeping a
    // stale id — the behaviour the old <select> could not express at all.
    const picker = fixture.nativeElement.querySelector('app-collection-picker');
    const checked = Array.from<HTMLInputElement>(picker.querySelectorAll('.option input[type=checkbox]')).find(
      (box) => box.checked,
    )!;
    checked.click();
    fixture.detectChanges();

    expect(component.form.collectionIds).toEqual([]);
  });

  it('does not break when the book references a stale collection id', () => {
    fixture.componentRef.setInput('book', {
      id: 'b1',
      title: 'Meditations',
      collectionIds: ['gone'],
    } as unknown as Book);
    fixture.detectChanges();

    expect(component.form.collectionIds).toEqual(['gone']);
    expect(fixture.nativeElement.querySelector('app-collection-picker')).toBeTruthy();
  });

  it('submits the membership set as the only collection field', async () => {
    const books = TestBed.inject(BooksService);
    const createSpy = vi
      .spyOn(books, 'createOrMatch')
      .mockReturnValue(of({ book: { id: 'new' } as unknown as Book, created: true }));
    fixture.detectChanges();

    component.form.title = 'Meditations';
    component.form.collectionIds = ['child', 'other'];
    component.submit();

    // No singular mirror is written any more — the column is gone, so sending
    // one would be a dead field on the wire.
    expect(createSpy).toHaveBeenCalledWith(
      expect.objectContaining({ collectionIds: ['child', 'other'] }),
    );
    expect(createSpy.mock.calls[0][0]).not.toHaveProperty('collectionId');
  });
  it('opens already knowing the book it was handed, and nothing is saved by that alone', async () => {
    const books = TestBed.inject(BooksService);
    const createSpy = vi.spyOn(books, 'createOrMatch');

    fixture.componentRef.setInput('isOpen', false);
    await fixture.whenStable();
    fixture.componentRef.setInput('prefill', {
      title: 'The Rings of Saturn',
      author: 'W. G. Sebald',
      isbn: '9780811214131',
      publishedDate: '1995',
    });
    fixture.componentRef.setInput('isOpen', true);
    await fixture.whenStable();

    expect(component.form.title).toBe('The Rings of Saturn');
    expect(component.form.author).toBe('W. G. Sebald');
    expect(component.form.isbn).toBe('9780811214131');
    expect(component.form.publishedDate).toBe('1995');
    // The same words are ready for the catalogue search.
    expect(component.sourceQuery()).toBe('The Rings of Saturn W. G. Sebald');
    expect(createSpy).not.toHaveBeenCalled();
  });

  it('reports the library book it produced, whether new or already there', () => {
    const books = TestBed.inject(BooksService);
    const created = vi.fn();
    component.bookCreated.subscribe(created);

    vi.spyOn(books, 'createOrMatch').mockReturnValue(
      of({ book: { id: 'new-book' } as unknown as Book, created: true }),
    );
    component.form.title = 'Middlemarch';
    component.form.type = 'physical';
    component.submit();
    expect(created).toHaveBeenLastCalledWith('new-book');

    vi.spyOn(books, 'createOrMatch').mockReturnValue(
      of({ book: { id: 'old-book', title: 'Middlemarch' } as unknown as Book, created: false }),
    );
    component.form.title = 'Middlemarch';
    component.form.type = 'physical';
    component.submit();
    expect(created).toHaveBeenLastCalledWith('old-book');
  });
});
