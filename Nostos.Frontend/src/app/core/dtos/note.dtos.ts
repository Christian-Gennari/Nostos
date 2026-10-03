// Nostos.Frontend/src/app/dtos/note.dtos.ts
export interface Note {
  id: string;
  bookId: string;
  content: string;
  cfiRange?: string;
  selectedText?: string;
  createdAt: string;
  bookTitle?: string;
  rawContent?: string | null;
  captureSource?: string;
  processingMode?: string;
  sourceAnchorKind?: string;
  sourceAnchorValue?: string | null;
  anchorVerified?: boolean;
}

/**
 * Resolve the location the active reader can consume for a saved note.
 *
 * cfiRange remains first because it is the established reader-navigation
 * contract used by manual notes/highlights. Assistant captures also persist a
 * typed source anchor; using it as a fallback makes older PDF captures (which
 * stored pdf_page but no cfiRange) navigable without migrating note rows.
 */
export function noteNavigationTarget(note: Note): string | number | null {
  const legacy = note.cfiRange?.trim();
  if (legacy) return legacy;

  if (!note.anchorVerified || !note.sourceAnchorValue) return null;

  const value = note.sourceAnchorValue.trim();
  if (!value) return null;

  switch (note.sourceAnchorKind?.toLowerCase()) {
    case 'epub_cfi':
      return value;
    case 'pdf_page': {
      const page = Number(value);
      return Number.isInteger(page) && page > 0 ? page : null;
    }
    default:
      return null;
  }
}

export interface CreateNoteDto {
  content: string;
  cfiRange?: string;
  selectedText?: string;
}

export interface UpdateNoteDto {
  content: string;
  selectedText?: string; // <--- ADDED
}

/**
 * A note found by searching note text, or listed in the unlinked-note review
 * queue (issues #158, #256). `snippet` is the fragment around the match, so a row
 * can show why it matched without the whole note body travelling to the index.
 */
export interface NoteSearchHit {
  id: string;
  bookId: string;
  bookTitle: string | null;
  content: string;
  selectedText: string | null;
  snippet: string | null;
  topicNames: string[];
  createdAt: string;
  cfiRange?: string | null;
  sourceAnchorKind?: string;
  sourceAnchorValue?: string | null;
  anchorVerified?: boolean;
  captureSource?: string;
  processingMode?: string;
  hasRawContent?: boolean;
}

export interface NoteRawTranscript {
  id: string;
  rawContent: string | null;
  content: string;
  processingMode: string;
}

/**
 * One bounded page of the unlinked-note review queue (issue #256).
 *
 * `totalCount` is the point of the envelope: review mode walks the whole set, and
 * a bare page of 50 would otherwise read as "the unlinked notes".
 */
export interface NoteSearchPage {
  items: NoteSearchHit[];
  totalCount: number;
  offset: number;
  limit: number;
}
