/**
 * A topic in the reader's own words (docs/brain.md): added under the topic's
 * title with Cmd/Ctrl+Enter, shown as paragraphs, cleared by saving nothing,
 * and shown shortened on the map card. Checked against the API.
 */
import { expect, test } from '@playwright/test';

import { apiGet, apiPost, loadFixture, newRunId } from './support/fixture';
import { cleanupBrain, findTopicId, snapshotTopicIds, type BrainSeed } from './support/brain-fixture';

test.use({ serviceWorkers: 'block' });

interface Topic {
  id: string;
  name: string;
  description?: string | null;
}

let seed: BrainSeed;
let topic: string;

test.beforeEach(async () => {
  const { baseUrl } = loadFixture();
  const run = newRunId();
  topic = `Words${run}`;
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', { type: 'physical', title: `Words ${run}`, author: 'Nostos QA' });
  seed = { bookId: book.id, topicNames: [topic], beforeTopicIds };
  await apiPost(baseUrl, `/api/books/${book.id}/notes`, { content: `A note. [[${topic}]]` });
});

test.afterEach(async () => {
  await cleanupBrain(loadFixture().baseUrl, seed);
});

const described = async (id: string) =>
  (await apiGet<Topic[]>(loadFixture().baseUrl, '/api/topics')).find((t) => t.id === id)?.description ?? null;

test('adds, edits and clears a topic in your own words', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const id = (await findTopicId(baseUrl, topic))!;
  await page.goto(`${baseUrl}/brain/topics/${id}`);

  await page.getByRole('button', { name: 'Add your own words…' }).click();
  const field = page.getByRole('textbox', { name: `Your own words about ${topic}` });
  await field.fill('Living well.\n\nAs I read it.');
  await field.press('ControlOrMeta+Enter');

  await expect(page.locator('.words-paragraph')).toHaveText(['Living well.', 'As I read it.']);
  await expect.poll(() => described(id)).toBe('Living well.\n\nAs I read it.');

  // The map card shows them.
  await page.goto(`${baseUrl}/brain/map?focus=${id}`);
  await expect(page.getByRole('region', { name: 'Selected topic' })).toContainText('Living well.');

  // Escape cancels an edit; saving nothing clears them.
  await page.goto(`${baseUrl}/brain/topics/${id}`);
  await page.getByRole('button', { name: `Edit your words about ${topic}` }).click();
  await field.fill('Not this.');
  await field.press('Escape');
  await expect(page.locator('.words-paragraph').first()).toHaveText('Living well.');

  await page.getByRole('button', { name: `Edit your words about ${topic}` }).click();
  await field.fill('');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('button', { name: 'Add your own words…' })).toBeVisible();
  await expect.poll(() => described(id)).toBeNull();
});
