import { Note, importedNoteLocation, noteNavigationTarget } from './note.dtos';

const note = (overrides: Partial<Note> = {}): Note => ({
  id: 'note-1',
  bookId: 'book-1',
  content: '',
  createdAt: '2026-09-21T10:00:00Z',
  ...overrides,
});

describe('noteNavigationTarget (issue #324)', () => {
  it('keeps the established cfiRange navigation contract first', () => {
    expect(
      noteNavigationTarget(
        note({
          cfiRange: '{"pageNumber":12,"yPercent":0,"rects":[]}',
          sourceAnchorKind: 'pdf_page',
          sourceAnchorValue: '37',
          anchorVerified: true,
        }),
      ),
    ).toBe('{"pageNumber":12,"yPercent":0,"rects":[]}');
  });

  it('falls back to a verified EPUB assistant anchor', () => {
    const cfi = 'epubcfi(/6/4[chapter]!/4/2/2)';
    expect(
      noteNavigationTarget(
        note({
          sourceAnchorKind: 'epub_cfi',
          sourceAnchorValue: cfi,
          anchorVerified: true,
        }),
      ),
    ).toBe(cfi);
  });

  it('converts a verified PDF assistant anchor to a numeric page', () => {
    expect(
      noteNavigationTarget(
        note({
          sourceAnchorKind: 'pdf_page',
          sourceAnchorValue: '37',
          anchorVerified: true,
        }),
      ),
    ).toBe(37);
  });

  it('refuses unverified or invalid typed locations', () => {
    expect(
      noteNavigationTarget(
        note({
          sourceAnchorKind: 'pdf_page',
          sourceAnchorValue: '37',
          anchorVerified: false,
        }),
      ),
    ).toBeNull();

    expect(
      noteNavigationTarget(
        note({
          sourceAnchorKind: 'pdf_page',
          sourceAnchorValue: '37.5',
          anchorVerified: true,
        }),
      ),
    ).toBeNull();

    expect(
      noteNavigationTarget(
        note({
          sourceAnchorKind: 'pdf_page',
          sourceAnchorValue: '-1',
          anchorVerified: true,
        }),
      ),
    ).toBeNull();
  });
});

describe('importedNoteLocation', () => {
  it('reads the highlighted text and spine item of a KOReader EPUB highlight', () => {
    expect(
      importedNoteLocation(
        note({
          selectedText: ' philosophy begins in wonder. ',
          sourceAnchorKind: 'koreader_xpointer',
          sourceAnchorValue:
            '/body/DocFragment[10]/body/section/p[31]/text()[1].186|/body/DocFragment[10]/body/section/p[31]/a/text().2',
        }),
      ),
    ).toEqual({ text: 'philosophy begins in wonder.', spineIndex: 9, page: null });
  });

  it('reads the page of a KOReader PDF highlight', () => {
    expect(
      importedNoteLocation(
        note({ selectedText: 'A passage.', sourceAnchorKind: 'koreader_page', sourceAnchorValue: '14' }),
      ),
    ).toEqual({ text: 'A passage.', spineIndex: null, page: 14 });
  });

  it('keeps the text of a highlight whose anchor says nothing about position', () => {
    expect(
      importedNoteLocation(
        note({ selectedText: 'A passage.', sourceAnchorKind: 'kobo_bookmark', sourceAnchorValue: 'abc' }),
      ),
    ).toEqual({ text: 'A passage.', spineIndex: null, page: null });
  });

  it('has nothing to offer for a note with neither text nor page', () => {
    expect(
      importedNoteLocation(
        note({ sourceAnchorKind: 'koreader_unknown', sourceAnchorValue: 'annotation:abc' }),
      ),
    ).toBeNull();
  });
});
