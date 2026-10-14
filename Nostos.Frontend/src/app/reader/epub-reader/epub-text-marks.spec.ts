import { Injector } from '@angular/core';
import { Contents, EpubCFI, Rendition } from 'epubjs';
import { of } from 'rxjs';
import { EpubAnnotationManager } from './epub-annotation-manager';
import { EpubTextMarks, textMarkCss, textMarkName, textMarksSupported } from './epub-text-marks';

/**
 * Marks painted by the text (CSS Custom Highlight API) instead of an overlay
 * measured once. jsdom has no Highlight API, so a minimal registry stands in
 * for the browser's: it records which live Ranges each name paints.
 */

class FakeHighlight {
  priority = 0;
  readonly ranges: Range[];
  constructor(...ranges: Range[]) {
    this.ranges = ranges;
  }
}

const CFI_A = 'epubcfi(/6/4!/4/2/1:0)';
const CFI_B = 'epubcfi(/6/4!/4/2/1:6)';
const OTHER_SECTION = 'epubcfi(/6/8!/4/2/1:0)';
const SECTION = new EpubCFI(CFI_A).spinePos;

let registry: Map<string, FakeHighlight>;

function installHighlightApi(): void {
  registry = new Map();
  vi.stubGlobal('Highlight', FakeHighlight);
  Object.defineProperty(window.CSS ?? (window.CSS = {} as typeof CSS), 'highlights', {
    value: registry,
    configurable: true,
  });
}

/** A section whose text is "Hello world"; a CFI resolves to a real Range over it. */
function makeContents(sectionIndex = SECTION): Contents {
  document.head.innerHTML = '';
  document.body.innerHTML = '<p>Hello world</p>';
  const text = document.body.querySelector('p')!.firstChild!;
  return {
    document,
    window,
    sectionIndex,
    range: vi.fn((cfi: string) => {
      const range = document.createRange();
      const start = cfi === CFI_B ? 6 : 0;
      range.setStart(text, start);
      range.setEnd(text, start + 5);
      return range;
    }),
  } as unknown as Contents;
}

const SAGE = textMarkName({ kind: 'pen', colour: 'sage' });
const SEARCH = textMarkName({ kind: 'search' });

describe('EpubTextMarks', () => {
  beforeEach(installHighlightApi);
  afterEach(() => {
    vi.unstubAllGlobals();
    delete (window.CSS as unknown as { highlights?: unknown }).highlights;
  });

  it('is only used where the browser has the Highlight API', () => {
    expect(textMarksSupported({})).toBe(false);
    expect(textMarksSupported({ Highlight: FakeHighlight, CSS: {} })).toBe(false);
    expect(textMarksSupported(window)).toBe(true);
  });

  it('paints a mark as a live Range in its own section, not as measured geometry', () => {
    const contents = makeContents();
    const marks = new EpubTextMarks(() => [contents]);

    marks.add(CFI_A, { kind: 'pen', colour: 'sage' });

    const painted = registry.get(SAGE)!;
    expect(painted.ranges).toHaveLength(1);
    expect(painted.ranges[0].toString()).toBe('Hello');

    // The browser repaints a Range wherever the text goes. Moving the text
    // (the DOM stand-in for a reflow) leaves the mark on the same words.
    const p = document.body.querySelector('p')!;
    p.insertBefore(document.createTextNode('Before. '), p.firstChild);
    expect(registry.get(SAGE)!.ranges[0].toString()).toBe('Hello');
  });

  it('leaves a mark for another section out of this one', () => {
    const contents = makeContents();
    const marks = new EpubTextMarks(() => [contents]);

    marks.add(OTHER_SECTION, { kind: 'pen', colour: 'sage' });

    expect(registry.has(SAGE)).toBe(false);
    expect(contents.range).not.toHaveBeenCalled();
  });

  it('removes by identity, so two marks at one place do not remove each other', () => {
    const contents = makeContents();
    const marks = new EpubTextMarks(() => [contents]);

    const saved = marks.add(CFI_A, { kind: 'pen', colour: 'sage' });
    const pending = marks.add(CFI_A, { kind: 'pen', colour: 'sage' });
    marks.add(CFI_B, { kind: 'pen', colour: 'sage' });

    marks.remove(pending);
    expect(registry.get(SAGE)!.ranges.map(String)).toEqual(['Hello', 'world']);

    marks.remove(saved);
    marks.remove(saved);
    expect(registry.get(SAGE)!.ranges.map(String)).toEqual(['world']);
  });

  it('drops a layer from the registry when its last mark goes', () => {
    const contents = makeContents();
    const marks = new EpubTextMarks(() => [contents]);

    const hit = marks.add(CFI_A, { kind: 'search' });
    expect(registry.get(SEARCH)!.priority).toBeGreaterThan(0);
    marks.remove(hit);

    expect(registry.has(SEARCH)).toBe(false);
  });

  it('paints existing marks and writes its stylesheet into a newly rendered section', () => {
    let rendered: Contents[] = [];
    const marks = new EpubTextMarks(() => rendered);
    marks.add(CFI_A, { kind: 'pen', colour: 'sage' });
    expect(registry.has(SAGE)).toBe(false);

    const contents = makeContents();
    marks.attach(contents);
    rendered = [contents];

    expect(registry.get(SAGE)!.ranges[0].toString()).toBe('Hello');
    expect(document.getElementById('nostos-text-marks')?.textContent).toContain(`::highlight(${SAGE})`);
  });

  it('tints the pens on the dark page instead of laying them down at full strength', () => {
    const contents = makeContents();
    const marks = new EpubTextMarks(() => [contents]);
    marks.attach(contents);
    const style = () => document.getElementById('nostos-text-marks')!.textContent!;

    expect(style()).toContain(`::highlight(${SAGE}){background-color:#b7e4cf;}`);
    marks.setTheme('dark');
    expect(style()).toContain(`::highlight(${SAGE}){background-color:#b7e4cf59;}`);
    expect(document.querySelectorAll('#nostos-text-marks')).toHaveLength(1);
  });

  it('declares a rule for every pen and every reading mark', () => {
    const css = textMarkCss('light');
    for (const name of ['pen-sage', 'pen-amber', 'pen-blue', 'pen-rose', 'search', 'landing', 'read-aloud']) {
      expect(css).toContain(`::highlight(nostos-mark-${name})`);
    }
  });

  it('skips a location the markup cannot resolve', () => {
    const contents = makeContents();
    (contents.range as ReturnType<typeof vi.fn>).mockImplementation(() => {
      throw new Error('bad cfi');
    });
    const marks = new EpubTextMarks(() => [contents]);

    expect(() => marks.add(CFI_A, { kind: 'pen', colour: 'sage' })).not.toThrow();
    expect(registry.has(SAGE)).toBe(false);
  });
});

describe('EpubAnnotationManager with marks painted by the text', () => {
  beforeEach(installHighlightApi);
  afterEach(() => {
    vi.unstubAllGlobals();
    delete (window.CSS as unknown as { highlights?: unknown }).highlights;
  });

  function setup() {
    const contents = makeContents();
    const annotations = { highlight: vi.fn(), underline: vi.fn(), add: vi.fn(), remove: vi.fn() };
    const rendition = {
      on: vi.fn(),
      off: vi.fn(),
      annotations,
      getContents: vi.fn(() => [contents]),
      views: vi.fn(() => ({ all: () => [] })),
    };
    const notesService = { create: vi.fn(() => of({ id: 'n1' })) };
    const manager = new EpubAnnotationManager(
      rendition as unknown as Rendition,
      'book-1',
      { get: () => notesService } as unknown as Injector,
    );
    return { manager, annotations, contents };
  }

  it('never asks epub.js for an overlay mark', () => {
    const { manager, annotations } = setup();

    manager.restoreHighlights([{ cfiRange: CFI_A, highlightColour: 'rose' } as never]);
    manager.showSearchHighlight(CFI_B);
    manager.showLandingMark(CFI_B);
    manager.showReadAloudMark(CFI_A);
    manager.recolourHighlight(CFI_A, 'blue');
    manager.removeHighlight(CFI_A);
    manager.clearSearchHighlight();

    expect(annotations.highlight).not.toHaveBeenCalled();
    expect(annotations.underline).not.toHaveBeenCalled();
    expect(annotations.add).not.toHaveBeenCalled();
    expect(annotations.remove).not.toHaveBeenCalled();
  });

  it('recolours a saved highlight by moving it to the other pen', () => {
    const { manager } = setup();
    manager.restoreHighlights([{ cfiRange: CFI_A, highlightColour: 'rose' } as never]);
    expect(registry.has(textMarkName({ kind: 'pen', colour: 'rose' }))).toBe(true);

    manager.recolourHighlight(CFI_A, 'blue');

    expect(registry.has(textMarkName({ kind: 'pen', colour: 'rose' }))).toBe(false);
    expect(registry.get(textMarkName({ kind: 'pen', colour: 'blue' }))!.ranges).toHaveLength(1);

    manager.removeHighlight(CFI_A);
    expect(registry.has(textMarkName({ kind: 'pen', colour: 'blue' }))).toBe(false);
  });

  it('swaps the offered passage for the saved highlight on commit', async () => {
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
      cb(0);
      return 1;
    });
    const { manager, contents } = setup();
    (contents as unknown as { cfiFromRange: () => string }).cfiFromRange = () => CFI_A;
    manager.registerContents(contents);
    manager.setHighlightColour('amber');
    const amber = textMarkName({ kind: 'pen', colour: 'amber' });

    const range = document.createRange();
    range.selectNodeContents(document.body.querySelector('p')!);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);
    document.dispatchEvent(new MouseEvent('mouseup', { button: 0 }));

    // Offered: painted once, in the pen in hand.
    expect(registry.get(amber)!.ranges).toHaveLength(1);

    await manager.commitHighlight();

    // Saved: the preview is gone and exactly the saved mark remains.
    expect(registry.get(amber)!.ranges).toHaveLength(1);
    manager.removeHighlight(CFI_A);
    expect(registry.has(amber)).toBe(false);
    manager.destroy();
  });
});
