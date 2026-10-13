import { PdfPageGeometry, PdfTextItem, buildPdfPageText, rectsForPdfSpan } from './pdf-read-aloud-text';

/** An upright 600 x 800 page: PDF y grows upward, the viewport's downward. */
const PAGE: PdfPageGeometry = { width: 600, height: 800, transform: [1, 0, 0, -1, 0, 800] };

/** A run at (x, y from the TOP of the page), 12 units high. */
function run(str: string, x: number, fromTop: number, width = str.length * 6, hasEOL = false): PdfTextItem {
  return { str, transform: [12, 0, 0, 12, x, 800 - fromTop], width, height: 12, hasEOL };
}

describe('buildPdfPageText', () => {
  it('joins the runs of a line and the lines of a paragraph with single spaces', () => {
    const page = buildPdfPageText(
      [run('It was a bright', 50, 200), run('cold day', 150, 200, 48, true), run('in April.', 50, 216)],
      PAGE,
    );

    expect(page.text).toBe('It was a bright cold day in April.');
  });

  it('rejoins a word hyphenated across a line break', () => {
    const page = buildPdfPageText(
      [run('a matter of philo-', 50, 200, 108, true), run('sophy and of habit.', 50, 216)],
      PAGE,
    );

    expect(page.text).toBe('a matter of philosophy and of habit.');
  });

  it('keeps a real hyphen before a capital or a number', () => {
    const page = buildPdfPageText(
      [run('the years 1914-', 50, 200, 90, true), run('1918 and the Anglo-', 50, 216, 114, true), run('Saxon world.', 50, 232)],
      PAGE,
    );

    expect(page.text).toBe('the years 1914- 1918 and the Anglo- Saxon world.');
  });

  it('starts a new block after a paragraph gap and at a heading', () => {
    const heading: PdfTextItem = { str: 'Chapter One', transform: [20, 0, 0, 20, 50, 700], width: 120, height: 20 };
    const page = buildPdfPageText(
      [
        heading,
        run('First line of the first paragraph', 50, 140, 200, true),
        run('and its second line.', 50, 156, 120, true),
        run('and its third line.', 50, 172, 120, true),
        run('A new paragraph after a gap.', 50, 220),
      ],
      PAGE,
    );

    expect(page.text.split('\n')).toEqual([
      'Chapter One',
      'First line of the first paragraph and its second line. and its third line.',
      'A new paragraph after a gap.',
    ]);
  });

  it('does not take the uneven word heights of a scanned page for headings', () => {
    const word = (str: string, x: number, fromTop: number, height: number, hasEOL = false): PdfTextItem => ({
      str,
      transform: [height, 0, 0, height, x, 800 - fromTop],
      width: str.length * 6,
      height,
      hasEOL,
    });
    const page = buildPdfPageText(
      [
        word('necessary', 50, 200, 6.8),
        word('reductions', 110, 200, 6.8),
        word('had', 176, 200, 8, true),
        word('been', 50, 212, 7.5),
        word('made,', 80, 212, 8),
        word('did', 116, 212, 9.5, true),
        word('there', 50, 224, 6.7),
        word('seem.', 86, 224, 6.5),
      ],
      PAGE,
    );

    expect(page.text).toBe('necessary reductions had been made, did there seem.');
  });

  it('does not read a raised note number, or let it break the line', () => {
    const marker: PdfTextItem = { str: '17', transform: [7, 0, 0, 7, 182, 800 - 196], width: 8, height: 7 };
    const page = buildPdfPageText(
      [run('came into being with this civil', 50, 200, 132), marker, run('world in which the king ruled.', 194, 200)],
      PAGE,
    );

    expect(page.text).toBe('came into being with this civil world in which the king ruled.');
  });

  it('leaves out a page number in the margin but not a number in the text', () => {
    const page = buildPdfPageText(
      [run('In all there were', 50, 300, 102, true), run('42', 50, 316, 12, true), run('of them.', 50, 332), run('127', 290, 780)],
      PAGE,
    );

    expect(page.text).toBe('In all there were 42 of them.');
  });

  it('leaves out a running header that also stands on the neighbouring page', () => {
    const items = [run('THE MAGIC MOUNTAIN 214', 200, 30), run('He had not meant to stay.', 50, 200)];

    const alone = buildPdfPageText(items, PAGE);
    expect(alone.text).toContain('THE MAGIC MOUNTAIN');
    expect(alone.marginLines).toEqual(['the magic mountain']);

    const withNeighbour = buildPdfPageText(items, PAGE, ['the magic mountain']);
    expect(withNeighbour.text).toBe('He had not meant to stay.');
  });

  it('answers an empty page with no text', () => {
    expect(buildPdfPageText([], PAGE).text).toBe('');
    expect(buildPdfPageText([run('   ', 50, 200)], PAGE).text).toBe('');
  });
});

describe('rectsForPdfSpan', () => {
  const page = buildPdfPageText(
    [run('It was a bright', 50, 200, 90), run('cold day', 150, 200, 48, true), run('in April.', 50, 216, 54)],
    PAGE,
  );

  it('gives one rectangle per line, as fractions of the page', () => {
    const rects = rectsForPdfSpan(page, 0, page.text.length);

    expect(rects).toHaveLength(2);
    // First line: from x=50 to the end of "cold day" at 150+48, 12 high, its top 12 above the baseline at 200.
    expect(rects[0].left).toBeCloseTo(50 / 600);
    expect(rects[0].width).toBeCloseTo((198 - 50) / 600);
    expect(rects[0].top).toBeCloseTo((200 - 12) / 800);
    expect(rects[0].height).toBeCloseTo(12 / 800);
    expect(rects[1].top).toBeCloseTo((216 - 12) / 800);
  });

  it('covers only part of a run when the sentence starts inside it', () => {
    const start = page.text.indexOf('bright');
    const [rect] = rectsForPdfSpan(page, start, 'bright'.length);

    // "bright" is characters 9..15 of a 15-character run 90 wide starting at x=50.
    expect(rect.left).toBeCloseTo((50 + 90 * (9 / 15)) / 600);
    expect(rect.width).toBeCloseTo((90 * (6 / 15)) / 600);
  });

  it('answers a span with no runs with nothing', () => {
    expect(rectsForPdfSpan(page, 500, 10)).toEqual([]);
  });
});
