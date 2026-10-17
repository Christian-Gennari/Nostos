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

  it('keeps a line whole when an OCR layer marks every word as ending a line', () => {
    // Each word sits a little off the one before, as on a scan that is not
    // quite straight. Read as one-word lines, that jitter would pass for the
    // line spacing and every word would become a block of its own.
    const word = (str: string, x: number, fromTop: number): PdfTextItem =>
      run(str, x, fromTop, str.length * 6, true);
    const page = buildPdfPageText(
      [
        word('tragic', 50, 200),
        word('view', 92, 199.7),
        word('of', 122, 199.5),
        word('current', 140, 199.3),
        word('societies;', 50, 216),
        word('our', 116, 216.2),
        word('societies', 140, 216.4),
        word('fail.', 200, 216.6),
      ],
      PAGE,
    );

    expect(page.text).toBe('tragic view of current societies; our societies fail.');
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
    expect(alone.marginLines.map((line) => line.key)).toContain('themagicmountain');

    const withNeighbour = buildPdfPageText(items, PAGE, [{ key: 'themagicmountain', size: 1, at: 30 / 800 - 12 / 800 }]);
    expect(withNeighbour.text).toBe('He had not meant to stay.');
  });

  it('finds the running header of a scan, set well inside the page and misread here and there', () => {
    const neighbour = buildPdfPageText(
      [run('66 GENEALOGY OF MORALS', 120, 110), run('the concept of revenge does not.', 50, 140)],
      PAGE,
    );
    const page = buildPdfPageText(
      [run('68 OENEALOOY OF MORALS', 120, 110), run('Perhaps in those days it was so.', 50, 140)],
      PAGE,
      neighbour.marginLines,
    );

    expect(page.text).toBe('Perhaps in those days it was so.');
  });

  it('reads a chapter title even when the pages after it repeat it as their header', () => {
    const title: PdfTextItem = { str: 'The End', transform: [20, 0, 0, 20, 200, 800 - 160], width: 80, height: 20 };
    const page = buildPdfPageText(
      [title, run('Lying on his back, he opened his eyes.', 50, 220)],
      PAGE,
      // The header on the next page, set in the text's size.
      [{ key: 'theend', size: 1, at: 0.05 }],
    );

    expect(page.text.split('\n')).toEqual(['The End', 'Lying on his back, he opened his eyes.']);
  });

  it('keeps the dated entries of a diary, which are not one running header', () => {
    const neighbour = buildPdfPageText([run('January 22nd', 250, 60), run('Some text.', 50, 200)], PAGE);
    const page = buildPdfPageText(
      [run('January 23rd', 250, 60), run('Today was long.', 50, 200)],
      PAGE,
      neighbour.marginLines,
    );

    expect(page.text).toContain('January 23rd');
  });

  it('leaves out a page number set just outside the margin, but not a year under a title', () => {
    const page = buildPdfPageText([run('Some text on the page.', 50, 300), run('6', 50, 745)], PAGE);
    expect(page.text).toBe('Some text on the page.');

    const partTitle = buildPdfPageText([run("Brodie's Report", 200, 150), run('(1970)', 260, 180)], PAGE);
    expect(partTitle.text).toContain('(1970)');
  });

  it('reads small capitals and old-style figures a font keeps in private code points', () => {
    const smallCaps = 'iberius'.replace(/./g, (ch) => String.fromCharCode(0xf700 + ch.charCodeAt(0)));
    const oldStyle = String.fromCharCode(0xf731);
    const page = buildPdfPageText([run(`The history of a T${smallCaps} in Part ${oldStyle}.`, 50, 300)], PAGE);

    expect(page.text).toBe('The history of a Tiberius in Part 1.');
  });

  it('closes up letter-spaced text so it is not spelled out', () => {
    const heading = buildPdfPageText([run('C O N T E N T S', 200, 300, 120, true), run('The opening.', 50, 340)], PAGE);
    expect(heading.text).toContain('CONTENTS');

    // Justified by spacing the letters: each word its own run, the word space a run of its own.
    const line = buildPdfPageText(
      [run('b u t', 50, 300, 20), run(' ', 70, 300, 6), run('r a t h e r', 76, 300, 34), run(' ', 110, 300, 6), run('d e p th s ,', 116, 300, 40)],
      PAGE,
    );
    expect(line.text).toBe('but rather depths,');
  });

  it('puts a drop cap back on the first word of its paragraph', () => {
    const cap: PdfTextItem = { str: 'T', transform: [43, 0, 0, 43, 50, 800 - 260], width: 26, height: 43 };
    const page = buildPdfPageText(
      [
        run('January 18th', 250, 120, 72, true),
        run('A heading', 50, 180, 54, true),
        run('here are some turns of phrase', 78, 236, 180, true),
        run('in this book.', 78, 252),
        cap,
      ],
      PAGE,
    );

    expect(page.text.split('\n').at(-1)).toBe('There are some turns of phrase in this book.');
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
