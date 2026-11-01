import { TestBed } from '@angular/core/testing';

import { PdfAnnotationManager } from './pdf-annotation-manager';

describe('PdfAnnotationManager #478 cross-page provenance', () => {
  it('rejects a cross-page selection instead of persisting one-page geometry', () => {
    const service = TestBed.inject(PdfAnnotationManager);
    const host = document.createElement('div');

    const page1 = document.createElement('div');
    page1.className = 'page';
    page1.setAttribute('data-page-number', '4');
    const layer1 = document.createElement('div');
    layer1.className = 'textLayer';
    const start = document.createTextNode('start');
    layer1.appendChild(start);
    page1.appendChild(layer1);

    const page2 = document.createElement('div');
    page2.className = 'page';
    page2.setAttribute('data-page-number', '5');
    const layer2 = document.createElement('div');
    layer2.className = 'textLayer';
    const end = document.createTextNode('end');
    layer2.appendChild(end);
    page2.appendChild(layer2);

    host.append(page1, page2);
    document.body.appendChild(host);

    const range = document.createRange();
    range.setStart(start, 0);
    range.setEnd(end, end.textContent?.length ?? 0);
    const selection = window.getSelection()!;
    selection.removeAllRanges();
    selection.addRange(range);

    try {
      const capture = service.captureHighlight(true);
      expect(capture?.status).toBe('cross-page');
    } finally {
      selection.removeAllRanges();
      host.remove();
    }
  });
});

describe('PdfAnnotationManager saved highlights on the page', () => {
  function pageLayer(): HTMLElement {
    const layer = document.createElement('div');
    layer.className = 'textLayer';
    // A 1000 x 800 page at the viewport's origin.
    layer.getBoundingClientRect = () =>
      ({ left: 0, top: 0, right: 1000, bottom: 800, width: 1000, height: 800 }) as DOMRect;
    return layer;
  }

  const marked = {
    id: 'note-1',
    pageNumber: 2,
    rects: [
      { left: 0.1, top: 0.1, width: 0.5, height: 0.025 },
      { left: 0.1, top: 0.125, width: 0.2, height: 0.025 },
    ],
  };

  it('finds the highlight under a point, with the box to open its card at', () => {
    const service = TestBed.inject(PdfAnnotationManager);
    const hits = service.hitTest(pageLayer(), [marked], 150, 110);

    expect(hits).toEqual([
      { identifier: 'note-1', anchor: { left: 100, top: 80, right: 600, bottom: 120 } },
    ]);
  });

  it('finds nothing beside the mark, and skips measuring when asked', () => {
    const service = TestBed.inject(PdfAnnotationManager);
    expect(service.hitTest(pageLayer(), [marked], 700, 110)).toEqual([]);
    expect(service.hitTest(pageLayer(), [marked], 150, 110, false)).toEqual([
      { identifier: 'note-1', anchor: null },
    ]);
  });

  it('draws a dotted note rule under each line of a highlight that carries a note, and none otherwise', () => {
    const service = TestBed.inject(PdfAnnotationManager);
    const layer = pageLayer();

    service.paint(layer, [{ ...marked, noted: true }]);
    const rules = layer.querySelectorAll<HTMLElement>('.highlight-note-rule');
    expect(rules.length).toBe(2);
    expect(rules[0].style.top).toBe('12.5%');
    expect(layer.querySelector('.highlight-group')!.classList).toContain('has-note');

    service.paint(layer, [marked]);
    expect(layer.querySelectorAll('.highlight-note-rule').length).toBe(0);
  });
});

describe('PdfAnnotationManager.quoteRects (#1338)', () => {
  function layerWith(lines: string[][]): HTMLElement {
    const layer = document.createElement('div');
    layer.className = 'textLayer';
    for (const line of lines) {
      for (const run of line) {
        const span = document.createElement('span');
        span.textContent = run;
        layer.appendChild(span);
      }
      layer.appendChild(document.createElement('br'));
    }
    document.body.appendChild(layer);
    layer.getBoundingClientRect = () => ({ left: 0, top: 0, width: 100, height: 200 }) as DOMRect;
    return layer;
  }

  function withRangeRects(rects: Partial<DOMRect>[]): { range: () => Range } {
    const made = { range: null as unknown as Range };
    const create = document.createRange.bind(document);
    vi.spyOn(document, 'createRange').mockImplementation(() => {
      const range = create();
      range.getClientRects = () => rects as unknown as DOMRectList;
      made.range = range;
      return range;
    });
    return { range: () => made.range };
  }

  afterEach(() => {
    vi.restoreAllMocks();
    document.body.replaceChildren();
  });

  const service = () => TestBed.inject(PdfAnnotationManager);

  it('stores fractions of the page, as a hand-made highlight does', () => {
    const layer = layerWith([['Sing, goddess, the anger of Achilles']]);
    const made = withRangeRects([{ left: 10, top: 20, width: 50, height: 4 }]);

    expect(service().quoteRects(layer, 'the anger')).toEqual([
      { left: 0.1, top: 0.1, width: 0.5, height: 0.02 },
    ]);
    expect(made.range().toString()).toBe('the anger');
  });

  it('follows a passage across lines and through a word split at a line end', () => {
    const layer = layerWith([['the anger of philo-'], ['sophy and'], ['of nothing else']]);
    const made = withRangeRects([{ left: 10, top: 20, width: 50, height: 4 }]);

    expect(service().quoteRects(layer, 'of philosophy and of nothing')).not.toBeNull();
    expect(made.range().toString()).toBe('of philo-sophy andof nothing');
  });

  it('draws one rectangle per line, however many runs the line is made of', () => {
    const layer = layerWith([['the anger '], ['of Achilles']]);
    withRangeRects([
      { left: 10, top: 20, width: 20, height: 4 },
      { left: 30, top: 20, width: 20, height: 4 },
      { left: 10, top: 30, width: 20, height: 4 },
    ]);

    expect(service().quoteRects(layer, 'the anger of Achilles')).toEqual([
      { left: 0.1, top: 0.1, width: 0.4, height: 0.02 },
      { left: 0.1, top: 0.15, width: 0.2, height: 0.02 },
    ]);
  });

  it('gives nothing unless the words stand on the page exactly once', () => {
    const layer = layerWith([['the anger and'], ['the anger']]);
    withRangeRects([{ left: 10, top: 20, width: 50, height: 4 }]);

    expect(service().quoteRects(layer, 'the anger')).toBeNull();
    expect(service().quoteRects(layer, 'the sorrow')).toBeNull();
    expect(service().quoteRects(layer, '   ')).toBeNull();
  });

  it('gives nothing, and no error, for a scanned page with no text', () => {
    expect(service().quoteRects(layerWith([]), 'the anger')).toBeNull();
  });
});
