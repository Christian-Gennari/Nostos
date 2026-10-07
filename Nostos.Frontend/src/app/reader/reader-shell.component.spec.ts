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

import { ReaderShell, selectionPreview } from './reader-shell.component';
import { ToastService } from '../core/services/toast.service';
import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HighlightColour,
} from './highlight-colours';
import { AudioReader } from './audio-reader/audio-reader.component';
import { PdfReader } from './pdf-reader/pdf-reader.component';
import { EpubReader } from './epub-reader/epub-reader.component';
import { TopicInputComponent } from '../ui/topic-input.component/topic-input.component';
import { NoteCardComponent } from '../ui/note-card.component/note-card.component';
import { BooksService } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { TopicsService } from '../core/services/topics.service';
import { TopicAutocompleteService } from '../ui/topic-autocomplete-panel/topic-autocomplete.service';
import { Book } from '../core/dtos/book.dtos';
import { Note } from '../core/dtos/note.dtos';
import { ThemeService, THEME_STORAGE_KEY } from '../core/services/theme.service';
import { AssistantService } from '../ui/assistant/assistant.service';
import { AssistantContextService } from '../ui/assistant/assistant-context.service';

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
  surfaceInteracted = output<void>();
  textCapability = signal<'unknown' | 'available' | 'unavailable'>('available');
  searchAvailable = signal(true);
  searchState = signal({ status: 'idle' as const, current: 0, total: 0 });
  search = vi.fn();
  nextSearchResult = vi.fn();
  previousSearchResult = vi.fn();
  clearSearch = vi.fn();
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
  commitHighlight = vi.fn();
  discardHighlight = vi.fn();
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
  selectionAnchored = output<unknown>();
  commitFailed = output<unknown>();
  exitRequested = output<void>();
  searchRequested = output<void>();
  surfaceInteracted = output<void>();
  loading = signal(false);
  searchAvailable = signal(true);
  searchState = signal({ status: 'idle' as const, current: 0, total: 0 });
  search = vi.fn();
  nextSearchResult = vi.fn();
  previousSearchResult = vi.fn();
  clearSearch = vi.fn();
  commitHighlight = vi.fn();
  discardHighlight = vi.fn();
  // Typography surface the shell panel binds (mirrors EpubReader).
  typography = signal({ fontFamily: 'libron', lineHeight: 1.6, margin: 'normal' });
  fontOptions = [
    { value: 'libron', label: 'Libron' },
    { value: 'publisher', label: 'Publisher' },
    { value: 'sans', label: 'Sans' },
    { value: 'mono', label: 'Mono' },
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

// The shell binds [(ngModel)] to app-topic-input; the stub must be a
// ControlValueAccessor so the ngModel directive resolves.
@Component({
  selector: 'app-topic-input',
  standalone: true,
  template: '',
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => TopicInputStub),
      multi: true,
    },
  ],
})
class TopicInputStub implements ControlValueAccessor {
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
  topicMap = input<unknown>(null);
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
  routeBookId = 'book-1',
): Promise<ComponentFixture<ReaderShell>> {
  const initialQueryParamMap = convertToParamMap(queryParams);
  const initialParamMap = convertToParamMap({ id: routeBookId });
  routeQueryParamMap$ = new BehaviorSubject<ParamMap>(initialQueryParamMap);
  routeParamMap$ = new BehaviorSubject<ParamMap>(initialParamMap);

  TestBed.overrideComponent(ReaderShell, {
    remove: {
      imports: [PdfReader, EpubReader, TopicInputComponent, NoteCardComponent],
    },
    add: { imports: [PdfReaderStub, EpubReaderStub, TopicInputStub, NoteCardStub] },
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
      { provide: TopicsService, useValue: { list: vi.fn(() => of([])) } },
      { provide: TopicAutocompleteService, useValue: { setTopics: vi.fn() } },
      {
        provide: AssistantService,
        useValue: {
          requestSurfaceOpen: vi.fn(),
          isOpen: vi.fn(() => false),
          surfaceAvailable: vi.fn(() => true),
        },
      },
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

  it.each([
    [
      'same GUID / same case',
      'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      true,
    ],
    [
      'same GUID / different case',
      'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE',
      'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      true,
    ],
    [
      'genuinely different GUID',
      'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeef',
      'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      false,
    ],
    [
      'malformed route id',
      'not-a-guid',
      'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      false,
    ],
  ])(
    'guards grounded navigation by semantic route/book identity: %s',
    async (_label, routeBookId, loadedBookId, shouldNavigate) => {
      const epubBook = {
        ...audiobook,
        id: loadedBookId,
        type: 'ebook',
        fileName: 'source.epub',
      } as Book;
      booksGetSpy.mockReturnValue(of(epubBook));

      const fixture = await configureReaderShell(
        { sourceHref: 'chapter-2.xhtml', sourceSpine: 2, sourceOffset: 314 },
        routeBookId,
      );
      fixture.detectChanges();
      fixture.detectChanges();

      await new Promise((resolve) => setTimeout(resolve, 130));
      fixture.detectChanges();

      const stub = fixture.debugElement.query(By.directive(EpubReaderStub))
        .componentInstance as EpubReaderStub;
      expect(stub.goToSource).toHaveBeenCalledTimes(shouldNavigate ? 1 : 0);

      fixture.destroy();
    },
  );

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

describe('ReaderShell toolbar contract', () => {
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

  // The theme is app-wide (ThemeService owns data-theme on <html>). The shell
  // offers it inside View settings (#651) but never as a header button and
  // never as a reader-local data-theme.
  it('renders no header theme button and no data-theme binding on the shell', async () => {
    fixture = await configureReaderShell();
    render();

    expect(fixture.debugElement.query(By.css('.theme-toggle'))).toBeNull();
    expect(fixture.debugElement.query(By.css('.theme-toggle-btn'))).toBeNull();
    const layout = fixture.debugElement.query(By.css('.reader-layout'));
    expect(layout.nativeElement.hasAttribute('data-theme')).toBe(false);
  });

  describe('App appearance row in View settings (#651)', () => {
    function appearanceButtons(): HTMLButtonElement[] {
      return Array.from(fixture.nativeElement.querySelectorAll(
        '[data-testid="reader-appearance"] .typo-opt',
      ));
    }

    function button(label: string): HTMLButtonElement {
      return appearanceButtons().find((el) => el.textContent?.trim() === label)!;
    }

    async function openPanelFor(fileName: string): Promise<HTMLElement> {
      booksGetSpy.mockReturnValue(of({ ...audiobook, id: 'book-theme', fileName } as Book));
      fixture = await configureReaderShell();
      render();
      fixture.componentInstance.toggleTypo();
      render();
      return fixture.nativeElement.querySelector('[data-testid="typo-panel"]');
    }

    for (const fileName of ['iliad.epub', 'being-and-time.pdf']) {
      it(`is the first row of the panel for ${fileName} and labels its scope`, async () => {
        const panel = await openPanelFor(fileName);

        const firstLabel = panel.querySelector('.typo-label')?.textContent?.trim();
        expect(firstLabel).toBe('App appearance');
        expect(panel.textContent).toContain('Applies throughout Nostos');
        expect(appearanceButtons().map((el) => el.textContent?.trim())).toEqual(['Light', 'Dark']);
      });
    }

    it('reflects the current theme when the panel opens', async () => {
      localStorage.setItem(THEME_STORAGE_KEY, 'dark');
      await openPanelFor('iliad.epub');

      expect(button('Dark').getAttribute('aria-pressed')).toBe('true');
      expect(button('Light').getAttribute('aria-pressed')).toBe('false');
    });

    it('drives the app-wide ThemeService and persists with the Settings key', async () => {
      await openPanelFor('being-and-time.pdf');
      const theme = TestBed.inject(ThemeService);
      expect(theme.theme()).toBe('light');

      button('Dark').click();
      render();
      expect(theme.theme()).toBe('dark');
      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
      expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
      expect(button('Dark').getAttribute('aria-pressed')).toBe('true');
      const layout = fixture.debugElement.query(By.css('.reader-layout'));
      expect(layout.nativeElement.hasAttribute('data-theme')).toBe(false);

      button('Light').click();
      render();
      expect(theme.theme()).toBe('light');
      expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
      expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');
    });
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

  it('offers the shared Search control for PDF', async () => {
    const pdfBook = { ...audiobook, id: 'book-pdf', fileName: 'being-and-time.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));
    fixture = await configureReaderShell();
    render();

    const searchBtn = fixture.debugElement
      .queryAll(By.css('.reader-header button.icon-btn'))
      .map((b) => b.nativeElement as HTMLButtonElement)
      .find((b) => b.getAttribute('title') === 'Search');

    expect(searchBtn).toBeTruthy();
    expect(searchBtn!.getAttribute('aria-label')).toBe('Search in book');
  });

  it('offers the same Search control for EPUB', async () => {
    const epubBook = { ...audiobook, id: 'book-epub', fileName: 'dracula.epub' } as Book;
    booksGetSpy.mockReturnValue(of(epubBook));
    fixture = await configureReaderShell();
    render();

    const searchBtn = fixture.debugElement
      .queryAll(By.css('.reader-header button.icon-btn'))
      .map((b) => b.nativeElement as HTMLButtonElement)
      .find((b) => b.getAttribute('title') === 'Search');

    expect(searchBtn).toBeTruthy();
    expect(searchBtn!.getAttribute('aria-label')).toBe('Search in book');
  });

  it('clears the mounted reader search before a book switch drops activeReader', async () => {
    const firstBook = { ...audiobook, id: 'book-pdf-first', fileName: 'first.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(firstBook));
    fixture = await configureReaderShell();
    render();

    const stub = fixture.debugElement.query(By.directive(PdfReaderStub))
      .componentInstance as PdfReaderStub;
    (fixture.componentInstance as unknown as { pdfReader: PdfReaderStub }).pdfReader = stub;
    fixture.componentInstance.ready.set(true);
    render();
    stub.clearSearch.mockClear();

    const secondBook = { ...firstBook, id: 'book-pdf-second', fileName: 'second.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(secondBook));
    (fixture.componentInstance as any).loadBook(secondBook.id);

    expect(stub.clearSearch).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.searchPanelOpen()).toBe(false);
  });

  it('keeps Search disabled until the mounted reader says its engine is ready', async () => {
    const pdfBook = { ...audiobook, id: 'book-pdf-ready', fileName: 'being-and-time.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));
    fixture = await configureReaderShell();
    render();

    const stub = fixture.debugElement.query(By.directive(PdfReaderStub))
      .componentInstance as PdfReaderStub;
    (fixture.componentInstance as unknown as { pdfReader: PdfReaderStub }).pdfReader = stub;
    fixture.componentInstance.ready.set(true);
    stub.searchAvailable.set(false);
    render();

    const searchBtn = fixture.debugElement
      .queryAll(By.css('.reader-header button.icon-btn'))
      .map((b) => b.nativeElement as HTMLButtonElement)
      .find((b) => b.getAttribute('title') === 'Search')!;
    expect(searchBtn.disabled).toBe(true);

    stub.searchAvailable.set(true);
    render();
    expect(searchBtn.disabled).toBe(false);
  });

  it('opens a Nostos-owned search panel and clears format search when closed', async () => {
    const pdfBook = { ...audiobook, id: 'book-pdf', fileName: 'being-and-time.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));
    fixture = await configureReaderShell();
    render();

    const stub = fixture.debugElement.query(By.directive(PdfReaderStub))
      .componentInstance as PdfReaderStub;
    (fixture.componentInstance as unknown as { pdfReader: PdfReaderStub }).pdfReader = stub;
    fixture.componentInstance.ready.set(true);
    render();

    const searchBtn = fixture.debugElement
      .queryAll(By.css('.reader-header button.icon-btn'))
      .map((b) => b.nativeElement as HTMLButtonElement)
      .find((b) => b.getAttribute('title') === 'Search')!;

    expect(searchBtn.getAttribute('aria-expanded')).toBe('false');
    searchBtn.click();
    render();

    expect(searchBtn.getAttribute('aria-expanded')).toBe('true');
    expect(fixture.nativeElement.querySelector('[data-testid="reader-search-panel"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.reader-search-input')).not.toBeNull();

    searchBtn.click();
    render();

    expect(searchBtn.getAttribute('aria-expanded')).toBe('false');
    expect(fixture.nativeElement.querySelector('[data-testid="reader-search-panel"]')).toBeNull();
    expect(stub.clearSearch).toHaveBeenCalled();
  });

  it('routes Ctrl/Cmd+F to the shared search surface', async () => {
    const pdfBook = { ...audiobook, id: 'book-pdf-shortcut', fileName: 'being-and-time.pdf' } as Book;
    booksGetSpy.mockReturnValue(of(pdfBook));
    fixture = await configureReaderShell();
    render();
    const stub = fixture.debugElement.query(By.directive(PdfReaderStub))
      .componentInstance as PdfReaderStub;
    (fixture.componentInstance as unknown as { pdfReader: PdfReaderStub }).pdfReader = stub;
    fixture.componentInstance.ready.set(true);
    render();

    const ctrl = new KeyboardEvent('keydown', {
      key: 'f',
      ctrlKey: true,
      bubbles: true,
      cancelable: true,
    });
    fixture.componentInstance.onDocumentKeydown(ctrl);

    expect(ctrl.defaultPrevented).toBe(true);
    expect(fixture.componentInstance.searchPanelOpen()).toBe(true);
  });

  it('composes reader search from canonical Nostos input and icon-button primitives', () => {
    const template = readSource('./reader-shell.component.html');
    const css = readSource('./reader-shell.component.css');

    const panel = template.slice(
      template.indexOf('class="reader-search-panel"'),
      template.indexOf('<div class="reader-body">'),
    );
    expect(panel).toContain('appInput');
    expect(panel).toContain('appIconButton');
    expect(panel).toContain('controlSize="compact"');
    expect(css).toContain('.reader-search-panel');
    expect(css).toContain('position: absolute');
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
    // Audio remains outside the text-search capability.
    booksGetSpy.mockReturnValue(of(audiobook));
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

    const typefaces = Array.from(
      fixture.nativeElement.querySelectorAll(
        '[data-testid="typo-panel"] [aria-labelledby="typo-typeface"] .typo-opt',
      ) as NodeListOf<HTMLButtonElement>,
    );
    // The reader's options are shown as given: four choices, Libron first and
    // marked as the current one.
    expect(typefaces.map((el) => el.textContent?.trim())).toEqual([
      'Libron',
      'Publisher',
      'Sans',
      'Mono',
    ]);
    expect(typefaces.map((el) => el.getAttribute('aria-pressed'))).toEqual([
      'true',
      'false',
      'false',
      'false',
    ]);

    typefaces[1].click();

    const stub = fixture.debugElement.query(By.directive(EpubReaderStub));
    expect(stub.componentInstance.setTypography).toHaveBeenCalledWith({ fontFamily: 'publisher' });
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


describe('ReaderShell immersive chrome (#759)', () => {
  let fixture: ComponentFixture<ReaderShell>;

  beforeEach(() => {
    booksGetSpy.mockReset();
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    mockMatchMedia();
  });

  function render() {
    fixture.detectChanges();
    fixture.detectChanges();
  }

  async function openBook(fileName: string) {
    booksGetSpy.mockReturnValue(of({ ...audiobook, id: 'book-immersive', fileName } as Book));
    fixture = await configureReaderShell();
    render();
    await new Promise((resolve) => setTimeout(resolve, 120));
    render();
    return fixture.componentInstance;
  }

  it('opens EPUB in a chrome-free immersive resting state', async () => {
    const component = await openBook('book.epub');
    expect(component.immersiveReader()).toBe(true);
    expect(component.chromeVisible()).toBe(false);
    expect(component.chromeShown()).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="reader-layout"]').classList)
      .toContain('immersive');
    expect(fixture.nativeElement.querySelector('[data-testid="reader-chrome-top"]').hasAttribute('inert'))
      .toBe(true);
  });

  it('opens PDF in the same chrome-free immersive resting state', async () => {
    const component = await openBook('book.pdf');
    expect(component.immersiveReader()).toBe(true);
    expect(component.chromeVisible()).toBe(false);
    expect(component.chromeShown()).toBe(false);
  });

  it('keeps the audio edge header persistent and shows title and author once', async () => {
    const component = await openBook('book.m4b');
    expect(component.immersiveReader()).toBe(false);
    expect(component.chromeShown()).toBe(true);

    const layout = fixture.nativeElement.querySelector('[data-testid="reader-layout"]');
    const header = fixture.nativeElement.querySelector('[data-testid="reader-chrome-top"]');
    expect(layout.classList).toContain('audio-immersive');
    expect(layout.classList).not.toContain('immersive');
    expect(header.hasAttribute('inert')).toBe(false);
    expect(header.hasAttribute('aria-hidden')).toBe(false);
    expect(fixture.nativeElement.querySelectorAll('.reader-book-title, .book-title')).toHaveLength(1);
    expect(fixture.nativeElement.querySelectorAll('.reader-book-author, .book-author')).toHaveLength(1);
  });

  it('toggles chrome from the EPUB reading surface and Escape returns to rest', async () => {
    const component = await openBook('book.epub');
    const epub = fixture.debugElement.query(By.directive(EpubReaderStub)).componentInstance as EpubReaderStub;

    epub.surfaceInteracted.emit();
    render();
    expect(component.chromeVisible()).toBe(true);
    expect(component.chromeShown()).toBe(true);
    expect(fixture.nativeElement.querySelector('[data-testid="reader-chrome-top"]').hasAttribute('inert'))
      .toBe(false);

    const back = fixture.nativeElement.querySelector('.reader-back') as HTMLButtonElement;
    back.focus();
    expect(document.activeElement).toBe(back);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    render();
    expect(component.chromeVisible()).toBe(false);
    expect(component.chromeShown()).toBe(false);
    expect(document.activeElement).not.toBe(back);
  });

  it('pins chrome while a shared overlay is open and restores the resting state afterwards', async () => {
    const component = await openBook('book.epub');
    expect(component.chromeVisible()).toBe(false);

    component.openSearch();
    render();
    expect(component.searchPanelOpen()).toBe(true);
    expect(component.chromeShown()).toBe(true);
    expect(component.chromeVisible()).toBe(false);

    component.closeSearch();
    render();
    expect(component.chromeShown()).toBe(false);
  });

  it('never lets a neutral surface gesture hide selection actions or an open tool', async () => {
    const component = await openBook('book.epub');
    component.pendingSelectionText.set('selected text');
    component.handleSurfaceInteraction();
    expect(component.chromeVisible()).toBe(false);

    component.pendingSelectionText.set(null);
    component.notesOpen.set(true);
    component.handleSurfaceInteraction();
    expect(component.notesOpen()).toBe(true);
    expect(component.chromeVisible()).toBe(false);
    expect(component.chromeShown()).toBe(true);
  });

  it('keeps immersive chrome outside document flow and removes the EPUB inset box', () => {
    const shellCss = readSource('./reader-shell.component.css');
    expect(shellCss).toContain('.reader-layout.immersive .reader-header');
    expect(shellCss).toContain('.reader-layout.immersive .reader-toolbar');
    expect(shellCss).toContain('position: absolute');

    const epubCss = readSource('./epub-reader/epub-reader.component.css');
    expect(epubCss).not.toContain('width: 80%');
    expect(epubCss).not.toContain('height: 90%');
    expect(epubCss).toContain('calc((100% - 80rem) / 2)');
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
    // The notes panel keeps its Save; an EPUB selection's actions are now
    // Highlight and Add note (#650), still canonical buttons.
    expect(canonicalLabels.filter((label) => label === 'Save')).toHaveLength(1);
    expect(canonicalLabels).toContain('Highlight');
    expect(canonicalLabels).toContain('Add note');
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
    // playback contract and stay product-owned. The menu close uses the shared
    // icon-button primitive like the other transient reader panels.
    const audioTemplate = readSource('./audio-reader/audio-reader.component.html');
    expect(audioTemplate).not.toContain('appButton');
    const transportStart = audioTemplate.indexOf('<div class="main-controls">');
    const transportEnd = audioTemplate.indexOf('</div>', transportStart);
    const transportTemplate = audioTemplate.slice(transportStart, transportEnd);
    expect(transportTemplate).not.toContain('appIconButton');
    expect(audioTemplate).toContain('class="play-btn"');
    expect(audioTemplate).toContain('class="skip-btn"');
    expect(audioTemplate).toContain('class="playback-pill"');
    expect(audioTemplate).toMatch(/<button\s+appIconButton[\s\S]*?aria-label="Close playback settings"/);

    // The settings surface floats above the controls so opening it cannot
    // resize the centered player block.
    const audioCss = readSource('./audio-reader/audio-reader.component.css');
    expect(audioCss).toMatch(/\.playback-menu\s*\{[^}]*position:\s*absolute;/s);
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


describe('ReaderShell in-text selection actions (#650, EPUB)', () => {
  let fixture: ComponentFixture<ReaderShell>;
  const anchor = { top: 200, bottom: 220, left: 300, right: 500 };

  beforeEach(() => {
    booksGetSpy.mockReset();
    localStorage.clear();
    mockMatchMedia();
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1280 });
    Object.defineProperty(window, 'innerHeight', { configurable: true, value: 800 });
  });

  function render() {
    fixture.detectChanges();
    fixture.detectChanges();
  }

  async function openBook(fileName = 'iliad.epub') {
    booksGetSpy.mockReturnValue(of({ ...audiobook, id: 'book-sel', fileName } as Book));
    fixture = await configureReaderShell();
    render();
    // The shell marks the reader ready 100 ms after load (see loadBook), which
    // is when activeReader() resolves to the rendered reader.
    await new Promise((resolve) => setTimeout(resolve, 120));
    render();
    return fixture.componentInstance;
  }

  function stub(): EpubReaderStub {
    return fixture.debugElement.query(By.directive(EpubReaderStub)).componentInstance;
  }

  function attachPdfStub(component: ReaderShell): PdfReaderStub {
    const pdf = fixture.debugElement.query(By.directive(PdfReaderStub)).componentInstance as PdfReaderStub;
    // ReaderShell intentionally queries the real PdfReader type. These focused
    // integration tests attach the lightweight stand-in explicitly, then toggle
    // the readiness dependency so activeReader() recomputes against it.
    component.ready.set(false);
    component.pdfReader = pdf as unknown as PdfReader;
    component.ready.set(true);
    render();
    return pdf;
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function capture(component: ReaderShell, at: typeof anchor | null = anchor) {
    component.handleSelectionCaptured('Sing, goddess, the anger of Achilles');
    component.handleSelectionAnchored(at);
    render();
  }

  it('desktop: anchors the menu at the selection with Highlight and Add note, and no docked bar', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);

    const menu = el('selection-menu')!;
    expect(menu).not.toBeNull();
    expect(el('selection-bar')).toBeNull();
    expect(el('selection-highlight')).not.toBeNull();
    expect(el('selection-add-note')).not.toBeNull();
    // Below the selection (room below), never over it; clamped horizontally.
    expect(parseFloat(menu.style.top)).toBeGreaterThan(anchor.bottom);
    expect(menu.style.bottom).toBe('');
    const left = parseFloat(menu.style.left);
    const width = parseFloat(menu.style.width);
    expect(left).toBeGreaterThanOrEqual(8);
    expect(left + width).toBeLessThanOrEqual(1280 - 8);
  });

  it('desktop: opens above the selection when there is no room below, growing away from it', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component, { top: 700, bottom: 760, left: 1200, right: 1270 });

    const menu = el('selection-menu')!;
    expect(menu.style.top).toBe('');
    expect(parseFloat(menu.style.bottom)).toBeGreaterThanOrEqual(800 - 700);
    expect(parseFloat(menu.style.left) + parseFloat(menu.style.width)).toBeLessThanOrEqual(1280 - 8);
  });

  it('Highlight saves in one action with no note text', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);

    (el('selection-highlight') as HTMLButtonElement).click();

    expect(stub().commitHighlight).toHaveBeenCalledTimes(1);
    expect(stub().commitHighlight.mock.calls[0]).toEqual([]);
  });

  it('Add note takes the note at the text and saves it with the mark', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);

    (el('selection-add-note') as HTMLButtonElement).click();
    render();
    const input = el('selection-note-input') as HTMLTextAreaElement;
    expect(input).not.toBeNull();
    expect((el('selection-save-note') as HTMLButtonElement).disabled).toBe(true);

    input.value = '  The rage that starts it all.  ';
    input.dispatchEvent(new Event('input'));
    render();
    (el('selection-save-note') as HTMLButtonElement).click();

    expect(stub().commitHighlight).toHaveBeenCalledWith('The rage that starts it all.');

    component.handleNoteCreated();
    render();
    expect(el('selection-menu')).toBeNull();
    expect(component.noteDraft()).toBe('');
  });

  it('Ctrl/Cmd+Enter in the note field saves; plain Enter does not', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);
    component.openNoteDraft();
    component.noteDraft.set('A thought');
    render();
    const input = el('selection-note-input') as HTMLTextAreaElement;

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(stub().commitHighlight).not.toHaveBeenCalled();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true, bubbles: true }));
    expect(stub().commitHighlight).toHaveBeenCalledWith('A thought');
  });

  it('a failed save keeps the menu and the typed note', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);
    component.openNoteDraft();
    component.noteDraft.set('Keep me');
    component.saveNote();

    component.handleCommitFailed();
    render();

    expect(el('selection-menu')).not.toBeNull();
    expect(component.noteDraft()).toBe('Keep me');
    expect(component.highlightSaving()).toBe(false);
  });

  it('the scrim dismisses without saving', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);

    (el('selection-scrim') as HTMLElement).click();
    render();

    expect(stub().discardHighlight).toHaveBeenCalled();
    expect(stub().commitHighlight).not.toHaveBeenCalled();
    expect(el('selection-menu')).toBeNull();
  });

  it('Escape dismisses the menu first, then the overlay stack continues as before', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    component.notesOpen.set(true);
    await capture(component);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    render();
    expect(stub().discardHighlight).toHaveBeenCalled();
    expect(component.pendingSelectionText()).toBeNull();
    expect(component.notesOpen()).toBe(true);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    render();
    expect(component.notesOpen()).toBe(false);
  });

  it('page-turn keys typed into the note field do not turn the page', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);
    component.openNoteDraft();
    render();
    const input = el('selection-note-input') as HTMLTextAreaElement;

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true }));

    expect(stub().next).not.toHaveBeenCalled();
  });

  it('Ask Nostos opens from the selection surface and keeps the passage as explicit context', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);
    const assistant = TestBed.inject(AssistantService);
    const context = TestBed.inject(AssistantContextService);

    expect(context.context().selectedText).toBe('Sing, goddess, the anger of Achilles');
    (el('selection-ask-nostos') as HTMLButtonElement).click();
    render();

    expect(assistant.requestSurfaceOpen).toHaveBeenCalledTimes(1);
    expect(context.context().selectedText).toBe('Sing, goddess, the anger of Achilles');
    expect(el('selection-menu')).not.toBeNull();
  });

  it('does not advertise Ask Nostos when the assistant is unavailable', async () => {
    const component = await openBook();
    const assistant = TestBed.inject(AssistantService);
    const available = assistant.surfaceAvailable as unknown as {
      mockReturnValue(value: boolean): void;
    };
    available.mockReturnValue(false);

    component.dockedLayout.set(true);
    await capture(component);
    render();

    expect(el('selection-copy')).not.toBeNull();
    expect(el('selection-ask-nostos')).toBeNull();
    expect(el('selection-highlight')).not.toBeNull();
  });

  it('keeps a pending selection behind Ask Nostos until a second Escape dismisses it', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);

    const assistant = TestBed.inject(AssistantService);
    const isOpen = assistant.isOpen as unknown as {
      mockReturnValue(value: boolean): void;
    };
    isOpen.mockReturnValue(true);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    render();
    expect(component.pendingSelectionText()).toBe('Sing, goddess, the anger of Achilles');
    expect(el('selection-menu')).not.toBeNull();

    isOpen.mockReturnValue(false);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    render();
    expect(component.pendingSelectionText()).toBeNull();
    expect(el('selection-menu')).toBeNull();
  });

  it('does not turn the page from keyboard input while selection actions are pending', async () => {
    const component = await openBook();
    await capture(component);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    render();

    expect(stub().next).not.toHaveBeenCalled();
    expect(component.pendingSelectionText()).toBe('Sing, goddess, the anger of Achilles');
  });

  it('phones keep the docked bar, with Add note, and no scrim over the page', async () => {
    const component = await openBook();
    component.dockedLayout.set(true);
    await capture(component);

    expect(el('selection-menu')).toBeNull();
    expect(el('selection-scrim')).toBeNull();
    const bar = el('selection-bar')!;
    expect(bar).not.toBeNull();
    expect(el('selection-copy')).not.toBeNull();
    expect(el('selection-ask-nostos')).not.toBeNull();
    expect(el('selection-add-note')).not.toBeNull();
    expect(el('selection-highlight')).not.toBeNull();

    (el('selection-add-note') as HTMLButtonElement).click();
    render();
    expect(bar.classList.contains('has-note')).toBe(true);
    expect(el('selection-note-input')).not.toBeNull();
  });

  it('an unmeasurable selection docks the menu instead of guessing a position', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component, null);

    expect(el('selection-menu')).toBeNull();
    expect(el('selection-bar')).not.toBeNull();
  });

  it('a page turn docks an anchored menu and keeps the typed note', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);
    component.openNoteDraft();
    component.noteDraft.set('Half-written');
    render();
    expect(el('selection-menu')).not.toBeNull();

    stub().progress.set({ label: 'Page 2', percentage: 10 });
    render();

    expect(el('selection-menu')).toBeNull();
    expect(el('selection-bar')).not.toBeNull();
    expect(component.noteDraft()).toBe('Half-written');
  });

  it('PDF exposes Copy, Ask Nostos, Highlight and Add note through the same compact surface', async () => {
    const component = await openBook('being-and-time.pdf');
    const pdf = attachPdfStub(component);
    component.dockedLayout.set(false);
    component.handleSelectionCaptured('A PDF passage');
    render();

    expect(el('selection-menu')).toBeNull();
    expect(el('selection-copy')).not.toBeNull();
    expect(el('selection-ask-nostos')).not.toBeNull();
    expect(el('selection-add-note')).not.toBeNull();
    expect(el('selection-highlight')).not.toBeNull();

    (el('selection-add-note') as HTMLButtonElement).click();
    component.noteDraft.set('A note on this PDF passage.');
    render();
    (el('selection-save-note') as HTMLButtonElement).click();

    expect(pdf.commitHighlight).toHaveBeenCalledWith('A note on this PDF passage.');
    expect(el('selection-bar')!.classList.contains('epub-actions')).toBe(false);
  });

  it('Escape dismisses the pending PDF contextual surface without saving', async () => {
    const component = await openBook('being-and-time.pdf');
    const pdf = attachPdfStub(component);
    component.handleSelectionCaptured('A PDF passage');
    render();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    render();

    expect(component.pendingSelectionText()).toBeNull();
    expect(pdf.discardHighlight).toHaveBeenCalledTimes(1);
    expect(pdf.commitHighlight).not.toHaveBeenCalled();
    expect(el('selection-bar')).toBeNull();
  });

  // --- #657: desktop polish -------------------------------------------------

  function stubClipboard(writeText: (text: string) => Promise<void>) {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
  }

  it('desktop: shows the whole short passage in a quote, not a 60-character slice', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    const passage =
      'Sing, goddess, the anger of Peleus son Achilles and its devastation, which put pains thousandfold upon the Achaians.';
    component.handleSelectionCaptured(passage);
    component.handleSelectionAnchored(anchor);
    render();

    const quote = el('selection-menu')!.querySelector('.selection-quote .selected-text')!;
    expect(quote.textContent!.trim()).toBe(passage);
  });

  it('desktop: compact controls — a Copy icon action, a ghost Cancel, no touch floor', async () => {
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);

    const copy = el('selection-copy') as HTMLButtonElement;
    expect(copy).not.toBeNull();
    expect(copy.getAttribute('aria-label')).toBe('Copy passage');
    expect(el('selection-cancel')!.classList.contains('nostos-button--ghost')).toBe(true);

    const css = readSource('./reader-shell.component.css');
    expect(css).toContain('.highlight-confirmation .reader-confirm-action {');
    expect(css).not.toMatch(/^\.reader-confirm-action \{/m);
  });

  it('phones keep Copy while retaining the secondary Cancel in the docked bar', async () => {
    const component = await openBook();
    component.dockedLayout.set(true);
    await capture(component);

    expect(el('selection-copy')).not.toBeNull();
    expect(el('selection-copy')?.getAttribute('aria-label')).toBe('Copy passage');
    expect(el('selection-cancel')!.classList.contains('nostos-button--secondary')).toBe(true);
  });

  it('Copy copies the full passage, confirms with a toast and closes without saving', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    stubClipboard(writeText);
    const component = await openBook();
    const toast = TestBed.inject(ToastService);
    const success = vi.spyOn(toast, 'success');
    component.dockedLayout.set(false);
    await capture(component);

    (el('selection-copy') as HTMLButtonElement).click();
    await Promise.resolve();
    render();

    expect(writeText).toHaveBeenCalledWith('Sing, goddess, the anger of Achilles');
    expect(success).toHaveBeenCalledWith('Passage copied.');
    expect(stub().discardHighlight).toHaveBeenCalled();
    expect(stub().commitHighlight).not.toHaveBeenCalled();
    expect(el('selection-menu')).toBeNull();
  });

  it('Ctrl/Cmd+C copies the captured passage while the menu is open', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    stubClipboard(writeText);
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);

    const event = new KeyboardEvent('keydown', { key: 'c', metaKey: true, bubbles: true, cancelable: true });
    document.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(writeText).toHaveBeenCalledWith('Sing, goddess, the anger of Achilles');
  });

  it('Ctrl+C in the note field is left to the field', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    stubClipboard(writeText);
    const component = await openBook();
    component.dockedLayout.set(false);
    await capture(component);
    component.openNoteDraft();
    render();
    const input = el('selection-note-input') as HTMLTextAreaElement;

    const event = new KeyboardEvent('keydown', { key: 'c', ctrlKey: true, bubbles: true, cancelable: true });
    input.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
    expect(writeText).not.toHaveBeenCalled();
  });

  it('a failed copy keeps the menu open and reports the failure', async () => {
    stubClipboard(() => Promise.reject(new Error('denied')));
    const component = await openBook();
    const error = vi.spyOn(TestBed.inject(ToastService), 'error');
    component.dockedLayout.set(false);
    await capture(component);

    component.copySelection();
    await Promise.resolve();
    await Promise.resolve();
    render();

    expect(error).toHaveBeenCalledWith('Could not copy the passage.');
    expect(el('selection-menu')).not.toBeNull();
  });
});

describe('selectionPreview (#657)', () => {
  it('returns short passages whole, with whitespace folded', () => {
    expect(selectionPreview('  Sing,\n\n goddess  ')).toBe('Sing, goddess');
    expect(selectionPreview(null)).toBe('');
  });

  it('cuts a long passage at a word boundary with an ellipsis', () => {
    const words = Array.from({ length: 40 }, (_, i) => `word${i}`).join(' ');
    const preview = selectionPreview(words, 50);
    expect(preview.endsWith('…')).toBe(true);
    expect(preview.length).toBeLessThanOrEqual(51);
    expect(preview.slice(0, -1).split(' ').every((w) => /^word\d+$/.test(w))).toBe(true);
  });
});
