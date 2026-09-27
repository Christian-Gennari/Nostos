import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import ePub from 'epubjs';

import { NotesService } from '../../core/services/notes.service';
import { BooksService } from '../../core/services/books.service';
import {
  DEFAULT_TYPOGRAPHY,
  EpubReader,
  findTocItemForHref,
  marginInsetPercent,
  progressLabel,
  resolveGroundedEpubResourceHref,
  spinePercentFrom,
  typographyCss,
} from './epub-reader.component';
import { EpubAnnotationManager } from './epub-annotation-manager';

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
      component.setTypography({ fontFamily: 'serif' });
      expect(applied).toHaveBeenCalledTimes(1); // deferred, not once per click

      vi.advanceTimersByTime(300);
      expect(applied).toHaveBeenCalledTimes(2); // one apply, at the end
      expect(component.typography().fontFamily).toBe('serif');
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
  it('keeps the publisher typeface on default but applies spacing', () => {
    const css = typographyCss({ fontFamily: 'default', lineHeight: 1.6, margin: 'normal' });
    expect(css).not.toContain('font-family');
    expect(css).toContain('line-height:1.6 !important');
    // Margins are NOT a stylesheet rule either: they are padding on our own
    // viewer (see marginInsetPercent), because epub.js writes its own
    // inline-important body padding that no stylesheet rule can beat.
    expect(css).not.toContain('padding');
  });

  it('overrides the typeface per choice', () => {
    const css = typographyCss({ fontFamily: 'serif', lineHeight: 2.0, margin: 'wide' });
    expect(css).toContain('font-family:Georgia, "Times New Roman", Times, serif !important');
    expect(css).toContain('line-height:2 !important');
    expect(css).not.toContain('padding');
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
      JSON.stringify({ fontFamily: 'serif', lineHeight: 2, margin: 'wide' }),
    );

    component.loadBook('book-9');

    // The book's own choice is honoured…
    expect(component.typography()).toEqual({ fontFamily: 'serif', lineHeight: 2, margin: 'wide' });
    // …and becomes the reader-wide preference for every other book.
    expect(JSON.parse(localStorage.getItem('nostos.epub-typography')!)).toEqual({
      fontFamily: 'serif',
      lineHeight: 2,
      margin: 'wide',
    });
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
      fontFamily: 'default',
      lineHeight: DEFAULT_TYPOGRAPHY.lineHeight,
      margin: 'normal',
    });
  });

  it('reset restores publisher defaults', () => {
    const component = fixture.componentInstance;
    component.setTypography({ fontFamily: 'mono', margin: 'wide' });
    component.zoomIn();
    component.resetTypography();
    expect(component.typography()).toEqual({
      fontFamily: 'default',
      lineHeight: 1.6,
      margin: 'normal',
    });
    expect(component.fontSizePercent()).toBe(100);
    expect(localStorage.getItem('nostos.epub-font-size')).toBe('100');
  });

  it('writes the rules into newly rendered sections', () => {
    const component = fixture.componentInstance;
    component.setTypography({ fontFamily: 'serif', lineHeight: 2.0, margin: 'narrow' });

    const doc = makeDocument();
    (component as unknown as { upsertTypographyStyle: (d: Document) => void }).upsertTypographyStyle(doc);
    const style = doc.getElementById('nostos-typography');
    expect(style?.textContent).toContain('font-family:Georgia, "Times New Roman", Times, serif !important');
    expect(style?.textContent).not.toContain('padding');
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
