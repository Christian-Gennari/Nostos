import {
  importedNoteLocation,
  isImportedAnchor,
  type NoteSearchHit,
} from '../core/dtos/note.dtos';
import type { NoteContextDto, TopicDto } from '../core/services/topics.service';

export type IndexSort = 'usage' | 'az' | 'za';
export type NoteSort = 'newest' | 'oldest' | 'source';
/** The mode the header toggle persists. */
export type BrainViewMode = 'list' | 'map' | 'notes';
/**
 * What the surface is actually showing. `unlinked` is deliberately NOT part of
 * `BrainViewMode`: review is a task the user enters and leaves, not a place to be
 * dropped back into on the next visit, so it is never persisted and never
 * restored with a stale queue.
 */
export type BrainPaneMode = BrainViewMode | 'unlinked';

export const ALL_SOURCES = 'all';

/**
 * How many unlinked notes one page of review mode holds. Small on purpose: the
 * queue is reviewed one note at a time, and the mode must be able to walk past
 * the page rather than end at it (issue #256).
 */
export const REVIEW_PAGE_SIZE = 25;

export interface SourceOption {
  value: string;
  label: string;
  count: number;
}

export type RenameSurface = 'index' | 'header';

export interface MergeRequest {
  sourceId: string;
  targetId: string;
  sourceName: string;
  targetName: string;
  noteCount: number;
}

export const INDEX_SORT_STORAGE_KEY = 'nostos.brain.indexSort';
export const BRAIN_VIEW_MODE_STORAGE_KEY = 'nostos.brain.viewMode';

export const INDEX_SORTS: readonly IndexSort[] = ['usage', 'az', 'za'];
export const BRAIN_VIEW_MODES: readonly BrainViewMode[] = ['list', 'map', 'notes'];

export interface NamePart {
  text: string;
  highlight: boolean;
}

export function normalizeSearchText(value: string): string {
  return value.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
}

/**
 * Live note-text search is debounced: it runs on the server per keystroke otherwise
 * (issue #158), and an index search is typed, not submitted.
 */
export const NOTE_SEARCH_DEBOUNCE_MS = 250;

export function searchRank(name: string, query: string): number {
  const normalizedName = normalizeSearchText(name);
  if (normalizedName === query) return 0;
  if (normalizedName.startsWith(query)) return 1;
  return 2;
}

/**
 * The `[[Topic]]` names a note body declares.
 *
 * This mirrors NoteProcessorService's rule on the server — trimmed, non-empty
 * names, case-insensitively distinct — because review mode has to answer one
 * question locally: did the save I just made resolve this note? On the server a
 * note is unlinked exactly when it declares no topic, so the same rule here is
 * not a second link model, it is the one link model read locally. Asking the
 * server again instead would make the queue end on a second round trip.
 */
export function declaredTopicNames(content: string): string[] {
  const names: string[] = [];
  for (const match of (content ?? '').matchAll(/\[\[(.*?)\]\]/g)) {
    const name = match[1].trim();
    if (!name) continue;
    if (names.some((existing) => existing.toLowerCase() === name.toLowerCase())) continue;
    names.push(name);
  }
  return names;
}

/** True when a note body declares at least one topic. */
export function declaresTopic(content: string): boolean {
  return declaredTopicNames(content).length > 0;
}

/**
 * A topic name the user typed that could become a new topic, or null when it
 * is empty, too long, would break the `[[name]]` syntax, or already exists.
 */
export function newTopicNameFor(query: string, topics: readonly TopicDto[]): string | null {
  const name = query.trim();
  if (!name || name.length > 100 || /[\[\]\r\n]/.test(name)) return null;
  return topics.some((topic) => topic.name.toLowerCase() === name.toLowerCase()) ? null : name;
}

/** `content` with an explicit `[[name]]` reference, appended unless already there. */
export function withTopicReference(content: string, name: string): string {
  const trimmedName = name.trim();
  const declared = declaredTopicNames(content).some(
    (existing) => existing.toLowerCase() === trimmedName.toLowerCase()
  );
  if (declared) return content;

  const body = content.trimEnd();
  return body.length ? `${body}\n\n[[${trimmedName}]]` : `[[${trimmedName}]]`;
}

function compareTopics(a: TopicDto, b: TopicDto, sort: IndexSort): number {
  switch (sort) {
    case 'az':
      return a.name.localeCompare(b.name);
    case 'za':
      return b.name.localeCompare(a.name);
    default:
      return b.usageCount - a.usageCount || a.name.localeCompare(b.name);
  }
}

/**
 * The topics whose name matches `queryText`, minus `excludedId`. A query ranks
 * an exact match first and a prefix match second; `sort` orders within a rank,
 * and orders everything when there is no query.
 */
export function rankTopics(
  topics: readonly TopicDto[],
  queryText: string,
  excludedId: string | null,
  sort: IndexSort,
): TopicDto[] {
  const query = normalizeSearchText(queryText.trim());
  const rows = topics.filter(
    (topic) => topic.id !== excludedId && (!query || normalizeSearchText(topic.name).includes(query))
  );

  return rows.sort((a, b) => {
    if (query) {
      const rankDifference = searchRank(a.name, query) - searchRank(b.name, query);
      if (rankDifference !== 0) return rankDifference;
    }
    return compareTopics(a, b, sort);
  });
}

/**
 * `name` split around the first place `queryText` matches it, ignoring case
 * and accents. The match is found in the normalized text and mapped back, so
 * the highlighted part keeps the name's own characters.
 */
export function highlightNameParts(name: string, queryText: string): NamePart[] {
  const query = normalizeSearchText(queryText.trim());
  if (!query) return [{ text: name, highlight: false }];

  const normalizedChars: string[] = [];
  const sourceStarts: number[] = [];
  const sourceEnds: number[] = [];

  for (let index = 0; index < name.length; ) {
    const codePoint = name.codePointAt(index);
    if (codePoint === undefined) break;
    const sourceChar = String.fromCodePoint(codePoint);
    const normalizedChar = normalizeSearchText(sourceChar);
    for (const character of normalizedChar) {
      normalizedChars.push(character);
      sourceStarts.push(index);
      sourceEnds.push(index + sourceChar.length);
    }
    index += sourceChar.length;
  }

  const matchStart = normalizedChars.join('').indexOf(query);
  if (matchStart < 0) return [{ text: name, highlight: false }];

  const matchEnd = matchStart + query.length - 1;
  const sourceStart = sourceStarts[matchStart];
  const sourceEnd = sourceEnds[matchEnd];
  return [
    { text: name.slice(0, sourceStart), highlight: false },
    { text: name.slice(sourceStart, sourceEnd), highlight: true },
    { text: name.slice(sourceEnd), highlight: false },
  ].filter((part) => part.text.length > 0);
}

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
