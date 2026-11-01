import type { NoteSearchHit } from './dtos/note.dtos';
import { noteSourceParams } from './note-source';

const note = (overrides: Partial<NoteSearchHit>): NoteSearchHit => ({
  id: 'n-1',
  bookId: 'b-1',
  bookTitle: null,
  content: '',
  selectedText: 'The words',
  snippet: null,
  topicNames: [],
  createdAt: '2026-01-01T00:00:00Z',
  ...overrides,
});

describe('noteSourceParams', () => {
  const pageTop = 'epubcfi(/6/4!/4/2/1:0)';

  it('opens a quote at its own passage, not the page the reader was on', () => {
    const range = 'epubcfi(/6/8!/4/8,/1:0,/1:33)';
    expect(
      noteSourceParams(
        note({ cfiRange: range, sourceAnchorKind: 'epub_cfi', sourceAnchorValue: pageTop, anchorVerified: true }),
      ),
    ).toEqual({ sourceCfi: range });
  });

  it('opens a quote not found yet at the page it was saved from', () => {
    expect(
      noteSourceParams(note({ sourceAnchorKind: 'epub_cfi', sourceAnchorValue: pageTop, anchorVerified: true })),
    ).toEqual({ sourceCfi: pageTop });
  });
});
