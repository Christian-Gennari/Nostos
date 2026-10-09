import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  MarkdownEditorComponent,
  caretScrollDelta,
  caretViewportTop,
} from './markdown-editor.component';

/**
 * TinyMCE is a heavy global; these specs stub it and assert the FINAL chrome
 * contract (expert design §1/§6): constant 'oxide' skin, exact toolbar and
 * plugin set, no menubar/statusbar, wordcount-plugin emissions, and a single
 * editor instance per component lifecycle (no reinitialization).
 */

interface EditorMock {
  on: (event: string, cb: (...args: unknown[]) => void) => void;
  getContent: () => string;
  setContent: (html: string) => void;
  insertContent: (html: string) => void;
  typeText?: (html: string) => void;
  focus: () => void;
  getBody: () => { style: Record<string, string> };
  getWin?: () => { scrollY?: number; scrollTo?: (x: number, y: number) => void };
  selection?: {
    getBookmark?: (...args: unknown[]) => unknown;
    moveToBookmark?: (bookmark: unknown) => void;
    getRng?: () => unknown;
  };
  plugins: { wordcount: { body: { getWordCount: () => number } } };
}

interface InitConfig {
  skin?: string;
  setup?: (editor: EditorMock) => void;
  plugins?: unknown;
  menubar?: unknown;
  statusbar?: unknown;
  branding?: unknown;
  promotion?: unknown;
  resize?: unknown;
  toolbar?: unknown;
  toolbar_mode?: unknown;
  toolbar_sticky?: unknown;
  quickbars_selection_toolbar?: unknown;
  quickbars_insert_toolbar?: unknown;
  contextmenu?: unknown;
  browser_spellcheck?: unknown;
  height?: unknown;
  suffix?: unknown;
  content_style?: unknown;
}

describe('MarkdownEditorComponent', () => {
  let fixture: ComponentFixture<MarkdownEditorComponent>;
  let initCalls: InitConfig[];
  let removedEditors: unknown[];
  let editors: EditorMock[];
  let emitted: string[];
  let wordCountEmissions: number[];
  let wordCountValue: number;
  let registeredEditorEvents: string[];
  let insertContentCalls: string[];
  let focusCalls: number;
  let moveToBookmarkCalls: unknown[];
  let scrollToCalls: Array<[number, number]>;

  function installTinyMceMock() {
    initCalls = [];
    removedEditors = [];
    editors = [];
    wordCountValue = 42;
    registeredEditorEvents = [];
    insertContentCalls = [];
    focusCalls = 0;
    moveToBookmarkCalls = [];
    scrollToCalls = [];

    (globalThis as Record<string, unknown>)['tinymce'] = {
      init: (config: InitConfig) => {
        initCalls.push(config);
        let content = '';
        const listeners = new Map<string, Array<(...args: unknown[]) => void>>();
        const fire = (event: string) => {
          for (const listener of listeners.get(event) ?? []) listener();
        };
        const editor: EditorMock = {
          on: (event, cb) => {
            for (const e of event.split(/\s+/)) {
              registeredEditorEvents.push(e);
              const callbacks = listeners.get(e) ?? [];
              callbacks.push(cb);
              listeners.set(e, callbacks);
            }
          },
          getContent: () => content,
          setContent: (html) => {
            content = html;
            fire('SetContent');
          },
          typeText: (html) => {
            content = html;
            fire('Input');
          },
          insertContent: (html) => {
            insertContentCalls.push(html);
            // TinyMCE owns the actual selection. Model a caret between two
            // paragraphs so the component can only succeed by delegating here.
            content = `<p>Before caret</p>${html}<p>After caret</p>`;
          },
          focus: () => {
            focusCalls++;
          },
          selection: {
            getBookmark: () => ({ start: [1, 0], forward: true }),
            moveToBookmark: (bookmark) => moveToBookmarkCalls.push(bookmark),
          },
          getWin: () => ({
            scrollY: 320,
            scrollTo: (x, y) => scrollToCalls.push([x, y]),
          }),
          getBody: () => ({ style: {} }),
          plugins: { wordcount: { body: { getWordCount: () => wordCountValue } } },
        };
        editors.push(editor);
        config.setup?.(editor);
        // Real TinyMCE fires 'init' asynchronously after setup.
        queueMicrotask(() => fire('init'));
        return Promise.resolve(editor);
      },
      remove: (editor: unknown) => removedEditors.push(editor),
    };
  }

  /** All component CSS injected by Angular (emulated encapsulation). */
  const componentCss = (): string =>
    Array.from(document.querySelectorAll('style'))
      .map((s) => s.textContent ?? '')
      .join('\n');

  beforeEach(async () => {
    localStorage.clear();
    installTinyMceMock();

    await TestBed.configureTestingModule({
      imports: [MarkdownEditorComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(MarkdownEditorComponent);
    fixture.componentRef.setInput('initialContent', '# Title');
    emitted = [];
    wordCountEmissions = [];
    fixture.componentInstance.contentChange.subscribe((md) => emitted.push(md));
    fixture.componentInstance.wordCountChange.subscribe((n) => wordCountEmissions.push(n));
    fixture.detectChanges();
    await fixture.whenStable();
  });

  it('initializes once with the constant oxide skin', () => {
    expect(initCalls).toHaveLength(1);
    expect(initCalls[0].skin).toBe('oxide');
  });

  it('themes startup surfaces without painting native textarea content', () => {
    expect(registeredEditorEvents).toContain('PreInit');

    const css = componentCss();
    // Angular scopes component selectors before tests can inspect them, so
    // assert the selector's semantic shape rather than its source spelling.
    expect(css).toMatch(/>\s*textarea/);
    expect(css).toContain('background: var(--editor-ui-bg, var(--bg-surface))');
    expect(css).toContain('.editor-starting');
    expect(css).toContain('color: transparent');
    expect(css).toContain('-webkit-text-fill-color: transparent');
    expect(css).toContain('caret-color: transparent');
    expect(css).toContain('.tox .tox-throbber');
    expect(fixture.componentInstance.editorStarting).toBe(false);
  });

  it('reveals the native fallback and leaves startup retryable when TinyMCE loading fails', async () => {
    const component = fixture.componentInstance as any;
    component.editor = null;
    component.destroyed = false;
    component.editorInit = Promise.resolve();
    component.tinyMceLoader = {
      load: () => Promise.reject(new Error('TinyMCE unavailable')),
    };

    await expect(component.initEditor()).rejects.toThrow('TinyMCE unavailable');

    expect(component.editorStarting).toBe(false);
    expect(component.editorInit).toBeNull();
  });

  it('uses the final chrome config: plugins, toolbar, quickbars, no menubar/statusbar', () => {
    const cfg = initCalls[0];

    expect(cfg.plugins).toBe('lists link image table wordcount searchreplace quickbars');
    expect(cfg.menubar).toBe(false);
    expect(cfg.statusbar).toBe(false);
    expect(cfg.branding).toBe(false);
    expect(cfg.promotion).toBe(false);
    expect(cfg.resize).toBe(false);
    expect(cfg.toolbar).toBe(
      'undo redo | blocks | bold italic underline | bullist numlist | link image | removeformat'
    );
    expect(cfg.toolbar_mode).toBe('sliding');
    expect(cfg.toolbar_sticky).toBe(false);
    expect(cfg.quickbars_selection_toolbar).toBe('bold italic | h2 h3 blockquote | link');
    expect(cfg.quickbars_insert_toolbar).toBe(false);
    expect(cfg.contextmenu).toBe(false);
    expect(cfg.browser_spellcheck).toBe(true);
    expect(cfg.height).toBe('100%');
    expect(cfg.suffix).toBe('.min');
    // Autoresize and the removed chrome plugins must be gone.
    expect(String(cfg.plugins)).not.toContain('autoresize');
    expect(String(cfg.plugins)).not.toContain('code');
    expect(String(cfg.plugins)).not.toContain('help');
    expect(String(cfg.plugins)).not.toContain('visualblocks');
    expect(String(cfg.plugins)).not.toContain('directionality');
  });

  it('ships the expert content stylesheet: cool ink on white paper, theme-independent', () => {
    const style = String(initCalls[0].content_style);
    expect(style).toContain('--paper: #ffffff');
    expect(style).toContain('--ink: #121316');
    expect(style).toContain("data-theme='dark'");
    expect(style).toContain('font-family: Newsreader, Georgia');
    // The old generic dark-mode-coupled palette must be gone.
    expect(style).not.toContain('--color-text-main');
    expect(style).not.toContain('#1a1a1a');
  });

  it('shares a wider manuscript measure with the toolbar without changing phone typography', () => {
    const contentCss = String(initCalls[0].content_style);
    const chromeCss = componentCss();

    // Writing and toolbar must use the same desktop measure. A fixed 600px
    // manuscript made the editor feel cramped on wide screens.
    expect(contentCss).toContain('max-width: 820px');
    expect(chromeCss).toContain('max-width: 820px');
    expect(contentCss).toContain('@media (max-width: 640px)');
    expect(contentCss).toContain('padding: 2rem 1.25rem 6rem');
    expect(contentCss).toContain('font-size: 17px');
  });

  it('keeps blockquotes flat and editorial instead of fading into the page', () => {
    const style = String(initCalls[0].content_style);
    const start = style.indexOf('blockquote {');
    expect(start).toBeGreaterThanOrEqual(0);
    const quoteBlock = style.slice(start, style.indexOf('}', start));

    expect(quoteBlock).toContain('background: transparent');
    expect(quoteBlock).toContain('border-left: 2px solid var(--quote-rule)');
    expect(quoteBlock).not.toContain('linear-gradient');
  });

  it('rides the prose roles on tokens, so the sheet cannot drift from the theme', () => {
    // Regression: `strong`, `th` and the link underline each carried a LITERAL
    // light value, so the dark sheet painted bold text near-black (1.13:1
    // against the paper), a near-white table-header band with muted ink on it
    // (1.61:1), and a cool-slate underline. Roles belong in both theme blocks.
    const style = String(initCalls[0].content_style);

    const darkStart = style.indexOf(":root[data-theme='dark']");
    const lightBlock = style.slice(style.indexOf(':root {'), darkStart);
    const darkBlock = style.slice(darkStart);

    const roles: Array<[RegExp, string]> = [
      [/strong\s*\{[^}]*color:\s*var\(--ink-strong\)/, '--ink-strong'],
      [/th\s*\{[^}]*background:\s*var\(--table-head-bg\)/, '--table-head-bg'],
      [/a\s*\{[^}]*text-decoration-color:\s*var\(--link-rule\)/, '--link-rule'],
    ];

    for (const [usage, token] of roles) {
      expect(style).toMatch(usage);
      expect(lightBlock).toContain(`${token}:`);
      expect(darkBlock).toContain(`${token}:`);
    }
  });

  it('emits Markdown on ordinary TinyMCE keyboard input without Change or blur', async () => {
    const before = emitted.length;
    const editor = editors[editors.length - 1];

    // Model typing in the iframe: the DOM/content changes and TinyMCE
    // fires Input, but no Change or blur event has occurred yet.
    editor.typeText!('<h1>Title</h1><p>Typed at the end</p>');
    await fixture.whenStable();

    expect(emitted.length).toBe(before + 1);
    expect(emitted[emitted.length - 1]).toContain('Typed at the end');
    expect(emitted[emitted.length - 1]).toContain('# Title');
  });

  it('emits the wordcount-plugin count on init and on content events', async () => {
    // Init (+ any SetContent from document switching) must have emitted.
    expect(wordCountEmissions.length).toBeGreaterThanOrEqual(1);
    expect(wordCountEmissions[wordCountEmissions.length - 1]).toBe(wordCountValue);

    wordCountValue = 7;
    const before = wordCountEmissions.length;
    editors[editors.length - 1].setContent('<p>changed</p>');
    await fixture.whenStable();
    expect(wordCountEmissions.length).toBe(before + 1);
    expect(wordCountEmissions[wordCountEmissions.length - 1]).toBe(7);
  });

  it('inserts Markdown through TinyMCE at the current selection and emits the resulting document', async () => {
    const before = emitted.length;
    const inserted = await fixture.componentInstance.insertMarkdown(
      '> Selected passage\n> — *The Republic*, p. 42',
    );

    expect(inserted).toBe(true);
    expect(insertContentCalls).toHaveLength(1);
    expect(insertContentCalls[0]).toContain('<blockquote>');
    expect(insertContentCalls[0]).toContain('The Republic');
    expect(focusCalls).toBe(1);

    expect(emitted.length).toBe(before + 1);
    const markdown = emitted[emitted.length - 1];
    expect(markdown).toContain('Before caret');
    expect(markdown).toContain('Selected passage');
    expect(markdown).toContain('The Republic');
    expect(markdown).toContain('After caret');
  });

  it('captures a history-safe TinyMCE bookmark and iframe scroll position', () => {
    expect(fixture.componentInstance.captureTransientState()).toEqual({
      bookmark: { start: [1, 0], forward: true },
      scrollY: 320,
    });
  });

  it('omits a bookmark that cannot be structured-cloned while preserving scroll', () => {
    editors[0].selection!.getBookmark = () => ({ node: () => 'not cloneable' });

    expect(fixture.componentInstance.captureTransientState()).toEqual({ scrollY: 320 });
  });

  it('restores selection and scroll without emitting a content change', async () => {
    const before = emitted.length;
    const restored = await fixture.componentInstance.restoreTransientState(
      { bookmark: { start: [2, 0] }, scrollY: 480 },
      '# Title',
    );
    await Promise.resolve();

    expect(restored).toBe(true);
    expect(moveToBookmarkCalls).toEqual([{ start: [2, 0] }]);
    expect(scrollToCalls).toContainEqual([0, 480]);
    expect(emitted).toHaveLength(before);
  });

  it('fails safely when capture/restore runs before editor readiness or with invalid state', async () => {
    (fixture.componentInstance as any).editorReady = false;
    expect(fixture.componentInstance.captureTransientState()).toBeNull();
    expect(await fixture.componentInstance.restoreTransientState({}, '# Title')).toBe(false);
  });

  it('queues restore until the requested document content has been applied', async () => {
    const restore = fixture.componentInstance.restoreTransientState(
      { bookmark: { start: [3, 0] }, scrollY: 640 },
      'Second document',
    );

    await Promise.resolve();
    expect(moveToBookmarkCalls).toEqual([]);

    fixture.componentRef.setInput('initialContent', 'Second document');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(await restore).toBe(true);
    expect(moveToBookmarkCalls).toEqual([{ start: [3, 0] }]);
  });

  it('fails safely when the TinyMCE instance is not ready instead of inventing a position', async () => {
    fixture.destroy();

    await expect(fixture.componentInstance.insertMarkdown('Source text')).resolves.toBe(false);
    expect(insertContentCalls).toHaveLength(0);
  });

  it('tears the editor down exactly once on component destroy', () => {
    fixture.destroy();
    expect(removedEditors).toHaveLength(1);
    expect(removedEditors[0]).toBe(editors[0]);
  });

  it('gives typewriter mode enough vertical runway to center first and last lines', () => {
    const contentCss = fixture.componentInstance.editorConfig.content_style as string;
    expect(contentCss).toContain('body.nostos-typewriter');
    expect(contentCss).toContain('padding-top: 45vh');
    expect(contentCss).toContain('padding-bottom: 55vh');
  });

  it('follows caret activity on typing, keyboard navigation, clicks, and node changes', () => {
    expect(registeredEditorEvents).toContain('Input');
    expect(registeredEditorEvents).toContain('KeyUp');
    expect(registeredEditorEvents).toContain('Click');
    expect(registeredEditorEvents).toContain('NodeChange');
  });

  it('kills Oxide focus rings, including the ::before pseudo-element ring', () => {
    // Regression: Oxide paints the blue focus rectangle on
    // .tox .tox-edit-area::before (2px solid #006ce7, opacity raised by
    // .tox.tox-edit-focus). Resetting border/outline on .tox-edit-area and the
    // iframe left the pseudo-element untouched, so the blue border stayed
    // visible whenever the editor had focus.
    const css = componentCss();

    expect(css).toContain('.tox .tox-edit-area::before');
    const ring = css.slice(css.indexOf('.tox .tox-edit-area::before'));
    expect(ring).toContain('opacity: 0 !important');
    // The reset must target the pseudo-element, not merely the element.
    expect(css).toContain('.tox .tox-edit-area__iframe');
  });
});

describe('caretScrollDelta (typewriter geometry)', () => {
  it('returns null inside the one-line deadband', () => {
    expect(caretScrollDelta(450, 1000)).toBeNull(); // exactly centered
    expect(caretScrollDelta(480, 1000)).toBeNull(); // 30px drift
    expect(caretScrollDelta(420, 1000)).toBeNull();
  });

  it('scrolls carets outside the deadband to 45% of the viewport', () => {
    expect(caretScrollDelta(900, 1000)).toBe(450);
    expect(caretScrollDelta(100, 1000)).toBe(-350);
    expect(caretScrollDelta(490, 1000)).toBe(40);
  });

  it('returns null for invalid viewports', () => {
    expect(caretScrollDelta(100, 0)).toBeNull();
    expect(caretScrollDelta(NaN, 1000)).toBeNull();
  });
});

describe('caretViewportTop (collapsed caret geometry)', () => {
  it('uses a normal range rectangle when the browser reports one', () => {
    expect(
      caretViewportTop({
        getBoundingClientRect: () => ({ top: 412, height: 24 }),
      }),
    ).toBe(412);
  });

  it('probes an adjacent text character when a collapsed range reports all zeroes', () => {
    const textNode = { nodeType: 3, length: 5, data: 'hello', parentElement: null };
    const probe = {
      setStart: vi.fn(),
      setEnd: vi.fn(),
      getBoundingClientRect: () => ({ top: 618, height: 28 }),
    };

    expect(
      caretViewportTop({
        collapsed: true,
        startContainer: textNode,
        startOffset: 5,
        getBoundingClientRect: () => ({ top: 0, height: 0 }),
        cloneRange: () => probe,
      }),
    ).toBe(618);
    expect(probe.setStart).toHaveBeenCalledWith(textNode, 4);
    expect(probe.setEnd).toHaveBeenCalledWith(textNode, 5);
  });

  it('falls back to the containing block for an empty caret line', () => {
    const block = {
      nodeType: 1,
      childNodes: [],
      getBoundingClientRect: () => ({ top: 275, height: 32 }),
    };

    expect(
      caretViewportTop({
        collapsed: true,
        startContainer: block,
        startOffset: 0,
        getBoundingClientRect: () => ({ top: 0, height: 0 }),
        cloneRange: () => ({}),
      }),
    ).toBe(275);
  });
});

describe('MarkdownEditorComponent typewriter follow', () => {
  let fixture: ComponentFixture<MarkdownEditorComponent>;
  let editors: any[];

  beforeEach(async () => {
    localStorage.clear();
    (globalThis as Record<string, unknown>)['tinymce'] = {
      init: (config: { setup?: (editor: EditorMock) => void }) => {
        const editor: EditorMock = {
          on: () => {},
          getContent: () => '',
          setContent: () => {},
          insertContent: () => {},
          focus: () => {},
          getBody: () => ({ style: {} }),
          plugins: { wordcount: { body: { getWordCount: () => 0 } } },
        };
        editors.push(editor);
        config.setup?.(editor);
        return Promise.resolve(editor);
      },
      remove: () => {},
    };
    editors = [];

    await TestBed.configureTestingModule({
      imports: [MarkdownEditorComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(MarkdownEditorComponent);
    fixture.componentRef.setInput('initialContent', 'hello');
    fixture.detectChanges();
    await fixture.whenStable();
  });

  function withCaret(rectTop: number, viewportHeight: number) {
    const scrolled: number[] = [];
    const editor = editors[editors.length - 1] as Record<string, unknown>;
    editor['selection'] = { getRng: () => ({ getBoundingClientRect: () => ({ top: rectTop, height: 20 }) }) };
    editor['getWin'] = () => ({ innerHeight: viewportHeight, scrollBy: (_x: number, y: number) => scrolled.push(y) });
    return { editor, scrolled };
  }

  it('does nothing while typewriter mode is off', () => {
    const { editor, scrolled } = withCaret(900, 1000);
    fixture.componentInstance.followCaret(editor);
    expect(scrolled).toEqual([]);
  });

  it('scrolls the caret line toward the viewport center when on', () => {
    fixture.componentRef.setInput('typewriter', true);
    const { editor, scrolled } = withCaret(900, 1000);
    fixture.componentInstance.followCaret(editor);
    expect(scrolled).toEqual([450]);
  });

  it('never throws on editors without a selection API', () => {
    fixture.componentRef.setInput('typewriter', true);
    expect(() => fixture.componentInstance.followCaret(editors[editors.length - 1])).not.toThrow();
  });
});
