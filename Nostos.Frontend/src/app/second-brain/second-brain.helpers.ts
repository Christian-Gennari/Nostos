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
 * The `[[Concept]]` names a note body declares.
 *
 * This mirrors NoteProcessorService's rule on the server — trimmed, non-empty
 * names, case-insensitively distinct — because review mode has to answer one
 * question locally: did the save I just made resolve this note? On the server a
 * note is unlinked exactly when it declares no concept, so the same rule here is
 * not a second link model, it is the one link model read locally. Asking the
 * server again instead would make the queue end on a second round trip.
 */
export function declaredConceptNames(content: string): string[] {
  const names: string[] = [];
  for (const match of (content ?? '').matchAll(/\[\[(.*?)\]\]/g)) {
    const name = match[1].trim();
    if (!name) continue;
    if (names.some((existing) => existing.toLowerCase() === name.toLowerCase())) continue;
    names.push(name);
  }
  return names;
}

/** True when a note body declares at least one concept. */
export function declaresConcept(content: string): boolean {
  return declaredConceptNames(content).length > 0;
}
