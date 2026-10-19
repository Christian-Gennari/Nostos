import type { NoteOrder, NoteSearchHit } from '../core/dtos/note.dtos';
import type { TopicDto } from '../core/services/topics.service';
import { normalizeSearchText } from '../core/search-text';
import { hasTopic } from '../ui/note-item/note-item.helpers';

/** A place in the Brain that lists notes. The map and a single note are routes of their own. */
export type BrainPlace = 'all' | 'passages' | 'unsorted' | 'topic';

/** How the sidebar orders topics. */
export type TopicSort = 'usage' | 'az';

export const TOPIC_SORT_KEY = 'nostos.brain.topicSort';
/** The order last chosen for note places, and for topic pages, kept apart. */
export const NOTE_ORDER_KEY = 'nostos.brain.order.notes';
export const TOPIC_ORDER_KEY = 'nostos.brain.order.topic';

export const ORDERS: readonly NoteOrder[] = ['newest', 'oldest', 'book'];

export const ORDER_LABELS: Record<NoteOrder, string> = {
  newest: 'Newest first',
  oldest: 'Oldest first',
  book: 'By book',
};

export function isOrder(value: unknown): value is NoteOrder {
  return typeof value === 'string' && (ORDERS as readonly string[]).includes(value);
}

/** What a place shows by default when no order was chosen: a topic reads best by book. */
export function defaultOrder(place: BrainPlace): NoteOrder {
  return place === 'topic' ? 'book' : 'newest';
}

/** The place's name, as its heading. */
export function placeTitle(place: BrainPlace, topic: TopicDto | null): string {
  switch (place) {
    case 'passages':
      return 'Passages';
    case 'unsorted':
      return 'Unsorted';
    case 'topic':
      return topic?.name ?? 'Topic';
    default:
      return 'All notes';
  }
}

/** "1 note", "3 notes". */
export function plural(count: number, word: string): string {
  return `${count} ${word}${count === 1 ? '' : 's'}`;
}

/** "65 notes", "1 passage", "43 notes without a topic". */
export function countLabel(place: BrainPlace, total: number): string {
  switch (place) {
    case 'passages':
      return plural(total, 'passage');
    case 'unsorted':
      return `${plural(total, 'note')} without a topic`;
    default:
      return plural(total, 'note');
  }
}

/**
 * A topic the Brain lists: one a note carries, or one the reader described
 * (its words keep it, as the server's cleanup does).
 */
export function isListedTopic(topic: TopicDto): boolean {
  return topic.usageCount > 0 || !!topic.description?.trim();
}

/** Sidebar topics: the listed ones (see {@link isListedTopic}), in the chosen order. */
export function sidebarTopics(topics: readonly TopicDto[], sort: TopicSort): TopicDto[] {
  return topics
    .filter(isListedTopic)
    .sort((a, b) =>
      sort === 'az'
        ? a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
        : b.usageCount - a.usageCount || a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
}

/** Topics whose name contains the search, accents ignored, most used first: the chips above results. */
export function topicsMatching(topics: readonly TopicDto[], query: string, limit = 8): TopicDto[] {
  const q = normalizeSearchText(query.trim());
  if (!q) return [];
  return topics
    .filter((topic) => isListedTopic(topic) && normalizeSearchText(topic.name).includes(q))
    .sort((a, b) => b.usageCount - a.usageCount || a.name.localeCompare(b.name))
    .slice(0, limit);
}

/**
 * Whether a note still belongs in the list after a change: an Unsorted note
 * that gained a topic leaves, as does a note that lost the topic (or the
 * narrowing topic) of the page it is listed on.
 */
export function stillBelongs(
  note: NoteSearchHit,
  place: BrainPlace,
  topic: TopicDto | null,
  withTopic: TopicDto | null,
): boolean {
  const has = (name: string) => hasTopic(note.topicNames, name);
  if (place === 'unsorted') return note.topicNames.length === 0;
  if (place === 'topic' && topic && !has(topic.name)) return false;
  if (withTopic && !has(withTopic.name)) return false;
  return true;
}

/** Reads a stored choice, tolerating storage that is missing or refuses access. */
export function readStored<T extends string>(key: string, valid: (value: unknown) => value is T, fallback: T): T {
  try {
    const value = localStorage.getItem(key);
    return valid(value) ? value : fallback;
  } catch {
    return fallback;
  }
}

export function writeStored(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Storage can be full or refused (private mode); the choice is a convenience.
  }
}

export function isTopicSort(value: unknown): value is TopicSort {
  return value === 'usage' || value === 'az';
}
