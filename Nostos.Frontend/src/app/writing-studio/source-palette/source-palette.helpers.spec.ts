import {
  PaletteSource,
  correctQuery,
  editDistance,
  fromSearchHit,
  highlightSegments,
  queryWords,
  rankSources,
  readRecentSources,
  rememberRecentSource,
  sourceByline,
  vocabularyOf,
} from './source-palette.helpers';
import { isPaletteQuery } from '../../ui/markdown-editor/source-suggestion.extension';

function source(overrides: Partial<PaletteSource>): PaletteSource {
  return {
    id: overrides.id ?? 'n',
    bookId: 'b',
    kind: 'note',
    bookTitle: 'The World as Will and Representation',
    bookAuthor: 'Arthur Schopenhauer',
    selectedText: null,
    content: '',
    lead: '',
    note: null,
    kept: false,
    ...overrides,
  };
}

describe('the @ palette helpers (issue #1039)', () => {
  it('builds a row from a search hit: the quotation leads, the note follows', () => {
    const row = fromSearchHit(
      {
        id: 'n1',
        bookId: 'b1',
        bookTitle: 'Thus Spoke Zarathustra',
        bookAuthor: 'Friedrich Nietzsche',
        content: 'Nietzsche answers the will.',
        selectedText: 'Willing liberates.',
        snippet: null,
        topicNames: [],
        createdAt: '',
      },
      new Set(['n1']),
    );
    expect(row.lead).toBe('Willing liberates.');
    expect(row.note).toBe('Nietzsche answers the will.');
    expect(row.kept).toBe(true);
    expect(sourceByline(row)).toBe('Friedrich Nietzsche · Thus Spoke Zarathustra');
  });

  it('puts the row holding every word first, then the direct quote over the passage', () => {
    const passage = source({ id: 'p', kind: 'passage', lead: 'the will to live', kept: true });
    const quote = source({ id: 'q', lead: 'Every true act of his will', selectedText: 'x' });
    const other = source({ id: 'o', lead: 'Life swings like a pendulum', bookTitle: 'Studies in Pessimism' });
    const ranked = rankSources([passage, other, quote], queryWords('schopenhauer will'));
    expect(ranked.map((s) => s.id)).toEqual(['q', 'p', 'o']);
  });

  it('corrects a misspelt author towards the library, and leaves real words alone', () => {
    const vocab = vocabularyOf(['Arthur Schopenhauer', 'The World as Will and Representation']);
    expect(correctQuery('schopenhaur will', vocab)).toBe('schopenhauer will');
    expect(correctQuery('schopenhauer', vocab)).toBe('schopenhauer');
    expect(correctQuery('kierkegaard', vocab)).toBe('kierkegaard');
  });

  it('measures edit distance with transpositions', () => {
    expect(editDistance('will', 'will')).toBe(0);
    expect(editDistance('wlil', 'will')).toBe(1);
    expect(editDistance('schopenhaur', 'schopenhauer')).toBe(1);
  });

  it('marks the matched words without changing the text', () => {
    const segs = highlightSegments('Willing liberates the will', ['will']);
    expect(segs.map((s) => s.text).join('')).toBe('Willing liberates the will');
    expect(segs.filter((s) => s.match).map((s) => s.text)).toEqual(['Will', 'will']);
  });

  it('remembers recent sources, newest first, without duplicates', () => {
    localStorage.clear();
    rememberRecentSource(source({ id: 'a', lead: 'A' }));
    rememberRecentSource(source({ id: 'b', lead: 'B' }));
    rememberRecentSource(source({ id: 'a', lead: 'A' }));
    expect(readRecentSources().map((s) => s.id)).toEqual(['a', 'b']);
  });

  it('reads a query as a query, and running prose as prose', () => {
    expect(isPaletteQuery('')).toBe(true);
    expect(isPaletteQuery('arthur schopenhauer will')).toBe(true);
    expect(isPaletteQuery(' leading space')).toBe(false);
    expect(isPaletteQuery('two  spaces')).toBe(false);
    expect(isPaletteQuery('one two three four five six seven eight nine')).toBe(false);
  });

  it('names the edition a note was made in only when its book has several (#1302)', () => {
    const hit = (editionCount: number) => fromSearchHit({
      id: 'n', bookId: 'b', bookTitle: 'The Magic Mountain', bookAuthor: 'Thomas Mann', content: 'x',
      selectedText: null, snippet: null, topicNames: [], createdAt: '', bookFormat: 'audiobook', editionCount,
    }, new Set());
    expect(sourceByline(hit(2))).toBe('Thomas Mann · The Magic Mountain · Audiobook');
    expect(sourceByline(hit(1))).toBe('Thomas Mann · The Magic Mountain');
  });
});
