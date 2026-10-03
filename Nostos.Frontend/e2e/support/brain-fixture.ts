/**
 * Seed/cleanup helpers for the Second Brain specs.
 *
 * Why this exists: every Playwright spec shares ONE fixture instance
 * (`workers: 1`, a single global setup), and `visual-regression.spec.ts`'s
 * `brain-empty-desktop` deliberately asserts the *pristine* empty state. A spec
 * that seeds topics into that shared database therefore poisons it for every
 * later spec — the empty-state test fails and, because the Second Brain matrix
 * is `mode: 'serial'`, its 8 remaining tests never run.
 *
 * So any spec that seeds a brain must put the fixture back the way it found it.
 * Snapshots are taken by topic id rather than by name: two specs may legitimately
 * use the same topic names ("Attention", "Memory"), and deleting by name could
 * remove a topic another spec is relying on.
 */
import { apiGet, apiPost } from './fixture';

export async function apiDelete(baseUrl: string, urlPath: string): Promise<void> {
  const res = await fetch(`${baseUrl}${urlPath}`, { method: 'DELETE' });
  // 404 is a pass: the row is already gone, which is the post-condition we want.
  if (!res.ok && res.status !== 404) {
    throw new Error(`DELETE ${urlPath} -> ${res.status}: ${await res.text()}`);
  }
}

export interface TopicRef {
  id: string;
  name: string;
}

/** Topic ids present right now, so a spec can delete only what it added. */
export async function snapshotTopicIds(baseUrl: string): Promise<Set<string>> {
  const topics = await apiGet<TopicRef[]>(baseUrl, '/api/topics');
  return new Set(topics.map((c) => c.id));
}

/** The topic created by this note content, or null if the note made none. */
export async function findTopicId(baseUrl: string, name: string): Promise<string | null> {
  const topics = await apiGet<TopicRef[]>(baseUrl, '/api/topics');
  return topics.find((c) => c.name === name)?.id ?? null;
}

export interface BrainSeed {
  bookId: string;
  topicNames: string[];
  /** Topic ids that existed before seeding (everything else this spec made). */
  beforeTopicIds: Set<string>;
}

/** Create a book plus one note per entry, and record what already existed. */
export async function seedBrain(
  baseUrl: string,
  title: string,
  notes: string[],
  topicNames: string[]
): Promise<BrainSeed> {
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
    type: 'physical',
    title,
    author: 'Nostos QA',
    categories: 'visual-qa',
  });
  for (const content of notes) {
    await apiPost(baseUrl, `/api/books/${book.id}/notes`, { content });
  }
  return { bookId: book.id, topicNames, beforeTopicIds };
}

/**
 * Undo `seedBrain`: remove the topics this spec created, then the book.
 *
 * Topics are deleted explicitly because the product's own orphan sweep
 * (`TopicCleanupWorker`) runs hourly, far too late for a test suite; until it
 * runs, a topic with no remaining notes still appears in the index and would
 * keep failing the empty-state assertion.
 *
 * Never throws: a cleanup failure must not mask the test's real result.
 */
export async function cleanupBrain(baseUrl: string, seed: BrainSeed): Promise<void> {
  try {
    const topics = await apiGet<TopicRef[]>(baseUrl, '/api/topics');
    for (const topic of topics) {
      if (!seed.beforeTopicIds.has(topic.id)) {
        await apiDelete(baseUrl, `/api/topics/${topic.id}`);
      }
    }
  } catch (error) {
    console.warn('[brain-fixture] topic cleanup failed:', error);
  }
  try {
    await apiDelete(baseUrl, `/api/books/${seed.bookId}`);
  } catch (error) {
    console.warn('[brain-fixture] book cleanup failed:', error);
  }
}
