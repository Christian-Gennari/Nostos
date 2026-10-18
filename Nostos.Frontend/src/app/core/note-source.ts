import {
  importedNoteLocation,
  isImportedAnchor,
  type NoteSearchHit,
} from './dtos/note.dtos';
import type { NoteContextDto } from './services/topics.service';

/** The reader query params that open a note's source passage. */
export type NoteSourceParams =
  | { sourcePage: number }
  | { sourceCfi: string }
  | { sourceTime: number }
  | { sourceNote: string };

/** Where in its book a note points, or null when it has no usable location. */
export function noteSourceParams(note: NoteSearchHit | NoteContextDto): NoteSourceParams | null {
  const anchor = note.anchorVerified ? note.sourceAnchorValue?.trim() : null;
  const kind = note.sourceAnchorKind?.toLowerCase();
  if (kind === 'pdf_page' && anchor) {
    const page = Number(anchor);
    if (Number.isInteger(page) && page > 0) return { sourcePage: page };
  }
  if (kind === 'epub_cfi' && anchor) return { sourceCfi: anchor };
  if (kind === 'audio_timestamp' && anchor) {
    const sourceTime = Number(anchor);
    if (Number.isFinite(sourceTime) && sourceTime >= 0) return { sourceTime };
  }
  const cfi = note.cfiRange?.trim();
  if (cfi?.startsWith('epubcfi(')) return { sourceCfi: cfi };

  // An imported highlight has no location until the reader has found it in
  // the book, so the link names the note and the reader looks it up.
  if (isImportedAnchor(note.sourceAnchorKind) && importedNoteLocation(note)) {
    return { sourceNote: 'noteId' in note ? note.noteId : note.id };
  }
  return null;
}
