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
  /**
   * The highlighter pen, by name (amber | sage | blue | rose). Absent or null
   * when none was recorded; `noteHighlightColour` resolves what to draw.
   */
  highlightColour?: string | null;
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
    case 'audio_timestamp':
      // Assistant audio captures store the time only in the typed anchor.
      return parseAudioSeconds(value) === null ? null : value;
    default:
      return null;
  }
}

function parseAudioSeconds(value: string | null | undefined): number | null {
  const text = value?.trim() ?? '';
  if (!/^\d+(\.\d+)?$/.test(text)) return null;
  const seconds = Number(text);
  return Number.isFinite(seconds) ? seconds : null;
}

/**
 * The playback time an audiobook note was taken at, in seconds across the
 * whole book, or null when it has none. Only meaningful for a note in an
 * audiobook: a PDF note's `cfiRange` can be a bare number too, so the caller
 * decides the format.
 */
export function noteAudioSeconds(note: Note): number | null {
  if (note.anchorVerified && note.sourceAnchorKind?.toLowerCase() === 'audio_timestamp') {
    const anchored = parseAudioSeconds(note.sourceAnchorValue);
    if (anchored !== null) return anchored;
  }
  return parseAudioSeconds(note.cfiRange);
}

/** Seconds to `m:ss`, or `h:mm:ss` from one hour on. */
export function formatAudioTime(seconds: number): string {
  const whole = Number.isFinite(seconds) ? Math.max(0, Math.floor(seconds)) : 0;
  const h = Math.floor(whole / 3600);
  const m = Math.floor((whole % 3600) / 60);
  const s = (whole % 60).toString().padStart(2, '0');
  return h > 0 ? `${h}:${m.toString().padStart(2, '0')}:${s}` : `${m}:${s}`;
}

/**
 * Where an imported highlight sits, in the terms its e-reader recorded. These
 * are never verified reader locations, so they stay out of
 * `noteNavigationTarget`: each needs the open reader to interpret it.
 */
export interface ImportedNoteLocation {
  /** The highlighted text, which a text reader can look up in the book. */
  text: string | null;
  /** Spine item of a KOReader position, to settle a passage that repeats. */
  spineIndex: number | null;
  /** Where a KOReader highlight or page bookmark starts in a reflowable book. */
  xpointer: string | null;
  /** KOReader's page: the real page of a PDF. */
  page: number | null;
}

export function importedNoteLocation(note: {
  selectedText?: string | null;
  sourceAnchorKind?: string;
  sourceAnchorValue?: string | null;
}): ImportedNoteLocation | null {
  const text = note.selectedText?.trim() || null;
  const kind = note.sourceAnchorKind?.toLowerCase();
  const value = note.sourceAnchorValue?.trim() ?? '';

  let spineIndex: number | null = null;
  let xpointer: string | null = null;
  let page: number | null = null;
  if (kind === 'koreader_xpointer') {
    // Stored as `start|end`. crengine numbers a book's spine items as
    // DocFragment[1..n].
    xpointer = value.split('|')[0] || null;
    const fragment = /\/DocFragment\[(\d+)\]/.exec(xpointer ?? '');
    if (fragment) spineIndex = Number(fragment[1]) - 1;
  } else if (kind === 'koreader_page' && /^\d+$/.test(value) && Number(value) > 0) {
    page = Number(value);
  }

  return text || xpointer || page !== null ? { text, spineIndex, xpointer, page } : null;
}

/** True for a note whose anchor is another reading app's own position. */
export function isImportedAnchor(kind: string | undefined): boolean {
  return /^(koreader|kobo)_/i.test(kind ?? '');
}

export interface CreateNoteDto {
  content: string;
  cfiRange?: string;
  selectedText?: string;
  /** The pen a highlight is made with, by name. */
  highlightColour?: string;
  /** Typed source anchor; omitted leaves the server default (`unknown`). */
  sourceAnchorKind?: string;
  sourceAnchorValue?: string;
  anchorVerified?: boolean;
}

export interface UpdateNoteDto {
  content: string;
  selectedText?: string; // <--- ADDED
  /** Omitted leaves the stored pen as it is. */
  highlightColour?: string;
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
