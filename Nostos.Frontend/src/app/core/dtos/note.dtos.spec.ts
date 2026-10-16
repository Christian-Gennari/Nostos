import {
  Note,
  formatAudioTime,
  isPassage,
  passageSizeLabel,
  importedNoteLocation,
  noteAudioSeconds,
  noteNavigationTarget,
} from './note.dtos';

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
    ).toEqual({
      text: 'philosophy begins in wonder.',
      spineIndex: 9,
      xpointer: '/body/DocFragment[10]/body/section/p[31]/text()[1].186',
      page: null,
    });
  });

  it('reads the page of a KOReader PDF highlight', () => {
    expect(
      importedNoteLocation(
        note({ selectedText: 'A passage.', sourceAnchorKind: 'koreader_page', sourceAnchorValue: '14' }),
      ),
    ).toEqual({ text: 'A passage.', spineIndex: null, xpointer: null, page: 14 });
  });

  it('keeps the text of a highlight whose anchor says nothing about position', () => {
    expect(
      importedNoteLocation(
        note({ selectedText: 'A passage.', sourceAnchorKind: 'kobo_bookmark', sourceAnchorValue: 'abc' }),
      ),
    ).toEqual({ text: 'A passage.', spineIndex: null, xpointer: null, page: null });
  });

  it('keeps the xpointer of a page bookmark, which has no highlighted text', () => {
    expect(
      importedNoteLocation(
        note({
          sourceAnchorKind: 'koreader_xpointer',
          sourceAnchorValue: '/body/DocFragment[13]/body/p[62]/text()[1].0|',
        }),
      ),
    ).toEqual({
      text: null,
      spineIndex: 12,
      xpointer: '/body/DocFragment[13]/body/p[62]/text()[1].0',
      page: null,
    });
  });

  it('has nothing to offer for a note with neither text nor page', () => {
    expect(
      importedNoteLocation(
        note({ sourceAnchorKind: 'koreader_unknown', sourceAnchorValue: 'annotation:abc' }),
      ),
    ).toBeNull();
  });
});

describe('audio note time (issue #906)', () => {
  it('reads the time of a manual audio note from cfiRange', () => {
    expect(noteAudioSeconds(note({ cfiRange: '754.5' }))).toBe(754.5);
  });

  it('prefers a verified audio anchor, and reads it when cfiRange is absent', () => {
    const anchored = {
      sourceAnchorKind: 'audio_timestamp',
      sourceAnchorValue: '90',
      anchorVerified: true,
    };
    expect(noteAudioSeconds(note({ cfiRange: '754.5', ...anchored }))).toBe(90);
    expect(noteAudioSeconds(note(anchored))).toBe(90);
    expect(noteNavigationTarget(note(anchored))).toBe('90');
  });

  it('has no time for a note without a location, or with a non-numeric one', () => {
    expect(noteAudioSeconds(note())).toBeNull();
    expect(noteAudioSeconds(note({ cfiRange: '' }))).toBeNull();
    expect(noteAudioSeconds(note({ cfiRange: 'epubcfi(/6/2)' }))).toBeNull();
    expect(
      noteAudioSeconds(
        note({ sourceAnchorKind: 'audio_timestamp', sourceAnchorValue: '90', anchorVerified: false }),
      ),
    ).toBeNull();
  });

  it('formats like the scrubber: m:ss, and h:mm:ss from one hour on', () => {
    expect(formatAudioTime(0)).toBe('0:00');
    expect(formatAudioTime(754.9)).toBe('12:34');
    expect(formatAudioTime(3599)).toBe('59:59');
    expect(formatAudioTime(3600)).toBe('1:00:00');
    expect(formatAudioTime(57_540)).toBe('15:59:00');
  });
});

describe('passageSizeLabel (issue #1014)', () => {
  it('names a passage by its words, its pages or its time', () => {
    expect(passageSizeLabel({ kind: 'passage', passageWordCount: 9262, sourceAnchorKind: 'epub_cfi' })).toBe(
      '9,262 words · 37 min',
    );
    expect(passageSizeLabel({ kind: 'passage', sourceAnchorKind: 'pdf_page', sourceAnchorValue: '241', passageEnd: '256' })).toBe(
      'Pages 241–256',
    );
    expect(passageSizeLabel({ kind: 'passage', sourceAnchorKind: 'pdf_page', sourceAnchorValue: '12', passageEnd: '12' })).toBe(
      'Page 12',
    );
    expect(
      passageSizeLabel({ kind: 'passage', sourceAnchorKind: 'audio_timestamp', sourceAnchorValue: '3600', passageEnd: '5400' }),
    ).toBe('1:00:00–1:30:00 · 30:00');
    expect(isPassage({ kind: 'passage' })).toBe(true);
    expect(isPassage({ kind: 'note' })).toBe(false);
    expect(isPassage({})).toBe(false);
  });
});
