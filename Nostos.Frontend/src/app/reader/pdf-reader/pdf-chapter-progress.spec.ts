import { TocItem } from '../reader.interface';
import { chapterRangeAt } from './pdf-chapter-progress';

const entry = (label: string, target: number, children: TocItem[] = []): TocItem => ({
  label,
  target,
  children,
});

describe('chapterRangeAt (#1269)', () => {
  const outline = [
    entry('Preface', 3),
    entry('Part One', 10, [entry('Chapter 1', 10), entry('Chapter 2', 20)]),
    entry('Chapter 3', 31),
    entry('Afterword', 40),
  ];

  it('places a page in the deepest entry and measures through its pages', () => {
    expect(chapterRangeAt(outline, 24, 50)).toEqual({
      label: 'Chapter 2',
      start: 20,
      end: 31,
      fraction: 5 / 11,
    });
  });

  it('names a page shared by two entries after the later (deeper) one', () => {
    expect(chapterRangeAt(outline, 10, 50)?.label).toBe('Chapter 1');
  });

  it('starts low on a chapter’s first page and is full on its last', () => {
    expect(chapterRangeAt(outline, 20, 50)?.fraction).toBeCloseTo(1 / 11);
    expect(chapterRangeAt(outline, 30, 50)?.fraction).toBe(1);
  });

  it('fills a single-page chapter on its page', () => {
    const single = [entry('One', 1), entry('Plate', 5), entry('Two', 6)];
    expect(chapterRangeAt(single, 5, 10)).toMatchObject({ label: 'Plate', start: 5, end: 6, fraction: 1 });
  });

  it('runs the last chapter to the end of the document', () => {
    expect(chapterRangeAt(outline, 50, 50)).toMatchObject({ label: 'Afterword', start: 40, end: 51, fraction: 1 });
  });

  it('treats pages before the first entry as untitled front matter', () => {
    expect(chapterRangeAt(outline, 1, 50)).toMatchObject({ label: null, start: 1, end: 3, fraction: 0.5 });
  });

  it('is null for a PDF without an outline', () => {
    expect(chapterRangeAt([], 5, 50)).toBeNull();
  });
});
