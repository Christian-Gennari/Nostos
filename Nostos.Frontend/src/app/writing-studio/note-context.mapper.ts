import { Note, NoteSearchHit } from '../core/dtos/note.dtos';
import { WritingSourceDto } from '../core/dtos/writing.dtos';
import { NoteContextDto } from '../core/services/topics.service';

/** Maps a Topic Brain `NoteContextDto` (`noteId`) into the Studio `Note` shape. */
export function noteContextToNote(n: NoteContextDto): Note {
  return {
    id: n.noteId,
    bookId: n.bookId,
    content: n.content,
    cfiRange: n.cfiRange,
    selectedText: n.selectedText,
    createdAt: n.createdAt ?? '',
    bookTitle: n.bookTitle,
    bookAuthor: n.bookAuthor,
    sourceAnchorKind: n.sourceAnchorKind,
    sourceAnchorValue: n.sourceAnchorValue,
    anchorVerified: n.anchorVerified,
    highlightColour: n.highlightColour,
    kind: n.kind,
    passageTitle: n.passageTitle,
    passageEnd: n.passageEnd,
    passageWordCount: n.passageWordCount,
  };
}

/** Maps a kept `WritingSourceDto` into the Studio `Note` shape. */
export function writingSourceToNote(s: WritingSourceDto): Note {
  return {
    id: s.id,
    bookId: s.bookId,
    content: s.content,
    selectedText: s.selectedText ?? undefined,
    cfiRange: s.cfiRange ?? undefined,
    createdAt: s.createdAt,
    bookTitle: s.bookTitle ?? undefined,
    bookAuthor: s.bookAuthor ?? undefined,
    sourceAnchorKind: s.sourceAnchorKind ?? undefined,
    sourceAnchorValue: s.sourceAnchorValue ?? undefined,
    anchorVerified: s.anchorVerified ?? undefined,
    kind: s.kind,
    passageTitle: s.passageTitle,
    passageEnd: s.passageEnd,
    passageWordCount: s.passageWordCount,
  };
}

/** Maps a cited-source `NoteSearchHit` into the Studio `Note` shape. */
export function noteSearchHitToNote(hit: NoteSearchHit): Note {
  return {
    id: hit.id,
    bookId: hit.bookId,
    content: hit.content,
    selectedText: hit.selectedText ?? undefined,
    cfiRange: hit.cfiRange ?? undefined,
    createdAt: hit.createdAt,
    bookTitle: hit.bookTitle ?? undefined,
    bookAuthor: hit.bookAuthor ?? undefined,
    sourceAnchorKind: hit.sourceAnchorKind ?? undefined,
    sourceAnchorValue: hit.sourceAnchorValue ?? undefined,
    anchorVerified: hit.anchorVerified ?? undefined,
    kind: hit.kind,
    passageTitle: hit.passageTitle,
    passageEnd: hit.passageEnd,
    passageWordCount: hit.passageWordCount,
  };
}
