import { formatAudioTime, type NoteSearchHit } from '../../core/dtos/note.dtos';
import type { TopicDto } from '../../core/services/topics.service';

const REFERENCE = /\[\[(.*?)\]\]/g;

/**
 * A note's own words as the card shows them. Lines that hold only `[[Topic]]`
 * references are left out, because the card lists the topics as chips right
 * under the text; a reference inside a sentence stays, as a link.
 */
export function thoughtForDisplay(content: string | null | undefined): string {
  if (!content) return '';
  const kept = content
    .split('\n')
    .filter((line) => !isReferenceLine(line));
  return kept
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

/** A line of references and separators only: what linking a topic writes. */
export function isReferenceLine(line: string): boolean {
  if (!line.match(REFERENCE)) return false;
  return /^[\s,;·|]*$/.test(line.replace(REFERENCE, ''));
}

/**
 * Where in its book the note sits, in words a reader uses: a page, a time in
 * the recording. An EPUB location has no number worth showing.
 */
export function noteLocationLabel(note: Pick<NoteSearchHit, 'sourceAnchorKind' | 'sourceAnchorValue' | 'anchorVerified'>): string | null {
  const value = note.sourceAnchorValue?.trim();
  if (!value) return null;
  switch (note.sourceAnchorKind?.toLowerCase()) {
    case 'pdf_page': {
      const page = Number(value);
      return Number.isInteger(page) && page > 0 ? `p. ${page}` : null;
    }
    case 'audio_timestamp': {
      const seconds = Number(value);
      return Number.isFinite(seconds) && seconds >= 0 ? formatAudioTime(seconds) : null;
    }
    default:
      return null;
  }
}

/** True when a name is already one of the note's topics, ignoring case and spaces. */
export function hasTopic(names: readonly string[], name: string): boolean {
  const wanted = name.trim().toLocaleLowerCase();
  return names.some((existing) => existing.trim().toLocaleLowerCase() === wanted);
}

/**
 * The topics a picker offers for `query`: an exact match first, then names
 * that start with it, then names that contain it; most used first within each.
 * Names the note already has are left out.
 */
export function rankTopicChoices(
  topics: readonly TopicDto[],
  query: string,
  exclude: readonly string[],
  limit = 8,
): TopicDto[] {
  const q = query.trim().toLocaleLowerCase();
  const rank = (name: string): number => {
    const n = name.toLocaleLowerCase();
    if (!q) return 3;
    if (n === q) return 0;
    if (n.startsWith(q)) return 1;
    if (n.includes(q)) return 2;
    return -1;
  };
  return topics
    .filter((topic) => !hasTopic(exclude, topic.name))
    .map((topic) => ({ topic, rank: rank(topic.name) }))
    .filter((entry) => entry.rank >= 0)
    .sort((a, b) =>
      a.rank - b.rank ||
      b.topic.usageCount - a.topic.usageCount ||
      a.topic.name.localeCompare(b.topic.name))
    .slice(0, limit)
    .map((entry) => entry.topic);
}

/**
 * Whether a name can stand inside `[[ ]]` and be read back as itself: the
 * server's rule (TopicReferences.IsValidName), so the picker never offers a
 * name the server refuses.
 */
export function isValidTopicName(name: string): boolean {
  const topic = name.trim();
  return topic.length > 0 && topic.length <= 200
    && !topic.includes('[[') && !topic.includes(']]')
    && !topic.endsWith(']') && !topic.startsWith('[')
    && !topic.includes('|') && !/[\r\n]/.test(topic);
}

/**
 * The name typed in a picker when it would create a new topic: a valid name
 * that is not already a topic.
 */
export function newTopicName(topics: readonly TopicDto[], query: string): string | null {
  const name = query.trim().replace(/\s+/g, ' ');
  if (!isValidTopicName(name)) return null;
  return topics.some((topic) => topic.name.toLocaleLowerCase() === name.toLocaleLowerCase()) ? null : name;
}
