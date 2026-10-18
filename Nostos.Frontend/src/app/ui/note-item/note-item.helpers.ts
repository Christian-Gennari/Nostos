import { formatAudioTime, parseAudioSeconds, type NoteSearchHit } from '../../core/dtos/note.dtos';
import { normalizeSearchText } from '../../core/search-text';
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
 * the recording. Only a verified location is shown, the same rule the card's
 * "Open in book" link follows; an EPUB location has no number worth showing.
 */
export function noteLocationLabel(
  note: Pick<NoteSearchHit, 'sourceAnchorKind' | 'sourceAnchorValue' | 'anchorVerified'>,
): string | null {
  if (!note.anchorVerified) return null;
  const value = note.sourceAnchorValue?.trim() ?? '';
  switch (note.sourceAnchorKind?.toLowerCase()) {
    case 'pdf_page':
      return /^[1-9]\d*$/.test(value) ? `p. ${value}` : null;
    case 'audio_timestamp': {
      const seconds = parseAudioSeconds(value);
      return seconds === null ? null : formatAudioTime(seconds);
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

/** A topic with its name folded once for matching (see {@link normalizeSearchText}). */
export interface TopicChoice {
  topic: TopicDto;
  key: string;
}

/** Fold every name once, most used first, so a keystroke only filters. */
export function topicChoices(topics: readonly TopicDto[]): TopicChoice[] {
  return topics
    .map((topic) => ({ topic, key: normalizeSearchText(topic.name.trim()) }))
    .sort((a, b) => b.topic.usageCount - a.topic.usageCount || a.key.localeCompare(b.key));
}

/**
 * The topics a picker offers for `query`: an exact match first, then names
 * that start with it, then names that contain it, accents ignored; most used
 * first within each. Names the note already has are left out.
 */
export function rankTopicChoices(
  choices: readonly TopicChoice[],
  query: string,
  exclude: readonly string[],
  limit = 8,
): TopicDto[] {
  const q = normalizeSearchText(query.trim());
  const excluded = new Set(exclude.map((name) => normalizeSearchText(name.trim())));
  const groups: TopicDto[][] = [[], [], []];
  for (const { topic, key } of choices) {
    if (excluded.has(key)) continue;
    const rank = !q || key.startsWith(q) ? (key === q ? 0 : 1) : key.includes(q) ? 2 : -1;
    if (rank >= 0 && groups[rank].length < limit) groups[rank].push(topic);
  }
  return groups.flat().slice(0, limit);
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

/** Topics by trimmed, lower-cased name: what chips and inline links resolve against. */
export function topicLookup(topics: readonly TopicDto[]): ReadonlyMap<string, TopicDto> {
  return new Map(topics.map((topic) => [topic.name.trim().toLocaleLowerCase(), topic]));
}
