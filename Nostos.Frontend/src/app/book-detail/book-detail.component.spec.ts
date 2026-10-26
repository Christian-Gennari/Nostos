import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { of, Subject } from 'rxjs';

import { BookDetail } from './book-detail.component';
import { Book } from '../core/dtos/book.dtos';
import { ToastService } from '../core/services/toast.service';
import { BookUploadService } from '../core/services/book-upload.service';
import { ImportService } from '../core/services/import.service';
import { LibraryChangesService } from '../core/services/library-changes.service';
import { bookNotes, brainReads, cardNote, notesPage } from './book-detail.testing';

const book: Book = {
  id: 'b1',
  title: 'Meditations',
  subtitle: null,
  author: 'Marcus Aurelius',
  editor: null,
  translator: null,
  narrator: null,
  description: null,
  type: 'ebook',
  edition: null,
  asin: null,
  duration: null,
  isbn: null,
  publisher: null,
  placeOfPublication: null,
  publishedDate: null,
  pageCount: null,
  language: null,
  categories: null,
  series: null,
  volumeNumber: null,
  createdAt: '2026-08-01T08:00:00+02:00',
  hasFile: false,
  fileName: null,
  coverUrl: null,
  collectionIds: [],
  lastLocation: null,
  progressPercent: 0,
  lastReadAt: null,
  rating: 0,
  isFavorite: false,
  personalReview: null,
  finishedAt: null,
};

describe('BookDetail reset progress', () => {
  let component: BookDetail;
  let fixture: ComponentFixture<BookDetail>;
  let httpMock: HttpTestingController;
  let toast: ToastService;

  /** Readable book with progress worth resetting (partial read). */
  function readableBook(overrides: Partial<Book> = {}): Book {
    return {
      ...book,
      hasFile: true,
      progressPercent: 42,
      lastLocation: 'epub.cfi',
      lastReadAt: '2026-08-10T08:00:00+02:00',
      ...overrides,
    };
  }

  async function setup(initial: Book = readableBook()) {
    fixture = TestBed.createComponent(BookDetail);
    component = fixture.componentInstance;
    await fixture.whenStable();
    fixture.detectChanges();

    httpMock.expectOne('/api/books/b1').flush(initial);
    httpMock.expectOne(bookNotes).flush(notesPage([]));
    httpMock.expectOne('/api/collections').flush([]);
    httpMock.match(brainReads).forEach((request) => request.flush([]));
    fixture.detectChanges();
  }

  /** The reading status is the shared dropdown; its trigger is the status pill. */
  function statusChipButton(): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('.status-dropdown .nostos-dropdown__trigger');
  }

  function statusOptions(): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.status-dropdown .nostos-dropdown__option'));
  }

  function openResetViaDropdown(): void {
    const chip = statusChipButton();
    chip?.click();
    fixture.detectChanges();
    const items = statusOptions();
    const notStartedItem = items.find((el) => el.textContent?.includes('Not started'));
    notStartedItem?.click();
    fixture.detectChanges();
  }

  function resetDialog(): HTMLElement | null {
    return fixture.nativeElement.querySelector('.status-confirm .confirm-modal-card');
  }

  function resetConfirmButton(): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('.status-confirm .btn-confirm.nostos-button--danger');
  }

  function resetCancelButton(): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('.status-confirm .btn-cancel');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BookDetail],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { paramMap: of(convertToParamMap({ id: book.id })) },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    toast = TestBed.inject(ToastService);
  });

  afterEach(() => {
    httpMock.match(brainReads).forEach((request) => request.flush([]));
    httpMock.verify();
  });

  it('displays the status chip button reflecting current reading state', async () => {
    await setup(readableBook({ progressPercent: 42 }));
    const chip = statusChipButton();
    expect(chip).toBeTruthy();
    expect(chip!.textContent).toContain('Reading');
  });

  it('carries the reading figure inside the status pill, not beside it', async () => {
    await setup(readableBook({ progressPercent: 42 }));
    const chip = statusChipButton()!;
    expect(chip.querySelector('.nostos-dropdown__detail')?.textContent?.trim()).toBe('· 42%');
    expect(chip.getAttribute('aria-label')).toBe('Reading status, 42% read');
    expect(fixture.nativeElement.querySelector('.strip-progress')).toBeNull();
  });

  it('shows no figure on a finished book, where it would only repeat the status', async () => {
    await setup(readableBook({ progressPercent: 100, finishedAt: '2026-08-02T08:00:00+02:00' }));
    const chip = statusChipButton()!;
    expect(chip.textContent).toContain('Finished');
    expect(chip.querySelector('.nostos-dropdown__detail')).toBeNull();
    expect(chip.getAttribute('aria-label')).toBe('Reading status');
  });

  it('groups the hero strip into verdict, state and action rows', async () => {
    await setup(readableBook({ progressPercent: 42, otherEditions: multiEditionBook().otherEditions }));
    const strip = fixture.nativeElement.querySelector('.stage-strip') as HTMLElement;
    const rows = Array.from(strip.children).map((row) => row.className.split(' ')[0]);
    expect(rows).toEqual(['strip-verdict', 'strip-state', 'primary-actions']);
    expect(strip.querySelector('.strip-verdict app-star-rating')).toBeTruthy();
    expect(strip.querySelector('.strip-verdict .favorite-btn')).toBeTruthy();
    expect(strip.querySelector('.strip-state .edition-switcher')).toBeTruthy();
    expect(strip.querySelector('.strip-state .status-dropdown')).toBeTruthy();
  });

  it('prompts to write the first note when the feed is empty', async () => {
    await setup(readableBook());

    const empty = fixture.nativeElement.querySelector('.notes-empty') as HTMLElement;
    expect(empty?.textContent).toContain('first thought');
  });

  it('says the notes could not be loaded, with Try again, instead of "No notes yet"', async () => {
    fixture = TestBed.createComponent(BookDetail);
    component = fixture.componentInstance;
    await fixture.whenStable();
    fixture.detectChanges();
    httpMock.expectOne('/api/books/b1').flush(readableBook());
    httpMock.expectOne(bookNotes).flush('nope', { status: 500, statusText: 'Server Error' });
    httpMock.expectOne('/api/collections').flush([]);
    httpMock.match(brainReads).forEach((request) => request.flush([]));
    fixture.detectChanges();

    const failed = fixture.nativeElement.querySelector('.notes-empty') as HTMLElement;
    expect(failed.getAttribute('role') ?? failed.querySelector('[role="alert"]')?.getAttribute('role')).toBe('alert');
    expect(failed.textContent).toContain('Your notes could not be loaded.');

    (failed.querySelector('button') as HTMLButtonElement).click();
    httpMock.expectOne(bookNotes).flush(notesPage([{ id: 'n1', content: 'Back again' }]));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.notes-empty')).toBeNull();
  });

  it('keeps a new note\'s text until it is saved', async () => {
    await setup();
    const toastError = vi.spyOn(toast, 'error');
    component.newNote.set('A thought worth keeping');
    component.addNote();

    httpMock.expectOne((r) => r.method === 'POST' && r.url.includes('/notes')).flush('no', { status: 500, statusText: 'Server Error' });
    expect(component.newNote()).toBe('A thought worth keeping');
    expect(toastError).toHaveBeenCalledWith('Could not save the note. Try again.');

    component.addNote();
    httpMock.expectOne((r) => r.method === 'POST' && r.url.includes('/notes')).flush({ id: 'n9', content: 'A thought worth keeping' });
    expect(component.newNote()).toBe('');
    httpMock.expectOne(bookNotes).flush(notesPage([]));
  });

  it('uses shared primitives for ordinary Book Detail controls without flattening product interactions', async () => {
    await setup(readableBook({ isFavorite: true }));

    const actions = fixture.nativeElement.querySelector('.primary-actions') as HTMLElement;
    const ordinaryActions = Array.from(actions.querySelectorAll('button')) as HTMLButtonElement[];
    expect(ordinaryActions.length).toBeGreaterThan(0);
    expect(ordinaryActions.every((button) => button.classList.contains('nostos-button'))).toBe(true);

    const favorite = fixture.nativeElement.querySelector('.favorite-btn') as HTMLButtonElement;
    expect(favorite.classList.contains('icon-btn')).toBe(true);
    expect(favorite.getAttribute('aria-pressed')).toBe('true');

    const notesBadge = fixture.nativeElement.querySelector('.notes-header .nostos-badge') as HTMLElement;
    expect(notesBadge).toBeTruthy();

    // These controls carry Book Detail-specific interaction contracts and must
    // remain product-owned rather than being flattened into generic primitives.
    expect(statusChipButton()!.classList.contains('nostos-chip')).toBe(false);
    // "← Library" navigates, so it is a real link drawn by the button primitive.
    const back = fixture.nativeElement.querySelector('.btn-back') as HTMLAnchorElement;
    expect(back.tagName).toBe('A');
    expect(back.getAttribute('href')).toBe('/library');
    expect(back.classList.contains('nostos-button--ghost')).toBe(true);
  });

  it('surfaces a timed-out replacement upload instead of failing silently', async () => {
    await setup(readableBook());
    const errorToast = vi.spyOn(toast, 'error').mockImplementation(() => undefined);

    component.store.uploadFile(
      new File(['replacement'], 'meditations.epub', { type: 'application/epub+zip' }),
    );

    httpMock
      .expectOne('/api/books/b1/file')
      .flush('timeout', { status: 504, statusText: 'Gateway Timeout' });

    expect(errorToast).toHaveBeenCalledWith('The upload timed out. Try again.');
  });

  it('uses the resumable contract when the host advertises it', async () => {
    await setup(readableBook());
    const uploads = TestBed.inject(BookUploadService);
    vi.spyOn(uploads, 'supportsResumable').mockReturnValue(true);
    const done = vi.fn().mockResolvedValue({ state: 'Complete' });
    const upload = vi.spyOn(uploads, 'upload').mockImplementation(done);
    const file = new File(['replacement'], 'meditations.epub', {
      type: 'application/epub+zip',
    });

    component.store.uploadFile(file);

    expect(upload).toHaveBeenCalledWith('b1', file);
    httpMock.expectNone('/api/books/b1/file');
    await vi.waitFor(() => expect(done).toHaveBeenCalledTimes(1));
    await vi.waitFor(() => expect(httpMock.match('/api/books/b1')).toHaveLength(1));
    httpMock.match('/api/books/b1').forEach((request) => request.flush(readableBook()));
    await vi.waitFor(() => expect(component.store.uploadingFile()).toBe(false));
  });

  it('does not offer file upload for a physical metadata-only book', async () => {
    await setup(readableBook({ type: 'physical', hasFile: false, fileName: null }));

    const actions = fixture.nativeElement.querySelector('.primary-actions') as HTMLElement;
    expect(actions.textContent).not.toContain('Upload file');
    expect(actions.querySelector('input[type="file"][accept*=".epub"]')).toBeNull();

    (actions.querySelector('.edit-metadata-btn') as HTMLButtonElement).click();
    fixture.detectChanges();

    // The menu is the shared nostos-menu, which renders in the CDK overlay
    // outside the component's own element.
    const editions = Array.from(
      document.querySelectorAll('.nostos-menu-item') as NodeListOf<HTMLElement>,
    ).find((item) => item.querySelector('.nostos-menu-item__label')?.textContent?.trim() === 'Editions');

    expect(editions?.querySelector('.nostos-menu-item__hint')?.textContent).toContain(
      'separately added digital editions',
    );
  });

  it('still offers file upload for a digital book without a file', async () => {
    await setup(readableBook({ type: 'ebook', hasFile: false, fileName: null }));

    const actions = fixture.nativeElement.querySelector('.primary-actions') as HTMLElement;
    expect(actions.textContent).toContain('Upload file');
    expect(actions.querySelector('input[type="file"][accept*=".epub"]')).toBeTruthy();
  });

  it('shows a truthful import state on direct entry and blocks every upload entry point', async () => {
    await setup(readableBook({ status: 1, hasFile: false, fileName: null }));
    // Direct Book Details navigation must discover imports without visiting Library.
    const active = httpMock.expectOne('/api/imports/active');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.detail-import-progress')?.textContent)
      .toContain('Downloading');
    expect(fixture.nativeElement.querySelector('.detail-import-progress')?.textContent)
      .not.toContain('%'); // No fabricated percentage before the feed arrives.
    expect(fixture.nativeElement.querySelector('.primary-actions')?.textContent)
      .not.toContain('Upload file');

    const picker = vi.fn();
    component.fileInput = { nativeElement: { click: picker } } as any;
    component.triggerFilePicker();
    component.onFileUploadSelected({
      target: { files: [new File(['data'], 'other.epub')], value: 'other.epub' },
    } as unknown as Event);
    component.store.uploadFile(new File(['data'], 'other.epub'));
    expect(picker).not.toHaveBeenCalled();
    httpMock.expectNone('/api/books/b1/file');
    active.flush([]);
  });

  it('reuses per-book feed percentages and changes to reader actions on completion', async () => {
    await setup(readableBook({ status: 2, hasFile: false, fileName: null, progressPercent: 42 }));
    httpMock.expectOne('/api/imports/active').flush([{
      id: 'job-1', source: 'job', state: 'running', stage: 'importing',
      percent: 73, detail: null, providerId: 'librivox', externalId: '123',
      assetId: 'mp3', bookId: 'b1', title: 'Meditations', author: null,
      coverUrl: null, errorCode: null, message: null, createdAt: '',
      updatedAt: '', canRetry: true,
    }]);
    fixture.detectChanges();

    const bar = fixture.nativeElement.querySelector('.detail-import-track') as HTMLElement;
    expect(bar.tagName).toBe('NOSTOS-PROGRESS');
    expect(bar.getAttribute('aria-valuenow')).toBe('73');
    expect(fixture.nativeElement.querySelector('.detail-import-progress')?.textContent)
      .toContain('Saving');
    expect(fixture.nativeElement.querySelector('.detail-import-progress')?.textContent)
      .toContain('73%');
    // Not the independent reading progress (42%).
    expect(bar.getAttribute('aria-valuenow')).not.toBe('42');

    const imports = TestBed.inject(ImportService);
    // The real terminal event removes the running entry before the book fetch resolves.
    imports.dismiss(imports.imports()[0]);
    imports.bookPatched.next(
      readableBook({ status: 0, hasFile: true, fileName: 'book.epub' }),
    );
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.detail-import-progress')).toBeNull();
    expect(fixture.nativeElement.querySelector('.primary-actions')?.textContent)
      .toContain('Read book');
  });

  it('renders a failed provider import without hiding its failure reason', async () => {
    await setup(readableBook({
      status: 3, hasFile: false, fileName: null, statusMessage: 'Download failed',
    }));
    expect(fixture.nativeElement.querySelector('.detail-import-failed')?.textContent)
      .toContain('Download failed');
    expect(fixture.nativeElement.querySelector('.detail-import-progress')).toBeNull();
  });

  it('toggles the status dropdown menu open and closed on chip click', async () => {
    await setup(readableBook());

    expect(statusChipButton()!.getAttribute('aria-expanded')).toBe('false');

    statusChipButton()!.click();
    fixture.detectChanges();

    expect(statusChipButton()!.getAttribute('aria-expanded')).toBe('true');
    expect(statusOptions().length).toBe(3);

    // Clicking again closes it
    statusChipButton()!.click();
    fixture.detectChanges();
    expect(statusChipButton()!.getAttribute('aria-expanded')).toBe('false');
  });

  it('opens confirmation modal when selecting Not Started from status dropdown', async () => {
    await setup(readableBook());

    openResetViaDropdown();

    const dialog = resetDialog();
    expect(dialog).toBeTruthy();
    expect(dialog!.textContent).toContain('Reset to not started?');
    expect(dialog!.textContent).toContain('start from the beginning');
    expect(dialog!.querySelector('.nostos-dialog-actions--inset')).toBeTruthy();
    expect(resetConfirmButton()).toBeTruthy();
    expect(resetCancelButton()).toBeTruthy();
    expect(httpMock.match((req) => req.method === 'POST' && req.url === '/api/books/b1/progress/reset').length).toBe(0);
  });

  it('asks only for the reset, in the danger tone; Finished is one tap, as in the Library', async () => {
    await setup(readableBook());

    openResetViaDropdown();
    expect(resetDialog()!.getAttribute('role')).toBe('alertdialog');
    expect(resetDialog()!.querySelector('.confirm-mark')).toBeTruthy();
    resetCancelButton()!.click();
    fixture.detectChanges();

    component.selectStatusOption('finished');
    fixture.detectChanges();
    expect(resetDialog()).toBeNull();
    const put = httpMock.expectOne((req) => req.method === 'PUT' && req.url === '/api/books/b1');
    expect(put.request.body).toEqual({ isFinished: true });
    put.flush(readableBook({ progressPercent: 100, finishedAt: '2026-10-24T00:00:00Z' }));
  });

  it('keeps the reset question open and busy until the server answers, and open on failure', async () => {
    await setup(readableBook());

    openResetViaDropdown();
    resetConfirmButton()!.click();
    fixture.detectChanges();

    // In flight: still up, locked, saying so.
    expect(resetDialog()).toBeTruthy();
    expect(resetDialog()!.textContent).toContain('Resetting…');
    expect(resetConfirmButton()!.disabled).toBe(true);
    expect(resetCancelButton()!.disabled).toBe(true);
    component.cancelStatusChange();
    expect(component.pendingStatus()).toBe('notstarted');

    httpMock
      .expectOne((req) => req.method === 'POST' && req.url === '/api/books/b1/progress/reset')
      .flush({}, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    // Failed: the question stays, the button is usable again, a toast says why.
    expect(resetDialog()).toBeTruthy();
    expect(resetConfirmButton()!.disabled).toBe(false);
    expect(toast.toasts().some((t) => t.message === 'Could not reset progress' && t.type === 'error')).toBe(true);
  });

  it('does nothing when the status change confirmation is cancelled', async () => {
    await setup(readableBook());

    openResetViaDropdown();
    expect(resetDialog()).toBeTruthy();

    resetCancelButton()!.click();
    fixture.detectChanges();

    expect(resetDialog()).toBeNull();
    expect(httpMock.match((req) => req.method === 'POST' && req.url === '/api/books/b1/progress/reset').length).toBe(0);
    expect(component.store.book()?.progressPercent).toBe(42);
  });

  it('closes the status confirmation dialog without submitting when backdrop is clicked', async () => {
    await setup(readableBook());

    openResetViaDropdown();
    expect(resetDialog()).toBeTruthy();

    const backdrop = fixture.nativeElement.querySelector('.status-confirm .modal-backdrop') as HTMLElement;
    for (const type of ['pointerdown', 'pointerup'] as const) {
      const event = new Event(type, { bubbles: true }) as PointerEvent;
      Object.defineProperty(event, 'pointerId', { value: 1 });
      backdrop.dispatchEvent(event);
    }
    fixture.detectChanges();

    expect(resetDialog()).toBeNull();
    expect(httpMock.match((req) => req.method === 'POST' && req.url === '/api/books/b1/progress/reset').length).toBe(0);
  });

  it('confirms Not Started, then calls reset endpoint and refetches the book', async () => {
    await setup(readableBook());

    openResetViaDropdown();
    resetConfirmButton()!.click();
    fixture.detectChanges();

    const posts = httpMock.match((req) => req.method === 'POST' && req.url === '/api/books/b1/progress/reset');
    expect(posts.length).toBe(1);
    expect(posts[0].request.body).toBeNull();

    posts[0].flush({ updated: true });
    const refetch = httpMock.expectOne('/api/books/b1');
    expect(refetch.request.method).toBe('GET');
    refetch.flush(
      readableBook({ progressPercent: 0, lastLocation: null, lastReadAt: null, finishedAt: null })
    );
    fixture.detectChanges();

    expect(component.store.book()?.progressPercent).toBe(0);
    expect(resetDialog()).toBeNull();
    expect(toast.toasts().some((t) => t.message === 'Reading progress reset' && t.type === 'success')).toBe(true);
  });

  // ── Hero edition switcher (issues #1062, #1086) ─────────────────────────────
  // The one place to switch edition and the one list of the work's editions: a
  // pill in the status row that opens a menu of every edition. The rail's
  // "Available Formats & Editions" card, which stacked below the notes feed on a
  // phone and read as a second switcher, is gone. Labelling and ordering are
  // covered in edition-switch-options.spec.ts, the control in
  // edition-switcher.component.spec.ts.

  function editionTrigger(): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('.strip-state .edition-switcher .es-trigger');
  }

  /** Opens the edition menu; its items and each as "label · hint". */
  function editionMenu(): { items: HTMLButtonElement[]; text: string[] } {
    editionTrigger()!.click();
    fixture.detectChanges();
    const items = Array.from(document.querySelectorAll('.nostos-menu-item')) as HTMLButtonElement[];
    const text = items.map((item) => {
      const part = (selector: string) => item.querySelector(selector)?.textContent?.trim();
      return `${part('.nostos-menu-item__label')} · ${part('.nostos-menu-item__hint')}`;
    });
    return { items, text };
  }

  it('puts an edition pill in the hero status row that switches from its menu', async () => {
    const router = TestBed.inject(Router);
    const navigateSpy = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    await setup(readableBook({ fileName: 'book.epub', otherEditions: multiEditionBook().otherEditions }));

    const nav = fixture.nativeElement.querySelector('.stage-strip .edition-switcher nav') as HTMLElement;
    expect(nav.getAttribute('aria-label')).toBe('Switch edition');
    // It precedes the notes feed in document order, so no scrolling past notes.
    expect(nav.compareDocumentPosition(fixture.nativeElement.querySelector('.detail-grid')))
      .toBe(Node.DOCUMENT_POSITION_FOLLOWING);
    expect(editionTrigger()!.textContent?.trim()).toBe('Ebook');

    editionMenu().items[0].click();
    expect(navigateSpy).toHaveBeenCalledWith(['/library', 'b2']);
  });

  it('lists every edition with its progress, the current one marked aria-current', async () => {
    await setup(
      readableBook({
        fileName: 'book.epub',
        progressPercent: 42,
        otherEditions: [
          { id: 'b2', type: 'audiobook', format: 'AUDIO', progressPercent: 15, hasFile: true },
          { id: 'b3', type: 'ebook', fileName: 'book.pdf', progressPercent: 100, hasFile: true, finishedAt: '2026-08-02T08:00:00+02:00' },
        ],
      }),
    );

    const { items, text } = editionMenu();
    expect(text).toEqual(['Audiobook · 15% read', 'Ebook · 42% read', 'PDF · Finished']);
    expect(items.map((item) => item.getAttribute('aria-current'))).toEqual([null, 'page', null]);
    // The status pill keeps the current edition's own figure.
    expect(statusChipButton()?.querySelector('.nostos-dropdown__detail')?.textContent?.trim()).toBe('· 42%');
  });

  it('renders no second editions list in the rail', async () => {
    await setup(multiEditionBook());

    expect(editionTrigger()).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.detail-side')?.textContent).not.toContain('Available Formats');
    expect(fixture.nativeElement.querySelector('.edition-section')).toBeNull();
  });

  it('tells apart two editions of one format by their edition detail', async () => {
    await setup(
      readableBook({
        fileName: 'book.epub',
        edition: '1st',
        progressPercent: 42,
        otherEditions: [{ id: 'b2', type: 'ebook', fileName: 'other.epub', edition: '2nd', progressPercent: 0, hasFile: true }],
      }),
    );

    expect(editionTrigger()!.textContent?.trim()).toBe('Ebook · 1st edition');
    expect(editionMenu().text).toEqual(['Ebook · 1st edition · 42% read', 'Ebook · 2nd edition · Not started']);
  });

  // ── Collapsed notes feed (issue #1062) ──────────────────────────────────────

  function seedNotes(count: number): void {
    component.store.notes.set(
      Array.from({ length: count }, (_, i) => cardNote({
        id: `n${i}`,
        content: `Note ${i}`,
        createdAt: `2026-08-${String(i + 1).padStart(2, '0')}T08:00:00Z`,
      })),
    );
    fixture.detectChanges();
  }

  function renderedNoteIds(): string[] {
    return component.visibleNotes().map((note) => note.id);
  }

  function showEarlierButton(): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('.notes-show-earlier');
  }

  it('shows only the latest notes of a long feed until asked', async () => {
    await setup();
    seedNotes(25);

    expect(fixture.nativeElement.querySelectorAll('.notes-feed app-note-item').length).toBe(10);
    // The latest ones, still oldest-first.
    expect(renderedNoteIds()).toEqual(Array.from({ length: 10 }, (_, i) => `n${i + 15}`));
    // The header count is still every note.
    expect(fixture.nativeElement.querySelector('.notes-header')?.textContent).toContain('25');

    const reveal = showEarlierButton()!;
    expect(reveal.textContent).toContain('Show 15 earlier notes');
    reveal.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.notes-feed app-note-item').length).toBe(25);
    expect(showEarlierButton()).toBeNull();
  });

  it('shows a short feed whole, with no reveal', async () => {
    await setup();
    seedNotes(10);

    expect(fixture.nativeElement.querySelectorAll('.notes-feed app-note-item').length).toBe(10);
    expect(showEarlierButton()).toBeNull();
  });

  it('says "note" for a single hidden note', async () => {
    await setup();
    seedNotes(11);

    expect(showEarlierButton()?.textContent).toContain('Show 1 earlier note');
    expect(showEarlierButton()?.textContent).not.toContain('notes');
  });

  it('renders no format switcher for a single-edition book', async () => {
    await setup(readableBook());

    expect(fixture.nativeElement.querySelector('.edition-switcher')).toBeNull();
  });

  // ── Advanced work membership (issue #143) ───────────────────────────────────
  // A collapsed, secondary surface that corrects an automatic grouping. It must
  // be present even for a book that is alone in its work — that is how a group
  // is created — and every mutation goes through the service, never a direct
  // WorkId write.

  /** The hero Edit chooser button. */
  function editButton(): HTMLButtonElement {
    return fixture.nativeElement.querySelector('.edit-metadata-btn') as HTMLButtonElement;
  }

  function editMenuItem(label: string): HTMLButtonElement | null {
    return (
      // nostos-menu renders in the CDK overlay, outside the fixture's element.
      Array.from(
        document.querySelectorAll('.nostos-menu-item') as NodeListOf<HTMLButtonElement>,
      ).find((item) => item.querySelector('.nostos-menu-item__label')?.textContent?.trim() === label) ??
      null
    );
  }

  /** The membership modal root, present only while it is open. */
  function editionsModal(): HTMLElement | null {
    return fixture.nativeElement.querySelector('.editions-modal-card');
  }

  function multiEditionBook(): Book {
    return readableBook({
      otherEditions: [
        {
          id: 'b2',
          type: 'audiobook',
          format: 'AUDIO',
          progressPercent: 15,
          hasFile: true,
          narrator: 'Narrator Guy',
          title: 'A Completely Different Book',
          author: 'Another Author',
        },
      ],
    });
  }

  /**
   * Open the membership modal through its one entry point, the Editions item on
   * the hero Edit chooser. Callers own the candidate-query flush, because several
   * of them assert on the request itself.
   */
  function openManage(): void {
    editButton().click();
    fixture.detectChanges();
    editMenuItem('Editions')!.click();
    fixture.detectChanges();
  }

  /** The empty-candidate response every open produces. */
  function flushNoCandidates(): void {
    httpMock
      .expectOne((req) => req.url === '/api/books' && req.method === 'GET')
      .flush({ items: [], totalCount: 0, page: 1, pageSize: 20 });
    fixture.detectChanges();
  }

  it('opens the membership modal from the Edit menu, and not before', async () => {
    await setup(multiEditionBook());

    // Nothing of the working surface rests on the page: no modal, no picker, so
    // the ordinary reading flow is uncluttered by construction.
    expect(editionsModal()).toBeNull();
    expect(fixture.nativeElement.querySelector('.manage-link-search')).toBeNull();

    openManage();

    expect(editionsModal()).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.manage-link-search')).toBeTruthy();
    httpMock.expectOne((req) => req.url === '/api/books' && req.method === 'GET')
      .flush({ items: [], totalCount: 0, page: 1, pageSize: 20 });
  });

  it('offers a route to managing editions for a single-edition book too', async () => {
    await setup(readableBook());

    // A lone book has no format switcher, and linking it is exactly what it
    // needs: the hero Edit chooser is the route, and it is present for every book.
    expect(editButton()).toBeTruthy();
    editButton().click();
    fixture.detectChanges();

    const editionsItem = editMenuItem('Editions');
    expect(editionsItem).toBeTruthy();

    editionsItem!.click();
    fixture.detectChanges();

    expect(editionsModal()).toBeTruthy();
    httpMock.expectOne((req) => req.url === '/api/books' && req.method === 'GET')
      .flush({ items: [], totalCount: 0, page: 1, pageSize: 20 });
  });

  it('keeps the Edit chooser offering Edit Book alongside editions', async () => {
    await setup(readableBook());

    editButton().click();
    fixture.detectChanges();

    const bookDetails = editMenuItem('Book details');
    expect(bookDetails).toBeTruthy();

    bookDetails!.click();
    fixture.detectChanges();

    // Edit Book still opens its own form, and the chooser closed behind it.
    // The dialog element now belongs to `app-modal-shell`, so this asserts the
    // role rather than the card class that moved into the shell.
    expect(fixture.nativeElement.querySelector('app-add-book-modal [role="dialog"]')).toBeTruthy();
    expect(editMenuItem('Book details')).toBeNull();
  });

  it('loads link candidates when the membership modal is opened', async () => {
    await setup(readableBook());

    openManage();

    // Candidates come from the canonical list endpoint, grouped-by-work off so a
    // collapsed work still yields its individual books.
    const request = httpMock.expectOne((req) => req.url === '/api/books' && req.method === 'GET');
    expect(request.request.params.get('groupByWork')).toBe('false');
    request.flush({
      items: [
        { id: 'b1', title: 'Meditations', author: 'Marcus Aurelius', workId: 'w1', editionCount: 1 },
        { id: 'b2', title: 'Something Else', author: 'Another Author', workId: 'w2', editionCount: 1 },
      ],
      totalCount: 2,
      page: 1,
      pageSize: 20,
    });
    fixture.detectChanges();

    const candidates = fixture.nativeElement.querySelectorAll('.manage-link-candidate');
    // The current book is excluded: linking it to itself is not offered.
    expect(candidates.length).toBe(1);
    expect(candidates[0].textContent).toContain('Something Else');
  });

  it('labels the candidate row with an explicit Link action', async () => {
    await setup(readableBook());
    openManage();
    httpMock.expectOne((req) => req.url === '/api/books').flush({
      items: [{ id: 'b2', title: 'Something Else', author: 'Another Author', workId: 'w2', editionCount: 1 }],
      totalCount: 1, page: 1, pageSize: 20,
    });
    fixture.detectChanges();

    // The row is the click target, but a click target with no label reads as
    // static text — the only other cue was a hover state, which touch never
    // shows. The label is the affordance, so it must exist in the DOM.
    const row = fixture.nativeElement.querySelector('.manage-link-candidate') as HTMLElement;
    const action = row.querySelector('.manage-link-candidate-action') as HTMLElement;
    expect(action).toBeTruthy();
    expect(action.textContent?.trim()).toBe('Link');

    // It is a label inside the button, not a nested button: a button inside a
    // button is invalid HTML and would swallow the click target.
    expect(action.tagName.toLowerCase()).toBe('span');
  });

  it('offers each candidate WORK once, however many editions it has', async () => {
    await setup(readableBook());
    openManage();
    httpMock.expectOne((req) => req.url === '/api/books').flush({
      items: [
        // One work with two editions — a group-level choice, so it is one row.
        { id: 'j1', title: 'Justice', author: 'Michael J. Sandel', workId: 'wj', editionCount: 2 },
        { id: 'j2', title: 'Justice', author: 'Michael J. Sandel', workId: 'wj', editionCount: 2 },
        { id: 'b9', title: 'Another Book', author: 'Someone', workId: 'w9', editionCount: 1 },
      ],
      totalCount: 3, page: 1, pageSize: 20,
    });
    fixture.detectChanges();

    const candidates = [...fixture.nativeElement.querySelectorAll('.manage-link-candidate')];
    expect(candidates.length).toBe(2);
    // The surviving row reports the WORK's size, which is what the merge
    // warning reads.
    expect(candidates[0].textContent).toContain('Justice');
    expect(candidates[0].textContent).toContain('2 editions');
  });

  it('lists every work member and offers Unlink only for the others', async () => {
    await setup(multiEditionBook());
    openManage();
    flushNoCandidates();

    const members = fixture.nativeElement.querySelectorAll('.manage-member');
    expect(members.length).toBe(2);
    // The book you are on is context; the sibling is the thing you can detach. The
    // current row wears the page's own "you are here" state (selection fill + the
    // Current badge), which is also why the badge sits in the action slot.
    expect(members[0].classList.contains('is-current')).toBe(true);
    expect(members[0].querySelector('.manage-member-flag')?.textContent).toContain('Current');
    expect(members[0].querySelector('.manage-member-action')).toBeNull();
    expect(members[1].classList.contains('is-current')).toBe(false);
    expect(members[1].querySelector('.manage-member-action')?.textContent).toContain('Unlink');

    // Each row names the book it is about. A sibling must NOT be labelled with
    // the current book's title — that would misname the book Unlink detaches.
    expect(members[0].textContent).toContain('Meditations');
    expect(members[1].textContent).toContain('A Completely Different Book');
    expect(members[1].textContent).toContain('Another Author');
    expect(members[1].textContent).not.toContain('Meditations');
  });

  it('links a selected book through the service and refetches the book', async () => {
    await setup(readableBook());
    // Opened the lone-book way: the hero Edit chooser.
    editButton().click();
    fixture.detectChanges();
    editMenuItem('Editions')!.click();
    fixture.detectChanges();
    httpMock.expectOne((req) => req.url === '/api/books').flush({
      items: [{ id: 'b9', title: 'Other Book', author: 'Someone', workId: 'w9', editionCount: 1 }],
      totalCount: 1, page: 1, pageSize: 20,
    });
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.manage-link-candidate') as HTMLButtonElement).click();
    fixture.detectChanges();

    // Confirmation first: a link can merge two whole groups, which is not
    // obvious from "link these two books".
    const dialog = fixture.nativeElement.querySelector('.confirm-modal-card') as HTMLElement;
    expect(dialog.textContent).toContain('Other Book');
    expect(httpMock.match((req) => req.url.includes('/work/link')).length).toBe(0);

    (dialog.querySelector('.btn-confirm') as HTMLButtonElement).click();
    fixture.detectChanges();

    const post = httpMock.expectOne((req) => req.method === 'POST' && req.url === '/api/books/b1/work/link');
    expect(post.request.body).toEqual({ targetBookId: 'b9' });
    post.flush({ bookId: 'b1', workId: 'w9', editionCount: 2 });

    // The server owns the grouping decision, so the page re-reads the book
    // rather than patching workId locally.
    httpMock.expectOne('/api/books/b1').flush(readableBook());
    fixture.detectChanges();

    expect(toast.toasts().some((t) => t.message.includes('Linked'))).toBe(true);
  });

  it('warns that a link merges both groups when either side is multi-edition', async () => {
    await setup(multiEditionBook());
    openManage();
    httpMock.expectOne((req) => req.url === '/api/books').flush({
      items: [{ id: 'b9', title: 'Grouped Book', author: 'Someone', workId: 'w9', editionCount: 3 }],
      totalCount: 1, page: 1, pageSize: 20,
    });
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.manage-link-candidate') as HTMLButtonElement).click();
    fixture.detectChanges();

    const dialog = fixture.nativeElement.querySelector('.confirm-modal-card') as HTMLElement;
    expect(dialog.textContent).toContain('everything already grouped with them');
  });

  it('unlinks a work member through the service after confirmation', async () => {
    await setup(multiEditionBook());
    openManage();
    flushNoCandidates();

    const unlinkButton = fixture.nativeElement.querySelector('.manage-member-action') as HTMLButtonElement;
    unlinkButton.click();
    fixture.detectChanges();

    const dialog = fixture.nativeElement.querySelector('.confirm-modal-card') as HTMLElement;
    expect(dialog.textContent).toContain('Unlink');
    expect(httpMock.match((req) => req.url.includes('/work/unlink')).length).toBe(0);

    (dialog.querySelector('.btn-confirm') as HTMLButtonElement).click();
    fixture.detectChanges();

    const post = httpMock.expectOne((req) => req.method === 'POST' && req.url === '/api/books/b2/work/unlink');
    post.flush({ bookId: 'b2', workId: 'w-new', editionCount: 1 });

    httpMock.expectOne('/api/books/b1').flush(readableBook());
    fixture.detectChanges();

    expect(toast.toasts().some((t) => t.message.includes('Unlinked'))).toBe(true);
  });

  it('does nothing when a work-membership confirmation is cancelled', async () => {
    await setup(multiEditionBook());
    openManage();
    flushNoCandidates();

    (fixture.nativeElement.querySelector('.manage-member-action') as HTMLButtonElement).click();
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.confirm-modal-card .btn-cancel') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeNull();
    expect(httpMock.match((req) => req.url.includes('/work/')).length).toBe(0);
  });

  it('narrows candidates by the management search box', async () => {
    await setup(readableBook());
    openManage();
    flushNoCandidates();

    const input = fixture.nativeElement.querySelector('.manage-link-search') as HTMLInputElement;
    input.value = 'medit';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const request = httpMock.expectOne((req) => req.url === '/api/books');
    expect(request.request.params.get('search')).toBe('medit');
    request.flush({ items: [], totalCount: 0, page: 1, pageSize: 20 });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.manage-link-empty')?.textContent)
      .toContain('No matching books');
  });

  it('does NOT render a danger zone card on the main page', async () => {
    await setup(readableBook());
    expect(fixture.nativeElement.querySelector('.danger-zone-card')).toBeNull();
    expect(fixture.nativeElement.querySelector('.primary-actions .delete-book-btn')).toBeNull();
  });

  it('triggers the confirm dialog via openDeleteConfirm and deletes on confirm', async () => {
    await setup(readableBook());
    const router = TestBed.inject(Router);
    const navigateSpy = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    component.openDeleteConfirm();
    fixture.detectChanges();

    const dialog = fixture.nativeElement.querySelector('.confirm-modal-card');
    expect(dialog).toBeTruthy();
    expect(dialog.textContent).toContain('Delete “Meditations”?');

    const deleteBtn = dialog.querySelector('.btn-confirm') as HTMLButtonElement;
    deleteBtn.click();
    fixture.detectChanges();

    httpMock.expectOne((req) => req.method === 'DELETE' && req.url === '/api/books/b1').flush(null);
    fixture.detectChanges();

    expect(navigateSpy).toHaveBeenCalledWith(['/library']);
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeNull();
  });

  it('does not display the uploaded file name in the meta strip', async () => {
    await setup(readableBook({ fileName: 'book.m4b' }));
    const metaStrip = fixture.nativeElement.querySelector('.meta-strip');
    expect(metaStrip).toBeTruthy();
    expect(metaStrip.textContent).not.toContain('File');
    expect(metaStrip.textContent).not.toContain('book.m4b');
  });

  // ── Hero band (cover-carried header) ────────────────────────────────────────
  // Regression context: the page used to render the cover in a narrow left column
  // inside an 800px centred container, which left most of a wide screen empty.
  // The hero band is what fills the top of the page, so these tests guard that it
  // exists, is driven by the book's own art, and degrades safely without a cover.

  function hero(): HTMLElement | null {
    return fixture.nativeElement.querySelector('.book-hero');
  }

  function heroArtImages(): HTMLImageElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.hero-art img'));
  }

  it('renders the title and author inside the hero band, not a side column', async () => {
    await setup(readableBook({ coverUrl: '/api/books/b1/cover' }));

    const band = hero();
    expect(band).toBeTruthy();
    expect(band!.querySelector('.book-title')?.textContent).toContain('Meditations');
    expect(band!.querySelector('.book-author')?.textContent).toContain('Marcus Aurelius');
  });

  it('drives the hero art from the cover, as real img elements not a CSS url() binding', async () => {
    await setup(readableBook({ coverUrl: '/api/books/b1/cover' }));

    const imgs = heroArtImages();
    // Two layers (defocused base + halation bloom) share the same source.
    expect(imgs.length).toBe(2);
    for (const img of imgs) {
      // A style-binding'd url() is stripped by the framework and renders an empty
      // rectangle; a real src is the only reliable carrier.
      expect(img.getAttribute('src')).toBeTruthy();
      expect(img.getAttribute('src')).toContain('/api/books/b1/cover');
    }
  });

  it('uses the small cover rendition for the hero art, not the full-size cover', async () => {
    await setup(readableBook({ coverUrl: '/api/books/b1/cover' }));

    // The art is defocused to the point where detail is irrelevant.
    expect(heroArtImages()[0].getAttribute('src')).toBe('/api/books/b1/cover/thumbnail?width=640');
  });

  it('marks the hero art decorative so it never captures pointer or a11y focus', async () => {
    await setup(readableBook({ coverUrl: '/api/books/b1/cover' }));

    expect(fixture.nativeElement.querySelector('.hero-art')!.getAttribute('aria-hidden')).toBe('true');
    for (const img of heroArtImages()) {
      expect(img.getAttribute('alt')).toBe('');
    }
  });

  it('falls back to a plain band (no art layers) when the book has no cover', async () => {
    await setup(readableBook({ coverUrl: null }));

    expect(hero()).toBeTruthy();
    expect(hero()!.classList.contains('no-art')).toBe(true);
    expect(heroArtImages().length).toBe(0);
    // The band must still carry the title.
    expect(hero()!.querySelector('.book-title')?.textContent).toContain('Meditations');
  });

  it('drops the hero art when its image fails to load', async () => {
    await setup(readableBook({ coverUrl: '/api/books/b1/cover' }));
    expect(heroArtImages().length).toBe(2);

    heroArtImages()[0].dispatchEvent(new Event('error'));
    fixture.detectChanges();

    // Otherwise the band keeps a broken image and, worse, keeps the scrim over a
    // blank field. It must fall back to the flat gradient band.
    expect(hero()!.classList.contains('no-art')).toBe(true);
    expect(heroArtImages().length).toBe(0);
  });

  it('renders a single favorite control (no duplicated desktop/mobile variants)', async () => {
    await setup(readableBook({ coverUrl: '/api/books/b1/cover' }));

    // A previous iteration shipped two heart buttons toggled by media queries and
    // leaked duplicates at intermediate widths.
    const favs = fixture.nativeElement.querySelectorAll('.favorite-btn');
    expect(favs.length).toBe(1);
  });

  it('keeps the details rail populated so the two-column body is never half empty', async () => {
    await setup(readableBook({ coverUrl: '/api/books/b1/cover' }));

    const side = fixture.nativeElement.querySelector('.detail-side') as HTMLElement | null;
    expect(side).toBeTruthy();

    // The editions block is conditional and absent for single-edition books, so
    // without the details card the rail would be an empty 320px gutter — which is
    // exactly the "empty right column" defect this layout exists to fix.
    expect(side!.querySelector('.details-card')).toBeTruthy();
    expect(side!.textContent).toContain('Publisher');
    expect(side!.querySelector('.meta-strip')).toBeTruthy();
  });

  it('moves the details out of the synopsis card and into the rail', async () => {
    await setup(readableBook({ coverUrl: '/api/books/b1/cover' }));

    const block = fixture.nativeElement.querySelector('.synopsis-metadata-block');
    expect(block).toBeTruthy();
    // Leaving a copy behind would duplicate every metadata row on the page.
    expect(block.querySelector('.meta-grid')).toBeNull();
    expect(block.querySelector('.meta-strip')).toBeNull();
  });
});

describe('BookDetail confirm-modal deletes (no window.confirm)', () => {
  let component: BookDetail;
  let fixture: ComponentFixture<BookDetail>;
  let httpMock: HttpTestingController;

  const note = {
    id: 'n1',
    bookId: 'b1',
    content: 'A thought',
    selectedText: null,
    cfiRange: null,
    pageNumber: null,
    createdAt: '2026-08-10T08:00:00+02:00',
  };

  async function setup(initial: Book = { ...book, hasFile: true, coverUrl: '/api/books/b1/cover' }) {
    fixture = TestBed.createComponent(BookDetail);
    component = fixture.componentInstance;
    await fixture.whenStable();
    fixture.detectChanges();

    httpMock.expectOne('/api/books/b1').flush(initial);
    httpMock.expectOne(bookNotes).flush(notesPage([note]));
    httpMock.expectOne('/api/collections').flush([]);
    httpMock.match(brainReads).forEach((request) => request.flush([]));
    fixture.detectChanges();
  }

  function openModals(): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.confirm-modal-card')) as HTMLElement[];
  }

  /** The detail page reloads topics on several paths; drain stragglers so
      httpMock.verify() only judges the requests each test cares about. */
  function drainTopics(): void {
    httpMock.match(brainReads).forEach((request) => request.flush([]));
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BookDetail],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { paramMap: of(convertToParamMap({ id: book.id })) },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('note delete opens the modal and only deletes on confirm', async () => {
    await setup();
    expect(component.store.notes().map((n) => n.id)).toEqual(['n1']);

    component.noteActions.askDelete(['n1']);
    fixture.detectChanges();
    expect(openModals().length).toBe(1);
    httpMock.expectNone('/api/notes/n1');

    component.noteActions.confirmDelete();
    httpMock.expectOne('/api/notes/n1').flush(null);
    // The note leaves the page; the Brain's counts and topics reload.
    httpMock.match(brainReads).forEach((request) => request.flush([]));
    fixture.detectChanges();
    expect(component.store.notes()).toEqual([]);
    expect(openModals().length).toBe(0);
    drainTopics();
  });

  it('cancelling note delete performs nothing', async () => {
    await setup();

    component.noteActions.askDelete(['n1']);
    component.noteActions.cancelDelete();
    fixture.detectChanges();
    expect(openModals().length).toBe(0);
    httpMock.expectNone('/api/notes/n1');
    expect(component.store.notes().map((n) => n.id)).toEqual(['n1']);
    drainTopics();
  });

  it('cover remove opens the modal and only deletes on confirm', async () => {
    await setup();

    component.deleteCover();
    fixture.detectChanges();
    expect(component.coverDeletePending()).toBe(true);
    expect(openModals().length).toBe(1);
    httpMock.expectNone((req) => req.url === '/api/books/b1/cover' && req.method === 'DELETE');

    component.confirmCoverDelete();
    httpMock
      .expectOne((req) => req.url === '/api/books/b1/cover' && req.method === 'DELETE')
      .flush(null);
    // The store reloads the book in the background after the delete.
    httpMock
      .expectOne((req) => req.url === '/api/books/b1' && req.method === 'GET')
      .flush({ ...book, hasFile: true, coverUrl: null });
    expect(component.coverDeletePending()).toBe(false);
    drainTopics();
  });

  it('cover remove stays open and busy until the server answers, and stays on failure', async () => {
    await setup();
    const toast = TestBed.inject(ToastService);
    const toastError = vi.spyOn(toast, 'error');
    const toastSuccess = vi.spyOn(toast, 'success');

    component.deleteCover();
    component.confirmCoverDelete();
    fixture.detectChanges();
    expect(component.store.removingCover()).toBe(true);
    expect(openModals()[0].textContent).toContain('Removing…');
    component.cancelCoverDelete();
    expect(component.coverDeletePending()).toBe(true);

    httpMock
      .expectOne((req) => req.url === '/api/books/b1/cover' && req.method === 'DELETE')
      .flush({}, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    expect(toastError).toHaveBeenCalledWith('Could not remove the cover');
    expect(component.coverDeletePending()).toBe(true);
    expect(component.store.removingCover()).toBe(false);

    component.confirmCoverDelete();
    httpMock
      .expectOne((req) => req.url === '/api/books/b1/cover' && req.method === 'DELETE')
      .flush(null);
    httpMock
      .expectOne((req) => req.url === '/api/books/b1' && req.method === 'GET')
      .flush({ ...book, hasFile: true, coverUrl: null });
    expect(toastSuccess).toHaveBeenCalledWith('Cover removed');
    expect(component.coverDeletePending()).toBe(false);
    drainTopics();
  });
});

describe('BookDetail live library changes', () => {
  let component: BookDetail;
  let fixture: ComponentFixture<BookDetail>;
  let httpMock: HttpTestingController;
  let libraryChanged: Subject<void>;

  async function setup() {
    fixture = TestBed.createComponent(BookDetail);
    component = fixture.componentInstance;
    await fixture.whenStable();
    fixture.detectChanges();

    httpMock.expectOne('/api/books/b1').flush(book);
    httpMock.expectOne(bookNotes).flush(notesPage([]));
    httpMock.expectOne('/api/collections').flush([]);
    httpMock.match(brainReads).forEach((request) => request.flush([]));
    fixture.detectChanges();
  }

  /** Every book read issued since the last call, flushed with `reply`. */
  function bookReads(reply: Book = book): number {
    const reads = httpMock.match('/api/books/b1');
    reads.forEach((request) => request.flush(reply));
    httpMock.match(bookNotes).forEach((request) => request.flush(notesPage([])));
    httpMock.match('/api/collections').forEach((request) => request.flush([]));
    return reads.length;
  }

  beforeEach(async () => {
    libraryChanged = new Subject<void>();
    await TestBed.configureTestingModule({
      imports: [BookDetail],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: LibraryChangesService, useValue: { watch: () => libraryChanged } },
        {
          provide: ActivatedRoute,
          useValue: { paramMap: of(convertToParamMap({ id: book.id })) },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  it('re-reads the open book quietly when the library changes elsewhere', async () => {
    await setup();

    libraryChanged.next();
    TestBed.flushEffects();

    expect(bookReads({ ...book, title: 'Renamed elsewhere' })).toBe(1);
    expect(component.store.book()?.title).toBe('Renamed elsewhere');
    expect(component.store.loading()).toBe(false);
  });

  it('waits for Edit Book to close, so a change elsewhere cannot wipe the form', async () => {
    await setup();
    component.openMetadataModal();
    fixture.detectChanges();

    libraryChanged.next();
    TestBed.flushEffects();
    expect(bookReads()).toBe(0);

    component.closeMetadataModal();
    TestBed.flushEffects();
    expect(bookReads()).toBe(1);
  });

  it('keeps the page when the background read fails', async () => {
    await setup();

    libraryChanged.next();
    TestBed.flushEffects();
    httpMock
      .expectOne('/api/books/b1')
      .flush('gone', { status: 404, statusText: 'Not Found' });
    httpMock.match(bookNotes).forEach((request) => request.flush(notesPage([])));
    httpMock.match('/api/collections').forEach((request) => request.flush([]));

    expect(component.store.book()?.id).toBe('b1');
    expect(component.store.error()).toBeNull();
  });
});
