import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Component, forwardRef, input, output, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import {
  ActivatedRoute,
  convertToParamMap,
  ParamMap,
  provideRouter,
  Router,
} from '@angular/router';
import { Location } from '@angular/common';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';

// @ts-expect-error — no @types/node in this repo; vitest resolves node:fs at
// runtime. Used only for static source guards (the shell stylesheet).
import { readFileSync } from 'node:fs';

/** Read one of this component's own source files for a static guard. */
const readSource = (file: string) => readFileSync(new URL(file, import.meta.url), 'utf-8');

import { ReaderShell } from './reader-shell.component';
import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HighlightColour,
} from './highlight-colours';
import { AudioReader } from './audio-reader/audio-reader.component';
import { PdfReader } from './pdf-reader/pdf-reader.component';
import { EpubReader } from './epub-reader/epub-reader.component';
import { ConceptInputComponent } from '../ui/concept-input.component/concept-input.component';
import { NoteCardComponent } from '../ui/note-card.component/note-card.component';
import { BooksService } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { ConceptsService } from '../core/services/concepts.service';
import { ConceptAutocompleteService } from '../ui/concept-autocomplete-panel/concept-autocomplete.service';
import { Book } from '../core/dtos/book.dtos';
import { Note } from '../core/dtos/note.dtos';

// The AudioReader is kept real so this spec guards the reader page's total
// GET /api/books/{id} count; Howl is mocked to avoid real media loading.
vi.mock('howler', () => ({
  // Keep the same observable mock shape as audio-reader.component.spec.ts.
  // Angular's unit-test builder can bundle these specs together, so either
  // module mock must be safe for the real AudioReader lifecycle tests.
  Howl: vi.fn(function (config: any) {
    return {
      config,
      unload: vi.fn(),
      seek: vi.fn(() => 0),
      playing: vi.fn(() => false),
      duration: vi.fn(() => 7200),
      play: vi.fn(),
      pause: vi.fn(),
      rate: vi.fn(),
    };
  }),
}));

// Stand-ins for the other reader children (epub.js / pdf.js are heavy and
// irrelevant to the audiobook single-fetch regression). They declare the exact
// inputs/outputs the shell template binds, so template compilation is real.
@Component({ selector: 'app-pdf-reader', standalone: true, template: '' })
class PdfReaderStub {
  bookId = input.required<string>();
  initialLocation = input<string | null>(null);
  sidebarVisible = input(false);
  highlightMode = input(false);
  highlightColour = input<HighlightColour>(DEFAULT_HIGHLIGHT_COLOUR);
  sidebarVisibleChange = output<boolean>();
  noteCreated = output<void>();
  selectionCaptured = output<unknown>();
  commitFailed = output<unknown>();
  /**
   * Search visibility (a #226 follow-up). The header control reads it to show its state
   * and to act as a close, because the library's find bar renders no close control
   * of its own.
   */
  findBarVisible = signal(false);
  textCapability = signal<'unknown' | 'available' | 'unavailable'>('available');
  toggleSearch = () => this.findBarVisible.update((v) => !v);
  /**
   * The IReader surface the shell needs to render the pager for a PDF. Without
   * these the shell's `activeReader()?.progress()` path could not be exercised by
   * a spec at all.
   */
  toc = signal<unknown[]>([]);
  progress = signal<{
    label?: string;
    pageNumber?: number;
    pageCount?: number;
    pageLabel?: string | null;
    percentage: number;
  }>({ label: '', percentage: 0 });
  currentLocationTarget = signal<unknown>(null);
  goTo = vi.fn();
  goToSource = vi.fn(() => Promise.resolve());
  next = vi.fn();
  previous = vi.fn();
}

@Component({ selector: 'app-epub-reader', standalone: true, template: '' })
class EpubReaderStub {
  bookId = input.required<string>();
  // The shell passes the loaded Book down so the reader can restore the saved
  // position without a second GET (issue #225 §1.2).
  book = input<unknown>(null);
  highlightMode = input(false);
  highlightColour = input<HighlightColour>(DEFAULT_HIGHLIGHT_COLOUR);
  noteCreated = output<void>();
  selectionCaptured = output<unknown>();
  commitFailed = output<unknown>();
  exitRequested = output<void>();
  // Typography surface the shell panel binds (mirrors EpubReader).
  typography = signal({ fontFamily: 'default', lineHeight: 1.6, margin: 'normal' });
  fontOptions = [
    { value: 'default', label: 'Publisher' },
    { value: 'serif', label: 'Serif' },
  ];
  lineOptions = [1.4, 1.6];
  marginOptions = [{ value: 'normal', label: 'Normal' }];
  // Text size: the toolbar's two steps moved into the Aa panel, so the shell now
  // binds the reader's size signal and zoom methods directly.
  fontSizePercent = signal(100);
  zoomIn = vi.fn();
  zoomOut = vi.fn();
  setTypography = vi.fn();
  resetTypography = vi.fn();
  next = vi.fn();
  previous = vi.fn();
  // IReader surface the shell reads once the reader is active.
  toc = signal<unknown[]>([]);
  progress = signal({ label: '', percentage: 0 });
  currentLocationTarget = signal<unknown>(null);
  goToSource = vi.fn(() => Promise.resolve());
}

// The shell binds [(ngModel)] to app-concept-input; the stub must be a
// ControlValueAccessor so the ngModel directive resolves.
@Component({
  selector: 'app-concept-input',
  standalone: true,
  template: '',
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => ConceptInputStub),
      multi: true,
    },
  ],
})
class ConceptInputStub implements ControlValueAccessor {
  placeholder = input('');
  rows = input(1);
  submitTrigger = output<void>();
  writeValue(): void {}
  registerOnChange(): void {}
  registerOnTouched(): void {}
}

@Component({ selector: 'app-note-card', standalone: true, template: '' })
class NoteCardStub {
  note = input<unknown>(null);
  conceptMap = input<unknown>(null);
  showNavigation = input(false);
  update = output<unknown>();
  delete = output<unknown>();
  cardClick = output<unknown>();
}

const audiobook = {
  id: 'book-1',
  title: 'The Iliad',
  subtitle: null,
  author: 'Homer',
  editor: null,
  translator: null,
  narrator: null,
  description: null,
  type: 'audiobook',
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
  createdAt: '2026-01-01T00:00:00Z',
  hasFile: true,
  fileName: 'iliad.m4b',
  coverUrl: null,
  collectionIds: [],
  lastLocation: '3721.5',
  progressPercent: 10,
  lastReadAt: null,
  rating: 0,
  isFavorite: false,
  personalReview: null,
  finishedAt: null,
  chapters: [{ title: 'Book One', startTime: 0 }],
} as Book;

const booksGetSpy = vi.fn();
let routeQueryParamMap$: BehaviorSubject<ParamMap>;
let routeParamMap$: BehaviorSubject<ParamMap>;

// jsdom does not implement matchMedia; the shell registers a change listener.
function mockMatchMedia() {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false,
      media: query,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
}

// Builds a fresh TestBed module with the heavy reader children stubbed out.
// Called per-test so each spec starts from a clean state.
async function configureReaderShell(
  queryParams: Record<string, string | number> = {},
): Promise<ComponentFixture<ReaderShell>> {
  const initialQueryParamMap = convertToParamMap(queryParams);
  const initialParamMap = convertToParamMap({ id: 'book-1' });
  routeQueryParamMap$ = new BehaviorSubject<ParamMap>(initialQueryParamMap);
  routeParamMap$ = new BehaviorSubject<ParamMap>(initialParamMap);

  TestBed.overrideComponent(ReaderShell, {
    remove: {
      imports: [PdfReader, EpubReader, ConceptInputComponent, NoteCardComponent],
    },
    add: { imports: [PdfReaderStub, EpubReaderStub, ConceptInputStub, NoteCardStub] },
  });

  await TestBed.configureTestingModule({
    imports: [ReaderShell],
    providers: [
      provideRouter([]),
      {
        provide: ActivatedRoute,
        useValue: {
          snapshot: {
            paramMap: initialParamMap,
            queryParamMap: initialQueryParamMap,
          },
          paramMap: routeParamMap$.asObservable(),
          queryParamMap: routeQueryParamMap$.asObservable(),
        },
      },
      {
        provide: BooksService,
        useValue: { get: booksGetSpy, updateProgress: vi.fn(() => of(null)) },
      },
      {
        provide: NotesService,
        useValue: {
          list: vi.fn(() => of([])),
          create: vi.fn(),
          update: vi.fn(),
          delete: vi.fn(),
        },
      },
      { provide: ConceptsService, useValue: { list: vi.fn(() => of([])) } },
      { provide: ConceptAutocompleteService, useValue: { setConcepts: vi.fn() } },
    ],
  }).compileComponents();

  return TestBed.createComponent(ReaderShell);
}

describe('ReaderShell Studio return origin (#510/#511)', () => {
  beforeEach(() => {
    booksGetSpy.mockReset();
    booksGetSpy.mockReturnValue(of(audiobook));
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    mockMatchMedia();
    window.history.replaceState({}, '', window.location.href);
  });

  afterEach(() => {
    window.history.replaceState({}, '', window.location.href);
  });

  it('keeps the existing explicit Book Detail destination without a Studio marker', async () => {
    const fixture = await configureReaderShell();
    const router = TestBed.inject(Router);
    const location = TestBed.inject(Location);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const back = vi.spyOn(location, 'back');

    fixture.detectChanges();
    fixture.componentInstance.goBack();

    expect(back).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/library', 'book-1'], { replaceUrl: true });
    fixture.destroy();
  });

  it('uses history back only for the explicit namespaced Studio origin', async () => {
    window.history.replaceState(
      {
        navigationId: 17,
        nostosReaderReturnOrigin: { version: 1, kind: 'studio', writingId: 'writing-a' },
      },
      '',
      window.location.href,
    );

    const fixture = await configureReaderShell({ sourcePage: 42 });
    const router = TestBed.inject(Router);
    const location = TestBed.inject(Location);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const back = vi.spyOn(location, 'back');

    fixture.detectChanges();
    fixture.componentInstance.goBack();

    expect(back).toHaveBeenCalledTimes(1);
    expect(navigate).not.toHaveBeenCalled();
    fixture.destroy();
  });

  it.each([
    [{ sourcePage: 42 }, 'sourcePage'],
    [{ sourceCfi: 'epubcfi(/6/2)' }, 'sourceCfi'],
  ])('never infers Studio origin from %s', async (queryParams, _label) => {
    const fixture = await configureReaderShell(queryParams as Record<string, string | number>);
    const router = TestBed.inject(Router);
    const location = TestBed.inject(Location);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const back = vi.spyOn(location, 'back');

    fixture.detectChanges();
    fixture.componentInstance.goBack();

    expect(back).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/library', 'book-1'], { replaceUrl: true });
    fixture.destroy();
  });

  it('ignores unrelated navigation state', async () => {
    window.history.replaceState({ someOtherFeature: { kind: 'studio' } }, '', window.location.href);
    const fixture = await configureReaderShell();
    const router = TestBed.inject(Router);
    const location = TestBed.inject(Location);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const back = vi.spyOn(location, 'back');

    fixture.detectChanges();
    fixture.componentInstance.goBack();

    expect(back).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/library', 'book-1'], { replaceUrl: true });
    fixture.destroy();
  });
});

describe('ReaderShell grounded book-text source navigation', () => {
  beforeEach(() => {
    booksGetSpy.mockReset();
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    mockMatchMedia();
  });

  it('passes a grounded PDF physical page and logical label to the PDF reader', async () => {
    const pdfBook = { ...audiobook, id: 'book-1', type: 'ebook', fileName: 'source.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));

    const fixture = await configureReaderShell({
      sourcePage: 9,
      sourcePageLabel: '7',
    });
    fixture.detectChanges();
    fixture.detectChanges();

    // ReaderShell intentionally queries the concrete PdfReader type, so the
    // lightweight stub is not populated through @ViewChild in this spec. Attach
    // the already-rendered stub before the shell's 100 ms grounded-navigation
    // settle runs, exactly as the other PDF shell tests do.
    const stub = fixture.debugElement.query(By.directive(PdfReaderStub))
      .componentInstance as PdfReaderStub;
    (fixture.componentInstance as unknown as { pdfReader: PdfReaderStub }).pdfReader = stub;

    await new Promise((resolve) => setTimeout(resolve, 130));
    fixture.detectChanges();

    expect(stub.goToSource).toHaveBeenCalledTimes(1);
    expect(stub.goToSource).toHaveBeenCalledWith({
      type: 'pdf',
      pdfPage: 9,
      pdfPageLabel: '7',
    });

    fixture.destroy();
  });

  it('re-navigates an already-mounted PDF when the grounded target changes', async () => {
    const pdfBook = { ...audiobook, id: 'book-1', type: 'ebook', fileName: 'source.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));

    const fixture = await configureReaderShell({
      sourcePage: 9,
      sourcePageLabel: '7',
    });
    fixture.detectChanges();
    fixture.detectChanges();

    const stub = fixture.debugElement.query(By.directive(PdfReaderStub))
      .componentInstance as PdfReaderStub;
    (fixture.componentInstance as unknown as { pdfReader: PdfReaderStub }).pdfReader = stub;

    await new Promise((resolve) => setTimeout(resolve, 130));
    fixture.detectChanges();
    expect(stub.goToSource).toHaveBeenCalledTimes(1);

    stub.goToSource.mockClear();
    routeQueryParamMap$.next(
      convertToParamMap({
        sourcePage: 14,
        sourcePageLabel: '12',
      }),
    );
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(stub.goToSource).toHaveBeenCalledTimes(1);
    expect(stub.goToSource).toHaveBeenLastCalledWith({
      type: 'pdf',
      pdfPage: 14,
      pdfPageLabel: '12',
    });

    // An unrelated query-param change must not replay the same source target.
    routeQueryParamMap$.next(
      convertToParamMap({
        sourcePage: 14,
        sourcePageLabel: '12',
        panel: 'notes',
      }),
    );
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(stub.goToSource).toHaveBeenCalledTimes(1);

    fixture.destroy();
  });

  it('re-consumes a prior PDF citation when same-book source params navigate away and back', async () => {
    const pdfBook = { ...audiobook, id: 'book-1', type: 'ebook', fileName: 'source.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));

    const fixture = await configureReaderShell({
      sourcePage: 9,
      sourcePageLabel: '7',
    });
    fixture.detectChanges();
    fixture.detectChanges();

    const stub = fixture.debugElement.query(By.directive(PdfReaderStub))
      .componentInstance as PdfReaderStub;
    (fixture.componentInstance as unknown as { pdfReader: PdfReaderStub }).pdfReader = stub;

    await new Promise((resolve) => setTimeout(resolve, 130));
    fixture.detectChanges();
    stub.goToSource.mockClear();

    routeQueryParamMap$.next(convertToParamMap({ sourcePage: 14, sourcePageLabel: '12' }));
    await new Promise((resolve) => setTimeout(resolve, 0));
    routeQueryParamMap$.next(convertToParamMap({ sourcePage: 9, sourcePageLabel: '7' }));
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(stub.goToSource).toHaveBeenCalledTimes(2);
    expect(stub.goToSource).toHaveBeenNthCalledWith(1, {
      type: 'pdf',
      pdfPage: 14,
      pdfPageLabel: '12',
    });
    expect(stub.goToSource).toHaveBeenNthCalledWith(2, {
      type: 'pdf',
      pdfPage: 9,
      pdfPageLabel: '7',
    });

    fixture.destroy();
  });

  it('rebinds a reused Reader when a grounded source changes the route book id', async () => {
    const pdfBook = { ...audiobook, type: 'ebook', fileName: 'source.pdf' } as Book;
    booksGetSpy.mockImplementation((id: string) => of({ ...pdfBook, id } as Book));

    const fixture = await configureReaderShell({
      sourcePage: 9,
      sourcePageLabel: '7',
    });
    fixture.detectChanges();
    fixture.detectChanges();

    const stub = fixture.debugElement.query(By.directive(PdfReaderStub))
      .componentInstance as PdfReaderStub;
    (fixture.componentInstance as unknown as { pdfReader: PdfReaderStub }).pdfReader = stub;

    await new Promise((resolve) => setTimeout(resolve, 130));
    fixture.detectChanges();
    expect(stub.goToSource).toHaveBeenCalledTimes(1);
    stub.goToSource.mockClear();

    routeParamMap$.next(convertToParamMap({ id: 'book-2' }));
    fixture.detectChanges();

    expect(booksGetSpy).toHaveBeenLastCalledWith('book-2');
    expect(stub.bookId()).toBe('book-2');

    await new Promise((resolve) => setTimeout(resolve, 130));
    fixture.detectChanges();

    expect(stub.goToSource).toHaveBeenCalledTimes(1);
    expect(stub.goToSource).toHaveBeenCalledWith({
      type: 'pdf',
      pdfPage: 9,
      pdfPageLabel: '7',
    });

    fixture.destroy();
  });

  it('passes grounded EPUB CFI plus structural fallback to the EPUB reader', async () => {
    const epubBook = { ...audiobook, id: 'book-1', type: 'ebook', fileName: 'source.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));

    const fixture = await configureReaderShell({
      sourceCfi: 'epubcfi(/6/4!/4/2/6:0)',
      sourceHref: 'chapter-2.xhtml',
      sourceSpine: 2,
      sourceOffset: 314,
      sourceExcerpt: 'A uniquely grounded passage.',
    });
    fixture.detectChanges();
    fixture.detectChanges();

    await new Promise((resolve) => setTimeout(resolve, 130));
    fixture.detectChanges();

    const stub = fixture.debugElement.query(By.directive(EpubReaderStub))
      .componentInstance as EpubReaderStub;
    expect(stub.goToSource).toHaveBeenCalledTimes(1);
    expect(stub.goToSource).toHaveBeenCalledWith({
      type: 'epub',
      epubCfi: 'epubcfi(/6/4!/4/2/6:0)',
      epubResourceHref: 'chapter-2.xhtml',
      epubSpineIndex: 2,
      epubTextOffset: 314,
      excerpt: 'A uniquely grounded passage.',
    });

    fixture.destroy();
  });

  it('re-consumes changed grounded EPUB params on an already-mounted reader', async () => {
    const epubBook = { ...audiobook, id: 'book-1', type: 'ebook', fileName: 'source.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));

    const fixture = await configureReaderShell({
      sourceCfi: 'epubcfi(/6/4!/4/2/6:0)',
      sourceHref: 'chapter-2.xhtml',
      sourceSpine: 2,
      sourceOffset: 314,
      sourceExcerpt: 'First grounded passage.',
    });
    fixture.detectChanges();
    fixture.detectChanges();

    await new Promise((resolve) => setTimeout(resolve, 130));
    fixture.detectChanges();

    const stub = fixture.debugElement.query(By.directive(EpubReaderStub))
      .componentInstance as EpubReaderStub;
    expect(stub.goToSource).toHaveBeenCalledTimes(1);
    stub.goToSource.mockClear();

    routeQueryParamMap$.next(
      convertToParamMap({
        sourceCfi: 'epubcfi(/6/6!/4/2/8:0)',
        sourceHref: 'chapter-3.xhtml',
        sourceSpine: 3,
        sourceOffset: 512,
        sourceExcerpt: 'Second grounded passage.',
      }),
    );
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(stub.goToSource).toHaveBeenCalledTimes(1);
    expect(stub.goToSource).toHaveBeenCalledWith({
      type: 'epub',
      epubCfi: 'epubcfi(/6/6!/4/2/8:0)',
      epubResourceHref: 'chapter-3.xhtml',
      epubSpineIndex: 3,
      epubTextOffset: 512,
      excerpt: 'Second grounded passage.',
    });

    fixture.destroy();
  });
});

describe('ReaderShell shared save and load ownership (#479)', () => {
  beforeEach(() => {
    booksGetSpy.mockReset();
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    mockMatchMedia();
  });

  it('keeps quick-note Save single-flight and preserves the draft after failure', async () => {
    booksGetSpy.mockReturnValue(of(audiobook));
    const fixture = await configureReaderShell();
    fixture.detectChanges();
    fixture.detectChanges();

    const notes = TestBed.inject(NotesService) as unknown as {
      create: ReturnType<typeof vi.fn>;
    };
    const first = new Subject<Note>();
    notes.create.mockReturnValueOnce(first.asObservable());

    const component = fixture.componentInstance;
    component.quickNoteContent.set('Remember this passage');
    component.saveQuickNote();
    component.saveQuickNote();

    expect(notes.create).toHaveBeenCalledTimes(1);
    expect(component.quickNoteSaving()).toBe(true);

    first.error(new Error('offline'));
    expect(component.quickNoteSaving()).toBe(false);
    expect(component.quickNoteContent()).toBe('Remember this passage');

    const retry = new Subject<Note>();
    notes.create.mockReturnValueOnce(retry.asObservable());
    component.saveQuickNote();

    expect(notes.create).toHaveBeenCalledTimes(2);
    expect(component.quickNoteSaving()).toBe(true);

    retry.next({
      id: 'note-retry',
      bookId: 'book-1',
      content: 'Remember this passage',
      createdAt: '2026-09-24T12:00:00Z',
    } as Note);
    retry.complete();

    expect(component.quickNoteSaving()).toBe(false);
    expect(component.quickNoteContent()).toBe('');
    fixture.destroy();
  });

  it('keeps an initial Book-load failure visible, actionable, and retryable', async () => {
    booksGetSpy.mockReturnValueOnce(throwError(() => new Error('offline')));
    const fixture = await configureReaderShell();
    fixture.detectChanges();
    fixture.detectChanges();

    const component = fixture.componentInstance;
    const layout = fixture.nativeElement.querySelector('.reader-layout') as HTMLElement;
    expect(layout.classList.contains('ready')).toBe(true);

    const error = fixture.nativeElement.querySelector(
      '[data-testid="reader-load-error"]',
    ) as HTMLElement;
    expect(error).toBeTruthy();
    expect(error.textContent).toContain('Couldn’t open this book');
    expect(error.textContent).toContain('Retry');

    booksGetSpy.mockReturnValueOnce(of(audiobook));
    const retry = Array.from(error.querySelectorAll('button') as NodeListOf<HTMLButtonElement>)
      .find((button) => button.textContent?.trim() === 'Retry');
    expect(retry).toBeTruthy();
    retry!.click();
    fixture.detectChanges();

    expect(booksGetSpy).toHaveBeenCalledTimes(2);
    await new Promise((resolve) => setTimeout(resolve, 130));
    fixture.detectChanges();

    expect(component.loadError()).toBeNull();
    expect(component.ready()).toBe(true);
    fixture.destroy();
  });
});

describe('ReaderShell assistant note navigation (issue #324)', () => {
  let fixture: ComponentFixture<ReaderShell>;

  beforeEach(async () => {
    booksGetSpy.mockReset();
    booksGetSpy.mockReturnValue(of(audiobook));
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    mockMatchMedia();
    fixture = await configureReaderShell();
  });

  const note = (overrides: Partial<Note>): Note => ({
    id: 'note-1',
    bookId: 'book-1',
    content: '',
    createdAt: '2026-09-21T10:00:00Z',
    ...overrides,
  });

  it('jumps to the verified PDF page stored by an older assistant capture', () => {
    const goTo = vi.fn();
    (fixture.componentInstance as any).activeReader = () => ({ goTo });

    fixture.componentInstance.onJumpToNote(
      note({
        sourceAnchorKind: 'pdf_page',
        sourceAnchorValue: '37',
        anchorVerified: true,
      }),
    );

    expect(goTo).toHaveBeenCalledOnce();
    expect(goTo).toHaveBeenCalledWith(37);
  });

  it('jumps to the verified EPUB CFI when an assistant note has no legacy cfiRange', () => {
    const goTo = vi.fn();
    (fixture.componentInstance as any).activeReader = () => ({ goTo });
    const cfi = 'epubcfi(/6/4[chapter]!/4/2/2)';

    fixture.componentInstance.onJumpToNote(
      note({
        sourceAnchorKind: 'epub_cfi',
        sourceAnchorValue: cfi,
        anchorVerified: true,
      }),
    );

    expect(goTo).toHaveBeenCalledOnce();
    expect(goTo).toHaveBeenCalledWith(cfi);
  });

  it('does not navigate from an unverified typed source anchor', () => {
    const goTo = vi.fn();
    (fixture.componentInstance as any).activeReader = () => ({ goTo });

    fixture.componentInstance.onJumpToNote(
      note({
        sourceAnchorKind: 'pdf_page',
        sourceAnchorValue: '37',
        anchorVerified: false,
      }),
    );

    expect(goTo).not.toHaveBeenCalled();
  });
});

describe('ReaderShell audiobook load (issue #7)', () => {
  let fixture: ComponentFixture<ReaderShell>;

  beforeEach(async () => {
    booksGetSpy.mockReset();
    booksGetSpy.mockReturnValue(of(audiobook));

    // Deterministic boot state for every test.
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');

    mockMatchMedia();

    fixture = await configureReaderShell();
  });

  function render() {
    fixture.detectChanges();
    fixture.detectChanges();
  }

  it('fetches the audiobook exactly once on the reader page (no duplicate GET from the audio reader)', () => {
    render();

    expect(booksGetSpy).toHaveBeenCalledTimes(1);
    expect(booksGetSpy).toHaveBeenCalledWith('book-1');
  });

  it('passes the loaded book into the audio reader as an input', () => {
    render();

    const audioReaderEl = fixture.debugElement.query(By.directive(AudioReader));
    expect(audioReaderEl).not.toBeNull();
    expect(audioReaderEl.componentInstance.bookId()).toBe('book-1');
    expect(audioReaderEl.componentInstance.book()).toBe(audiobook);
  });
});

describe('ReaderShell toolbar contract (theme system removed)', () => {
  let fixture: ComponentFixture<ReaderShell>;

  beforeEach(() => {
    booksGetSpy.mockReset();
    booksGetSpy.mockReturnValue(of(audiobook));

    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');

    mockMatchMedia();
  });

  function render() {
    fixture.detectChanges();
    fixture.detectChanges();
  }

  it('renders no theme toggle and no data-theme binding anywhere in the shell', async () => {
    fixture = await configureReaderShell();
    render();

    expect(fixture.debugElement.query(By.css('.theme-toggle'))).toBeNull();
    expect(fixture.debugElement.query(By.css('.theme-toggle-btn'))).toBeNull();
    const layout = fixture.debugElement.query(By.css('.reader-layout'));
    expect(layout.nativeElement.hasAttribute('data-theme')).toBe(false);
  });

  it('splits the chrome: configuration in the header, page turns in the pager', async () => {
    // Non-audio book. Every control that CONFIGURES the reader belongs to the
    // surface header now; the bottom bar is the pager and nothing else.
    const epubBook = { ...audiobook, id: 'book-epub', fileName: 'iliad.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));

    fixture = await configureReaderShell();
    render();

    const titlesOf = (selector: string) =>
      fixture.debugElement
        .queryAll(By.css(selector))
        .map((b) => b.nativeElement.getAttribute('title'))
        .filter((t): t is string => !!t);

    const header = titlesOf('.reader-header .icon-btn');
    expect(header.filter((t) => t === 'Table of Contents')).toHaveLength(1);
    expect(header.filter((t) => t === 'Notes & Highlights')).toHaveLength(1);
    expect(header.filter((t) => t === 'View settings')).toHaveLength(1);
    // The highlight control merged into the notes panel: at 390px the header held
    // five controls and left the book title 78px ("Being an…").
    expect(header.filter((t) => t === 'Highlight mode')).toHaveLength(0);
    // Back lives with the title it returns to, not with the page keys.
    expect(header.filter((t) => t === 'Book details')).toHaveLength(1);
    // And nothing about turning pages is up here.
    expect(header.filter((t) => t === 'Previous' || t === 'Next')).toHaveLength(0);

    const pager = titlesOf('.reader-toolbar .icon-btn');
    expect(pager.filter((t) => t === 'Previous')).toHaveLength(1);
    expect(pager.filter((t) => t === 'Next')).toHaveLength(1);
    expect(pager.filter((t) => t === 'Highlight mode' || t === 'View settings')).toHaveLength(0);

    // The progress cluster sits between prev and next in the center group.
    const center = fixture.debugElement.query(By.css('.toolbar-center'));
    expect(center).not.toBeNull();
    expect(center.query(By.css('.progress-display'))).not.toBeNull();
  });

  it('merges the highlight control into the notes panel', async () => {
    // One "my marks" control in the header instead of two. The tap that turns
    // highlighting ON also closes the panel, so the reader is ready for a
    // selection — the same single tap the header button used to take.
    const epubBook = { ...audiobook, id: 'book-epub', fileName: 'iliad.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));

    fixture = await configureReaderShell();
    render();
    const component = fixture.componentInstance;

    component.toggleNotes();
    render();
    const toggle = fixture.debugElement.query(By.css('[data-testid="reader-highlight-toggle"]'));
    expect(toggle).not.toBeNull();
    expect(toggle.nativeElement.textContent).toContain('Highlight text');
    expect(toggle.nativeElement.getAttribute('aria-pressed')).toBe('false');

    toggle.nativeElement.click();
    render();
    expect(component.highlightMode()).toBe(true);
    expect(component.notesOpen()).toBe(false);

    // Reopening with the mode on: the row reports it, and switching it off leaves
    // the panel open because the user is looking at their notes.
    component.toggleNotes();
    render();
    const toggleAgain = fixture.debugElement.query(By.css('[data-testid="reader-highlight-toggle"]'));
    expect(toggleAgain.nativeElement.textContent).toContain('Highlighting is on');
    expect(toggleAgain.nativeElement.getAttribute('aria-pressed')).toBe('true');
    toggleAgain.nativeElement.click();
    render();
    expect(component.highlightMode()).toBe(false);
    expect(component.notesOpen()).toBe(true);
  });

  it('exposes selected state on every Reader view-setting option', () => {
    const source = readSource('./reader-shell.component.html');
    const optionTags = [...source.matchAll(/<button\b(?=[^>]*class="typo-opt")[^>]*>/g)]
      .map((m) => m[0]);

    expect(optionTags.length).toBeGreaterThan(0);
    expect(optionTags.every((tag) => tag.includes('[attr.aria-pressed]'))).toBe(true);
  });

  it('has no overflow menu left to reach the desktop-only controls', async () => {
    // The mobile "More" menu existed only because ten controls could not share
    // one 44px row. The header holds them, so the menu and its CSS hook are gone
    // — this test is what stops them creeping back in.
    const epubBook = { ...audiobook, id: 'book-epub', fileName: 'iliad.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));

    fixture = await configureReaderShell();
    render();

    expect(fixture.debugElement.queryAll(By.css('.overflow-toggle'))).toHaveLength(0);
    expect(fixture.debugElement.queryAll(By.css('.overflow-menu'))).toHaveLength(0);
    expect(fixture.nativeElement.querySelector('.reader-back')).not.toBeNull();
  });

  /**
   * Every reader control must be a real `appIconButton` button, and none of them
   * may be hidden on a phone any more. The PDF zoom pair used to carry
   * `.desktop-only`, which left a phone with a page fitted to about 9.5px and no
   * way to enlarge it (issue #226 §4), so this test now asserts the opposite:
   * the hook is applied to no reader control at all.
   */
  it('renders every reader control as a real button, none hidden on mobile', async () => {
    // A PDF book: the one format that carries the render-scale pair (an EPUB's
    // text size lives in the Aa panel instead).
    const pdfBook = { ...audiobook, id: 'book-pdf', fileName: 'being-and-time.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));

    fixture = await configureReaderShell();
    render();

    const headerTitles = fixture.debugElement
      .queryAll(By.css('.reader-header button.icon-btn'))
      .map((b) => b.nativeElement.getAttribute('title'));
    // Zoom moved into the view panel: at 390px the two zoom buttons plus search,
    // highlight, notes and contents left the title about 60px of a 390px header.
    expect(headerTitles).not.toContain('Zoom out');
    expect(headerTitles).not.toContain('Zoom in');
    expect(headerTitles).toContain('View settings');

    expect(fixture.debugElement.queryAll(By.css('button.desktop-only'))).toHaveLength(0);

    const controls = fixture.debugElement.queryAll(
      By.css('.reader-header button.icon-btn, .reader-toolbar button.icon-btn')
    );
    expect(controls.length).toBeGreaterThan(0);
    for (const b of controls) {
      const el = b.nativeElement as HTMLButtonElement;
      expect(el.tagName).toBe('BUTTON');
      expect(el.classList.contains('icon-btn')).toBe(true);
    }
  });

  it('offers a search control for PDF, and only for PDF', async () => {
    // A phone has no Ctrl+F, so without a visible control search is unreachable
    // by touch at all (issue #226 §2). Formats that do not implement search must
    // not be offered the control — the capability is optional on IReader.
    const pdfBook = { ...audiobook, id: 'book-pdf', fileName: 'being-and-time.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));
    fixture = await configureReaderShell();
    render();

    const searchBtn = fixture.debugElement
      .queryAll(By.css('.reader-header button.icon-btn'))
      .map((b) => b.nativeElement as HTMLButtonElement)
      .find((b) => b.getAttribute('title') === 'Search');
    expect(searchBtn).toBeTruthy();
    expect(searchBtn!.getAttribute('aria-label')).toBe('Search in document');

    const spy = vi.spyOn(fixture.componentInstance, 'toggleSearch');
    searchBtn!.click();
    expect(spy).toHaveBeenCalledTimes(1);
  });

  /**
   * A #226 follow-up. The library's find bar renders no close control — its only
   * buttons are prev/next — so the header control has to be the way out as well
   * as the way in: pressing it again used to do nothing at all.
   */
  it('shows the search control’s state and closes the bar when pressed again', async () => {
    const pdfBook = { ...audiobook, id: 'book-pdf', fileName: 'being-and-time.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));
    fixture = await configureReaderShell();
    render();

    // `@ViewChild(PdfReader)` is a TYPE query, so the stub does not resolve into
    // it — deliberate, and why these specs stay light. Attach it by hand: the
    // binding under test is the shell's, and it reads the reader's signal.
    const stub = fixture.debugElement.query(By.directive(PdfReaderStub))
      .componentInstance as PdfReaderStub;
    (fixture.componentInstance as unknown as { pdfReader: PdfReaderStub }).pdfReader = stub;
    const searchBtn = fixture.debugElement
      .queryAll(By.css('.reader-header button.icon-btn'))
      .map((b) => b.nativeElement as HTMLButtonElement)
      .find((b) => b.getAttribute('title') === 'Search')!;

    expect(searchBtn.getAttribute('aria-expanded')).toBe('false');
    expect(searchBtn.classList.contains('active')).toBe(false);

    searchBtn.click();
    render();
    expect(stub.findBarVisible()).toBe(true);
    expect(searchBtn.getAttribute('aria-expanded')).toBe('true');
    expect(searchBtn.classList.contains('active')).toBe(true);

    searchBtn.click();
    render();
    expect(stub.findBarVisible()).toBe(false);
    expect(searchBtn.getAttribute('aria-expanded')).toBe('false');
  });

  /**
   * A #226 follow-up. The page indicator is one box, not two, and the field behaves
   * like a jump box: Enter commits, leaving without Enter reverts.
   */
  it('commits a page jump on Enter and reverts the field on blur', async () => {
    const pdfBook = { ...audiobook, id: 'book-pdf-pager', fileName: 'being-and-time.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));
    fixture = await configureReaderShell();
    render();

    const stub = fixture.debugElement.query(By.directive(PdfReaderStub))
      .componentInstance as PdfReaderStub;
    (fixture.componentInstance as unknown as { pdfReader: PdfReaderStub }).pdfReader = stub;
    // `ready` gates `activeReader()`, and it is set by a 100ms timer after the
    // book loads — the binding under test is the pager's, not the load timing.
    fixture.componentInstance.ready.set(true);
    stub.progress.set({ label: '', pageNumber: 12, pageCount: 162, percentage: 7 });
    render();

    const input = fixture.nativeElement.querySelector('.page-input') as HTMLInputElement;
    expect(input).not.toBeNull();
    expect(input.value).toBe('12');
    expect(fixture.nativeElement.querySelector('.total-pages').textContent).toContain('162');

    // Abandoned edit: focus leaves without Enter, so the box must not keep a page
    // the reader never went to (and must not navigate).
    input.value = '999';
    input.dispatchEvent(new Event('blur'));
    expect(input.value).toBe('12');
    expect(stub.goTo).not.toHaveBeenCalled();

    // Enter is the commit.
    input.value = '40';
    input.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter' }));
    expect(stub.goTo).toHaveBeenCalledWith(40);
  });

  it('keeps the active ToC rail square instead of bending with the row radius', () => {
    const template = readSource('./reader-shell.component.html');
    const css = readSource('./reader-shell.component.css');

    expect(template).toContain('[class.nostos-accent-rail]="isActive(item)"');

    const activeRule = css.slice(
      css.indexOf('.toc-item.active {'),
      css.indexOf('.toc-subitems {'),
    );
    expect(activeRule).toContain(
      '--nostos-accent-rail-color: var(--color-brand-accent)',
    );
    expect(activeRule).not.toContain('box-shadow: inset 3px 0 0');
  });

  it('keeps the page indicator to a single bordered field', () => {
    // The control used to nest two boxes: a filled grey pill around a field that
    // carried its own border (measured live — pill #EEEEEF x650..789 with a
    // #C7C6CB border at x681..720.5 inside it). `.pager` drops the pill, and the
    // field keeps a VISIBLE hairline because hover does not exist on a phone.
    const css = readSource('./reader-shell.component.css');

    expect(css).toContain('.progress-display.pager');
    expect(css).toContain('background: transparent');
    expect(css).toContain('border: 1px solid var(--border-color)');
    // One typeface for the whole control: an <input> does not inherit the page's
    // type, so without `font: inherit` the number rendered in the system font
    // beside a Hanken Grotesk "/ 162".
    const inputRule = css.slice(css.indexOf('.page-input {'), css.indexOf('.page-input:hover'));
    expect(inputRule).toContain('font: inherit');
    expect(inputRule).not.toContain('border: 1px solid');
  });

  it('aligns the highlighter pens with the panel they live in', () => {
    // The dots sat 4px from the drawer's edge (measured x=1064..1154 in a panel
    // spanning 1060..1440) while the control directly above them was inset 20px:
    // the whole row hung 16px to the left of everything else (a #226 follow-up).
    const css = readSource('./reader-shell.component.css');
    const pensRule = css.slice(css.indexOf('.hl-pens {'), css.indexOf('.hl-pen {'));

    // 12px above, 16px below: it was `margin: 12px 20px 0`, so the row's bottom
    // edge landed exactly on `.quick-note`'s top edge and the dots read as glued
    // to the composer. The bottom value is the panel's own 16px rhythm.
    expect(pensRule).toContain('margin: 12px 20px 16px');
    expect(pensRule).not.toContain('padding: 10px 2px 2px');
    expect(pensRule).not.toContain('margin: 12px 20px 0;');
    // The paint stays 22px; the touch target is an invisible ::before, and it is
    // narrower than it is tall so two neighbours' hit boxes cannot overlap on a
    // 34px pitch and send the tap to the wrong pen.
    expect(css).toContain('.hl-pen::before');
    expect(css).toContain('inset: -11px -6px');
  });

  it('offers no search control to a format that has no search', async () => {
    // The capability is optional on IReader; an EPUB implements no search, so the
    // shell must not hand it a control that would do nothing.
    const epubBook = { ...audiobook, id: 'book-epub', fileName: 'iliad.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));
    fixture = await configureReaderShell();
    render();

    const titles = fixture.debugElement
      .queryAll(By.css('.reader-header button.icon-btn'))
      .map((b) => b.nativeElement.getAttribute('title'));
    expect(titles).not.toContain('Search');
  });
});

describe('ReaderShell note delete (no window.confirm)', () => {
  let fixture: ComponentFixture<ReaderShell>;

  beforeEach(() => {
    booksGetSpy.mockReset();
    booksGetSpy.mockReturnValue(of(audiobook));

    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');

    mockMatchMedia();
  });

  function render() {
    fixture.detectChanges();
    fixture.detectChanges();
  }

  it('opens ConfirmModal and only deletes on confirm', async () => {
    fixture = await configureReaderShell();
    render();

    const notes = TestBed.inject(NotesService) as unknown as { delete: ReturnType<typeof vi.fn> };
    notes.delete.mockReset();
    notes.delete.mockReturnValue(of(undefined));

    const component = fixture.componentInstance;
    component.onDeleteNote('n1');
    expect(component.pendingNoteDelete()).toBe('n1');
    expect(notes.delete).not.toHaveBeenCalled();

    render();
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeTruthy();

    component.confirmNoteDelete();
    expect(notes.delete).toHaveBeenCalledWith('n1');
    expect(component.pendingNoteDelete()).toBeNull();
  });

  it('cancelling performs nothing', async () => {
    fixture = await configureReaderShell();
    render();

    const notes = TestBed.inject(NotesService) as unknown as { delete: ReturnType<typeof vi.fn> };
    notes.delete.mockReset();
    notes.delete.mockReturnValue(of(undefined));

    const component = fixture.componentInstance;
    component.onDeleteNote('n1');
    component.cancelNoteDelete();
    expect(component.pendingNoteDelete()).toBeNull();
    expect(notes.delete).not.toHaveBeenCalled();
  });
});

describe('ReaderShell typography panel (EPUB)', () => {
  let fixture: ComponentFixture<ReaderShell>;

  beforeEach(() => {
    booksGetSpy.mockReset();
    booksGetSpy.mockReturnValue(of(audiobook));

    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');

    mockMatchMedia();
  });

  function render() {
    fixture.detectChanges();
    fixture.detectChanges();
  }

  it('offers the Aa toggle for epubs and opens the panel', async () => {
    const epubBook = { ...audiobook, id: 'book-epub', fileName: 'iliad.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));

    fixture = await configureReaderShell();
    render();

    const toggle = fixture.nativeElement.querySelector('[data-testid="typo-toggle"]');
    expect(toggle).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="typo-panel"]')).toBeNull();

    toggle.click();
    render();
    expect(fixture.nativeElement.querySelector('[data-testid="typo-panel"]')).toBeTruthy();
  });

  it('offers the same view control for a PDF, with the zoom rows and no typeface rows', async () => {
    const pdfBook = { ...audiobook, id: 'book-pdf', fileName: 'being-and-time.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));

    fixture = await configureReaderShell();
    render();

    const toggle = fixture.nativeElement.querySelector('[data-testid="typo-toggle"]');
    expect(toggle).toBeTruthy();
    toggle.click();
    render();

    const panel = fixture.nativeElement.querySelector('[data-testid="typo-panel"]');
    expect(panel).toBeTruthy();
    const labels = [...panel.querySelectorAll('.typo-label')].map(
      (e: HTMLElement) => e.textContent?.trim() ?? ''
    );
    expect(labels).toContain('Reading mode');
    expect(labels).toContain('Zoom');
    expect(labels).toContain('Page fit');
    // A fixed-layout page has no reflow to retype.
    expect(labels).not.toContain('Typeface');
    expect(labels).not.toContain('Line height');
  });

  it('shows no Aa toggle for audiobooks', async () => {
    fixture = await configureReaderShell();
    render();

    expect(fixture.nativeElement.querySelector('[data-testid="typo-toggle"]')).toBeNull();
  });

  it('forwards a typeface choice to the epub reader', async () => {
    const epubBook = { ...audiobook, id: 'book-epub', fileName: 'iliad.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));

    fixture = await configureReaderShell();
    render();

    fixture.componentInstance.toggleTypo();
    render();

    const serif = Array.from(
      fixture.nativeElement.querySelectorAll(
        '[data-testid="typo-panel"] .typo-opt',
      ) as NodeListOf<HTMLButtonElement>,
    ).find((el) => el.textContent?.trim() === 'Serif');
    expect(serif).toBeTruthy();
    serif!.click();

    const stub = fixture.debugElement.query(By.directive(EpubReaderStub));
    expect(stub.componentInstance.setTypography).toHaveBeenCalledWith({ fontFamily: 'serif' });
  });

  /**
   * Issue #208. The pens are per BOOK, like the reader's zoom: the choice is
   * remembered, handed to the reader, and must not leak to the next book.
   */
  it('offers four highlighter pens, remembers the choice per book and hands it to the reader', async () => {
    const epubBook = { ...audiobook, id: 'book-pens', fileName: 'iliad.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));
    localStorage.clear();

    fixture = await configureReaderShell();
    render();
    const component = fixture.componentInstance;
    component.toggleNotes();
    render();

    const pens = Array.from(
      fixture.nativeElement.querySelectorAll('.hl-pen') as NodeListOf<HTMLButtonElement>,
    );
    expect(pens.length).toBe(4);
    // Amber is the default pen, announced as pressed rather than only drawn.
    expect(pens[0].getAttribute('aria-pressed')).toBe('true');
    expect(component.highlightColour()).toBe(DEFAULT_HIGHLIGHT_COLOUR);

    pens[1].click();
    render();

    expect(localStorage.getItem('nostos.highlight.book-pens')).toBe('sage');
    const stub = fixture.debugElement.query(By.directive(EpubReaderStub))
      .componentInstance as EpubReaderStub;
    expect(stub.highlightColour()).toBe('sage');
    expect(pens[1].getAttribute('aria-pressed')).toBe('true');

    // That a SECOND book does not inherit this pen is pinned in
    // highlight-colours.spec.ts, which owns the per-book storage contract; the
    // reader fixture is configured once per test and cannot be re-rendered with
    // another book.
  });

  /**
   * Issue #225 §1.5. The shell owns the document-level half of the binding; the
   * EPUB reader owns the half inside the book's iframe.
   */
  it('turns pages from the keyboard, ignoring chords and text fields', async () => {
    const epubBook = { ...audiobook, id: 'book-epub', fileName: 'iliad.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));

    fixture = await configureReaderShell();
    render();
    // The shell unlocks the reader after a short settle, so activeReader() is
    // only non-null once that has run.
    await new Promise((resolve) => setTimeout(resolve, 120));
    render();

    const stub = fixture.debugElement.query(By.directive(EpubReaderStub))
      .componentInstance as EpubReaderStub;

    const press = (key: string, init: KeyboardEventInit = {}) =>
      document.dispatchEvent(
        new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }),
      );

    press('ArrowRight');
    press(' ');
    expect(stub.next).toHaveBeenCalledTimes(2);

    press('ArrowLeft');
    press('PageUp');
    expect(stub.previous).toHaveBeenCalledTimes(2);

    // A chord belongs to the browser, and a text field keeps its own keys.
    press('ArrowRight', { ctrlKey: true });
    const textarea = document.createElement('textarea');
    document.body.appendChild(textarea);
    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    expect(stub.next).toHaveBeenCalledTimes(2);
    textarea.remove();
  });
});


describe('ReaderShell UI kit migration (#362)', () => {
  let fixture: ComponentFixture<ReaderShell>;

  beforeEach(() => {
    booksGetSpy.mockReset();
    booksGetSpy.mockReturnValue(of(audiobook));
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    mockMatchMedia();
  });

  function render() {
    fixture.detectChanges();
    fixture.detectChanges();
  }

  it('uses appButton for ordinary shell actions without genericising Reader interactions', async () => {
    const epubBook = { ...audiobook, id: 'book-epub-kit', fileName: 'iliad.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));

    fixture = await configureReaderShell();
    render();

    const component = fixture.componentInstance;
    component.notesOpen.set(true);
    component.typoOpen.set(true);
    component.pendingSelectionText.set('Sing, goddess, the anger of Peleus son Achilles.');
    render();

    const canonicalLabels = Array.from(
      fixture.nativeElement.querySelectorAll('button.nostos-button') as NodeListOf<HTMLButtonElement>,
    ).map((button) => button.textContent?.replace(/\s+/g, ' ').trim() ?? '');

    expect(canonicalLabels).toContain('Cancel');
    expect(canonicalLabels.filter((label) => label === 'Save')).toHaveLength(2);
    expect(canonicalLabels).toContain('Reset');

    // These are intentionally Reader-owned interaction contracts, not ordinary
    // actions wearing local styling.
    expect(fixture.nativeElement.querySelector('.highlight-toggle.nostos-button')).toBeNull();
    expect(fixture.nativeElement.querySelector('.typo-opt.nostos-button')).toBeNull();
    expect(fixture.nativeElement.querySelector('.typo-step.nostos-button')).toBeNull();
    expect(fixture.nativeElement.querySelector('.page-input.nostos-form-control')).toBeNull();

    const template = readSource('./reader-shell.component.html');
    expect(template).not.toContain('class="btn btn-primary"');
    expect(template).not.toContain('class="btn btn-secondary"');

    // Audio transport, time navigation, speed and sleep controls have a distinct
    // playback contract and stay product-owned.
    const audioTemplate = readSource('./audio-reader/audio-reader.component.html');
    expect(audioTemplate).not.toContain('appButton');
    expect(audioTemplate).not.toContain('appIconButton');
    expect(audioTemplate).toContain('class="play-btn"');
    expect(audioTemplate).toContain('class="skip-btn"');
    expect(audioTemplate).toContain('class="playback-pill"');
  });

  it('keeps mobile touch floors on custom Reader controls after the migration', () => {
    const css = readSource('./reader-shell.component.css');
    const mobileStart = css.indexOf('@media (max-width: 768px)');
    const mobileEnd = css.indexOf('@media (max-width: 360px)');
    const mobile = css.slice(mobileStart, mobileEnd);

    expect(mobile).toContain('.highlight-toggle,');
    expect(mobile).toContain('.typo-opt,');
    expect(mobile).toContain('.typo-step,');
    expect(mobile).toContain('.quick-actions button[appButton]');
    expect(mobile).toContain('min-height: var(--control-h-touch)');
  });
});
