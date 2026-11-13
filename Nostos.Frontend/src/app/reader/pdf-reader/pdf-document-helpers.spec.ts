import {
  buildPdfPassageDraft,
  detectPdfTextCapability,
  extractPdfPageLabels,
  extractPdfVisibleExcerpt,
  isSubstantialPageText,
  mapPdfOutline,
} from './pdf-document-helpers';

describe('pdf-document-helpers', () => {
  it('detects available, unavailable, and stale documents for text capability', async () => {
    const textDoc = {
      getPage: vi.fn(async (n: number) => ({
        getTextContent: async () => ({ items: n === 4 ? [{ str: 'Hello' }] : [] }),
      })),
    };
    expect(await detectPdfTextCapability(textDoc, 10, () => true)).toBe('available');

    const scannedDoc = {
      getPage: vi.fn(async () => ({
        getTextContent: async () => ({ items: [{ str: '   ' }] }),
      })),
    };
    expect(await detectPdfTextCapability(scannedDoc, 5, () => true)).toBe('unavailable');

    let current = true;
    const staleDoc = {
      getPage: vi.fn(async () => ({
        getTextContent: async () => {
          current = false;
          return { items: [{ str: 'Stale' }] };
        },
      })),
    };
    expect(await detectPdfTextCapability(staleDoc, 3, () => current)).toBeNull();
  });

  it('extracts page labels and trims blanks to null', async () => {
    const doc = {
      getPageLabels: vi.fn(async () => [' i ', '  ', '1', 42]),
    };
    expect(await extractPdfPageLabels(doc)).toEqual(['i', null, '1', null]);
  });

  it('extracts visible excerpt with lookahead past sparse pages', async () => {
    const body = 'A'.repeat(120);
    const doc = {
      getPage: vi.fn(async (page: number) => ({
        getTextContent: async () => ({
          items: [{ str: page === 1 ? 'Plate I' : body }],
        }),
      })),
    };

    expect(isSubstantialPageText(body)).toBe(true);
    expect(await extractPdfVisibleExcerpt(doc, 1, 5, () => true)).toBe(body);
  });

  it('maps embedded PDF outline with named destinations and fallbacks', async () => {
    const doc = {
      getDestination: vi.fn(async () => [{ num: 5 }]),
      getPageIndex: vi.fn(async (ref: { num: number }) => ref.num - 1),
    };
    const toc = await mapPdfOutline(
      [
        { title: 'Preface', dest: 'preface', items: [] },
        { title: 'Broken', dest: [{ num: 99 }], items: [] },
      ],
      doc,
    );
    expect(toc[0]).toEqual({ label: 'Preface', target: 5, children: [] });
    expect(toc[1]).toEqual({ label: 'Broken', target: 99, children: [] });
  });

  it('builds a passage draft across PDF pages', async () => {
    const snippets: Record<number, string> = {
      1: 'Opening context on the first page before the selection begins.',
      2: 'Body text for page two discusses the main argument in detail.',
      3: 'Concluding thoughts on page three wrap up the selected passage.',
      4: 'Trailing context on page four after the passage.',
    };
    const doc = {
      getPage: vi.fn(async (pageNumber: number) => ({
        getTextContent: async () => ({
          items: [
            { str: `Header`, transform: [1, 0, 0, 1, 50, 760], width: 60, height: 10 },
            { str: snippets[pageNumber] ?? '', transform: [1, 0, 0, 1, 50, 400], width: 200, height: 12 },
          ],
        }),
        getViewport: () => ({ width: 600, height: 800, transform: [1, 0, 0, -1, 0, 800] }),
      })),
    };

    const draft = await buildPdfPassageDraft({
      a: { format: 'pdf', start: '2', end: '2', excerpt: '', label: 'p. 2' },
      b: { format: 'pdf', start: '3', end: '3', excerpt: '', label: 'p. 3' },
      pdfDoc: doc,
      totalPages: 10,
      isCurrent: () => true,
      pageName: (p) => String(p),
      chapterTitleAt: () => 'Chapter One',
    });

    expect(draft).toMatchObject({
      anchorKind: 'pdf_page',
      start: '2',
      end: '3',
      title: 'Chapter One',
    });
    expect(draft?.text).toContain('Body text for page two');
    expect(draft?.text).toContain('Concluding thoughts on page three');
  });
});
