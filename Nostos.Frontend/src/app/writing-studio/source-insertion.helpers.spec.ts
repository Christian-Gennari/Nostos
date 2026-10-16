import {
  buildNoteMarkdown,
  buildQuoteMarkdown,
  buildReferenceMarkdown,
  hasMeaningfulNoteContent,
  hasMeaningfulSelectedText,
  sourceHumanLabel,
} from './source-insertion.helpers';

describe('source insertion provenance (#493)', () => {
  const pdfSource = {
    id: '36fbe47d-13f0-4a37-9c72-f79290745923',
    bookId: '6b27ab56-f1c6-47ae-b3bb-e59085779030',
    bookTitle: 'The Republic',
    selectedText: 'Justice is harmony.\r\nIt orders the soul.',
    content: 'A reflection on justice.',
    sourceAnchorKind: 'pdf_page',
    sourceAnchorValue: '42',
    anchorVerified: true,
  };

  it('builds multiline quote markdown line-by-line with visible PDF provenance', () => {
    expect(buildQuoteMarkdown(pdfSource)).toBe(
      '> Justice is harmony.\n> It orders the soul.\n> — *The Republic*, p. 42',
    );
  });

  it('builds note and reference markdown with readable provenance', () => {
    expect(buildNoteMarkdown(pdfSource)).toBe(
      'A reflection on justice.\n\n*Source: The Republic, p. 42*',
    );
    expect(buildReferenceMarkdown(pdfSource)).toBe('*See: The Republic, p. 42*');
  });

  it('never emits UUIDs, internal identifiers or EPUB CFIs into prose', () => {
    const epubSource = {
      ...pdfSource,
      bookTitle: 'The Magic Mountain',
      sourceAnchorKind: 'epub_cfi',
      sourceAnchorValue: 'epubcfi(/6/14[chapter]!/4/2/6:12)',
      cfiRange: 'epubcfi(/6/14[chapter]!/4/2/6:12)',
    };

    const markdown = [
      buildQuoteMarkdown(epubSource),
      buildNoteMarkdown(epubSource),
      buildReferenceMarkdown(epubSource),
    ].join('\n');

    expect(markdown).toContain('The Magic Mountain');
    expect(markdown).not.toContain('epubcfi');
    expect(markdown).not.toContain(pdfSource.id);
    expect(markdown).not.toContain(pdfSource.bookId);
  });

  it('degrades unsupported or unverified locations to title-only provenance', () => {
    expect(
      buildReferenceMarkdown({
        bookTitle: 'Walden',
        sourceAnchorKind: 'audio_timestamp',
        sourceAnchorValue: '01:23:45',
        anchorVerified: true,
      }),
    ).toBe('*See: Walden*');

    expect(
      buildReferenceMarkdown({
        bookTitle: 'Meditations',
        sourceAnchorKind: 'pdf_page',
        sourceAnchorValue: '19',
        anchorVerified: false,
      }),
    ).toBe('*See: Meditations*');
  });

  it('requires meaningful source text for quote/note actions and a readable title for references', () => {
    expect(hasMeaningfulSelectedText({ selectedText: '  \n ' })).toBe(false);
    expect(hasMeaningfulSelectedText({ selectedText: 'quoted' })).toBe(true);
    expect(hasMeaningfulNoteContent({ content: '  ' })).toBe(false);
    expect(hasMeaningfulNoteContent({ content: 'reflection' })).toBe(true);
    expect(sourceHumanLabel({ bookTitle: '   ' })).toBeNull();
    expect(buildReferenceMarkdown({ bookTitle: null })).toBeNull();
  });

  it('escapes Markdown emphasis characters in source titles without changing source text', () => {
    const source = {
      bookTitle: 'Book *One* _Revised_',
      selectedText: 'Literal *source* text',
      content: 'Literal _note_ text',
    };

    expect(buildQuoteMarkdown(source)).toBe(
      '> Literal *source* text\n> — *Book \\*One\\* \\_Revised\\_*',
    );
    expect(buildNoteMarkdown(source)).toContain('Literal _note_ text');
  });
});

describe('quoting from a passage (issue #1021)', () => {
  it('names the passage in the attribution, never where it starts', () => {
    expect(
      buildQuoteMarkdown({
        selectedText: 'Man prefers peace, and even death,\nto freedom of choice.',
        bookTitle: 'The Brothers Karamazov',
        passageTitle: 'The Grand Inquisitor',
        sourceAnchorKind: 'pdf_page',
        sourceAnchorValue: '241',
        anchorVerified: true,
      }),
    ).toBe(
      '> Man prefers peace, and even death,\n> to freedom of choice.\n> — *The Brothers Karamazov*, “The Grand Inquisitor”',
    );
  });

  it('names the passage in a reference too', () => {
    expect(buildReferenceMarkdown({ bookTitle: 'The Brothers Karamazov', passageTitle: 'The Grand Inquisitor' })).toBe(
      '*See: The Brothers Karamazov, “The Grand Inquisitor”*',
    );
  });
});
