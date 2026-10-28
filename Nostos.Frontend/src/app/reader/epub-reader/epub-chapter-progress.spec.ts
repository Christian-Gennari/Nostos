import { TocItem } from '../reader.interface';
import {
  chapterFractionFromLocations,
  chapterFractionFromPages,
  chapterLocationBounds,
  chapterRangeForSpine,
  epubChapterRanges,
  spineIndexFromCfi,
} from './epub-chapter-progress';

const entry = (label: string, target: string, children: TocItem[] = []): TocItem => ({
  label,
  target,
  children,
});

// cover, title, ch1, ch2a, ch2b (untitled continuation), ch3 (two fragments)
const SPINE = ['cover.xhtml', 'title.xhtml', 'ch1.xhtml', 'ch2a.xhtml', 'ch2b.xhtml', 'ch3.xhtml'];
const TOC = [
  entry('Part One', 'ch1.xhtml', [entry('Chapter 1', 'ch1.xhtml'), entry('Chapter 2', 'ch2a.xhtml')]),
  entry('Chapter 3', 'ch3.xhtml#start', [entry('Section A', 'ch3.xhtml#a'), entry('Section B', 'ch3.xhtml#b')]),
];

describe('epubChapterRanges (#1269)', () => {
  const ranges = epubChapterRanges(TOC, SPINE);

  it('turns the TOC into spine ranges, with front matter first', () => {
    expect(ranges).toEqual([
      { label: null, startSpine: 0, endSpine: 2 },
      { label: 'Part One', startSpine: 2, endSpine: 3 },
      { label: 'Chapter 2', startSpine: 3, endSpine: 5 },
      { label: 'Chapter 3', startSpine: 5, endSpine: 6 },
    ]);
  });

  it('groups an untitled section with the chapter before it', () => {
    expect(chapterRangeForSpine(ranges, 4)?.label).toBe('Chapter 2');
  });

  it('collapses several entries inside one file into one range (accepted limit)', () => {
    expect(chapterRangeForSpine(ranges, 5)).toEqual({ label: 'Chapter 3', startSpine: 5, endSpine: 6 });
  });

  it('matches TOC hrefs that spell the path differently', () => {
    expect(epubChapterRanges([entry('One', './ch1.xhtml')], SPINE)[1]).toMatchObject({ startSpine: 2 });
  });

  it('is empty when the TOC places nothing', () => {
    expect(epubChapterRanges([], SPINE)).toEqual([]);
    expect(epubChapterRanges([entry('Elsewhere', 'missing.xhtml')], SPINE)).toEqual([]);
    expect(chapterRangeForSpine([], 2)).toBeNull();
  });
});

describe('spineIndexFromCfi', () => {
  it('reads the spine step', () => {
    expect(spineIndexFromCfi('epubcfi(/6/30!/4/1342/1:0)')).toBe(14);
    expect(spineIndexFromCfi('epubcfi(/6/4[ch2]!/4/2/1:0)')).toBe(1);
    expect(spineIndexFromCfi('not a cfi')).toBeNull();
  });
});

describe('chapter fractions', () => {
  const ranges = epubChapterRanges(TOC, SPINE);
  // Two locations in the front matter, three in ch1, four across ch2a/ch2b, one in ch3.
  const cfis = [
    'epubcfi(/6/2!/4/2/1:0)',
    'epubcfi(/6/4!/4/2/1:0)',
    'epubcfi(/6/6!/4/2/1:0)',
    'epubcfi(/6/6!/4/4/1:0)',
    'epubcfi(/6/6!/4/6/1:0)',
    'epubcfi(/6/8!/4/2/1:0)',
    'epubcfi(/6/8!/4/4/1:0)',
    'epubcfi(/6/10!/4/2/1:0)',
    'epubcfi(/6/10!/4/4/1:0)',
    'epubcfi(/6/12!/4/2/1:0)',
  ];

  it('finds each chapter’s locations in one pass', () => {
    expect(chapterLocationBounds(ranges, cfis)).toEqual([
      { first: 0, end: 2 },
      { first: 2, end: 5 },
      { first: 5, end: 9 },
      { first: 9, end: 10 },
    ]);
  });

  it('is 0 where the chapter starts and 1 once the page shows its last location', () => {
    const chapter2 = { first: 5, end: 9 };
    expect(chapterFractionFromLocations(chapter2, 5, 6)).toBe(0);
    expect(chapterFractionFromLocations(chapter2, 6, 7)).toBe(0.25);
    expect(chapterFractionFromLocations(chapter2, 7, 8)).toBe(1);
    expect(chapterFractionFromLocations({ first: 3, end: 3 }, 3, null)).toBe(0);
  });

  it('estimates from the section’s pages before locations exist', () => {
    const chapter2 = ranges[2]; // two sections
    expect(chapterFractionFromPages(chapter2, 3, 1, 4)).toBe(0);
    expect(chapterFractionFromPages(chapter2, 3, 3, 4)).toBe(0.25);
    expect(chapterFractionFromPages(chapter2, 4, 1, 4)).toBe(0.5);
    expect(chapterFractionFromPages(chapter2, 4, 4, 4)).toBe(1);
    // No pagination reported yet: the start of the section.
    expect(chapterFractionFromPages(chapter2, 4, null, null)).toBe(0.5);
  });
});
