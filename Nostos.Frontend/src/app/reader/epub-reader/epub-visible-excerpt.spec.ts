import {
  visibleEpubExcerpt,
  VisibleEpubContent,
} from './epub-visible-excerpt';

describe('visibleEpubExcerpt', () => {
  const viewport = new DOMRect(0, 0, 600, 400);

  function makeContent(
    text: string,
    firstVisibleOffset = 0,
  ): VisibleEpubContent {
    const doc = document.implementation.createHTMLDocument('chapter');
    const paragraph = doc.createElement('p');
    paragraph.textContent = text;
    doc.body.appendChild(paragraph);

    const frame = document.createElement('iframe');

    Object.defineProperty(frame, 'getBoundingClientRect', {
      value: () => new DOMRect(0, 0, 600, 400),
    });

    const originalCreateRange = doc.createRange.bind(doc);

    vi.spyOn(doc, 'createRange').mockImplementation(() => {
      const range = originalCreateRange();
      let start = 0;
      let end = text.length;

      const originalStart = range.setStart.bind(range);
      const originalEnd = range.setEnd.bind(range);

      vi.spyOn(range, 'setStart').mockImplementation((node, offset) => {
        start = offset;
        originalStart(node, offset);
      });

      vi.spyOn(range, 'setEnd').mockImplementation((node, offset) => {
        end = offset;
        originalEnd(node, offset);
      });

      // jsdom ranges have no layout methods to spy on; define one.
      (range as unknown as { getClientRects: () => DOMRectList }).getClientRects = () => {
        const visible = end > firstVisibleOffset &&
          (end - start > 1 || start >= firstVisibleOffset);

        const rect = visible
          ? new DOMRect(12, 12, 400, 20)
          : new DOMRect(700, 12, 100, 20);

        return [rect] as unknown as DOMRectList;
      };

      return range;
    });

    return {
      document: doc,
      window: {
        frameElement: frame,
      } as unknown as Window,
    };
  }

  afterEach(() => vi.restoreAllMocks());

  it('extracts and normalizes text from the visible page', () => {
    const content = makeContent(
      'The evening was quiet.   The last light\nrested on the mountains.',
    );

    expect(visibleEpubExcerpt([content], viewport)).toBe(
      'The evening was quiet. The last light rested on the mountains.',
    );
  });

  it('starts at the next full sentence when the page begins mid-sentence', () => {
    const text =
      'The first sentence began on the previous page and ends here. ' +
      'The next sentence begins on this page and provides enough ' +
      'context to identify the matching audiobook passage accurately.';

    const content = makeContent(text, 30);

    expect(visibleEpubExcerpt([content], viewport)).toBe(
      'The next sentence begins on this page and provides enough ' +
      'context to identify the matching audiobook passage accurately.',
    );
  });

  it('uses the first visible character when no sentence boundary exists', () => {
    const text =
      'Earlier invisible text and then the passage continues without ' +
      'a new sentence boundary or punctuation for the rest of the page';

    const content = makeContent(text, 32);
    const result = visibleEpubExcerpt([content], viewport);

    expect(result).toBe(text.slice(32));
  });

  it('does not fall back to chapter text on an image-only page', () => {
    const doc = document.implementation.createHTMLDocument('cover');
    doc.body.innerHTML = '<img src="cover.jpg" alt="Cover">';

    const frame = document.createElement('iframe');

    expect(visibleEpubExcerpt([{
      document: doc,
      window: { frameElement: frame } as unknown as Window,
    }], viewport)).toBeNull();
  });

  it('does not use off-screen chapter text', () => {
    const content = makeContent(
      'This text belongs to a different page.',
      1000,
    );

    expect(visibleEpubExcerpt([content], viewport)).toBeNull();
  });

  it('returns null before the rendition has usable geometry', () => {
    const content = makeContent('Some readable text.');

    expect(visibleEpubExcerpt([content], null)).toBeNull();
    expect(visibleEpubExcerpt([], viewport)).toBeNull();
  });

  it('caps the submitted excerpt at 280 characters', () => {
    const content = makeContent(
      'A fairly long reading passage with multiple words. '.repeat(20),
    );

    const result = visibleEpubExcerpt([content], viewport);

    expect(result).toBeTruthy();
    expect(result!.length).toBeLessThanOrEqual(280);
    expect(result).not.toMatch(/\s$/);
  });
});
