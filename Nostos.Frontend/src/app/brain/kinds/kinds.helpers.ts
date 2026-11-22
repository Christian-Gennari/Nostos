import type { TopicDto, TopicKindDto, TopicKindSource } from '../../core/services/topics.service';
import { normalizeSearchText } from '../../core/search-text';
import { plural } from '../../core/format';

/** The twelve palette keys a kind may use, in palette order. */
export const KIND_COLOR_KEYS = [
  'kind-1',
  'kind-2',
  'kind-3',
  'kind-4',
  'kind-5',
  'kind-6',
  'kind-7',
  'kind-8',
  'kind-9',
  'kind-10',
  'kind-11',
  'kind-12',
] as const;

export type KindColorKey = (typeof KIND_COLOR_KEYS)[number];

/** The most kinds a library may hold: one per palette colour. */
export const MAX_TOPIC_KINDS = 12;

/** The filter values the sorter's URL understands. A kind id is any other value. */
export const UNTYPED_FILTER = 'untyped';
export const ASSISTANT_FILTER = 'assistant';
export const CONNECTED_APP_FILTER = 'connected-app';

export interface KindFilterChip {
  value: string;
  label: string;
  colorKey: string | null;
}

/** The sorter's filters: everything, untyped, each kind, then who set the kind. */
export function kindFilterChips(kinds: readonly TopicKindDto[]): KindFilterChip[] {
  return [
    { value: 'all', label: 'All topics', colorKey: null },
    { value: UNTYPED_FILTER, label: 'No kind', colorKey: null },
    ...kinds.map((kind) => ({ value: kind.id, label: kind.name, colorKey: kind.colorKey })),
    { value: ASSISTANT_FILTER, label: 'Set by Ask Nostos', colorKey: null },
    { value: CONNECTED_APP_FILTER, label: 'Set by a connected app', colorKey: null },
  ];
}

/** The filter value the sorter understands, case-folded: `Untyped` → `untyped`. */
export function normalizeKindFilter(value: string | null | undefined): string {
  const raw = value?.trim() ?? '';
  if (!raw) return 'all';
  const lower = raw.toLowerCase();
  return lower === 'all' || lower === UNTYPED_FILTER || lower === ASSISTANT_FILTER
    || lower === CONNECTED_APP_FILTER
    ? lower
    : raw;
}

/** The header's count: "13 of 40 topics" while a filter or a search is on. */
export function kindCountLabel(shown: number, total: number, filtered: boolean): string {
  return filtered ? `${shown} of ${plural(total, 'topic')}` : plural(total, 'topic');
}

/** Whether a topic belongs under the sorter's `filter` value. */
export function topicMatchesKindFilter(topic: TopicDto, filter: string): boolean {
  switch (filter) {
    case '':
    case 'all':
      return true;
    case UNTYPED_FILTER:
      return !topic.kindId;
    case ASSISTANT_FILTER:
      return topic.kindSource === 'assistant';
    case CONNECTED_APP_FILTER:
      return topic.kindSource === 'connected-app';
    default:
      return topic.kindId === filter;
  }
}

/** The sorter's visible topics: the filter, then the name search, A–Z. */
export function kindSorterTopics(
  topics: readonly TopicDto[],
  filter: string,
  query: string,
): TopicDto[] {
  const term = normalizeSearchText(query.trim());
  return topics
    .filter((topic) => topicMatchesKindFilter(topic, filter))
    .filter((topic) => !term || normalizeSearchText(topic.name).includes(term))
    .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
}

/** "Set by Ask Nostos", "Set by a connected app", "Set by you"; null when untyped. */
export function kindSourceLabel(source: TopicKindSource | null | undefined): string | null {
  switch (source) {
    case 'assistant':
      return 'Set by Ask Nostos';
    case 'connected-app':
      return 'Set by a connected app';
    case 'reader':
      return 'Set by you';
    default:
      return null;
  }
}

/**
 * The colours a kind may take: every palette key no other kind uses. The kind's
 * own colour stays in its list, so the current swatch remains visible.
 */
export function freeKindColorKeys(
  kinds: readonly TopicKindDto[],
  currentKindId: string | null = null,
): KindColorKey[] {
  const used = new Set(
    kinds.filter((kind) => kind.id !== currentKindId).map((kind) => kind.colorKey),
  );
  return KIND_COLOR_KEYS.filter((key) => !used.has(key));
}

/** The kind already using a palette colour, if any. */
export function kindUsingColor(
  kinds: readonly TopicKindDto[],
  colorKey: string,
  exceptKindId: string | null = null,
): TopicKindDto | null {
  return kinds.find((kind) => kind.id !== exceptKindId && kind.colorKey === colorKey) ?? null;
}

/** The first free palette colour, or null when all twelve are taken. */
export function firstFreeKindColor(kinds: readonly TopicKindDto[]): KindColorKey | null {
  return freeKindColorKeys(kinds)[0] ?? null;
}

/** What a delete confirm asks before a kind is removed. */
export function kindDeleteDescription(kind: TopicKindDto): string {
  const topics = kind.topicCount === 1 ? '1 topic' : `${kind.topicCount} topics`;
  return `${topics} will have no kind. The topics stay.`;
}

/** The kind named Person, if the reader still keeps one. */
export function personKind(kinds: readonly TopicKindDto[]): TopicKindDto | null {
  return kinds.find((kind) => normalizeName(kind.name) === normalizeName('Person')) ?? null;
}

/**
 * Lower-cases and strips accents and punctuation, so "Ursula K. Le Guin",
 * "ursula k le guin" and "Ursula K Le Guin" compare alike.
 */
export function normalizePersonName(value: string): string {
  return value
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLocaleLowerCase()
    .replace(/['’`]/g, '')
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();
}

/** The individual authors named in a book's author field. */
export function authorNames(author: string | null | undefined): string[] {
  if (!author?.trim()) return [];
  return author
    .split(/\s*(?:;|&|\band\b|,\s*)\s*/i)
    .map((name) => name.trim())
    .filter(Boolean);
}

/**
 * The names a topic may match, folded once for a whole list: every author's
 * full name and each surname tail ("Le Guin", "Guin" for "Ursula K. Le Guin"),
 * so a row never re-normalises the library on every change detection.
 */
export function authorNameIndex(authors: Iterable<string>): ReadonlySet<string> {
  const index = new Set<string>();
  for (const author of authors) {
    for (const name of authorNames(author)) {
      const parts = normalizePersonName(name).split(' ').filter(Boolean);
      // All suffixes: the whole name, then each shorter tail. `topic` is at
      // least three letters, so "Eco" matches while a lone initial never does.
      for (let i = 0; i < parts.length; i++) index.add(parts.slice(i).join(' '));
    }
  }
  return index;
}

/**
 * Whether a topic is named after an author in the library: the whole name, or
 * the surname ("Le Guin", "Tolkien") when the library spells the full name.
 */
export function topicMatchesAuthor(topicName: string, authors: Iterable<string>): boolean {
  return topicMatchesAuthorIndex(topicName, authorNameIndex(authors));
}

/** The same check against an index built once by `authorNameIndex`. */
export function topicMatchesAuthorIndex(topicName: string, index: ReadonlySet<string>): boolean {
  const topic = normalizePersonName(topicName);
  return topic.length >= 3 && index.has(topic);
}

/** The offline "Person?" suggestion for an untyped topic, or null. */
export function personSuggestionFor(
  topic: TopicDto,
  kinds: readonly TopicKindDto[],
  authorIndex: ReadonlySet<string>,
): TopicKindDto | null {
  if (topic.kindId) return null;
  const kind = personKind(kinds);
  if (!kind) return null;
  return topicMatchesAuthorIndex(topic.name, authorIndex) ? kind : null;
}

/**
 * Shift-click selection over the rows as they are shown. `desired` is the
 * clicked checkbox's new state; the range from the anchor to the target takes
 * that state, and the anchor is kept so a second shift-click extends the range.
 */
export function rangeSelection(
  orderedIds: readonly string[],
  selected: ReadonlySet<string>,
  anchorId: string | null,
  targetId: string,
  desired: boolean,
): Set<string> {
  const next = new Set(selected);
  const target = orderedIds.indexOf(targetId);
  const anchor = anchorId ? orderedIds.indexOf(anchorId) : -1;
  if (target < 0 || anchor < 0) {
    if (desired) next.add(targetId);
    else next.delete(targetId);
    return next;
  }
  const from = Math.min(anchor, target);
  const to = Math.max(anchor, target);
  for (let i = from; i <= to; i++) {
    if (desired) next.add(orderedIds[i]);
    else next.delete(orderedIds[i]);
  }
  return next;
}

/** A stable key for the sorter's selected set, so a no-op keeps its identity. */
export function toggleId(selected: ReadonlySet<string>, id: string, on: boolean): Set<string> {
  const next = new Set(selected);
  if (on) next.add(id);
  else next.delete(id);
  return next;
}

/**
 * The name folded the way the server compares kind names: trimmed, NFC, then
 * upper-cased with the invariant mapping (`toUpperCase`, not the locale's, so a
 * Turkish viewer refuses the same duplicate the server does).
 */
export function normalizeName(value: string): string {
  return value.trim().normalize('NFC').toUpperCase();
}
