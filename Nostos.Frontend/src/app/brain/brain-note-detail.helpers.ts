import type { NoteContextDto, TopicWritingUsageDto } from '../core/services/topics.service';
import type { CitingWriting } from '../ui/note-item/note-item.component';
import { replaceTopicReferences, thoughtForDisplay } from '../ui/note-item/note-item.helpers';

/** The reader-facing text of a note: `[[Topic]]` references become their word or go. */
export function plainNoteText(content: string | null | undefined): string {
  const words = replaceTopicReferences(thoughtForDisplay(content ?? ''), () => '');
  return words.replace(/[ \t]{2,}/g, ' ').replace(/[ \t]+\n/g, '\n').trim();
}

/** One note that shares at least one topic with the open note. */
export interface RelatedNote {
  id: string;
  bookTitle: string;
  createdAt?: string;
  excerpt: string;
  /** The topic names the two notes have in common, in the order asked. */
  sharedTopics: string[];
}

/** One topic's notes, as read for the related-notes section. */
export interface TopicNotes {
  topicName: string;
  notes: readonly NoteContextDto[];
}

/** One topic's writing usage, as read for the citing-writings meta. */
export interface TopicUsage {
  usage: TopicWritingUsageDto | null;
}

/**
 * The notes that share topics with `noteId`, most shared topics first, then
 * newest. The note itself is never related to itself; `limit` caps the pane.
 *
 * Reads only what the topic endpoints already carry, so the wide pane needs no
 * per-note API.
 */
export function relatedNotesFrom(
  topics: readonly TopicNotes[],
  noteId: string,
  limit = 4,
): RelatedNote[] {
  const wanted = noteId.toLowerCase();
  const byId = new Map<string, { note: NoteContextDto; shared: Set<string> }>();
  for (const topic of topics) {
    for (const note of topic.notes) {
      if (note.noteId.toLowerCase() === wanted) continue;
      const key = note.noteId.toLowerCase();
      const found = byId.get(key);
      if (found) {
        found.shared.add(topic.topicName);
      } else {
        byId.set(key, { note, shared: new Set([topic.topicName]) });
      }
    }
  }
  return [...byId.values()]
    .sort((a, b) =>
      b.shared.size - a.shared.size
      || (Date.parse(b.note.createdAt ?? '') || 0) - (Date.parse(a.note.createdAt ?? '') || 0))
    .slice(0, limit)
    .map(({ note, shared }) => ({
      id: note.noteId,
      bookTitle: note.bookTitle,
      createdAt: note.createdAt,
      excerpt: (note.selectedText?.trim() || plainNoteText(note.content)).trim(),
      sharedTopics: [...shared],
    }));
}

/**
 * The writings that cite `noteId`, named by the topic usage the server already
 * reports. A writing that cites notes under two of the note's topics is named
 * once.
 */
export function writingsCitingNote(
  topics: readonly TopicUsage[],
  noteId: string,
): CitingWriting[] {
  const wanted = noteId.toLowerCase();
  const byId = new Map<string, CitingWriting>();
  for (const { usage } of topics) {
    if (!usage) continue;
    const cited = usage.noteCitations.find((citation) => citation.noteId.toLowerCase() === wanted);
    if (!cited?.writingIds?.length) continue;
    const names = new Map(usage.writings.map((writing) => [writing.id.toLowerCase(), writing.name]));
    for (const id of cited.writingIds) {
      const name = names.get(id.toLowerCase());
      if (name && !byId.has(id.toLowerCase())) byId.set(id.toLowerCase(), { id, name });
    }
  }
  return [...byId.values()];
}
