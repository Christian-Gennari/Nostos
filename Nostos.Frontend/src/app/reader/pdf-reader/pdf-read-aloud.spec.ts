import { PdfPageContent, PdfReadAloud, PdfReadAloudHost } from './pdf-read-aloud';
import { PdfPageGeometry, PdfTextItem } from './pdf-read-aloud-text';

const GEOMETRY: PdfPageGeometry = { width: 600, height: 800, transform: [1, 0, 0, -1, 0, 800] };

/** A page of body lines, one run per line, with an optional running header. */
function page(lines: string[], header?: string): PdfPageContent {
  const items: PdfTextItem[] = [];
  if (header) items.push({ str: header, transform: [10, 0, 0, 10, 200, 770], width: 150, height: 10 });
  lines.forEach((str, index) =>
    items.push({ str, transform: [12, 0, 0, 12, 50, 600 - index * 16], width: str.length * 6, height: 12, hasEOL: true }),
  );
  return { items, geometry: GEOMETRY };
}

function setup(pages: Array<PdfPageContent | null>, startPage = 1) {
  const calls = { turns: [] as number[], marks: [] as Array<{ page: number; rects: number }>, cleared: 0 };
  let current = startPage;
  const host: PdfReadAloudHost = {
    language: () => 'en',
    pageCount: () => pages.length,
    currentPage: () => current,
    loadPage: async (number) => pages[number - 1] ?? null,
    goToPage: (number) => {
      calls.turns.push(number);
      current = number;
    },
    showMark: (pageNumber, rects) => calls.marks.push({ page: pageNumber, rects: rects.length }),
    clearMark: () => calls.cleared++,
  };
  return { readAloud: new PdfReadAloud(host), calls };
}

describe('PdfReadAloud', () => {
  it('starts with the first sentence of the open page and marks it there', async () => {
    const { readAloud, calls } = setup(
      [page(['Page one has a sentence here.']), page(['The second page opens with this one.', 'And goes on with another sentence.'])],
      2,
    );

    await expect(readAloud.begin()).resolves.toEqual({ text: 'The second page opens with this one.' });
    expect(calls.marks.at(-1)).toEqual({ page: 2, rects: 1 });
    expect(calls.turns).toEqual([]);
    expect(readAloud.upcoming(2)).toEqual(['And goes on with another sentence.']);
  });

  it('turns the page when the current one runs out', async () => {
    const { readAloud, calls } = setup([
      page(['The first page has one sentence.']),
      page(['The second page has another one.']),
    ]);
    await readAloud.begin();

    await expect(readAloud.step(1)).resolves.toEqual({ text: 'The second page has another one.' });
    expect(calls.turns).toEqual([2]);
    expect(calls.marks.at(-1)!.page).toBe(2);
  });

  it('skips a page with nothing to read, such as a plate or a scan', async () => {
    const { readAloud, calls } = setup([
      page(['The first page has one sentence.']),
      page([]),
      null,
      page(['The fourth page carries on from there.']),
    ]);
    await readAloud.begin();

    await expect(readAloud.step(1)).resolves.toEqual({ text: 'The fourth page carries on from there.' });
    expect(calls.turns).toEqual([4]);
  });

  it('says a sentence that runs over the page break whole, and does not repeat its end', async () => {
    const { readAloud, calls } = setup([
      page(['A complete opening sentence stands here.', 'This one starts on the first page and']),
      page(['ends on the second, as sentences do.', 'Then a fresh sentence begins again.']),
    ]);
    await readAloud.begin();

    await expect(readAloud.step(1)).resolves.toEqual({
      text: 'This one starts on the first page and ends on the second, as sentences do.',
    });
    expect(calls.marks.at(-1)!.page).toBe(1);

    await expect(readAloud.step(1)).resolves.toEqual({ text: 'Then a fresh sentence begins again.' });
    expect(calls.turns).toEqual([2]);
  });

  it('rejoins a word hyphenated over the page break', async () => {
    const { readAloud } = setup([
      page(['He spoke at length about the philo-']),
      page(['sophy of the thing and then sat down.']),
    ]);

    await expect(readAloud.begin()).resolves.toEqual({
      text: 'He spoke at length about the philosophy of the thing and then sat down.',
    });
  });

  it('does not read a running header at the top of every page', async () => {
    const { readAloud } = setup([
      page(['The first page has one sentence.'], 'THE MAGIC MOUNTAIN 213'),
      page(['The second page has another one.'], 'THE MAGIC MOUNTAIN 214'),
    ]);

    await expect(readAloud.begin()).resolves.toEqual({ text: 'The first page has one sentence.' });
    await expect(readAloud.step(1)).resolves.toEqual({ text: 'The second page has another one.' });
  });

  it('does not read running headers that differ between left and right pages', async () => {
    const { readAloud } = setup(
      [
        page(['The first page has one sentence.'], '212 THE MAGIC MOUNTAIN'),
        page(['The second page has another one.'], 'SNOW 213'),
        page(['The third page has a third one.'], '214 THE MAGIC MOUNTAIN'),
        page(['The fourth page has the last one.'], 'SNOW 215'),
      ],
      3,
    );

    await expect(readAloud.begin()).resolves.toEqual({ text: 'The third page has a third one.' });
  });

  it('offers the next page’s sentences to fetch ahead before the page turns', async () => {
    const { readAloud } = setup([
      page(['A complete opening sentence stands here.', 'This one starts on the first page and']),
      page(['ends on the second, as sentences do.', 'Then a fresh sentence begins again.', 'And one more after that.']),
    ]);
    await readAloud.begin();
    // Reading the next page ahead is not awaited by the cursor.
    await new Promise((resolve) => setTimeout(resolve));

    // What is offered is what will be said: the sentence split by the break whole, and its end not again.
    expect(readAloud.upcoming(2)).toEqual([
      'This one starts on the first page and ends on the second, as sentences do.',
      'Then a fresh sentence begins again.',
    ]);
    await readAloud.step(1);
    expect(readAloud.upcoming(2)).toEqual(['Then a fresh sentence begins again.', 'And one more after that.']);
  });

  it('goes back into the previous page at its last sentence', async () => {
    const { readAloud, calls } = setup(
      [page(['One sentence on the first page.', 'And a second one right after it.']), page(['The second page starts here.'])],
      2,
    );
    await readAloud.begin();

    await expect(readAloud.step(-1)).resolves.toEqual({ text: 'And a second one right after it.' });
    expect(calls.turns).toEqual([1]);
  });

  it('answers null at the end of the document and for a document with no text', async () => {
    const ending = setup([page(['The only sentence in the whole document.'])]);
    await ending.readAloud.begin();
    await expect(ending.readAloud.step(1)).resolves.toBeNull();

    const scan = setup([page([]), page([])]);
    await expect(scan.readAloud.begin()).resolves.toBeNull();
  });

  it('clears the mark when reading ends, and reports the person moving only while reading', async () => {
    const { readAloud, calls } = setup([page(['The only sentence in the whole document.'])]);

    readAloud.notifyUserMoved();
    expect(readAloud.userMoved()).toBe(0);

    await readAloud.begin();
    readAloud.notifyUserMoved();
    expect(readAloud.userMoved()).toBe(1);

    readAloud.end();
    expect(calls.cleared).toBe(1);
    expect(readAloud.active).toBe(false);
  });
});
