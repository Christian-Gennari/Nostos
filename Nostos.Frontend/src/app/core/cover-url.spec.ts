import { coverImageUrl } from './cover-url';

describe('coverImageUrl', () => {
  const book = { coverUrl: '/api/books/b1/cover', coverVersion: 'abc123' };

  it('is null without a cover', () => {
    expect(coverImageUrl(null)).toBeNull();
    expect(coverImageUrl({ coverUrl: null, coverVersion: 'abc123' })).toBeNull();
  });

  it('names the cover version so the image can be cached for good', () => {
    expect(coverImageUrl(book)).toBe('/api/books/b1/cover?v=abc123');
  });

  it('asks for a thumbnail of the given width', () => {
    expect(coverImageUrl(book, 320)).toBe('/api/books/b1/cover/thumbnail?width=320&v=abc123');
  });

  it('falls back to the revalidated URL when the book carries no version', () => {
    expect(coverImageUrl({ coverUrl: '/api/books/b1/cover' })).toBe('/api/books/b1/cover');
    expect(coverImageUrl({ coverUrl: '/api/books/b1/cover', coverVersion: null }, 640)).toBe(
      '/api/books/b1/cover/thumbnail?width=640',
    );
  });
});
