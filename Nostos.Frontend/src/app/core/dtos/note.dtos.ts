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
  /** `note` or `passage` (issue #1014); absent on older payloads, meaning note. */
  kind?: NoteKind;
  /** A passage's title. */
  passageTitle?: string | null;
  /** Where a passage ends, in the same terms as its start anchor. */
  passageEnd?: string | null;
  /** A passage's length in words; for a passage `selectedText` is only its opening. */
  passageWordCount?: number | null;
  /** The book's author, when known (issue #1038). */
  bookAuthor?: string | null;
}

export type NoteKind = 'note' | 'passage';

/** The fields that make a note a passage, shared by every note shape. */
export interface PassageFields {
  kind?: NoteKind | string;
  passageTitle?: string | null;
  passageEnd?: string | null;
  passageWordCount?: number | null;
}

/**
 * A passage in full, for the passage viewer (issue #1014). Start and end are
 * in the terms `anchorKind` names: CFIs, page numbers or seconds.
 */
export interface Passage {
  id: string;
  bookId: string;
  bookTitle: string | null;
  title: string;
  anchorKind: 'epub_cfi' | 'pdf_page' | 'audio_timestamp' | string;
  start: string;
  end: string;
  html: string | null;
  text: string | null;
  wordCount: number;
  content: string;
  highlightColour: string | null;
  createdAt: string;
  /** The book's author, when known (issue #1038). */
  bookAuthor?: string | null;
}

/** Whether a note is a passage: a stretch of the book saved to be read again. */
export function isPassage(note: PassageFields | null | undefined): boolean {
  return note?.kind === 'passage';
}

/** Minutes a passage takes to read, at an unhurried 250 words a minute. */
export function passageReadingMinutes(words: number): number {
  return Math.max(1, Math.round(words / 250));
}

/** "6,800 words · 27 min", or the page range of a PDF passage, or a clip's length. */
export function passageSizeLabel(
  note: PassageFields & { sourceAnchorKind?: string; sourceAnchorValue?: string | null },
): string {
  const kind = note.sourceAnchorKind?.toLowerCase();
  if (kind === 'pdf_page') {
    const first = Number(note.sourceAnchorValue);
    const last = Number(note.passageEnd);
    if (Number.isInteger(first) && Number.isInteger(last)) {
      return first === last ? `Page ${first}` : `Pages ${first}–${last}`;
    }
  }
  if (kind === 'audio_timestamp') {
    const from = Number(note.sourceAnchorValue);
    const to = Number(note.passageEnd);
    if (Number.isFinite(from) && Number.isFinite(to) && to > from) {
      return `${formatAudioTime(from)}–${formatAudioTime(to)} · ${formatAudioTime(to - from)}`;
    }
  }
  const words = note.passageWordCount ?? 0;
  if (words <= 0) return 'Passage';
  return `${words.toLocaleString('en-US')} words · ${passageReadingMinutes(words)} min`;
}

/**
 * Whether a highlight carries a note of its own, beyond the passage it marks.
 * A plain highlight is saved with empty content.
 */
export function noteHasText(note: Pick<Note, 'content'>): boolean {
  return !!note.content?.trim();
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

export function parseAudioSeconds(value: string | null | undefined): number | null {
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
  /** A passage (issue #1014): start in the anchor, end here, text in selectedText. */
  kind?: NoteKind;
  passageTitle?: string;
  passageEnd?: string;
  passageHtml?: string;
}

export interface UpdateNoteDto {
  content: string;
  selectedText?: string; // <--- ADDED
  /** Omitted leaves the stored pen as it is. */
  highlightColour?: string;
  /** A passage's title; omitted leaves it as it is. */
  passageTitle?: string;
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
  kind?: NoteKind;
  passageTitle?: string | null;
  passageEnd?: string | null;
  passageWordCount?: number | null;
  /** The book's author, when known (issue #1038). */
  bookAuthor?: string | null;
  /** The highlighter pen, by name; absent on older payloads. */
  highlightColour?: string | null;
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

/** How many notes each of the Brain's fixed places holds. */
export interface NoteSummary {
  total: number;
  passages: number;
  unsorted: number;
}

/** A book with notes in one place of the Brain, for its Book filter. */
export interface NoteBookFacet {
  bookId: string;
  bookTitle: string;
  bookAuthor: string | null;
  noteCount: number;
}

/** The order a list of notes is shown in. */
export type NoteOrder = 'newest' | 'oldest' | 'book';

/** The notes a topic link or unlink changed, and every listed note as it now is. */
export interface TopicLinkResult {
  changedNoteIds: string[];
  notes: NoteSearchHit[];
}
