import { closingOf, countWords, excerptOf, readPassageMark, writePassageMark } from './passage-mark';

describe('passage marks (issue #1014)', () => {
  afterEach(() => localStorage.clear());

  it('keeps a waiting mark per book until it is cleared', () => {
    const mark = { format: 'epub' as const, start: 'epubcfi(/6/4!/4/2/1:0)', end: 'epubcfi(/6/4!/4/2/1:9)', excerpt: 'Imagine', label: 'Chapter V' };
    writePassageMark('book-a', mark);
    expect(readPassageMark('book-a')).toEqual(mark);
    expect(readPassageMark('book-b')).toBeNull();
    writePassageMark('book-a', null);
    expect(readPassageMark('book-a')).toBeNull();
  });

  it('reads nothing from a damaged or foreign value', () => {
    localStorage.setItem('nostos.reader.passageMark.book-a', '{nope');
    expect(readPassageMark('book-a')).toBeNull();
    localStorage.setItem('nostos.reader.passageMark.book-a', JSON.stringify({ format: 'scroll', start: 'a', end: 'b' }));
    expect(readPassageMark('book-a')).toBeNull();
  });

  it('cuts openings and closings at a word', () => {
    const text = 'Imagine, then, that it takes place in Spain, in Seville, in the most terrible time of the Inquisition';
    expect(excerptOf(text, 30)).toBe('Imagine, then, that it takes…');
    expect(closingOf(text, 30)).toBe('…time of the Inquisition');
    expect(excerptOf('Short.', 30)).toBe('Short.');
    expect(countWords('  one two\n\nthree ')).toBe(3);
    expect(countWords(null)).toBe(0);
  });
});
