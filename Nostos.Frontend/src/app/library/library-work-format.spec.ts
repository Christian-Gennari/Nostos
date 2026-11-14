import { workFormatGlyphs, workFormatType, workFormatsTooltip } from './library-work-format';
import type { Book, EditionSummaryDto } from '../core/dtos/book.dtos';

const edition = (overrides: Record<string, unknown>) => overrides as unknown as EditionSummaryDto;

describe('library work formats', () => {
  it('names one edition format from its type, then its declared format, then its file', () => {
    expect(workFormatType(edition({ type: 'audiobook' }))).toBe('audio');
    expect(workFormatType(edition({ type: 'audio', format: null }))).toBe('audio');
    expect(workFormatType(edition({ type: 'physical' }))).toBe('physical');
    expect(workFormatType(edition({ type: 'ebook', fileName: 'paper.pdf' }))).toBe('pdf');
    expect(workFormatType(edition({ type: 'ebook', format: 'pdf' }))).toBe('pdf');
    expect(workFormatType(edition({ type: 'ebook', fileName: 'book.epub' }))).toBe('epub');
  });

  it('draws one glyph per format the work is available in, in the shelf’s fixed order', () => {
    const audiobook = { type: 'audiobook' } as unknown as Book;
    const glyphs = workFormatGlyphs({
      type: 'ebook',
      fileName: null,
      otherEditions: [audiobook, { type: 'ebook', fileName: 'notes.pdf' }],
    } as unknown as Book);

    expect(glyphs.map((glyph) => glyph.type)).toEqual(['audio', 'epub', 'pdf']);
    expect(glyphs.map((glyph) => glyph.icon)).toEqual(['headphones', 'book-open', 'file-text']);
    expect(glyphs.map((glyph) => glyph.label)).toEqual(['Audiobook', 'Ebook', 'PDF']);
  });

  it('names a single-format work once, and its tooltip reads as a sentence', () => {
    const single = { type: 'ebook', fileName: null, otherEditions: [] } as unknown as Book;

    expect(workFormatGlyphs(single).map((glyph) => glyph.type)).toEqual(['epub']);
    expect(workFormatsTooltip(single)).toBe('Available in Ebook');
  });

  it('survives a work whose editions list is missing', () => {
    expect(workFormatGlyphs({ type: 'physical' } as unknown as Book).map((g) => g.type)).toEqual([
      'physical',
    ]);
  });
});
