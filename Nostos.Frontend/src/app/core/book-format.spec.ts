import { BOOK_FORMAT_LABEL, bookFormatConcept } from './book-format';

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

  it('has a short label for every concept', () => {
    expect(BOOK_FORMAT_LABEL).toEqual({ audiobook: 'Audiobook', ebook: 'eBook', pdf: 'PDF', physical: 'Physical' });
  });
});
