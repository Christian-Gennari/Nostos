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

  it('draws a pen that carries a note as its own layer, the same fill with a dotted rule', () => {
    const noted = textMarkName({ kind: 'pen', colour: 'sage', noted: true });
    expect(noted).toBe('nostos-mark-pen-sage-noted');

    const css = textMarkCss('light');
    const rule = css.slice(css.indexOf(`::highlight(${noted})`));
    expect(rule).toMatch(/^::highlight\(nostos-mark-pen-sage-noted\)\{background-color:#b7e4cf;[^}]*text-decoration-style:dotted/);
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

  it('draws a saved highlight with a note on the noted layer, and keeps it there through a recolour', () => {
    const { manager } = setup();
    const roseNoted = textMarkName({ kind: 'pen', colour: 'rose', noted: true });
    const blueNoted = textMarkName({ kind: 'pen', colour: 'blue', noted: true });
    const blue = textMarkName({ kind: 'pen', colour: 'blue' });

    manager.restoreHighlights([{ cfiRange: CFI_A, highlightColour: 'rose', content: 'a thought' } as never]);
    expect(registry.get(roseNoted)!.ranges).toHaveLength(1);

    manager.recolourHighlight(CFI_A, 'blue');
    expect(registry.has(roseNoted)).toBe(false);
    expect(registry.get(blueNoted)!.ranges).toHaveLength(1);

    // The note was cleared: same pen, no rule.
    manager.recolourHighlight(CFI_A, 'blue', false);
    expect(registry.has(blueNoted)).toBe(false);
    expect(registry.get(blue)!.ranges).toHaveLength(1);
  });

  describe('finding the saved highlight under a point', () => {
    const original = Range.prototype.getClientRects;
    beforeEach(() => {
      // jsdom does no layout. "Hello" (offset 0) is laid out at x 0..50 and
      // "world" (offset 6) at x 60..110, both on the line y 10..30.
      Range.prototype.getClientRects = function (this: Range) {
        const left = this.startOffset === 0 ? 0 : 60;
        return [{ left, right: left + 50, top: 10, bottom: 30, width: 50, height: 20 }] as never;
      };
    });
    afterEach(() => {
      Range.prototype.getClientRects = original;
    });

    it('reports the saved highlight a point falls on, with the box to open at', () => {
      const { manager, contents } = setup();
      manager.restoreHighlights([
        { cfiRange: CFI_A, highlightColour: 'rose' } as never,
        { cfiRange: CFI_B, highlightColour: 'sage' } as never,
      ]);

      expect(manager.highlightsAt(contents, 80, 20)).toEqual([
        { identifier: CFI_B, anchor: { left: 60, right: 110, top: 10, bottom: 30 } },
      ]);
      expect(manager.highlightsAt(contents, 25, 20, false)).toEqual([{ identifier: CFI_A, anchor: null }]);
      expect(manager.highlightsAt(contents, 25, 60)).toEqual([]);
    });

    it('ignores highlights in other sections and ones that have been removed', () => {
      const { manager, contents } = setup();
      manager.restoreHighlights([
        { cfiRange: CFI_A, highlightColour: 'rose' } as never,
        { cfiRange: OTHER_SECTION, highlightColour: 'rose' } as never,
      ]);
      manager.removeHighlight(CFI_A);

      expect(manager.highlightsAt(contents, 25, 20)).toEqual([]);
      expect(contents.range).not.toHaveBeenCalledWith(OTHER_SECTION);
    });
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

  it('saves a highlight made with a note straight onto the noted layer', async () => {
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
      cb(0);
      return 1;
    });
    const { manager, contents } = setup();
    (contents as unknown as { cfiFromRange: () => string }).cfiFromRange = () => CFI_A;
    manager.registerContents(contents);
    manager.setHighlightColour('amber');

    const range = document.createRange();
    range.selectNodeContents(document.body.querySelector('p')!);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);
    document.dispatchEvent(new MouseEvent('mouseup', { button: 0 }));

    await manager.commitHighlight('Why this line?');

    expect(registry.has(textMarkName({ kind: 'pen', colour: 'amber' }))).toBe(false);
    expect(registry.get(textMarkName({ kind: 'pen', colour: 'amber', noted: true }))!.ranges).toHaveLength(1);
    manager.destroy();
  });
});
