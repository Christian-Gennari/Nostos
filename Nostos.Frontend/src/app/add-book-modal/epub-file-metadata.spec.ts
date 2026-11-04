import { readEpubFileMetadata, isEpubFile, MAX_EPUB_METADATA_BYTES } from './epub-file-metadata';
import { epubFile } from './testing/epub-file.fixture';

describe('readEpubFileMetadata (#1358)', () => {
  it('reads the title and the author from the package document', async () => {
    const file = await epubFile(
      'Walden.epub',
      '<dc:title>Walden; or, Life in the Woods</dc:title><dc:creator>Henry David Thoreau</dc:creator>',
    );

    await expect(readEpubFileMetadata(file)).resolves.toEqual({
      title: 'Walden; or, Life in the Woods',
      author: 'Henry David Thoreau',
    });
  });

  it('collapses whitespace and returns null for what the EPUB leaves out', async () => {
    const file = await epubFile('x.epub', '<dc:title>\n   Walden\n </dc:title>');

    await expect(readEpubFileMetadata(file)).resolves.toEqual({ title: 'Walden', author: null });
  });

  it('offers nothing, and does not throw, for a file that is not an EPUB', async () => {
    const file = new File(['not a zip'], 'broken.epub', { type: 'application/epub+zip' });

    await expect(readEpubFileMetadata(file)).resolves.toEqual({ title: null, author: null });
  });

  it('does not read an empty or oversized file', async () => {
    await expect(readEpubFileMetadata(new Blob([]))).resolves.toEqual({ title: null, author: null });

    const huge = new Blob(['x']);
    Object.defineProperty(huge, 'size', { value: MAX_EPUB_METADATA_BYTES + 1 });
    const read = vi.spyOn(huge, 'slice');
    await expect(readEpubFileMetadata(huge)).resolves.toEqual({ title: null, author: null });
    expect(read).not.toHaveBeenCalled();
  });

  it('recognises an EPUB by type or by extension', () => {
    expect(isEpubFile(new File([''], 'a.EPUB'))).toBe(true);
    expect(isEpubFile(new File([''], 'a', { type: 'application/epub+zip' }))).toBe(true);
    expect(isEpubFile(new File([''], 'a.pdf', { type: 'application/pdf' }))).toBe(false);
  });
});
