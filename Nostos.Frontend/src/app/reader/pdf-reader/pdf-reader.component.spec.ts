import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Component, input, output } from '@angular/core';
import { of, throwError } from 'rxjs';

// @ts-expect-error — no @types/node in this repo; vitest resolves node:fs at
// runtime. Used only for static source guards (template/css/ts files).
import { readFileSync } from 'node:fs';

import { PdfReader } from './pdf-reader.component';
import { NgxExtendedPdfViewerModule, ScrollModeType } from 'ngx-extended-pdf-viewer';
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
  // Search (issue #226 §2): the find bar and the options the reader trims.
  findbarVisible = input<boolean>(false);
  showFindHighlightAll = input<boolean>(true);
  showFindMatchCase = input<boolean>(false);
  showFindResultsCount = input<boolean>(true);
  showFindMessages = input<boolean>(true);
  showFindMatchDiacritics = input<boolean>(false);
  showFindEntireWord = input<boolean>(false);
  showFindMultiple = input<boolean>(false);
  // The find bar's input area is re-declared by the reader (a #226 follow-up); the
  // stub must accept the binding or the template fails to compile.
  customFindbarInputArea = input<unknown>();

  pageChange = output<number>();
  sidebarVisibleChange = output<boolean>();
  scrollModeChange = output<number>();
  findbarVisibleChange = output<boolean>();
  pagesLoaded = output<any>();
  pageRender = output<any>();
  pageRendered = output<any>();
  pdfLoaded = output<any>();
  textLayerRendered = output<any>();
  textSelection = output<any>();
}

/**
 * The find bar's own pieces are declared INSIDE `NgxExtendedPdfViewerModule` and
 * are not standalone, so a standalone component cannot list them in `imports`.
 * The specs below remove that module to keep the suite light, so the three
 * selectors our template re-declares (a #226 follow-up) need stand-ins: the stub viewer
 * never instantiates that ng-template, but Angular still compiles its content.
 */
@Component({ selector: 'pdf-search-input-field', standalone: true, template: '' })
class PdfSearchInputFieldStub {}

@Component({ selector: 'pdf-find-previous', standalone: true, template: '' })
class PdfFindPreviousStub {}

@Component({ selector: 'pdf-find-next', standalone: true, template: '' })
class PdfFindNextStub {}

/** Everything the overridden PdfReader needs to compile in these specs. */
const PDF_READER_TEST_IMPORTS = [
  PdfViewerStub,
  PdfSearchInputFieldStub,
  PdfFindPreviousStub,
  PdfFindNextStub,
];

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
 * Search was unreachable: the library's find bar was bound to nothing and no
 * other search path existed, so Ctrl+F did nothing at all in a PDF (issue #226
 * §2). These pin the shortcut that opens it, and the Escape that closes it
 * before the shell can treat it as "close a rail".
 */
describe('PdfReader search shortcut', () => {
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

    fixture = TestBed.createComponent(PdfReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.detectChanges();
  });

  const key = (init: KeyboardEventInit) =>
    new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init });

  it('opens the find bar on Ctrl+F and on Cmd+F, and prevents the browser’s own find', () => {
    const component = fixture.componentInstance;

    const ctrl = key({ key: 'f', ctrlKey: true });
    component.onShortcutKeydown(ctrl);
    expect(component.findBarVisible()).toBe(true);
    expect(ctrl.defaultPrevented).toBe(true);

    component.findBarVisible.set(false);
    const meta = key({ key: 'F', metaKey: true });
    component.onShortcutKeydown(meta);
    expect(component.findBarVisible()).toBe(true);
  });

  it('leaves other modifiers and plain keys alone', () => {
    const component = fixture.componentInstance;

    // Ctrl+Shift+F is not our shortcut, and neither is Ctrl+A.
    component.onShortcutKeydown(key({ key: 'f', ctrlKey: true, shiftKey: true }));
    expect(component.findBarVisible()).toBe(false);

    component.onShortcutKeydown(key({ key: 'a', ctrlKey: true }));
    expect(component.findBarVisible()).toBe(false);

    // A bare "f" must still reach the page (and the shell's page keys).
    const plain = key({ key: 'f' });
    component.onShortcutKeydown(plain);
    expect(component.findBarVisible()).toBe(false);
    expect(plain.defaultPrevented).toBe(false);
  });

  it('opens the find bar through openSearch(), the path the header control uses', () => {
    // The shell's search button calls this method (the interface makes it
    // optional so a format without search is never handed the control), so a
    // phone — which has no Ctrl+F — has a way in at all (issue #226 §2).
    const component = fixture.componentInstance;
    expect(component.findBarVisible()).toBe(false);

    component.openSearch();

    expect(component.findBarVisible()).toBe(true);
  });

  /**
   * A #226 follow-up. The library's find bar renders NO close control — read its own
   * template: its only buttons are prev/next — so a header control that could
   * only OPEN left Escape as the way out, and Escape is neither discoverable nor
   * available on a phone.
   */
  it('toggles the bar shut again from the header control', () => {
    const component = fixture.componentInstance;

    component.toggleSearch();
    expect(component.findBarVisible()).toBe(true);

    component.toggleSearch();
    expect(component.findBarVisible()).toBe(false);
  });

  it('carries no close control of its own, and does not re-declare the input area', () => {
    // A close control used to live inside the bar (a #226 follow-up) because the
    // header control could only OPEN. The header control became a TOGGLE, so the
    // second exit was redundant — and it could not be styled: pdf.js ships
    // `ngx-extended-pdf-viewer button:focus { outline: none; border: 1px solid
    // blue }` at specificity (0,1,1), which out-specifies global
    // `.icon-btn { all: unset }` at (0,1,0), so focusing it painted a literal
    // blue border. Dismissal is the header toggle (`aria-expanded`) and Escape,
    // both pinned by the tests above.
    //
    // The re-declared input area went with it: `customFindbarInputArea` existed
    // only to append that control, and the library's default template renders
    // `<div id="findbarInputContainer">` with exactly `pdf-search-input-field`,
    // `pdf-find-previous`, `pdf-find-next` — the same three components in the same
    // order — so keeping the shim would be surface with no behaviour.
    const html = readSource('./pdf-reader.component.html');
    // Comments stripped first: the comment above the find bar names
    // `customFindbarInputArea` and `findInputArea` on purpose, to record why the
    // shim went. A guard that cannot tell prose from markup would forbid
    // explaining the change it pins.
    const markup = html.replace(/<!--[\s\S]*?-->/g, '');

    expect(markup).not.toContain('customFindbarInputArea');
    expect(markup).not.toContain('findInputArea');
    expect(markup).not.toContain('Close search');
    expect(markup).not.toContain('(click)="closeSearch()"');
    // The id-based CSS still has a target: it is the DEFAULT template's id.
    expect(markup).toContain('[findbarVisible]="findBarVisible()"');
  });

  it('styles the find field by the id it renders, not an attribute it never has', () => {
    // The field's rule was keyed on `.toolbarField[type='text']`, and the
    // library's input template declares NO `type` attribute at all — so the
    // selector matched nothing and the field kept pdf.js's `message-box` stack,
    // #fff fill, rgba(0,0,0,.4) border and 2px corners while everything around it
    // wore the house tokens (measured live; this is the "plain HTML" report).
    // This guard is what stops the attribute creeping back.
    const css = readSource('./pdf-reader.component.css');
    // Comments stripped first: the fix's own comment names the broken selector on
    // purpose, and a guard that cannot tell prose from a rule would forbid
    // explaining the bug it pins.
    const rules = css.replace(/\/\*[\s\S]*?\*\//g, '');

    expect(rules).not.toContain("[type='text']");
    expect(rules).toContain('.findbar #findInput.toolbarField');
    expect(rules).toContain('.findbar #findInput.toolbarField::placeholder');
    // Not a hover-only affordance either: hover does not exist on touch.
    expect(rules).toContain('.findbar #findInput.toolbarField:focus');
  });

  it('lays the bar out as a card on desktop and a flush strip on a phone', () => {
    const css = readSource('./pdf-reader.component.css');

    // The library writes left/right and a scale transform as INLINE styles from
    // its own measurements, so a rule without !important loses silently.
    expect(css).toContain('width: min(24rem, calc(100vw - 1.5rem)) !important');
    expect(css).toContain('transform: none !important');
    expect(css).toContain('right: 0.75rem !important');
    // <=768px is the reader's own breakpoint (the shell uses the same one), and
    // there the same element becomes the strip under the header.
    expect(css).toContain('@media (max-width: 768px)');
    expect(css).toContain('border-bottom: 1px solid var(--border-color)');
  });

  it('Escape closes the bar without the event reaching the shell', () => {
    const component = fixture.componentInstance;
    component.findBarVisible.set(true);

    const esc = key({ key: 'Escape' });
    component.onShortcutKeydown(esc);

    expect(component.findBarVisible()).toBe(false);
    expect(esc.defaultPrevented).toBe(true);
  });

  it('Escape is left to the shell when the bar is not open', () => {
    const esc = key({ key: 'Escape' });
    fixture.componentInstance.onShortcutKeydown(esc);

    expect(esc.defaultPrevented).toBe(false);
  });

  it('drives the viewer’s find bar from that signal, with the jargon options off', () => {
    const viewer = fixture.debugElement.query(By.directive(PdfViewerStub))
      .componentInstance as PdfViewerStub;
    expect(viewer.findbarVisible()).toBe(false);

    fixture.componentInstance.findBarVisible.set(true);
    fixture.detectChanges();
    expect(viewer.findbarVisible()).toBe(true);

    // The two options a reader of a book uses stay; the three pdf.js engine
    // options do not get a row each in a reading interface.
    expect(viewer.showFindHighlightAll()).toBe(true);
    expect(viewer.showFindMatchCase()).toBe(true);
    expect(viewer.showFindMatchDiacritics()).toBe(false);
    expect(viewer.showFindEntireWord()).toBe(false);
    expect(viewer.showFindMultiple()).toBe(false);
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
