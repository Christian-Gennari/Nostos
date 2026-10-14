import { EpubReadAloud, EpubReadAloudHost, ReadAloudEpubContent } from './epub-read-aloud';

/**
 * The cursor against real documents. Layout does not exist here, so "which
 * sentence is on the visible page" is the browser check's job; these tests pin
 * the order sentences are read in, section changes, the mark and page turns.
 */
function section(index: number, html: string): ReadAloudEpubContent {
  const document = new DOMParser().parseFromString(`<html><body>${html}</body></html>`, 'text/html');
  return {
    document,
    sectionIndex: index,
    cfiFromRange: (range: Range) => `cfi(${index}:${range.toString()})`,
  };
}

function setup(sections: ReadAloudEpubContent[], options: { visible?: boolean | null } = {}) {
  let shown = 0;
  const calls = {
    displayedSections: [] as number[],
    displayedCfis: [] as string[],
    marks: [] as string[],
    cleared: 0,
  };
  const host: EpubReadAloudHost = {
    language: () => 'en',
    renderedContents: () => [sections[shown]],
    readingViewRect: () => null,
    spineLength: () => sections.length,
    displaySection: async (index) => {
      calls.displayedSections.push(index);
      if (!sections[index]) return false;
      shown = index;
      return true;
    },
    displayCfi: async (cfi) => {
      calls.displayedCfis.push(cfi);
    },
    isRangeVisible: () => (options.visible === undefined ? true : options.visible),
    showMark: (cfi) => calls.marks.push(cfi),
    clearMark: () => calls.cleared++,
  };
  return { readAloud: new EpubReadAloud(host), calls };
}

const CHAPTER_ONE =
  '<h1>Chapter One</h1><p>It was a bright cold day in April. The clocks were striking thirteen, all of them.</p>';
const CHAPTER_TWO = '<p>The hallway smelt of boiled cabbage and old rag mats.</p>';

describe('EpubReadAloud', () => {
  it('starts with the first sentence of the rendered section and marks it', async () => {
    const { readAloud, calls } = setup([section(0, CHAPTER_ONE)]);

    await expect(readAloud.begin()).resolves.toEqual({ text: 'Chapter One' });
    expect(calls.marks).toEqual(['cfi(0:Chapter One)']);
    expect(readAloud.active).toBe(true);
  });

  it('reads on sentence by sentence and knows what comes next', async () => {
    const { readAloud } = setup([section(0, CHAPTER_ONE)]);
    await readAloud.begin();

    expect(readAloud.upcoming(3)).toEqual([
      'It was a bright cold day in April.',
      'The clocks were striking thirteen, all of them.',
    ]);
    await expect(readAloud.step(1)).resolves.toEqual({ text: 'It was a bright cold day in April.' });
    await expect(readAloud.step(-1)).resolves.toEqual({ text: 'Chapter One' });
  });

  it('opens the next section when the current one runs out', async () => {
    const { readAloud, calls } = setup([section(0, CHAPTER_ONE), section(1, CHAPTER_TWO)]);
    await readAloud.begin();
    await readAloud.step(1);
    await readAloud.step(1);

    await expect(readAloud.step(1)).resolves.toEqual({
      text: 'The hallway smelt of boiled cabbage and old rag mats.',
    });
    expect(calls.displayedSections).toEqual([1]);
  });

  it('skips a section with nothing to say, such as a cover', async () => {
    const { readAloud, calls } = setup([
      section(0, '<p>The end of the first part of the story.</p>'),
      section(1, '<img alt="" src="plate.png">'),
      section(2, CHAPTER_TWO),
    ]);
    await readAloud.begin();

    await expect(readAloud.step(1)).resolves.toEqual({
      text: 'The hallway smelt of boiled cabbage and old rag mats.',
    });
    expect(calls.displayedSections).toEqual([1, 2]);
  });

  it('answers null at the end of the book', async () => {
    const { readAloud } = setup([section(0, CHAPTER_TWO)]);
    await readAloud.begin();

    await expect(readAloud.step(1)).resolves.toBeNull();
  });

  it('goes back into the previous section at its last sentence', async () => {
    const { readAloud, calls } = setup([section(0, CHAPTER_ONE), section(1, CHAPTER_TWO)]);
    await readAloud.begin();
    await readAloud.step(1);
    await readAloud.step(1);
    await readAloud.step(1);

    await expect(readAloud.step(-1)).resolves.toEqual({
      text: 'The clocks were striking thirteen, all of them.',
    });
    expect(calls.displayedSections).toEqual([1, 0]);
  });

  it('turns the page only when the sentence is not on it', async () => {
    const shown = setup([section(0, CHAPTER_ONE)], { visible: true });
    await shown.readAloud.begin();
    expect(shown.calls.displayedCfis).toEqual([]);

    // Starting never turns the page: the person is already looking at it.
    const hidden = setup([section(0, CHAPTER_ONE)], { visible: false });
    await hidden.readAloud.begin();
    expect(hidden.calls.displayedCfis).toEqual([]);
    await hidden.readAloud.step(1);
    expect(hidden.calls.displayedCfis).toEqual(['cfi(0:It was a bright cold day in April.)']);

    const unmeasured = setup([section(0, CHAPTER_ONE)], { visible: null });
    await unmeasured.readAloud.begin();
    await unmeasured.readAloud.step(1);
    expect(unmeasured.calls.displayedCfis).toEqual([]);
  });

  it('clears the mark and forgets the cursor when reading ends', async () => {
    const { readAloud, calls } = setup([section(0, CHAPTER_ONE)]);
    await readAloud.begin();

    readAloud.end();

    expect(calls.cleared).toBe(1);
    expect(readAloud.active).toBe(false);
    await expect(readAloud.step(1)).resolves.toBeNull();
  });

  it('reports the person moving the book only while it is being read', async () => {
    const { readAloud } = setup([section(0, CHAPTER_ONE)]);

    readAloud.notifyUserMoved();
    expect(readAloud.userMoved()).toBe(0);

    await readAloud.begin();
    readAloud.notifyUserMoved();
    expect(readAloud.userMoved()).toBe(1);
  });
});
