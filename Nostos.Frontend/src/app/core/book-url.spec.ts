import { bookFileDownloadUrl, bookFileUrl, bookTrackUrl } from './book-url';

describe('book file URLs', () => {
  it('points the file at the book, and its download at that file', () => {
    expect(bookFileUrl('b1')).toBe('/api/books/b1/file');
    expect(bookFileDownloadUrl('b1')).toBe('/api/books/b1/file/download');
  });

  it('names a track by its number', () => {
    expect(bookTrackUrl('b1', 3)).toBe('/api/books/b1/tracks/3');
  });

  it('escapes an id that would otherwise change the path', () => {
    expect(bookFileUrl('a/b?c')).toBe('/api/books/a%2Fb%3Fc/file');
    expect(bookTrackUrl('a b', 1)).toBe('/api/books/a%20b/tracks/1');
  });
});
