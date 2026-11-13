import { WritingSourceDto } from '../core/dtos/writing.dtos';
import { NoteSearchHit } from '../core/dtos/note.dtos';
import { NoteContextDto } from '../core/services/topics.service';
import { noteContextToNote, noteSearchHitToNote, writingSourceToNote } from './note-context.mapper';

describe('note-context.mapper', () => {
  it('maps NoteContextDto to Note preserving anchor, passage, and highlight fields', () => {
    const dto: NoteContextDto = {
      noteId: 'n-1',
      bookId: 'b-1',
      content: 'Note body',
      cfiRange: 'epubcfi(/6/2)',
      selectedText: 'Quoted words',
      createdAt: '2026-08-01T10:00:00Z',
      bookTitle: 'Book One',
      bookAuthor: 'Author One',
      sourceAnchorKind: 'epub_cfi',
      sourceAnchorValue: 'epubcfi(/6/2)',
      anchorVerified: true,
      highlightColour: 'yellow',
      kind: 'passage',
      passageTitle: 'Chapter 1',
      passageEnd: 'epubcfi(/6/4)',
      passageWordCount: 120,
    };

    expect(noteContextToNote(dto)).toEqual({
      id: 'n-1',
      bookId: 'b-1',
      content: 'Note body',
      cfiRange: 'epubcfi(/6/2)',
      selectedText: 'Quoted words',
      createdAt: '2026-08-01T10:00:00Z',
      bookTitle: 'Book One',
      bookAuthor: 'Author One',
      sourceAnchorKind: 'epub_cfi',
      sourceAnchorValue: 'epubcfi(/6/2)',
      anchorVerified: true,
      highlightColour: 'yellow',
      kind: 'passage',
      passageTitle: 'Chapter 1',
      passageEnd: 'epubcfi(/6/4)',
      passageWordCount: 120,
    });
  });

  it('defaults missing NoteContextDto.createdAt to empty string', () => {
    const dto: NoteContextDto = {
      noteId: 'n-2',
      bookId: 'b-2',
      bookTitle: 'Book Two',
      content: 'Body',
    };
    expect(noteContextToNote(dto).createdAt).toBe('');
  });

  it('normalizes nullable fields in WritingSourceDto and NoteSearchHit to undefined', () => {
    const source: WritingSourceDto = {
      id: 's-1',
      bookId: 'b-1',
      bookTitle: null,
      bookAuthor: null,
      content: 'Source body',
      selectedText: null,
      cfiRange: null,
      createdAt: '2026-08-01T10:00:00Z',
      addedAt: '2026-08-02T10:00:00Z',
      sourceAnchorKind: undefined,
      sourceAnchorValue: null,
      anchorVerified: undefined,
    };
    expect(writingSourceToNote(source)).toEqual({
      id: 's-1',
      bookId: 'b-1',
      content: 'Source body',
      selectedText: undefined,
      cfiRange: undefined,
      createdAt: '2026-08-01T10:00:00Z',
      bookTitle: undefined,
      bookAuthor: undefined,
      sourceAnchorKind: undefined,
      sourceAnchorValue: undefined,
      anchorVerified: undefined,
      kind: undefined,
      passageTitle: undefined,
      passageEnd: undefined,
      passageWordCount: undefined,
    });

    const hit: NoteSearchHit = {
      id: 'h-1',
      bookId: 'b-1',
      bookTitle: null,
      bookAuthor: null,
      content: 'Hit body',
      selectedText: null,
      snippet: null,
      cfiRange: null,
      topicNames: ['Memory'],
      createdAt: '2026-08-01T10:00:00Z',
      sourceAnchorKind: undefined,
      sourceAnchorValue: null,
      anchorVerified: undefined,
    };
    expect(noteSearchHitToNote(hit)).toEqual({
      id: 'h-1',
      bookId: 'b-1',
      content: 'Hit body',
      selectedText: undefined,
      cfiRange: undefined,
      createdAt: '2026-08-01T10:00:00Z',
      bookTitle: undefined,
      bookAuthor: undefined,
      sourceAnchorKind: undefined,
      sourceAnchorValue: undefined,
      anchorVerified: undefined,
      kind: undefined,
      passageTitle: undefined,
      passageEnd: undefined,
      passageWordCount: undefined,
    });
  });
});
