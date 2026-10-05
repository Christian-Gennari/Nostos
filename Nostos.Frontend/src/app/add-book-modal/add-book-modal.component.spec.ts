import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';

import { AddBookModal } from './add-book-modal.component';
import { Book, BookLookupError, BooksService } from '../core/services/books.service';
import { ToastService } from '../core/services/toast.service';

const collections = [
  { id: 'root', name: 'Root', parentId: null },
  { id: 'child', name: 'Child', parentId: 'root' },
  { id: 'other', name: 'Other', parentId: null },
];

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
    expect(buttons.some((button) => button.textContent?.includes('Delete Book'))).toBe(false);
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
      button.textContent?.includes('Delete Book'),
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
    const createSpy = vi.spyOn(books, 'create');
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
    vi.spyOn(books, 'create').mockReturnValue(of({ id: 'physical-new' } as unknown as Book));
    const uploadSpy = vi.spyOn(books, 'uploadFile');

    component.form.title = 'Physical Meditations';
    component.form.type = 'physical';
    component.selectedFile.set(new File(['stale'], 'stale.epub', { type: 'application/epub+zip' }));
    component.submit();

    expect(uploadSpy).not.toHaveBeenCalled();
  });

  it('allows only one create while the first add request is still in flight', () => {
    const books = TestBed.inject(BooksService);
    const createResult = new Subject<Book>();
    const createSpy = vi.spyOn(books, 'create').mockReturnValue(createResult);

    component.form.title = 'The Magic Mountain';
    component.form.type = 'physical';

    component.submit();
    component.submit();

    expect(createSpy).toHaveBeenCalledTimes(1);
    expect(component.isSubmitting()).toBe(true);

    createResult.error(new Error('create failed'));
    expect(component.isSubmitting()).toBe(false);
  });

  it('keeps the add action locked after create returns while the file upload is running', () => {
    const books = TestBed.inject(BooksService);
    const createSpy = vi
      .spyOn(books, 'create')
      .mockReturnValue(of({ id: 'new-book' } as unknown as Book));
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

    const submitButton = fixture.nativeElement.querySelector(
      'button[type="submit"][form="add-book-form"]',
    ) as HTMLButtonElement;

    expect(createSpy).toHaveBeenCalledTimes(1);
    expect(uploadSpy).toHaveBeenCalledTimes(1);
    expect(component.isSubmitting()).toBe(true);
    expect(submitButton.disabled).toBe(true);
    expect(submitButton.textContent).toContain('Uploading…');

    component.submit();

    expect(createSpy).toHaveBeenCalledTimes(1);
    expect(uploadSpy).toHaveBeenCalledTimes(1);
  });

  it('retries a failed upload against the created book instead of creating another row', () => {
    const books = TestBed.inject(BooksService);
    const createdBook = { id: 'existing-row' } as unknown as Book;
    const createSpy = vi.spyOn(books, 'create').mockReturnValue(of(createdBook));
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
    expect(fixture.nativeElement.textContent).toContain('Retry upload');

    // The primary form action is also safe recovery: it must route back to the
    // already-created row rather than POST /api/books again.
    component.submit();

    expect(createSpy).toHaveBeenCalledTimes(1);
    expect(uploadSpy).toHaveBeenCalledTimes(2);
    expect(uploadSpy).toHaveBeenNthCalledWith(2, createdBook.id, file);
    expect(component.isSubmitting()).toBe(true);
    expect(component.uploadError()).toBeNull();
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
    const pressed = Array.from<Element>(picker.querySelectorAll('.option')).find(
      (b) => b.getAttribute('aria-pressed') === 'true',
    )!;
    (pressed as HTMLElement).click();
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
      .spyOn(books, 'create')
      .mockReturnValue(of({ id: 'new' } as unknown as Book));
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
});
