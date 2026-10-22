import type { NoteSearchHit } from '../core/dtos/note.dtos';

/**
 * What a writing touches (issue #1162), worked out from what it cites: never
 * stored, never tagged. A writing reaches a topic only through a cited note
 * that carries it, so these are pure functions of the cited ids (read live
 * from the editor) and the notes as the server has them now.
 */

/** Note ids compare lower-case: the editor and the server may differ in case. */
export function sourceKey(id: string | null | undefined): string {
  return (id ?? '').toLowerCase();
}

/**
 * The topics of the cited notes, each once: the one most sources share first,
 * then by name. Kept sources that are not cited never count.
 */
export function touchedTopics(
  citedIds: readonly string[],
  lookup: ReadonlyMap<string, NoteSearchHit>,
): string[] {
  const counts = new Map<string, { name: string; sources: number }>();
  for (const id of citedIds) {
    for (const name of lookup.get(sourceKey(id))?.topicNames ?? []) {
      const key = name.toLocaleLowerCase();
      const entry = counts.get(key);
      if (entry) entry.sources++;
      else counts.set(key, { name, sources: 1 });
    }
  }
  return [...counts.values()]
    .sort((a, b) => b.sources - a.sources || a.name.localeCompare(b.name))
    .map((entry) => entry.name);
}

/**
 * Cited ids the server was asked about and does not have: the note, its
 * passage or its book was deleted. Ids not yet looked up are not called
 * removed, so a citation just pasted never flashes as missing.
 */
export function removedCitedIds(
  citedIds: readonly string[],
  lookup: ReadonlyMap<string, NoteSearchHit>,
  lookedUp: ReadonlySet<string>,
): string[] {
  return citedIds.filter((id) => lookedUp.has(sourceKey(id)) && !lookup.has(sourceKey(id)));
}
