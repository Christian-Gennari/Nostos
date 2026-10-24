import { BOOK_FORMAT_LABEL, bookFormatConcept, formatOf } from './book-format';

describe('bookFormatConcept', () => {
  it('names each format by its concept', () => {
    expect(bookFormatConcept('audiobook')).toBe('audiobook');
    expect(bookFormatConcept('Audio')).toBe('audiobook');
    expect(bookFormatConcept('physical')).toBe('physical');
    expect(bookFormatConcept('ebook', 'moby-dick.epub')).toBe('ebook');
    expect(bookFormatConcept('ebook', 'Moby-Dick.PDF')).toBe('pdf');
    expect(bookFormatConcept('ebook', null)).toBe('ebook');
    expect(bookFormatConcept(undefined)).toBe('ebook');
  });

  it('names one format the same way everywhere', () => {
    expect(formatOf({ type: 'audiobook' })).toEqual({ format: 'Audiobook', concept: 'audiobook' });
    expect(formatOf({ type: 'physical' })).toEqual({ format: 'Physical', concept: 'physical' });
    expect(formatOf({ type: 'ebook' })).toEqual({ format: 'Ebook', concept: 'ebook' });
    expect(formatOf({ type: 'ebook', format: 'ebook' })).toEqual({ format: 'Ebook', concept: 'ebook' });
    expect(formatOf({ type: 'ebook', fileName: 'a.epub' })).toEqual({ format: 'Ebook', concept: 'ebook' });
    expect(formatOf({ type: 'ebook', fileName: 'a.PDF' })).toEqual({ format: 'PDF', concept: 'pdf' });
    expect(formatOf({ type: 'ebook', fileName: 'a.mobi' })).toEqual({ format: 'Ebook', concept: 'ebook' });
  });

  it('has a short label for every concept', () => {
    expect(BOOK_FORMAT_LABEL).toEqual({ audiobook: 'Audiobook', ebook: 'Ebook', pdf: 'PDF', physical: 'Physical' });
  });
});
