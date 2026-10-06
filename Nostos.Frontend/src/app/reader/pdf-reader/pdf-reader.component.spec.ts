import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Component, input, output } from '@angular/core';
import { of, throwError } from 'rxjs';

// @ts-expect-error — no @types/node in this repo; vitest resolves node:fs at
// runtime. Used only for static source guards (template/css/ts files).
import { readFileSync } from 'node:fs';

import { PdfReader } from './pdf-reader.component';
import {
  NgxExtendedPdfViewerModule,
  NgxExtendedPdfViewerService,
  ScrollModeType,
} from 'ngx-extended-pdf-viewer';
import { PdfAnnotationManager } from './pdf-annotation-manager';
import { NotesService } from '../../core/services/notes.service';
import { BooksService } from '../../core/services/books.service';
import { ThemeService } from '../../core/services/theme.service';
import { AssistantContextService } from '../../ui/assistant/assistant-context.service';

/**
 * Minimal stand-in for the heavy ngx-extended-pdf-viewer component (same
 * pattern as reader-shell.component.spec.ts stubs). It reflects the inputs
 * the template binds so specs can assert what the reader passes down.
 */
@Component({
  selector: 'ngx-extended-pdf-viewer',
  standalone: true,
  template: '',
})
class PdfViewerStub {
  src = input<string>();
  height = input<string>();
  sidebarVisible = input<boolean>(false);
  page = input<number>(1);
  backgroundColor = input<string>();
  pdfBackgroundColor = input<string>();
  scrollMode = input<number>(0);
  theme = input<string>('light');
  showBorders = input<boolean>(true);
  zoom = input<string | number>('page-fit');
  rotation = input<number>(0);
  ignoreKeys = input<string[]>([]);
  showToolbar = input<boolean>(true);
  textLayer = input<boolean>(false);
  handTool = input<boolean>(false);
  showHighlightEditor = input<boolean>(true);
  showHandToolButton = input<boolean>(true);
  showSidebarButton = input<boolean>(true);
  showFindButton = input<boolean>(true);
  showPagingButtons = input<boolean>(true);
  showZoomButtons = input<boolean>(true);
  showPresentationModeButton = input<boolean>(true);
  showOpenFileButton = input<boolean>(true);
  showPrintButton = input<boolean>(true);
  showDownloadButton = input<boolean>(true);
  showSecondaryToolbarButton = input<boolean>(true);
  showRotateButton = input<boolean>(true);
  showSpreadButton = input<boolean>(true);
  showPropertiesButton = input<boolean>(true);
  showTextEditor = input<boolean>(true);
  showDrawEditor = input<boolean>(true);
  showStampEditor = input<boolean>(true);
  pageChange = output<number>();
  sidebarVisibleChange = output<boolean>();
  scrollModeChange = output<number>();
  pagesLoaded = output<any>();
  pageRender = output<any>();
  pageRendered = output<any>();
  pdfLoaded = output<any>();
  textLayerRendered = output<any>();
  textSelection = output<any>();
  updateFindMatchesCount = output<any>();
  updateFindState = output<any>();
}

/** Everything the overridden PdfReader needs to compile in these specs. */
const PDF_READER_TEST_IMPORTS = [PdfViewerStub];

const readSource = (file: string) =>
  readFileSync(new URL(file, import.meta.url), 'utf-8');

describe('PdfReader theme-following surround and page inversion (#259)', () => {
  let fixture: ComponentFixture<PdfReader>;
  let themeService: ThemeService;

  const notesService = { list: vi.fn(() => of([])) };
  const booksService = { updateProgress: vi.fn(() => of(null)) };

  beforeEach(async () => {
    localStorage.clear();

    await TestBed.configureTestingModule({
      imports: [PdfReader],
      providers: [
        { provide: NotesService, useValue: notesService },
        { provide: BooksService, useValue: booksService },
        {
          provide: PdfAnnotationManager,
          useValue: {
            paint: vi.fn(),
            captureHighlight: vi.fn(),
            captureNoteLocation: vi.fn(() => null),
          },
        },
      ],
    })
      .overrideComponent(PdfReader, {
        remove: { imports: [NgxExtendedPdfViewerModule] },
        add: { imports: PDF_READER_TEST_IMPORTS },
      })
      .compileComponents();

    themeService = TestBed.inject(ThemeService);
  });

  afterEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
    // Reset to light so other suites are not affected.
    themeService.setTheme('light');
  });

  function setupComponent() {
    fixture = TestBed.createComponent(PdfReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.detectChanges();
    return fixture;
  }

  function viewerStub() {
    const debugEl = fixture.debugElement.query(By.directive(PdfViewerStub));
    expect(debugEl).not.toBeNull();
    return debugEl!.componentInstance as PdfViewerStub;
  }

  it('keeps a continuous-scroll touch drag native and consumes only its synthetic click', () => {
    setupComponent();
    const component = fixture.componentInstance;
    const next = vi.spyOn(component, 'next');
    const previous = vi.spyOn(component, 'previous');
    const surface = vi.fn();
    component.surfaceInteracted.subscribe(surface);

    const target = document.createElement('div');
    const container = document.createElement('div');
    Object.defineProperty(container, 'clientWidth', { configurable: true, value: 390 });
    container.getBoundingClientRect = () =>
      ({ left: 0, top: 0, right: 390, bottom: 844, width: 390, height: 844, x: 0, y: 0, toJSON() {} }) as DOMRect;

    component.onSurfaceTouchStart({
      touches: [{ clientX: 280, clientY: 220 }],
      target,
    } as unknown as TouchEvent);
    component.onSurfaceTouchEnd({
      changedTouches: [{ clientX: 120, clientY: 230 }],
    } as unknown as TouchEvent);

    expect(component.scrollMode()).toBe(ScrollModeType.vertical);
    expect(next).not.toHaveBeenCalled();
    expect(previous).not.toHaveBeenCalled();

    component.onSurfaceClick({
      clientX: 120,
      clientY: 230,
      target,
      currentTarget: container,
    } as unknown as MouseEvent);
    expect(surface).not.toHaveBeenCalled();

    component.onSurfaceClick({
      clientX: 300,
      clientY: 300,
      target,
      currentTarget: container,
    } as unknown as MouseEvent);
    expect(surface).toHaveBeenCalledTimes(1);
  });

  it('follows the light theme with the established surround and light library theme', () => {
    themeService.setTheme('light');
    setupComponent();

    expect(viewerStub().backgroundColor()).toBe('#fefeff');
    expect(viewerStub().theme()).toBe('light');
  });

  it('follows the dark theme with a dark surround and dark library theme', () => {
    themeService.setTheme('dark');
    setupComponent();

    expect(viewerStub().backgroundColor()).toBe('#0d0e11');
    expect(viewerStub().theme()).toBe('dark');
  });

  it('applies a grounded PDF source received before pagesLoaded instead of losing it', () => {
    setupComponent();

    fixture.componentInstance.goToSource({
      type: 'pdf',
      pdfPage: 9,
      pdfPageLabel: '7',
    });
    expect(fixture.componentInstance.currentPage).toBe(1);

    fixture.componentInstance.onPagesLoaded({ pagesCount: 20 } as any);

    expect(fixture.componentInstance.currentPage).toBe(9);
    expect(fixture.componentInstance.progress().pageNumber).toBe(9);
    expect(fixture.componentInstance.progress().pageCount).toBe(20);
    expect(fixture.componentInstance.progress().pageLabel).toBe('7');
    expect(fixture.componentInstance.progress().label).toBe('p. 7 · PDF 9 of 20');
  });

  it('binds theme and backgroundColor reactively, not as hardcoded strings', () => {
    const html = readSource('./pdf-reader.component.html');
    expect(html).toContain('[backgroundColor]="pdfBgColor()"');
    expect(html).toContain('[theme]="pdfTheme()"');
    // No leftover hardcoded surround.
    expect(html).not.toContain("[backgroundColor]=\"'#fefeff'\"");
  });

  it('defaults to inverted in dark mode and as-printed in light mode', () => {
    themeService.setTheme('dark');
    setupComponent();
    expect(fixture.componentInstance.pageInverted()).toBe(true);

    themeService.setTheme('light');
    fixture = TestBed.createComponent(PdfReader);
    fixture.componentRef.setInput('bookId', 'book-2');
    fixture.detectChanges();
    expect(fixture.componentInstance.pageInverted()).toBe(false);
  });

  it('switches theme mid-document without recreating the viewer, keeping chrome and pages in step (#651)', () => {
    themeService.setTheme('light');
    setupComponent();
    const viewer = viewerStub();
    const container = fixture.debugElement.query(By.css('.pdf-container')).nativeElement as HTMLElement;
    expect(container.classList.contains('inverted')).toBe(false);

    themeService.setTheme('dark');
    fixture.detectChanges();
    expect(viewerStub()).toBe(viewer);
    expect(viewer.theme()).toBe('dark');
    expect(viewer.backgroundColor()).toBe('#0d0e11');
    // With no per-book choice the dark default (inverted) applies in the same
    // change as the chrome, so there is no frame where they disagree.
    expect(container.classList.contains('inverted')).toBe(true);

    themeService.setTheme('light');
    fixture.detectChanges();
    expect(viewerStub()).toBe(viewer);
    expect(viewer.theme()).toBe('light');
    expect(container.classList.contains('inverted')).toBe(false);
  });

  it('keeps an explicit As printed choice across theme switches and never inverts in light', () => {
    themeService.setTheme('dark');
    setupComponent();
    fixture.componentInstance.setPageInverted(false);

    themeService.setTheme('light');
    fixture.detectChanges();
    expect(fixture.componentInstance.pageInverted()).toBe(false);

    themeService.setTheme('dark');
    fixture.detectChanges();
    expect(fixture.componentInstance.pageInverted()).toBe(false);

    fixture.componentInstance.setPageInverted(true);
    themeService.setTheme('light');
    fixture.detectChanges();
    // Page colours are a dark-mode choice.
    expect(fixture.componentInstance.pageInverted()).toBe(false);
  });

  it('persists the inversion choice per book and restores it', () => {
    themeService.setTheme('dark');
    setupComponent();
    // Override the default.
    fixture.componentInstance.setPageInverted(false);
    expect(localStorage.getItem('nostos.pdf-invert.book-1')).toBe('false');

    // Re-create: should restore the persisted choice.
    fixture = TestBed.createComponent(PdfReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.detectChanges();
    expect(fixture.componentInstance.pageInverted()).toBe(false);
  });

  it('applies the inverted class when pageInverted is true', () => {
    themeService.setTheme('dark');
    setupComponent();
    const container = fixture.debugElement.query(By.css('.pdf-container'));
    expect(container.nativeElement.classList.contains('inverted')).toBe(true);

    fixture.componentInstance.setPageInverted(false);
    fixture.detectChanges();
    expect(container.nativeElement.classList.contains('inverted')).toBe(false);
  });

  it('declares a dark page-edge variant in CSS', () => {
    const css = readSource('./pdf-reader.component.css');
    // Dark page edge.
    expect(css).toContain("host-context([data-theme='dark'])");
    expect(css).toContain('--pdf-page-outline');
    expect(css).toContain('outline: var(--pdf-page-outline)');
    expect(css).toContain('box-shadow: var(--pdf-page-shadow)');
  });

  it('declares the inversion filter in CSS behind the .inverted class', () => {
    const css = readSource('./pdf-reader.component.css');
    expect(css).toContain('.pdf-container.inverted');
    expect(css).toContain('filter: invert(1) hue-rotate(180deg)');
  });

  it('hidden-toolbar state uses no negative margin (offset reset at #viewerContainer)', () => {
    const css = readSource('./pdf-reader.component.css');

    expect(css).not.toContain('-34px');
    expect(css).not.toMatch(/margin(?:-top)?\s*:\s*-/);

    // The hidden internal toolbar offset is reset at the scrollport instead.
    expect(css).toContain('#mainContainer.toolbar-hidden');
    expect(css).toContain('margin-top: 0 !important');
    expect(css).toContain('#mainContainer.toolbar-hidden #viewerContainer');
    expect(css).toContain('top: 0 !important');
  });

  it('keeps the PDF scrollport above the shell toolbar with bottom scroll padding', () => {
    const css = readSource('./pdf-reader.component.css');

    // The scrollport must not end flush with the shell toolbar: bottom
    // padding sized to the toolbar height guarantees the final page clears it.
    const viewerContainerRule = css.slice(css.indexOf('#viewerContainer'));
    expect(viewerContainerRule).toContain('padding-bottom');
    expect(viewerContainerRule).toContain('var(--toolbar-height');
  });

  it('adds safe-area inset so mobile final-page content clears toolbar + safe area', () => {
    const css = readSource('./pdf-reader.component.css');

    expect(css).toContain('env(safe-area-inset-bottom, 0px)');
    const viewerContainerRule = css.slice(css.indexOf('#viewerContainer'));
    expect(viewerContainerRule).toContain(
      'calc(var(--toolbar-height, 60px) + env(safe-area-inset-bottom, 0px))',
    );
  });
});

/**
 * The contents rail was empty for EVERY PDF: the library's `PdfLoadedEvent` is
 * `{ pagesCount }` and nothing else, so the old handler's `if (pdfDoc)` guard
 * never held and the outline was never fetched. A 512-page book with 129
 * embedded bookmarks rendered "No Table of Contents available." (issue #226 §1).
 * These specs pin the document reference to the event that carries it.
 */
describe('PdfReader contents rail from the embedded outline', () => {
  let fixture: ComponentFixture<PdfReader>;

  const pageRef = (num: number) => ({ num, gen: 0 });
  const dest = (num: number) => [pageRef(num), { name: 'Fit' }];

  const outline = [
    { title: 'Introduction', dest: dest(10), items: [] },
    {
      title: 'Part One',
      dest: 'part-one',
      items: [{ title: 'Chapter 1', dest: dest(20), items: [] }],
    },
  ];

  const makePdfDoc = (over: Record<string, unknown> = {}) => ({
    getOutline: vi.fn(async () => outline),
    getDestination: vi.fn(async () => dest(20)),
    getPageIndex: vi.fn(async (ref: { num: number }) => ref.num - 1),
    ...over,
  });

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [PdfReader],
      providers: [
        { provide: NotesService, useValue: { list: vi.fn(() => of([])) } },
        { provide: BooksService, useValue: { updateProgress: vi.fn(() => of(null)) } },
        {
          provide: PdfAnnotationManager,
          useValue: { paint: vi.fn(), captureHighlight: vi.fn(), captureNoteLocation: vi.fn(() => null) },
        },
      ],
    })
      .overrideComponent(PdfReader, {
        remove: { imports: [NgxExtendedPdfViewerModule] },
        add: { imports: PDF_READER_TEST_IMPORTS },
      })
      .compileComponents();
  });

  function setupComponent() {
    fixture = TestBed.createComponent(PdfReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.detectChanges();
    return fixture;
  }

  /** The outline load is fired from the event handler, so let its microtasks run. */
  const flushAsync = () => new Promise((resolve) => setTimeout(resolve, 0));

  it('maps the outline carried by pagesLoaded into the contents rail', async () => {
    setupComponent();
    const pdfDoc = makePdfDoc();

    await fixture.componentInstance.onPagesLoaded({
      pagesCount: 512,
      source: { pdfDocument: pdfDoc },
    } as never);
    await flushAsync();

    const toc = fixture.componentInstance.toc();
    expect(toc).toHaveLength(2);
    expect(toc[0]).toMatchObject({ label: 'Introduction', target: 10 });
    // A named destination resolves through getDestination -> getPageIndex.
    expect(toc[1].label).toBe('Part One');
    expect(toc[1].target).toBe(20);
    expect(toc[1].children?.[0]).toMatchObject({ label: 'Chapter 1', target: 20 });
  });

  it('leaves the rail empty for a PDF with no outline, without erroring', async () => {
    setupComponent();
    const pdfDoc = makePdfDoc({ getOutline: vi.fn(async () => null) });

    await fixture.componentInstance.onPagesLoaded({
      pagesCount: 12,
      source: { pdfDocument: pdfDoc },
    } as never);
    await flushAsync();

    expect(fixture.componentInstance.toc()).toEqual([]);
  });

  it('survives a failing getOutline instead of throwing out of the event handler', async () => {
    setupComponent();
    const error = vi.spyOn(console, 'error').mockImplementation(() => {});
    const pdfDoc = makePdfDoc({
      getOutline: vi.fn(async () => {
        throw new Error('outline unreadable');
      }),
    });

    await fixture.componentInstance.onPagesLoaded({
      pagesCount: 40,
      source: { pdfDocument: pdfDoc },
    } as never);
    await flushAsync();

    expect(fixture.componentInstance.toc()).toEqual([]);
    expect(error).toHaveBeenCalled();
  });

  it('pins the root cause: the library event that carries a document is pagesLoaded', () => {
    // Read the installed library's own interface. If `pdfLoaded` ever grows a
    // document, this fails and the `pagesLoaded.source` path can be revisited
    // deliberately rather than by accident.
    const dts = readFileSync(
      // Path relative to the frontend root (the test runner's cwd): resolving it
      // against import.meta.url crosses out of the source tree, where vitest
      // hands back a non-file URL.
      'node_modules/ngx-extended-pdf-viewer/lib/events/pdf-loaded-event.d.ts',
      'utf-8',
    );
    expect(dts).toContain('pagesCount');
    expect(dts).not.toContain('pdfDocument');
  });
});

/**
 * Search chrome is owned by ReaderShell; PdfReader only adapts the public
 * ngx/PDF.js find API and publishes portable match state.
 */
describe('PdfReader shared search adapter (#761)', () => {
  let fixture: ComponentFixture<PdfReader>;
  const pdfSearch = {
    find: vi.fn(() => [Promise.resolve(2), Promise.resolve(1)]),
    findNext: vi.fn(() => true),
    findPrevious: vi.fn(() => true),
  };

  beforeEach(async () => {
    localStorage.clear();
    pdfSearch.find.mockClear();
    pdfSearch.findNext.mockClear();
    pdfSearch.findPrevious.mockClear();

    await TestBed.configureTestingModule({
      imports: [PdfReader],
      providers: [
        { provide: NotesService, useValue: { list: vi.fn(() => of([])) } },
        { provide: BooksService, useValue: { updateProgress: vi.fn(() => of(null)) } },
        { provide: NgxExtendedPdfViewerService, useValue: pdfSearch },
        {
          provide: PdfAnnotationManager,
          useValue: { paint: vi.fn(), captureHighlight: vi.fn(), captureNoteLocation: vi.fn(() => null) },
        },
      ],
    })
      .overrideComponent(PdfReader, {
        remove: { imports: [NgxExtendedPdfViewerModule] },
        add: { imports: PDF_READER_TEST_IMPORTS },
      })
      .compileComponents();

    fixture = TestBed.createComponent(PdfReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.detectChanges();
    (fixture.componentInstance as any).pdfSearchReady.set(true);
    fixture.componentInstance.textCapability.set('available');
  });

  it('drops search state and readiness when the component is reused for another PDF', () => {
    fixture.componentInstance.search('Being');
    expect(fixture.componentInstance.searchAvailable()).toBe(true);

    fixture.componentInstance.ngOnChanges({
      bookId: { firstChange: false },
    } as any);

    expect(pdfSearch.find).toHaveBeenLastCalledWith('', {
      highlightAll: false,
      dontScrollIntoView: true,
    });
    expect(fixture.componentInstance.searchState()).toEqual({
      status: 'idle',
      current: 0,
      total: 0,
    });
    expect(fixture.componentInstance.searchAvailable()).toBe(false);
    expect(fixture.componentInstance.textCapability()).toBe('unknown');
  });

  it('does not start a PDF search before the viewer find engine is ready', () => {
    (fixture.componentInstance as any).pdfSearchReady.set(false);

    fixture.componentInstance.search('Being');

    expect(pdfSearch.find).not.toHaveBeenCalled();
    expect(fixture.componentInstance.searchState()).toEqual({
      status: 'idle',
      current: 0,
      total: 0,
    });
  });

  it('recovers to idle if the viewer reports ready before find() is actually usable', () => {
    pdfSearch.find.mockReturnValueOnce(undefined as any);

    fixture.componentInstance.search('Being');

    expect(fixture.componentInstance.searchState()).toEqual({
      status: 'idle',
      current: 0,
      total: 0,
    });
    expect(fixture.componentInstance.searchAvailable()).toBe(false);
  });

  it('delegates literal search to the public PDF viewer service', async () => {
    fixture.componentInstance.search('Being');

    expect(pdfSearch.find).toHaveBeenCalledWith('Being', {
      highlightAll: false,
      matchCase: false,
      dontScrollIntoView: false,
    });

    await Promise.resolve();
    await Promise.resolve();
    expect(fixture.componentInstance.searchState()).toEqual({
      status: 'ready',
      current: 1,
      total: 3,
    });
  });

  it('uses PDF.js match-count events for current/total navigation state', () => {
    fixture.componentInstance.search('Being');
    fixture.componentInstance.onFindMatchesCount({ current: 2, total: 5 } as any);

    expect(fixture.componentInstance.searchState()).toEqual({
      status: 'ready',
      current: 2,
      total: 5,
    });

    fixture.componentInstance.nextSearchResult();
    fixture.componentInstance.previousSearchResult();
    expect(pdfSearch.findNext).toHaveBeenCalledTimes(1);
    expect(pdfSearch.findPrevious).toHaveBeenCalledTimes(1);
  });

  it('clears PDF.js search and resets the shared state', () => {
    fixture.componentInstance.search('Being');
    fixture.componentInstance.clearSearch();

    expect(pdfSearch.find).toHaveBeenLastCalledWith('', {
      highlightAll: false,
      dontScrollIntoView: true,
    });
    expect(fixture.componentInstance.searchState()).toEqual({
      status: 'idle',
      current: 0,
      total: 0,
    });
  });

  it('clears the previous PDF.js mark when the user empties the query', () => {
    fixture.componentInstance.search('Being');
    fixture.componentInstance.search('   ');

    expect(pdfSearch.find).toHaveBeenLastCalledWith('', {
      highlightAll: false,
      dontScrollIntoView: true,
    });
    expect(fixture.componentInstance.searchState().status).toBe('idle');
  });

  it('no longer renders or styles the embedded PDF find bar', () => {
    const html = readSource('./pdf-reader.component.html');
    const css = readSource('./pdf-reader.component.css');

    expect(html).not.toContain('findbarVisible');
    expect(html).toContain('(updateFindMatchesCount)="onFindMatchesCount($event)"');
    expect(css).not.toContain('.findbar');
  });
});

/**
 * Reading mode (issue #226 §3 and §4): the viewer was pinned to
 * `ScrollMode.PAGE`, so a page could only be left by clicking Next, and the page
 * was fitted whole on a phone at roughly 9.5px with the zoom controls hidden.
 */
describe('PdfReader reading mode', () => {
  let fixture: ComponentFixture<PdfReader>;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [PdfReader],
      providers: [
        { provide: NotesService, useValue: { list: vi.fn(() => of([])) } },
        { provide: BooksService, useValue: { updateProgress: vi.fn(() => of(null)) } },
        {
          provide: PdfAnnotationManager,
          useValue: { paint: vi.fn(), captureHighlight: vi.fn(), captureNoteLocation: vi.fn(() => null) },
        },
      ],
    })
      .overrideComponent(PdfReader, {
        remove: { imports: [NgxExtendedPdfViewerModule] },
        add: { imports: PDF_READER_TEST_IMPORTS },
      })
      .compileComponents();
  });

  const withViewport = <T,>(width: number, run: () => T): T => {
    const original = window.innerWidth;
    Object.defineProperty(window, 'innerWidth', { value: width, configurable: true, writable: true });
    try {
      return run();
    } finally {
      Object.defineProperty(window, 'innerWidth', { value: original, configurable: true, writable: true });
    }
  };

  function make() {
    const f = TestBed.createComponent(PdfReader);
    f.componentRef.setInput('bookId', 'book-1');
    f.detectChanges();
    return f;
  }

  it('reads continuously instead of one page at a time', () => {
    fixture = make();
    const component = fixture.componentInstance;

    expect(component.scrollMode()).toBe(ScrollModeType.vertical);
    // Sanity: `page` is 3, i.e. exactly what the pinning used to mean. If this
    // number ever moves, the note in the component is describing the wrong enum.
    expect(ScrollModeType.page).toBe(3);

    // The stub's own default is also 0, so asserting through it would pass
    // vacuously. Guard the template binding instead.
    const html = readSource('./pdf-reader.component.html');
    expect(html).toContain('[scrollMode]="scrollMode()"');
    expect(html).not.toContain('[scrollMode]="3"');
  });

  it('offers page-by-page as well, remembering the choice per book', () => {
    fixture = make();
    const component = fixture.componentInstance;

    expect(component.readingModes.map((m) => m.label)).toEqual(['Scroll', 'Page']);
    expect(component.isScrollMode(ScrollModeType.vertical)).toBe(true);

    component.setScrollMode(ScrollModeType.page);
    expect(component.scrollMode()).toBe(ScrollModeType.page);
    expect(component.isScrollMode(ScrollModeType.vertical)).toBe(false);
    expect(localStorage.getItem('nostos.pdf-scroll.book-1')).toBe('page');

    // The viewer can flip it itself (its own controls or keys), and that persists.
    component.onScrollModeChange(ScrollModeType.vertical);
    expect(localStorage.getItem('nostos.pdf-scroll.book-1')).toBe('scroll');
  });

  it('restores a remembered page-by-page mode', () => {
    localStorage.setItem('nostos.pdf-scroll.book-1', 'page');
    fixture = make();
    expect(fixture.componentInstance.scrollMode()).toBe(ScrollModeType.page);
  });

  it('fits the page width on a phone, the whole page on desktop', () => {
    withViewport(390, () => {
      expect(make().componentInstance.zoomLevel()).toBe('page-width');
    });
    withViewport(1440, () => {
      expect(make().componentInstance.zoomLevel()).toBe('page-fit');
    });
  });

  it('lets the header zoom controls move the zoom away from the default', () => {
    withViewport(390, () => {
      fixture = make();
      expect(fixture.componentInstance.zoomLevel()).toBe('page-width');

      fixture.componentInstance.zoomIn();
      // From a named fit the first step lands on a concrete percentage, so the
      // phone reader is never stuck on a fit it cannot enlarge.
      expect(fixture.componentInstance.zoomLevel()).toBe(110);
    });
  });

  it('blocks pdf.js rotation shortcuts and keeps app-owned rotation recoverable', () => {
    fixture = make();
    const component = fixture.componentInstance;
    const viewer = fixture.debugElement.query(By.directive(PdfViewerStub))
      .componentInstance as PdfViewerStub;

    expect(viewer.ignoreKeys()).toEqual(['R', 'SHIFT+R']);
    expect(viewer.rotation()).toBe(0);

    component.rotateClockwise();
    fixture.detectChanges();
    expect(component.rotation()).toBe(90);
    expect(viewer.rotation()).toBe(90);

    component.rotateCounterclockwise();
    fixture.detectChanges();
    expect(component.rotation()).toBe(0);

    component.rotateCounterclockwise();
    expect(component.rotation()).toBe(270);

    component.resetRotation();
    fixture.detectChanges();
    expect(component.rotation()).toBe(0);
    expect(viewer.rotation()).toBe(0);
  });

  it('exposes left, reset, and right rotation controls in PDF View settings', () => {
    const shell = readSource('../reader-shell.component.html');

    expect(shell).toContain('id="pdf-rotation"');
    expect(shell).toContain('(click)="pdfReader?.rotateCounterclockwise()"');
    expect(shell).toContain('(click)="pdfReader?.resetRotation()"');
    expect(shell).toContain('(click)="pdfReader?.rotateClockwise()"');
  });

  it('offers the three fits the acceptance criteria ask for', () => {
    fixture = make();

    expect(fixture.componentInstance.zoomPresets.map((p) => p.value)).toEqual([
      'page-width',
      'page-fit',
      100,
    ]);
  });

  it('sets zoom and remembers it for that book only', () => {
    fixture = make();
    const component = fixture.componentInstance;

    component.setZoom('page-fit');
    expect(component.zoomLevel()).toBe('page-fit');
    // Stored by NAME, so a fit keeps adapting when the window changes.
    expect(localStorage.getItem('nostos.pdf-zoom.book-1')).toBe('"page-fit"');

    component.setZoom(150);
    expect(localStorage.getItem('nostos.pdf-zoom.book-1')).toBe('150');
    expect(JSON.parse(localStorage.getItem('nostos.pdf-zoom.book-1')!)).toBe(150);
  });

  it('restores the remembered zoom instead of the viewport default', () => {
    // The phone default is 'page-width'; a remembered choice must win.
    localStorage.setItem('nostos.pdf-zoom.book-1', JSON.stringify(175));
    withViewport(390, () => {
      fixture = make();
      expect(fixture.componentInstance.zoomLevel()).toBe(175);
    });
  });

  it('reports which preset is active and labels the current zoom', () => {
    fixture = make();
    const component = fixture.componentInstance;

    component.setZoom('page-width');
    expect(component.isZoomPreset('page-width')).toBe(true);
    expect(component.isZoomPreset('page-fit')).toBe(false);
    expect(component.zoomLabel()).toBe('Fit width');

    component.setZoom(100);
    expect(component.isZoomPreset(100)).toBe(true);
    // A number never matches a named fit, so no chip stays lit by accident.
    expect(component.isZoomPreset('page-width')).toBe(false);
    expect(component.zoomLabel()).toBe('100%');
  });
});


/**
 * #478 P0 trust regressions. Keep this block intentionally narrow: one test for
 * retriable persistence and one for Ask Nostos native-selection lifecycle.
 */
describe('PdfReader highlight trust regressions (#478)', () => {
  let fixture: ComponentFixture<PdfReader>;
  let selectionText: string | null;
  let captureHighlight: ReturnType<typeof vi.fn>;
  let captureSelectionText: ReturnType<typeof vi.fn>;
  let createNote: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    localStorage.clear();
    selectionText = null;
    captureSelectionText = vi.fn(() => selectionText);
    captureHighlight = vi.fn(() => ({
      status: 'captured',
      pageNumber: 3,
      rects: [{ left: 0.1, top: 0.2, width: 0.3, height: 0.04 }],
      selectedText: 'same difficult selection',
    }));

    let attempt = 0;
    createNote = vi.fn(() => {
      attempt += 1;
      return attempt === 1
        ? throwError(() => new Error('transient save failure'))
        : of({ id: 'note-1' } as any);
    });

    await TestBed.configureTestingModule({
      imports: [PdfReader],
      providers: [
        {
          provide: NotesService,
          useValue: { list: vi.fn(() => of([])), create: createNote },
        },
        { provide: BooksService, useValue: { updateProgress: vi.fn(() => of(null)) } },
        {
          provide: PdfAnnotationManager,
          useValue: {
            paint: vi.fn(),
            captureHighlight,
            captureSelectionText,
            captureNoteLocation: vi.fn(() => null),
          },
        },
      ],
    })
      .overrideComponent(PdfReader, {
        remove: { imports: [NgxExtendedPdfViewerModule] },
        add: { imports: PDF_READER_TEST_IMPORTS },
      })
      .compileComponents();

    fixture = TestBed.createComponent(PdfReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.componentRef.setInput('highlightMode', true);
    fixture.detectChanges();
  });

  afterEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('does not page under a PDF selection even in page-by-page mode', () => {
    const component = fixture.componentInstance;
    component.setScrollMode(ScrollModeType.page);
    selectionText = 'selected passage';
    const next = vi.spyOn(component, 'next');

    component.onSurfaceTouchStart({
      touches: [{ clientX: 300, clientY: 200 }],
      target: document.createElement('div'),
    } as unknown as TouchEvent);
    component.onSurfaceTouchEnd({
      changedTouches: [{ clientX: 140, clientY: 205 }],
    } as unknown as TouchEvent);

    expect(next).not.toHaveBeenCalled();
    expect(TestBed.inject(AssistantContextService).context().selectedText).toBe('selected passage');
  });

  it('retries the exact same pending PDF highlight after a transient save failure', () => {
    const component = fixture.componentInstance;

    component.onTextSelection();
    component.commitHighlight();
    expect(createNote).toHaveBeenCalledTimes(1);

    const firstDto = createNote.mock.calls[0][1];
    component.commitHighlight();

    expect(createNote).toHaveBeenCalledTimes(2);
    expect(createNote.mock.calls[1][1]).toEqual(firstDto);
    expect(firstDto.selectedText).toBe('same difficult selection');
    expect(JSON.parse(firstDto.cfiRange)).toMatchObject({
      pageNumber: 3,
      colour: 'amber',
    });
  });

  it('publishes current native PDF selection without highlight mode and clears it when stale', () => {
    const component = fixture.componentInstance;
    const context = TestBed.inject(AssistantContextService);
    fixture.componentRef.setInput('highlightMode', false);
    fixture.detectChanges();

    selectionText = 'phrase A';
    component.onNativeSelectionChange();
    expect(context.context().selectedText).toBe('phrase A');

    selectionText = null;
    component.onNativeSelectionChange();
    expect(context.context().selectedText).toBeNull();

    selectionText = 'phrase B';
    component.onNativeSelectionChange();
    expect(context.context().selectedText).toBe('phrase B');

    component.currentPage = 1;
    component.onPageChange(2);
    expect(context.context().selectedText).toBeNull();
  });
});
