/**
 * The `[[Topic]]` text rules the note composer needs, mirroring the server's
 * `TopicReferences` (Nostos.Product/Services/TopicReferences.cs). Both sides run
 * the cases in Nostos.Shared/Fixtures/topic-references.json, so they cannot drift.
 *
 * A note's text is the record of its topics (docs/topic-system.md). A topic
 * added as a pill is written as its own `[[Name]]` line; the composer shows
 * those lines as pills and never as text, and a `[[link]]` inside a sentence
 * stays in the words.
 */

const REFERENCE = /\[\[(.*?)\]\]/g;

/** The placeholder every note field uses: it teaches the inline way. */
export const NOTE_PLACEHOLDER = 'Write your thought, or link a [[Topic]]';

/** A line of references and separators only: what adding a pill writes. */
export function isReferenceLine(line: string): boolean {
  if (!line.match(REFERENCE)) return false;
  return /^[\s,;·|]*$/.test(line.replace(REFERENCE, ''));
}

/** The topic names `content` declares, trimmed and distinct (any case), in order. */
export function topicNames(content: string | null | undefined): string[] {
  return distinct([...(content ?? '').matchAll(REFERENCE)].map((match) => match[1].trim()));
}

/** Whether `names` holds `name`, compared trimmed and in any case. */
export function includesTopic(names: readonly string[], name: string): boolean {
  const key = name.trim().toLocaleLowerCase();
  return names.some((other) => other.trim().toLocaleLowerCase() === key);
}

/**
 * A note's text as the composer edits it: its own words (inline links kept),
 * and the topics written on lines of their own, which show as pills.
 */
export function splitTopics(content: string | null | undefined): { words: string; topics: string[] } {
  const words: string[] = [];
  const topics: string[] = [];
  for (const line of (content ?? '').split('\n')) {
    if (isReferenceLine(line)) topics.push(...topicNames(line));
    else words.push(line);
  }
  return {
    words: words.join('\n').replace(/\n{3,}/g, '\n\n').trim(),
    topics: distinct(topics),
  };
}

/**
 * `content` without its references to `name`. A reference inside a sentence
 * becomes its plain words, so no sentence loses a word; a line of references
 * loses it, and the line goes when nothing is left on it.
 */
export function removeTopic(content: string, name: string): string {
  const key = name.trim().toLocaleLowerCase();
  const matches = (inner: string) => inner.trim().toLocaleLowerCase() === key;
  const kept: string[] = [];
  for (const line of content.split('\n')) {
    if (!isReferenceLine(line)) {
      kept.push(line.replace(REFERENCE, (whole, inner: string) => (matches(inner) ? inner.trim() : whole)));
      continue;
    }
    const left = [...line.matchAll(REFERENCE)].filter((match) => !matches(match[1])).map((match) => match[0]);
    if (left.length === [...line.matchAll(REFERENCE)].length) kept.push(line);
    else if (left.length) kept.push(left.join(' '));
  }
  return kept.join('\n').replace(/\n{3,}/g, '\n\n').trim();
}

function distinct(names: readonly string[]): string[] {
  const seen = new Set<string>();
  return names.filter((name) => {
    const key = name.toLocaleLowerCase();
    if (!name || seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}
