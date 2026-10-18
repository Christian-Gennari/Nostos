/**
 * Sort one by one (docs/brain.md): a whole screen for one unsorted note.
 * Sorted with the keyboard alone, resumed after a reload, and the counts
 * agree with the server.
 */
import { expect, test } from '@playwright/test';

import { apiGet, apiPost, loadFixture, newRunId } from './support/fixture';
import { cleanupBrain, snapshotTopicIds, type BrainSeed } from './support/brain-fixture';

test.use({ serviceWorkers: 'block' });

interface Hit {
  id: string;
  topicNames: string[];
}

let seed: BrainSeed;
let run: string;
const ids: string[] = [];

test.beforeEach(async () => {
  const { baseUrl } = loadFixture();
  run = newRunId();
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', { type: 'physical', title: `Sort ${run}`, author: 'Nostos QA' });
  seed = { bookId: book.id, topicNames: [], beforeTopicIds };
  ids.length = 0;
  for (const text of ['First', 'Second', 'Third', 'Fourth']) {
    const note = await apiPost<{ id: string }>(baseUrl, `/api/books/${book.id}/notes`, {
      content: `${text} loose note ${run}.`,
    });
    ids.push(note.id);
  }
});

test.afterEach(async () => {
  await cleanupBrain(loadFixture().baseUrl, seed);
});

const topics = async (id: string) => (await apiGet<Hit>(loadFixture().baseUrl, `/api/notes/${id}`)).topicNames;

test('sorts notes with the keyboard alone and resumes after a reload', async ({ page }) => {
  const { baseUrl } = loadFixture();
  await page.goto(`${baseUrl}/brain/unsorted?book=${seed.bookId}&order=oldest`);
  await page.getByRole('link', { name: 'Sort one by one' }).click();
  await expect(page).toHaveURL(/\/brain\/unsorted\/sort/);

  const field = page.getByRole('combobox', { name: 'Topics for this note' });
  await expect(page.getByText(`First loose note ${run}.`)).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'of 4' })).toHaveText('1 of 4');

  // Two topics, then Enter on the empty field saves and moves on.
  await field.fill(`Alpha${run}`);
  await field.press('Enter');
  await field.fill(`Beta${run}`);
  await field.press('Enter');
  await field.press('Enter');
  await expect(page.getByText(`Second loose note ${run}.`)).toBeVisible();
  await expect.poll(() => topics(ids[0])).toEqual([`Alpha${run}`, `Beta${run}`]);
  await expect(page.getByRole('status').filter({ hasText: 'of 3' })).toHaveText('1 of 3');

  // → skips, ← comes back, Recent offers the topic just used.
  await field.press('ArrowRight');
  await expect(page.getByText(`Third loose note ${run}.`)).toBeVisible();
  await field.press('ArrowLeft');
  await expect(page.getByText(`Second loose note ${run}.`)).toBeVisible();
  await field.fill(`alp`);
  await field.press('Enter');
  await field.press('Enter');
  await expect(page.getByText(`Third loose note ${run}.`)).toBeVisible();
  await expect.poll(() => topics(ids[1])).toEqual([`Alpha${run}`]);

  // Skip to the fourth, reload: the place is kept.
  await field.press('ArrowRight');
  await expect(page.getByText(`Fourth loose note ${run}.`)).toBeVisible();
  await page.reload();
  await expect(page.getByText(`Fourth loose note ${run}.`)).toBeVisible();

  // Unsorted counts what the server counts.
  const summary = await apiGet<{ unsorted: number }>(baseUrl, '/api/notes/summary');
  await page.keyboard.press('Escape');
  await expect(page).toHaveURL(/\/brain\/unsorted/);
  await expect(page.locator('.nav-item', { hasText: 'Unsorted' }).locator('.count-badge')).toHaveText(String(summary.unsorted));
  expect(await topics(ids[2])).toEqual([]);
});

test('shows All sorted when the queue is done', async ({ page }) => {
  const { baseUrl } = loadFixture();
  await page.goto(`${baseUrl}/brain/unsorted/sort?book=${seed.bookId}&order=oldest`);
  const field = page.getByRole('combobox', { name: 'Topics for this note' });
  for (let i = 0; i < 4; i++) {
    await expect(page.getByRole('status').filter({ hasText: `of ${4 - i}` })).toBeVisible();
    await field.fill(`Done${run}`);
    await field.press('Enter');
    await field.press('Enter');
  }
  await expect(page.getByRole('heading', { name: 'All sorted' })).toBeVisible();
});
