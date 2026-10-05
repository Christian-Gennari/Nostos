import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import ePub from 'epubjs';

import { NotesService } from '../../core/services/notes.service';
import { BooksService } from '../../core/services/books.service';
import { ThemeService } from '../../core/services/theme.service';
import {
  DEFAULT_TYPOGRAPHY,
  EPUB_FONT_OPTIONS,
  EpubReader,
  findTocItemForHref,
  fontFamilyFromStored,
  libronFontFaceCss,
  marginInsetPercent,
  progressLabel,
  resolveGroundedEpubResourceHref,
  spinePercentFrom,
  typographyCss,
} from './epub-reader.component';
import { EpubAnnotationManager } from './epub-annotation-manager';
import type { ReaderSourceTarget } from '../reader.interface';

vi.mock('epubjs', () => ({ default: vi.fn() }));

/**
 * Highlight-mode wiring of the EPUB reader (issue #16), "Minimal Section A
 * specs" items 14-16: manager init before display, single propagation of mode
 * changes, and manager cleanup before book/rendition destruction.
 */
describe('EpubReader highlight-mode lifecycle (issue #16)', () => {
  let fixture: ComponentFixture<EpubReader>;
  let log: string[];
  let lastRendition: any;
  let lastEmit: (type: string) => void;

  const notesService = {
    list: vi.fn(() => of([])),
    create: vi.fn(() => of({ id: 'n1' } as never)),
  };
  const booksService = {
    getLocations: vi.fn(() => of({ locations: null })),
    saveLocations: vi.fn(() => of(null)),
    updateProgress: vi.fn(() => of(null)),
    get: vi.fn(() => of({ lastLocation: null })),
  };

  const createFakeBook = () => {
    const rendition = {
      hooks: { content: { register: vi.fn(() => log.push('content-hook')) } },
      on: vi.fn(),
      off: vi.fn(),
      annotations: { highlight: vi.fn(), add: vi.fn(), remove: vi.fn() },
      getContents: vi.fn(() => []),
      views: vi.fn(() => []),
      getRange: vi.fn(),
      themes: { register: vi.fn(), select: vi.fn(), fontSize: vi.fn() },
      display: vi.fn(() => {
        log.push('display');
        return Promise.resolve();
      }),
      resize: vi.fn(),
      next: vi.fn(),
      prev: vi.fn(),
      currentLocation: vi.fn(() => ({ start: { cfi: 'epubcfi(/6)' } })),
    };
    const listeners: Record<string, Array<(...args: unknown[]) => void>> = {};
    const book = {
      renderTo: vi.fn(() => rendition),
      ready: Promise.resolve({ navigation: { toc: [] } }),
      on: vi.fn((type: string, cb: (...args: unknown[]) => void) => {
        if (!listeners[type]) listeners[type] = [];
        listeners[type].push(cb);
        return book;
      }),
      off: vi.fn(),
      emit: (type: string) => (listeners[type] ?? []).forEach((cb) => cb()),
      locations: {
        load: vi.fn(),
        generate: vi.fn(() => Promise.resolve()),
        save: vi.fn(),
        length: () => 0,
        percentageFromCfi: () => 0,
        locationFromCfi: () => 0,
      },
      navigation: { toc: [] },
      spine: {
        spineItems: [{ href: 'chapter-2.xhtml', index: 2 }],
      },
      destroy: vi.fn(() => log.push('book-destroy')),
    };
    return { book, rendition, emit: book.emit };
  };

  beforeEach(async () => {
    log = [];
    vi.mocked(ePub).mockImplementation(() => {
      const fake = createFakeBook();
      lastRendition = fake.rendition;
      lastEmit = fake.emit;
      return fake.book as never;
    });
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );

    await TestBed.configureTestingModule({
      imports: [EpubReader],
      providers: [
        { provide: NotesService, useValue: notesService },
        { provide: BooksService, useValue: booksService },
      ],
    }).compileComponents();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  async function setupComponent() {
    fixture = TestBed.createComponent(EpubReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.detectChanges();
    // Settle book.ready + rendition.display() + service subscriptions.
    await new Promise((resolve) => setTimeout(resolve, 0));
    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  it('applies a grounded CFI received before the opening display settles', async () => {
    fixture = TestBed.createComponent(EpubReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.detectChanges();

    await fixture.componentInstance.goToSource({
      type: 'epub',
      epubCfi: 'epubcfi(/6/8!/4/2:0)',
      epubResourceHref: 'chapter-2.xhtml',
      epubSpineIndex: 2,
      epubTextOffset: 120,
      excerpt: 'Grounded passage',
    });

    await new Promise((resolve) => setTimeout(resolve, 0));
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(lastRendition.display).toHaveBeenCalledWith('epubcfi(/6/8!/4/2:0)');
  });


  it('routes a legacy archive-root locator through the resolved epub.js spine href', async () => {
    await setupComponent();
    lastRendition.display.mockImplementation((target?: string) => {
      log.push('display');
      return target === 'OEBPS/chapter-2.xhtml'
        ? Promise.reject(new Error('No Section Found'))
        : Promise.resolve();
    });

    await fixture.componentInstance.goToSource({
      type: 'epub',
      epubResourceHref: 'OEBPS/chapter-2.xhtml',
      epubSpineIndex: 2,
      epubTextOffset: null,
      excerpt: 'Legacy grounded passage',
    });
    fixture.detectChanges();

    expect(lastRendition.display).toHaveBeenCalledWith('OEBPS/chapter-2.xhtml');
    expect(lastRendition.display).toHaveBeenCalledWith('chapter-2.xhtml');
    expect(fixture.componentInstance.sourceNavigationMessage()).toBeNull();
  });

  it('shows a calm visible state when a grounded EPUB resource cannot be resolved', async () => {
    await setupComponent();
    lastRendition.display.mockImplementation((target?: string) => {
      log.push('display');
      return target === 'missing.xhtml'
        ? Promise.reject(new Error('No Section Found'))
        : Promise.resolve();
    });

    await fixture.componentInstance.goToSource({
      type: 'epub',
      epubResourceHref: 'missing.xhtml',
      epubSpineIndex: 7,
      epubTextOffset: 12,
      excerpt: 'Missing grounded passage',
    });
    fixture.detectChanges();

    const status = fixture.nativeElement.querySelector(
      '.source-navigation-status',
    ) as HTMLElement | null;
    expect(status).not.toBeNull();
    expect(status!.textContent).toContain("Couldn't locate this passage in the EPUB.");
    expect(fixture.componentInstance.errorMessage()).toBeNull();
  });

  it('initializes the annotation manager before rendition.display()', async () => {
    const initSpy = vi
      .spyOn(EpubAnnotationManager.prototype, 'init')
      .mockImplementation(() => {
        log.push('manager-init');
      });

    await setupComponent();

    expect(initSpy).toHaveBeenCalledTimes(1);
    expect(log.indexOf('content-hook')).toBeGreaterThanOrEqual(0);
    expect(log.indexOf('manager-init')).toBeGreaterThanOrEqual(0);
    expect(log.indexOf('manager-init')).toBeLessThan(log.indexOf('display'));
    // The content hook is registered before display so the opening section is wired.
    expect(log.indexOf('content-hook')).toBeLessThan(log.indexOf('display'));
  });

  it('propagates highlight mode input changes exactly once', async () => {
    const modeSpy = vi.spyOn(EpubAnnotationManager.prototype, 'setHighlightMode');
    await setupComponent();

    modeSpy.mockClear();
    fixture.componentRef.setInput('highlightMode', true);
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(modeSpy).toHaveBeenCalledTimes(1);
    expect(modeSpy).toHaveBeenCalledWith(true);
  });

  it('destroys the annotation manager before the book/rendition', async () => {
    const destroySpy = vi
      .spyOn(EpubAnnotationManager.prototype, 'destroy')
      .mockImplementation(() => {
        log.push('manager-destroy');
      });

    await setupComponent();
    fixture.destroy();

    expect(destroySpy).toHaveBeenCalledTimes(1);
    expect(log.indexOf('manager-destroy')).toBeGreaterThanOrEqual(0);
    expect(log.indexOf('manager-destroy')).toBeLessThan(log.indexOf('book-destroy'));
  });

  it('surfaces the EPUB failure state when the book reports a failed open', async () => {
    await setupComponent();
    const updatesBefore = booksService.updateProgress.mock.calls.length;

    // epub.js announces a failed open ONLY through this event: `book.ready` and
    // `opened` never settle and `rendition.display()` stays pending.
    lastEmit('openFailed');
    fixture.detectChanges();

    const overlay = fixture.nativeElement.querySelector('.error-overlay') as HTMLElement | null;
    expect(overlay).not.toBeNull();
    expect(overlay!.textContent).toContain('Could not open this EPUB');
    expect([...overlay!.querySelectorAll('button')].map((b) => b.textContent?.trim())).toEqual([
      'Back',
      'Retry',
    ]);
    // A failed open must not report progress: the write barrier stays closed.
    expect(booksService.updateProgress.mock.calls.length).toBe(updatesBefore);

    // Retry attempts the load again (re-locking progress writes first).
    const ePubCallsBefore = vi.mocked(ePub).mock.calls.length;
    (overlay!.querySelectorAll('button')[1] as HTMLButtonElement).click();
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(vi.mocked(ePub).mock.calls.length).toBe(ePubCallsBefore + 1);
  });
});

/**
 * Theme-following normalization: both Nostos themes are registered once per
 * rendition via `rendition.themes` and the one matching the app theme is
 * selected at rendition creation (before first display), and every newly
 * rendered chapter inherits it. The fake rendition's `themes` object mimics
 * the verified epub.js 0.3.93 Themes behavior: an inject hook registered on
 * `hooks.content` injects the CURRENT theme's rules and body class into
 * every new contents.
 */
describe('EpubReader theme-following normalization', () => {
  let fixture: ComponentFixture<EpubReader>;
  let log: string[];
  let contentHooks: ((contents: any) => void)[];
  let hookRegistrations: number;
  let renditions: any[];
  let books: any[];

  const notesService = {
    list: vi.fn(() => of([])),
    create: vi.fn(() => of({ id: 'n1' } as never)),
  };
  const booksService = {
    getLocations: vi.fn(() => of({ locations: null })),
    saveLocations: vi.fn(() => of(null)),
    updateProgress: vi.fn(() => of(null)),
    get: vi.fn(() => of({ lastLocation: null })),
  };

  const makeContents = () => {
    const doc = new DOMParser().parseFromString(
      '<html><head></head><body></body></html>',
      'text/html',
    );
    return { document: doc, window, content: doc.body };
  };

  const createFakeBook = () => {
    contentHooks = [];

    const themes = {
      rules: {} as Record<string, Record<string, Record<string, string>>>,
      registered: [] as string[],
      selected: [] as string[],
      current: 'default',
      fontSize: vi.fn(),
      register(name: string, rules: any) {
        themes.rules[name] = rules;
        themes.registered.push(name);
        log.push(`register:${name}`);
      },
      select(name: string) {
        themes.selected.push(name);
        themes.current = name;
        log.push(`select:${name}`);
      },
    };

    const rendition = {
      hooks: {
        content: {
          register: vi.fn((cb: (contents: any) => void) => {
            contentHooks.push(cb);
            hookRegistrations++;
          }),
        },
      },
      themes,
      on: vi.fn(),
      off: vi.fn(),
      annotations: { highlight: vi.fn(), add: vi.fn(), remove: vi.fn() },
      getContents: vi.fn(() => []),
      views: vi.fn(() => []),
      getRange: vi.fn(),
      display: vi.fn(() => {
        log.push('display');
        return Promise.resolve();
      }),
      resize: vi.fn(),
      next: vi.fn(),
      prev: vi.fn(),
      currentLocation: vi.fn(() => ({ start: { cfi: 'epubcfi(/6/4)' } })),
    };

    // Mimic epub.js Themes' constructor hook: every new contents document
    // receives the CURRENT theme's rules (injected stylesheet) and the
    // theme class on the body.
    rendition.hooks.content.register((contents: any) => {
      const active = themes.current;
      if (themes.rules[active]) {
        const style = contents.document.createElement('style');
        style.id = `epubjs-inserted-css-${active}`;
        style.textContent = `${active} ${JSON.stringify(themes.rules[active])}`;
        contents.document.head.appendChild(style);
        contents.document.body.classList.add(active);
      }
    });

    const book = {
      renderTo: vi.fn(() => {
        renditions.push(rendition);
        return rendition;
      }),
      ready: Promise.resolve({ navigation: { toc: [] } }),
      on: vi.fn(),
      off: vi.fn(),
      locations: {
        load: vi.fn(),
        generate: vi.fn(() => Promise.resolve()),
        save: vi.fn(),
        length: () => 0,
        percentageFromCfi: () => 0,
        locationFromCfi: () => 0,
      },
      navigation: { toc: [] },
      destroy: vi.fn(() => log.push('book-destroy')),
    };
    books.push(book);
    return { book, rendition, themes };
  };

  beforeEach(async () => {
    log = [];
    renditions = [];
    books = [];
    hookRegistrations = 0;
    localStorage.clear();
    vi.mocked(ePub).mockImplementation(() => createFakeBook().book as never);
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );

    await TestBed.configureTestingModule({
      imports: [EpubReader],
      providers: [
        { provide: NotesService, useValue: notesService },
        { provide: BooksService, useValue: booksService },
      ],
    }).compileComponents();
  });

  afterEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  async function setupComponent() {
    fixture = TestBed.createComponent(EpubReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.detectChanges();
    // Settle book.ready + rendition.display() + service subscriptions + effects.
    await new Promise((resolve) => setTimeout(resolve, 0));
    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  it('registers both normalizations exactly once and selects the app theme before first display', async () => {
    await setupComponent();

    const themes = renditions[0].themes;
    expect(themes.registered).toEqual(['nostos-light', 'nostos-dark']);
    expect(log.filter((l) => l.startsWith('register:')).length).toBe(2);
    // The eager selection at rendition creation happens BEFORE display().
    // The test env has no stored choice and no dark OS preference: light.
    expect(log.indexOf('select:nostos-light')).toBeGreaterThanOrEqual(0);
    expect(log.indexOf('select:nostos-light')).toBeLessThan(log.indexOf('display'));
    expect(themes.current).toBe('nostos-light');
    // The registered rules are the light tokens (white surface, dark ink,
    // publisher-color normalization, link + selection colors).
    const rules = themes.rules['nostos-light'];
    expect(rules.body.background).toBe('#ffffff !important');
    expect(rules.body.color).toBe('#1a1a1a !important');
    expect(rules['body *'].color).toBe('inherit !important');
    expect(rules.a.color).toBe('#60a5fa !important');
    // The dark rules mirror the dark tokens (slate ground, silver ink).
    const dark = themes.rules['nostos-dark'];
    expect(dark.body.background).toBe('#121318 !important');
    expect(dark.body.color).toBe('#f0f1f4 !important');
  });

  it('a chapter rendered after the eager selection inherits the app-theme rules', async () => {
    await setupComponent();

    // Simulate a new section: epub.js fires every registered content hook
    // with the new contents document.
    const contents = makeContents();
    contentHooks.forEach((hook) => hook(contents));

    expect(contents.document.body.classList.contains('nostos-light')).toBe(true);
    const themeStyle = contents.document.getElementById('epubjs-inserted-css-nostos-light');
    expect(themeStyle).not.toBeNull();
    expect(themeStyle!.textContent).toContain('#ffffff');
  });

  it('a stored dark theme selects the dark normalization before first display', async () => {
    localStorage.setItem('nostos.theme', 'dark');
    await setupComponent();

    const themes = renditions[0].themes;
    expect(themes.current).toBe('nostos-dark');
    expect(log.indexOf('select:nostos-dark')).toBeGreaterThanOrEqual(0);
    expect(log.indexOf('select:nostos-dark')).toBeLessThan(log.indexOf('display'));

    const contents = makeContents();
    contentHooks.forEach((hook) => hook(contents));
    expect(contents.document.body.classList.contains('nostos-dark')).toBe(true);
  });

  it('an in-book theme switch is colour-only: same rendition, no re-display, no reflow (#651)', async () => {
    await setupComponent();
    const rendition = renditions[0];
    const themes = rendition.themes;
    const displays = log.filter((entry) => entry === 'display').length;
    themes.fontSize.mockClear();
    rendition.resize.mockClear();

    TestBed.inject(ThemeService).setTheme('dark');
    fixture.detectChanges();

    expect(themes.current).toBe('nostos-dark');
    expect(renditions).toHaveLength(1);
    expect(log.filter((entry) => entry === 'display').length).toBe(displays);
    expect(themes.fontSize).not.toHaveBeenCalled();
    expect(rendition.resize).not.toHaveBeenCalled();

    // A section loaded after the switch arrives in the new theme, with no
    // old-theme frame.
    const contents = makeContents();
    contentHooks.forEach((hook) => hook(contents));
    expect(contents.document.body.classList.contains('nostos-dark')).toBe(true);
    expect(contents.document.body.classList.contains('nostos-light')).toBe(false);

    TestBed.inject(ThemeService).setTheme('light');
    fixture.detectChanges();
    expect(themes.current).toBe('nostos-light');
    expect(renditions).toHaveLength(1);
  });

  it('font size persists reader-wide and adopts the old per-book value', async () => {
    await setupComponent();

    const component = fixture.componentInstance;
    component.zoomIn();
    component.zoomIn();
    expect(localStorage.getItem('nostos.epub-font-size')).toBe('120');
    expect(localStorage.getItem('nostos.epub-font-size.book-1')).toBeNull();

    // A legacy per-book value is adopted when no reader-wide size exists.
    localStorage.removeItem('nostos.epub-font-size');
    localStorage.setItem('nostos.epub-font-size.book-2', '130');
    fixture.destroy();
    fixture = TestBed.createComponent(EpubReader);
    fixture.componentRef.setInput('bookId', 'book-2');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(localStorage.getItem('nostos.epub-font-size')).toBe('130');
    expect(renditions[1].themes.fontSize).toHaveBeenCalledWith('130%');
  });

  it('coalesces a burst of text-size steps into two re-paginations', async () => {
    await setupComponent();
    vi.useFakeTimers();
    try {
      const component = fixture.componentInstance;
      const themes = renditions[0].themes;
      themes.fontSize.mockClear();

      // Five steps, the way a reader hunts for a comfortable size. Each apply
      // re-paginates the whole section and widens its strip, so applying once
      // per click is what makes this feel slow — measured on a real chapter:
      // 1.2s still re-laying-out after the last of five clicks, with the strip
      // growing 11.9k -> 29.5k px.
      for (let i = 0; i < 5; i++) component.zoomIn();

      // Leading edge: the first step lands immediately (a lone step stays 32ms).
      expect(themes.fontSize).toHaveBeenCalledTimes(1);
      expect(themes.fontSize).toHaveBeenLastCalledWith('110%');

      // …and the rest collapse into ONE trailing apply at the final size.
      vi.advanceTimersByTime(300);
      expect(themes.fontSize).toHaveBeenCalledTimes(2);
      expect(themes.fontSize).toHaveBeenLastCalledWith('150%');
    } finally {
      vi.useRealTimers();
    }
  });

  it('skips the trailing apply when it would repaint the same size', async () => {
    await setupComponent();
    vi.useFakeTimers();
    try {
      const component = fixture.componentInstance;
      const themes = renditions[0].themes;
      themes.fontSize.mockClear();

      component.zoomIn();
      expect(themes.fontSize).toHaveBeenCalledTimes(1);

      // The quiet-window apply fires with the value it already applied — a
      // second full re-pagination that would buy nothing.
      vi.advanceTimersByTime(300);
      expect(themes.fontSize).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('coalesces rapid typography changes the same way', async () => {
    await setupComponent();
    vi.useFakeTimers();
    try {
      const component = fixture.componentInstance;
      const applied = vi.spyOn(component as never, 'applyTypographyToOpenContents' as never);

      component.setTypography({ fontFamily: 'sans' });
      expect(applied).toHaveBeenCalledTimes(1); // leading edge
      component.setTypography({ fontFamily: 'mono' });
      component.setTypography({ fontFamily: 'publisher' });
      expect(applied).toHaveBeenCalledTimes(1); // deferred, not once per click

      vi.advanceTimersByTime(300);
      expect(applied).toHaveBeenCalledTimes(2); // one apply, at the end
      expect(component.typography().fontFamily).toBe('publisher');
    } finally {
      vi.useRealTimers();
    }
  });

  it('annotation styles stay visible alongside the eager theme normalization', async () => {
    await setupComponent();

    const contents = makeContents();
    contentHooks.forEach((hook) => hook(contents));

    // Light normalization styles AND annotation styles coexist in the same
    // contents head.
    expect(contents.document.getElementById('epubjs-inserted-css-nostos-light')).not.toBeNull();
    const annotationStyle = Array.from(contents.document.head.querySelectorAll('style')).find(
      (s) => s.textContent?.includes('.nostos-highlight-mode'),
    );
    expect(annotationStyle).toBeDefined();
    expect(annotationStyle!.textContent).toContain('user-select: text');

    // The `.epubjs-hl*` fill rules that used to live here were DEAD CODE: the
    // highlight marks are built in a pane SVG in the PARENT document, so a rule
    // inside the contents document cannot reach them. The colour is passed to
    // epub.js as an explicit fill instead (issue #225 §1.6).
    expect(contents.document.head.textContent).not.toContain('.epubjs-hl');
  });

  it('reopening the reader keeps no stale rendition or duplicate effects', async () => {
    await setupComponent();
    const firstRendition = renditions[0];

    fixture.destroy();

    // Reopen: a fresh component instance builds a fresh rendition.
    fixture = TestBed.createComponent(EpubReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(renditions.length).toBe(2);
    const secondRendition = renditions[1];

    // Exactly two content hooks per rendition (themes inject + component).
    expect(hookRegistrations).toBe(4);

    // Each rendition registered both Nostos themes exactly once.
    expect(firstRendition.themes.registered).toEqual(['nostos-light', 'nostos-dark']);
    expect(secondRendition.themes.registered).toEqual(['nostos-light', 'nostos-dark']);
  });

  /**
   * Issue #225 §1.2. The rendition reports its opening section as soon as it
   * lays out; the saved position arrives from the Book the shell passed down.
   * Persisting the opening section is what silently reset a reader's position.
   */
  it('never persists the opening section — the write follows the restore', async () => {
    const OPENING_CFI = 'epubcfi(/6/2!/4/1:0)';
    const LIVE_CFI = 'epubcfi(/6/4)'; // what the fake rendition reports live

    fixture = TestBed.createComponent(EpubReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.componentRef.setInput('book', { id: 'book-1', lastLocation: OPENING_CFI } as never);
    fixture.detectChanges();

    const writesBefore = booksService.updateProgress.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, 1100));

    // The restore was applied (from the input, with no second GET) …
    expect(renditions[0].display).toHaveBeenCalledWith(OPENING_CFI);
    expect(booksService.get).not.toHaveBeenCalled();

    // … and the only position written is where the reader actually sits.
    const writes = booksService.updateProgress.mock.calls.slice(writesBefore);
    expect(writes).toHaveLength(1);
    expect(writes[0]).toEqual(['book-1', LIVE_CFI, expect.any(Number)]);
  });

  /**
   * Issue #225 §1.5. The contents document is an iframe: a key pressed while
   * reading never reaches the shell's document listener.
   */
  it('turns pages from keys pressed inside the contents document', async () => {
    await setupComponent();

    const contents = makeContents();
    contentHooks.forEach((hook) => hook(contents));

    const rendition = renditions[0];
    const press = (key: string, init: KeyboardEventInit = {}) =>
      contents.document.dispatchEvent(
        new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }),
      );

    press('ArrowRight');
    press('PageDown');
    press(' ');
    expect(rendition.next).toHaveBeenCalledTimes(3);

    press('ArrowLeft');
    press('PageUp');
    press(' ', { shiftKey: true });
    expect(rendition.prev).toHaveBeenCalledTimes(3);

    // A text field inside the book keeps its own key semantics.
    const input = contents.document.createElement('input');
    contents.document.body.appendChild(input);
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    // An unhandled key changes nothing either.
    press('a');

    expect(rendition.next).toHaveBeenCalledTimes(3);
  });
});

describe('typographyCss', () => {
  it('keeps the publisher typeface on Publisher but applies spacing', () => {
    const css = typographyCss({ fontFamily: 'publisher', lineHeight: 1.6, margin: 'normal' });
    expect(css).not.toContain('font-family');
    expect(css).toContain(
      'body,body p,body blockquote,body li,body dt,body dd,body figcaption,body caption,body td,body th{line-height:1.6 !important;}',
    );
    // Text size stays owned by epub.js, and margins stay on our own viewer
    // (see marginInsetPercent). typographyCss must not fight either control.
    expect(css).not.toContain('font-size');
    expect(css).not.toContain('padding');
  });

  it('overrides explicit publisher fonts on normal reading text and headings', () => {
    const css = typographyCss({ fontFamily: 'sans', lineHeight: 2.0, margin: 'wide' });
    expect(css).toContain(
      'body,body p,body blockquote,body li,body dt,body dd,body figcaption,body caption,body td,body th,body h1,body h2,body h3,body h4,body h5,body h6{font-family:system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif !important;}',
    );
    expect(css).toContain('line-height:2 !important');
    expect(css).not.toContain('font-size');
    expect(css).not.toContain('padding');
    expect(typographyCss({ fontFamily: 'mono', lineHeight: 1.6, margin: 'normal' })).toContain(
      'font-family:ui-monospace, SFMono-Regular, monospace !important',
    );
  });

  it('does not flatten directly styled code, preformatted text or icon glyph fonts', () => {
    const publisher = document.createElement('style');
    publisher.textContent =
      'p,h2{font-family:"PublisherSerif";line-height:1.2}' +
      'pre,code{font-family:"PublisherCode"}' +
      '.book-icon{font-family:"PublisherIcons"}';

    const nostos = document.createElement('style');
    nostos.textContent = typographyCss({ fontFamily: 'sans', lineHeight: 1.8, margin: 'normal' });

    const host = document.createElement('div');
    host.innerHTML =
      '<p data-testid="prose">Text <code data-testid="code">x</code>' +
      '<span class="book-icon" data-testid="icon">\ue000</span></p>' +
      '<h2 data-testid="heading">Heading</h2>' +
      '<pre data-testid="pre">const x = 1;</pre>';

    document.head.append(publisher, nostos);
    document.body.appendChild(host);

    try {
      const prose = host.querySelector<HTMLElement>('[data-testid="prose"]')!;
      const heading = host.querySelector<HTMLElement>('[data-testid="heading"]')!;
      const code = host.querySelector<HTMLElement>('[data-testid="code"]')!;
      const pre = host.querySelector<HTMLElement>('[data-testid="pre"]')!;
      const icon = host.querySelector<HTMLElement>('[data-testid="icon"]')!;

      expect(getComputedStyle(prose).fontFamily).toContain('system-ui');
      expect(getComputedStyle(heading).fontFamily).toContain('system-ui');
      expect(getComputedStyle(prose).lineHeight).toBe('1.8');
      expect(getComputedStyle(code).fontFamily).toContain('PublisherCode');
      expect(getComputedStyle(pre).fontFamily).toContain('PublisherCode');
      expect(getComputedStyle(icon).fontFamily).toContain('PublisherIcons');
    } finally {
      host.remove();
      nostos.remove();
      publisher.remove();
    }
  });

  it('sets Libron with a conventional serif fallback', () => {
    const css = typographyCss({ fontFamily: 'libron', lineHeight: 1.6, margin: 'normal' });
    expect(css).toContain(
      'font-family:"Libron", Georgia, "Times New Roman", Times, serif !important',
    );
  });
});

describe('EPUB typeface model (issue #670)', () => {
  it('defaults to Libron and keeps the other defaults', () => {
    expect(DEFAULT_TYPOGRAPHY).toEqual({ fontFamily: 'libron', lineHeight: 1.6, margin: 'normal' });
  });

  it('offers exactly Libron, Publisher, Sans, Mono, in that order', () => {
    expect(EPUB_FONT_OPTIONS).toEqual([
      { value: 'libron', label: 'Libron' },
      { value: 'publisher', label: 'Publisher' },
      { value: 'sans', label: 'Sans' },
      { value: 'mono', label: 'Mono' },
    ]);
  });

  it('migrates stored values and falls back to Libron for anything unknown', () => {
    expect(fontFamilyFromStored('default')).toBe('publisher');
    expect(fontFamilyFromStored('serif')).toBe('libron');
    expect(fontFamilyFromStored('sans')).toBe('sans');
    expect(fontFamilyFromStored('mono')).toBe('mono');
    expect(fontFamilyFromStored('libron')).toBe('libron');
    expect(fontFamilyFromStored('publisher')).toBe('publisher');
    for (const bad of ['comic-sans', '', 'toString', 'constructor', undefined, null, 3, {}]) {
      expect(fontFamilyFromStored(bad)).toBe('libron');
    }
  });

  it('declares the four bundled Libron faces against app-absolute URLs', () => {
    const css = libronFontFaceCss('https://nostos.example/app/');
    const faces = css.split('@font-face').filter(Boolean);
    expect(faces).toHaveLength(4);
    const expected = [
      ['normal', 400, 'Libron-Regular.woff2'],
      ['italic', 400, 'Libron-Italic.woff2'],
      ['normal', 700, 'Libron-Bold.woff2'],
      ['italic', 700, 'Libron-BoldItalic.woff2'],
    ] as const;
    expected.forEach(([style, weight, file], i) => {
      expect(faces[i]).toContain('font-family:"Libron"');
      expect(faces[i]).toContain(`font-style:${style};`);
      expect(faces[i]).toContain(`font-weight:${weight};`);
      expect(faces[i]).toContain(
        `src:url("https://nostos.example/app/fonts/libron/${file}") format("woff2")`,
      );
    });
    // Bundled with the app: no third-party font host.
    expect(css).not.toMatch(/googleapis|gstatic|jsdelivr|unpkg/);
  });
});

describe('marginInsetPercent', () => {
  it('maps the presets to outer margins, narrow being the book as published', () => {
    expect(marginInsetPercent('narrow')).toBe(0);
    expect(marginInsetPercent('normal')).toBe(4);
    expect(marginInsetPercent('wide')).toBe(8);
  });
});

describe('EpubReader typography persistence', () => {
  let fixture: ComponentFixture<EpubReader>;

  const notesService = {
    list: vi.fn(() => of([])),
    create: vi.fn(() => of({ id: 'n1' } as never)),
  };
  const booksService = {
    getLocations: vi.fn(() => of({ locations: null })),
    saveLocations: vi.fn(() => of(null)),
    updateProgress: vi.fn(() => of(null)),
    get: vi.fn(() => of({ lastLocation: null })),
  };

  function makeDocument() {
    const doc = new DOMParser().parseFromString(
      '<html><head></head><body></body></html>',
      'text/html',
    );
    return doc;
  }

  beforeEach(async () => {
    localStorage.clear();
    vi.mocked(ePub).mockImplementation(
      () =>
        ({
          renderTo: () => ({
            hooks: { content: { register: vi.fn() } },
            themes: { register: vi.fn(), select: vi.fn(), fontSize: vi.fn() },
            on: vi.fn(),
            off: vi.fn(),
            getContents: () => [],
            display: vi.fn(() => Promise.resolve()),
            resize: vi.fn(),
          }),
          ready: Promise.resolve({ navigation: { toc: [] } }),
          on: vi.fn(),
          off: vi.fn(),
          locations: {
            load: vi.fn(),
            generate: vi.fn(() => Promise.resolve()),
            save: vi.fn(),
            length: () => 0,
            percentageFromCfi: () => 0,
            locationFromCfi: () => 0,
          },
          navigation: { toc: [] },
          destroy: vi.fn(),
        }) as never,
    );
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );

    await TestBed.configureTestingModule({
      imports: [EpubReader],
      providers: [
        { provide: NotesService, useValue: notesService },
        { provide: BooksService, useValue: booksService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(EpubReader);
    fixture.componentRef.setInput('bookId', 'book-9');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));
    await new Promise((resolve) => setTimeout(resolve, 0));
  });

  afterEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('persists one reader-wide preference and restores it on open', () => {
    const component = fixture.componentInstance;
    component.setTypography({ fontFamily: 'sans', lineHeight: 1.8 });
    // One key for the whole reader, not per book.
    expect(JSON.parse(localStorage.getItem('nostos.epub-typography')!)).toEqual({
      fontFamily: 'sans',
      lineHeight: 1.8,
      margin: 'normal',
    });
    expect(localStorage.getItem('nostos.epub-typography.book-9')).toBeNull();

    // Reopen: the remembered typography is restored, not the defaults.
    component.loadBook('book-9');
    expect(component.typography()).toEqual({
      fontFamily: 'sans',
      lineHeight: 1.8,
      margin: 'normal',
    });
  });

  it('adopts a per-book value written by an earlier version, once', () => {
    const component = fixture.componentInstance;
    localStorage.clear();
    localStorage.setItem(
      'nostos.epub-typography.book-9',
      JSON.stringify({ fontFamily: 'mono', lineHeight: 2, margin: 'wide' }),
    );

    component.loadBook('book-9');

    // The book's own choice is honoured…
    expect(component.typography()).toEqual({ fontFamily: 'mono', lineHeight: 2, margin: 'wide' });
    // …and becomes the reader-wide preference for every other book.
    expect(JSON.parse(localStorage.getItem('nostos.epub-typography')!)).toEqual({
      fontFamily: 'mono',
      lineHeight: 2,
      margin: 'wide',
    });
  });

  it('opens in Libron when no preference has been saved', () => {
    expect(localStorage.getItem('nostos.epub-typography')).toBeNull();
    expect(fixture.componentInstance.typography()).toEqual({
      fontFamily: 'libron',
      lineHeight: 1.6,
      margin: 'normal',
    });
  });

  it.each([
    ['default', 'publisher'],
    ['serif', 'libron'],
    ['sans', 'sans'],
    ['mono', 'mono'],
  ])('reads a saved "%s" typeface as %s and keeps the rest of the preference', (saved, now) => {
    const component = fixture.componentInstance;
    localStorage.setItem(
      'nostos.epub-typography',
      JSON.stringify({ fontFamily: saved, lineHeight: 1.8, margin: 'wide' }),
    );

    component.loadBook('book-9');

    expect(component.typography()).toEqual({ fontFamily: now, lineHeight: 1.8, margin: 'wide' });
  });

  it('migrates a per-book value written under the old names too', () => {
    const component = fixture.componentInstance;
    localStorage.setItem(
      'nostos.epub-typography.book-9',
      JSON.stringify({ fontFamily: 'default', lineHeight: 2, margin: 'wide' }),
    );

    component.loadBook('book-9');

    expect(component.typography().fontFamily).toBe('publisher');
    expect(JSON.parse(localStorage.getItem('nostos.epub-typography')!).fontFamily).toBe(
      'publisher',
    );
  });

  it('ignores an invalid stored preference rather than trusting it', () => {
    const component = fixture.componentInstance;
    localStorage.clear();
    localStorage.setItem(
      'nostos.epub-typography',
      JSON.stringify({ fontFamily: 'comic-sans', lineHeight: 7, margin: 'enormous' }),
    );

    component.loadBook('book-9');

    expect(component.typography()).toEqual({
      fontFamily: 'libron',
      lineHeight: DEFAULT_TYPOGRAPHY.lineHeight,
      margin: 'normal',
    });
  });

  it('reset restores the Nostos defaults, Libron included', () => {
    const component = fixture.componentInstance;
    component.setTypography({ fontFamily: 'mono', margin: 'wide' });
    component.zoomIn();
    component.resetTypography();
    expect(component.typography()).toEqual({
      fontFamily: 'libron',
      lineHeight: 1.6,
      margin: 'normal',
    });
    expect(JSON.parse(localStorage.getItem('nostos.epub-typography')!).fontFamily).toBe('libron');
    expect(component.fontSizePercent()).toBe(100);
    expect(localStorage.getItem('nostos.epub-font-size')).toBe('100');
  });

  it('writes the rules into newly rendered sections', () => {
    const component = fixture.componentInstance;
    component.setTypography({ fontFamily: 'libron', lineHeight: 2.0, margin: 'narrow' });

    const doc = makeDocument();
    (component as unknown as { upsertTypographyStyle: (d: Document) => void }).upsertTypographyStyle(doc);
    const style = doc.getElementById('nostos-typography');
    expect(style?.textContent).toContain(
      'font-family:"Libron", Georgia, "Times New Roman", Times, serif !important',
    );
    expect(style?.textContent).not.toContain('padding');
  });

  it('gives every section the Libron faces, once, ahead of the typography rules', () => {
    const component = fixture.componentInstance;
    const upsert = (d: Document) =>
      (component as unknown as { upsertTypographyStyle: (d: Document) => void }).upsertTypographyStyle(d);

    // Two sections, as epub.js renders them: each is its own document.
    const [first, second] = [makeDocument(), makeDocument()];
    for (const doc of [first, second]) {
      upsert(doc);
      const fonts = doc.getElementById('nostos-reader-fonts');
      expect(fonts?.textContent).toBe(libronFontFaceCss(document.baseURI));
      expect(fonts?.textContent?.match(/@font-face/g)).toHaveLength(4);
      // The faces are declared before the rule that asks for them.
      expect(fonts?.nextElementSibling?.id).toBe('nostos-typography');
    }

    // The faces stay available whatever is selected, and a repaint of an open
    // section does not stack a second copy.
    component.setTypography({ fontFamily: 'publisher' });
    upsert(first);
    expect(first.querySelectorAll('#nostos-reader-fonts')).toHaveLength(1);
    expect(first.getElementById('nostos-typography')?.textContent).not.toContain('font-family');
  });

  it('repaints open sections in place on a typeface change, without re-displaying', () => {
    const component = fixture.componentInstance;
    const rendition = (component as unknown as {
      rendition: { display: ReturnType<typeof vi.fn>; getContents: () => unknown };
    }).rendition;
    const doc = makeDocument();
    rendition.getContents = () => [{ document: doc }];
    const displaysBefore = rendition.display.mock.calls.length;
    const progressBefore = booksService.updateProgress.mock.calls.length;

    vi.useFakeTimers();
    try {
      for (const fontFamily of ['publisher', 'sans', 'mono', 'libron'] as const) {
        component.setTypography({ fontFamily });
        vi.runAllTimers();
        expect(doc.getElementById('nostos-typography')?.textContent).toBe(
          typographyCss({ ...DEFAULT_TYPOGRAPHY, fontFamily }),
        );
      }
    } finally {
      vi.useRealTimers();
    }

    // The reading position is epub.js's to keep across the re-layout: the
    // reader neither navigates nor writes a new position for a typeface change.
    expect(rendition.display.mock.calls.length).toBe(displaysBefore);
    expect(booksService.updateProgress.mock.calls.length).toBe(progressBefore);
  });

  it('resizes the rendition into the padded page box when the margin changes', () => {
    const component = fixture.componentInstance;
    const rendition = (component as unknown as {
      rendition: { resize: ReturnType<typeof vi.fn> } | null;
    }).rendition;
    expect(rendition).toBeTruthy();

    // jsdom reports 0 for layout boxes, so give the page box a size.
    const page = fixture.nativeElement.querySelector('#epub-page') as HTMLElement;
    expect(page).toBeTruthy();
    Object.defineProperty(page, 'clientWidth', { value: 800, configurable: true });
    Object.defineProperty(page, 'clientHeight', { value: 600, configurable: true });

    vi.useFakeTimers();
    try {
      component.setTypography({ margin: 'wide' });
      vi.runAllTimers();
    } finally {
      vi.useRealTimers();
    }

    // The margin is padding on our viewer, so epub.js has to be told the page
    // got smaller — nothing is written into the book.
    expect(rendition!.resize).toHaveBeenCalledWith(800, 600);
  });

});

/**
 * Issue #225 §1.3 and §1.4. The progress pill used to read "30% • 3h 43m left"
 * on the strength of one location being treated as one minute, and a book whose
 * locations were still generating sat on "Calculating…" for ever.
 */
describe('reader progress reporting (issue #225 §1.3, §1.4)', () => {
  it('reports the percentage and the chapter, never a time estimate', () => {
    expect(progressLabel(30, 'Chapter 3')).toBe('30% • Chapter 3');
    expect(progressLabel(0, null)).toBe('0%');
    expect(progressLabel(100, '')).toBe('100%');
  });

  it('falls back to the spine position as a floor, never a guess', () => {
    expect(spinePercentFrom(0, 10)).toBe(0);
    expect(spinePercentFrom(5, 10)).toBe(50);
    expect(spinePercentFrom(9, 10)).toBe(90);
    // Without a spine index or a spine length there is nothing honest to show.
    expect(spinePercentFrom(null, 10)).toBe(0);
    expect(spinePercentFrom(3, null)).toBe(0);
    expect(spinePercentFrom(0, 0)).toBe(0);
    expect(spinePercentFrom(undefined, undefined)).toBe(0);
  });
});

describe('findTocItemForHref', () => {
  const toc = [
    {
      label: 'Part One',
      target: 'part1.xhtml',
      children: [{ label: 'Chapter 1', target: 'ch1.xhtml#start', children: [] }],
    },
    { label: 'Chapter 2', target: 'ch2.xhtml', children: [] },
  ];

  it('matches on the section and ignores the fragment', () => {
    expect(findTocItemForHref(toc, 'ch1.xhtml#anything')?.label).toBe('Chapter 1');
    expect(findTocItemForHref(toc, 'ch2.xhtml')?.label).toBe('Chapter 2');
    expect(findTocItemForHref(toc, 'part1.xhtml')?.label).toBe('Part One');
  });

  it('returns null when the TOC cannot place the section', () => {
    expect(findTocItemForHref(toc, 'nope.xhtml')).toBeNull();
    expect(findTocItemForHref(toc, null)).toBeNull();
    expect(findTocItemForHref([], 'ch1.xhtml')).toBeNull();
  });
});


describe('resolveGroundedEpubResourceHref', () => {
  const nestedSpine = [
    { href: 'xhtml/chapter015.html', index: 14 },
    { href: 'xhtml/chapter016.html', index: 15 },
  ];

  it('keeps a canonical root-OPF manifest href exact', () => {
    expect(
      resolveGroundedEpubResourceHref('xhtml/chapter015.html', 14, nestedSpine),
    ).toBe('xhtml/chapter015.html');
  });

  it('resolves a legacy archive-root href for a nested OPF to the exact spine item', () => {
    expect(
      resolveGroundedEpubResourceHref(
        'TheIdeaofJustice/xhtml/chapter015.html',
        14,
        nestedSpine,
      ),
    ).toBe('xhtml/chapter015.html');
  });

  it('accepts the new canonical nested-OPF locator without adding the package directory', () => {
    expect(
      resolveGroundedEpubResourceHref('xhtml/chapter016.html', 15, nestedSpine),
    ).toBe('xhtml/chapter016.html');
  });

  it('fails closed when a suffix could refer to more than one spine resource', () => {
    const ambiguous = [
      { href: 'volume1/xhtml/chapter01.html', index: 1 },
      { href: 'volume2/xhtml/chapter01.html', index: 2 },
    ];

    expect(
      resolveGroundedEpubResourceHref('xhtml/chapter01.html', null, ambiguous),
    ).toBeNull();
  });

  it('fails closed when the structural path matches but the grounded spine index disagrees', () => {
    expect(
      resolveGroundedEpubResourceHref(
        'TheIdeaofJustice/xhtml/chapter015.html',
        99,
        nestedSpine,
      ),
    ).toBeNull();
  });

  it('returns null for a missing resource instead of guessing', () => {
    expect(
      resolveGroundedEpubResourceHref('unknown/missing.xhtml', null, nestedSpine),
    ).toBeNull();
  });
});

/**
 * PR #582. A resolved `rendition.display(cfi)` is not a settled display:
 * epub.js re-lays-out once the container size is known and then re-displays
 * the view's last seen location (`Rendition.onResized`). On the reported book
 * (*The Idea of Justice*, spine 22, offset 9202) the stale re-display arrived
 * 23 ms after the grounded CFI and silently won: the reader ended at the
 * section start and that position was persisted as reading progress. These
 * specs reproduce that shape with the fake rendition — display the grounded
 * target, then fire the resize/reflow that reports the stale location — and
 * assert the reader stays on (or is returned to) the target, that exactly one
 * controlled retry is used, and that progress writes are held until the target
 * is confirmed.
 */
describe('EpubReader grounded-source settle protection (PR #582)', () => {
  const TARGET_CFI = 'epubcfi(/6/46!/4/36[the0002422]/2[p326]/1:0)';
  const STALE_CFI = 'epubcfi(/6/46!/4/2[the0002366]/1:0)';
  const NEW_TARGET_CFI = 'epubcfi(/6/48!/4/2:0)';
  const USER_CFI = 'epubcfi(/6/50[user]!/4/1:0)';
  const HREF = 'xhtml/chapter015.html';
  const OPENING_CFI = 'epubcfi(/6/2[cover]!/4/1:0)';

  let fixture: ComponentFixture<EpubReader>;
  let contents: any;
  let rendition: any;
  let listeners: Record<string, Array<(...args: unknown[]) => void>>;
  let resizeObservers: Array<() => void>;
  /** What the fake rendition reports as the active location. */
  let liveCfi: string;
  /** Where the re-derived target range would sit; null = no measurable layout. */
  let rangeRect: DOMRect | null;

  const rect = (left: number, top: number, right: number, bottom: number) =>
    ({ left, top, right, bottom, width: right - left, height: bottom - top }) as DOMRect;
  const pageRect = () => rect(0, 0, 800, 600);
  /** The passage's first character inside the visible page box. */
  const onPage = () => rect(96, 48, 140, 72);
  /** The "one page short" shape measured from the defect: past the right edge. */
  const offPage = () => rect(880, 48, 940, 72);

  const notesService = {
    list: vi.fn(() => of([])),
    create: vi.fn(() => of({ id: 'n1' } as never)),
  };
  const booksService = {
    getLocations: vi.fn(() => of({ locations: null })),
    saveLocations: vi.fn(() => of(null)),
    updateProgress: vi.fn((_bookId: string, _location: string, _percentage: number) => of(null)),
    get: vi.fn(() => of({ lastLocation: null })),
  };

  const groundedTarget = (overrides: Partial<ReaderSourceTarget> = {}): ReaderSourceTarget => ({
    type: 'epub',
    epubCfi: TARGET_CFI,
    epubResourceHref: HREF,
    epubSpineIndex: 22,
    epubTextOffset: 4,
    excerpt: 'The cited passage begins here',
    ...overrides,
  });

  const makeRange = () =>
    ({
      setStart: vi.fn(),
      collapse: vi.fn(),
      // jsdom has no Range geometry: absence means "cannot measure" to the
      // component, which then falls back to the reported location.
      getBoundingClientRect: rangeRect ? () => rangeRect as DOMRect : undefined,
    }) as unknown as Range;

  const displayCallsFor = (cfi: string) =>
    rendition.display.mock.calls.filter(([target]: [string?]) => target === cfi).length;

  const emitRelocated = (cfi: string) => {
    liveCfi = cfi;
    (listeners['relocated'] ?? []).forEach((callback) =>
      callback({ start: { cfi, href: HREF, index: 22 } }),
    );
  };

  const emitResized = () => {
    (listeners['resized'] ?? []).forEach((callback) => callback());
  };

  const progressWritesWith = (cfi: string) =>
    booksService.updateProgress.mock.calls.filter(([, location]) => location === cfi);

  beforeEach(async () => {
    localStorage.clear();
    listeners = {};
    resizeObservers = [];
    liveCfi = OPENING_CFI;
    rangeRect = null;

    const doc = document.implementation.createHTMLDocument('chapter');
    doc.body.innerHTML =
      '<p>The cited passage begins here and continues to the end of the paragraph.</p>' +
      '<p>A following paragraph stays put.</p>';
    vi.spyOn(doc, 'createRange').mockImplementation(() => makeRange());

    const frame = { getBoundingClientRect: () => pageRect() };
    contents = {
      document: doc,
      window: { frameElement: frame },
      section: { href: HREF },
      range: vi.fn(() => makeRange()),
      cfiFromRange: vi.fn(() => TARGET_CFI),
    };

    rendition = {
      hooks: { content: { register: vi.fn() } },
      themes: { register: vi.fn(), select: vi.fn(), fontSize: vi.fn() },
      on: vi.fn((type: string, callback: (...args: unknown[]) => void) => {
        (listeners[type] ??= []).push(callback);
      }),
      off: vi.fn((type: string, callback: (...args: unknown[]) => void) => {
        listeners[type] = (listeners[type] ?? []).filter((entry) => entry !== callback);
      }),
      annotations: { highlight: vi.fn(), add: vi.fn(), remove: vi.fn() },
      getContents: vi.fn(() => [contents]),
      views: vi.fn(() => []),
      getRange: vi.fn(),
      display: vi.fn(() => Promise.resolve()),
      resize: vi.fn(),
      next: vi.fn(),
      prev: vi.fn(),
      currentLocation: vi.fn(() => ({ start: { cfi: liveCfi, href: HREF, index: 22 } })),
    };

    const book = {
      renderTo: vi.fn(() => rendition),
      ready: Promise.resolve({ navigation: { toc: [] } }),
      on: vi.fn(),
      off: vi.fn(),
      locations: {
        load: vi.fn(),
        generate: vi.fn(() => Promise.resolve()),
        save: vi.fn(),
        length: () => 0,
        percentageFromCfi: () => 0,
        locationFromCfi: () => 0,
      },
      navigation: { toc: [] },
      spine: { spineItems: [{ href: HREF, index: 22 }] },
      destroy: vi.fn(),
    };

    vi.mocked(ePub).mockImplementation(() => book as never);
    vi.stubGlobal(
      'ResizeObserver',
      class {
        constructor(callback: () => void) {
          resizeObservers.push(callback);
        }
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );

    await TestBed.configureTestingModule({
      imports: [EpubReader],
      providers: [
        { provide: NotesService, useValue: notesService },
        { provide: BooksService, useValue: booksService },
      ],
    }).compileComponents();

    vi.useFakeTimers({
      toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    localStorage.clear();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  async function openReader() {
    fixture = TestBed.createComponent(EpubReader);
    fixture.componentRef.setInput('bookId', 'book-1');
    fixture.detectChanges();
    // Settle `book.ready`, the opening display, and the service subscriptions.
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(0);
    // Flush the opening progress write so later assertions only see new ones.
    await vi.advanceTimersByTimeAsync(1100);
    booksService.updateProgress.mockClear();

    const page = fixture.nativeElement.querySelector('#epub-page') as HTMLElement;
    Object.defineProperty(page, 'getBoundingClientRect', {
      configurable: true,
      value: () => pageRect(),
    });
  }

  it('confirms a grounded target that stays in the reading view and persists it', async () => {
    await openReader();
    rangeRect = onPage();

    await fixture.componentInstance.goToSource(groundedTarget());

    expect(displayCallsFor(TARGET_CFI)).toBe(1);

    // Settle quiet → verify (in view) → confirm; then the debounced write.
    await vi.advanceTimersByTimeAsync(400);
    await vi.advanceTimersByTimeAsync(1100);

    expect(fixture.componentInstance.sourceNavigationMessage()).toBeNull();
    expect(booksService.updateProgress).toHaveBeenCalledWith(
      'book-1',
      TARGET_CFI,
      expect.any(Number),
    );
    expect(progressWritesWith(STALE_CFI)).toEqual([]);
  });

  it('reasserts the target once when the reflow re-displays the stale section start', async () => {
    await openReader();
    rangeRect = onPage();
    await fixture.componentInstance.goToSource(groundedTarget());
    expect(displayCallsFor(TARGET_CFI)).toBe(1);

    // The initial settle: a resize re-lays-out and re-displays the section
    // start (the measured defect), moving the target out of view.
    rangeRect = offPage();
    emitRelocated(STALE_CFI);
    emitResized();

    expect(progressWritesWith(STALE_CFI)).toEqual([]);

    await vi.advanceTimersByTimeAsync(400); // quiet → verify → displaced
    expect(displayCallsFor(TARGET_CFI)).toBe(2); // exactly one controlled retry

    rangeRect = onPage(); // the retry landed on the target again
    await vi.advanceTimersByTimeAsync(400); // verify → confirmed
    await vi.advanceTimersByTimeAsync(1100); // debounced confirmed write

    expect(fixture.componentInstance.sourceNavigationMessage()).toBeNull();
    expect(booksService.updateProgress).toHaveBeenCalledWith(
      'book-1',
      TARGET_CFI,
      expect.any(Number),
    );
    expect(progressWritesWith(STALE_CFI)).toEqual([]);
  });

  it('protects a stored-CFI locator through the rendition location when there is no geometry', async () => {
    await openReader();
    rangeRect = null; // no measurable layout: only the CFI can vouch for the target

    await fixture.componentInstance.goToSource(
      groundedTarget({ epubTextOffset: null, epubCfi: TARGET_CFI }),
    );
    expect(displayCallsFor(TARGET_CFI)).toBe(1);

    // Displaced: the rendition still reports the section start.
    liveCfi = STALE_CFI;
    await vi.advanceTimersByTimeAsync(400);
    expect(displayCallsFor(TARGET_CFI)).toBe(2);

    liveCfi = TARGET_CFI; // the retry landed on target
    await vi.advanceTimersByTimeAsync(400);
    await vi.advanceTimersByTimeAsync(1100);

    expect(fixture.componentInstance.sourceNavigationMessage()).toBeNull();
    expect(booksService.updateProgress).toHaveBeenCalledWith(
      'book-1',
      TARGET_CFI,
      expect.any(Number),
    );
    expect(progressWritesWith(STALE_CFI)).toEqual([]);
  });

  it('holds progress writes closed while settling and persists only the confirmed target', async () => {
    await openReader();
    rangeRect = onPage();
    await fixture.componentInstance.goToSource(groundedTarget());

    // epub.js's reflow reports the stale location while the target is being
    // protected: it must not become a reading-progress write.
    emitRelocated(STALE_CFI);
    await vi.advanceTimersByTimeAsync(999); // quiet fired at 400; write debounce pending
    expect(booksService.updateProgress).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(500); // confirmed at 400 + 1000 ms debounce
    expect(booksService.updateProgress).toHaveBeenCalledWith(
      'book-1',
      TARGET_CFI,
      expect.any(Number),
    );
    expect(progressWritesWith(STALE_CFI)).toEqual([]);
  });

  it('lets a manual page turn cancel the pending settle and the reassert', async () => {
    await openReader();
    rangeRect = offPage();
    await fixture.componentInstance.goToSource(groundedTarget());
    expect(displayCallsFor(TARGET_CFI)).toBe(1);

    emitResized(); // settle in flight
    fixture.componentInstance.next();

    expect(rendition.next).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(5000);
    expect(displayCallsFor(TARGET_CFI)).toBe(1); // no delayed reassert
    expect(fixture.componentInstance.sourceNavigationMessage()).toBeNull();

    // Normal persistence resumes for the reader's own navigation.
    emitRelocated(USER_CFI);
    await vi.advanceTimersByTimeAsync(1100);
    expect(booksService.updateProgress).toHaveBeenCalledWith(
      'book-1',
      USER_CFI,
      expect.any(Number),
    );
  });

  it('does not let an older settle override a newer grounded navigation', async () => {
    await openReader();
    rangeRect = offPage();
    await fixture.componentInstance.goToSource(groundedTarget());
    expect(displayCallsFor(TARGET_CFI)).toBe(1);

    // The first navigation is still settling when a new grounded target lands.
    emitResized();
    rangeRect = onPage();
    await fixture.componentInstance.goToSource(
      groundedTarget({ epubCfi: NEW_TARGET_CFI, epubTextOffset: null, epubSpineIndex: 23 }),
    );

    await vi.advanceTimersByTimeAsync(5000);

    expect(displayCallsFor(TARGET_CFI)).toBe(1); // the superseded settle never reasserted it
    expect(displayCallsFor(NEW_TARGET_CFI)).toBe(1);
    expect(fixture.componentInstance.sourceNavigationMessage()).toBeNull();
    expect(booksService.updateProgress).toHaveBeenCalledWith(
      'book-1',
      NEW_TARGET_CFI,
      expect.any(Number),
    );
  });

  it('restarts the settle window on rendition resizes and on the reader resize stream', async () => {
    await openReader();
    rangeRect = offPage();
    await fixture.componentInstance.goToSource(groundedTarget());
    expect(displayCallsFor(TARGET_CFI)).toBe(1);

    await vi.advanceTimersByTimeAsync(300);
    emitResized(); // epub.js re-layout at t=300 restarts the quiet window
    await vi.advanceTimersByTimeAsync(300); // t=600: would be a verdict without the restart
    expect(displayCallsFor(TARGET_CFI)).toBe(1);

    resizeObservers.forEach((callback) => callback()); // reader resize stream at t=600
    await vi.advanceTimersByTimeAsync(300); // t=900
    expect(displayCallsFor(TARGET_CFI)).toBe(1);

    await vi.advanceTimersByTimeAsync(100); // t=1000 = last restart + quiet window
    expect(displayCallsFor(TARGET_CFI)).toBe(2); // displaced → one reassert

    rangeRect = onPage();
    await vi.advanceTimersByTimeAsync(400);
    await vi.advanceTimersByTimeAsync(1100);
    expect(fixture.componentInstance.sourceNavigationMessage()).toBeNull();
    expect(booksService.updateProgress).toHaveBeenCalledWith(
      'book-1',
      TARGET_CFI,
      expect.any(Number),
    );
  });

  it('reaches a verdict at the bounded deadline even when resizes never stop', async () => {
    await openReader();
    rangeRect = offPage();
    await fixture.componentInstance.goToSource(groundedTarget());
    expect(displayCallsFor(TARGET_CFI)).toBe(1);

    // Resizes every 300 ms: the quiet window never elapses on its own, so the
    // bounded deadline is what must produce the (single) verdict.
    for (let step = 0; step < 7; step++) {
      await vi.advanceTimersByTimeAsync(300);
      emitResized();
    }
    // t≈2100 — the 2000 ms deadline fired mid-stream.
    expect(displayCallsFor(TARGET_CFI)).toBe(2);
    expect(fixture.componentInstance.sourceNavigationMessage()).toBeNull();

    await vi.advanceTimersByTimeAsync(3000); // stop the churn: the retry fails closed
    expect(displayCallsFor(TARGET_CFI)).toBe(2);
    expect(fixture.componentInstance.sourceNavigationMessage()).toBe(
      "Couldn't locate this passage in the EPUB.",
    );
  });

  it('fails closed with the existing message after exactly one controlled retry', async () => {
    await openReader();
    rangeRect = offPage(); // the passage never makes it onto the visible page
    await fixture.componentInstance.goToSource(groundedTarget());
    expect(displayCallsFor(TARGET_CFI)).toBe(1);

    await vi.advanceTimersByTimeAsync(400); // verdict → reassert
    expect(displayCallsFor(TARGET_CFI)).toBe(2);
    expect(fixture.componentInstance.sourceNavigationMessage()).toBeNull();

    await vi.advanceTimersByTimeAsync(400); // verdict → fail closed
    fixture.detectChanges();

    expect(fixture.componentInstance.sourceNavigationMessage()).toBe(
      "Couldn't locate this passage in the EPUB.",
    );
    const status = fixture.nativeElement.querySelector(
      '.source-navigation-status',
    ) as HTMLElement | null;
    expect(status).not.toBeNull();
    expect(status!.textContent).toContain("Couldn't locate this passage in the EPUB.");

    await vi.advanceTimersByTimeAsync(5000);
    expect(displayCallsFor(TARGET_CFI)).toBe(2); // no further retries
    expect(progressWritesWith(STALE_CFI)).toEqual([]);
  });
});
