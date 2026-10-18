/**
 * The map as a place (docs/brain.md): the finder centres a topic, the card
 * shows a line from its notes, Open topic goes to the topic, and Back returns
 * to the same camera and selection. Show on map opens centred on a topic.
 */
import { expect, test, type Page } from '@playwright/test';
import { PNG } from 'pngjs';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { cleanupBrain, findTopicId, snapshotTopicIds, type BrainSeed } from './support/brain-fixture';

test.use({ serviceWorkers: 'block' });

let seed: BrainSeed;
let ethics: string;
let virtue: string;
let quote: string;

test.beforeEach(async () => {
  const { baseUrl } = loadFixture();
  const run = newRunId();
  ethics = `Ethics${run}`;
  virtue = `Virtue${run}`;
  quote = `The unexamined map ${run} is not worth reading.`;
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', { type: 'physical', title: `Map ${run}`, author: 'Nostos QA' });
  seed = { bookId: book.id, topicNames: [ethics, virtue], beforeTopicIds };
  await apiPost(baseUrl, `/api/books/${book.id}/notes`, { content: `Joined. [[${ethics}]] [[${virtue}]]` });
  await apiPost(baseUrl, `/api/books/${book.id}/notes`, { content: `Why it matters. [[${ethics}]]`, selectedText: quote });
});

test.afterEach(async () => {
  await cleanupBrain(loadFixture().baseUrl, seed);
});

const card = (page: Page) => page.getByRole('region', { name: 'Selected topic' });
const stage = (page: Page) => page.locator('.sigma-container');

/** Mean per-channel difference between two screenshots of the same size, 0–255. */
function meanDifference(a: Buffer, b: Buffer): number {
  const one = PNG.sync.read(a);
  const two = PNG.sync.read(b);
  if (one.width !== two.width || one.height !== two.height) return 255;
  let total = 0;
  for (let i = 0; i < one.data.length; i++) total += Math.abs(one.data[i] - two.data[i]);
  return total / one.data.length;
}

async function findOnMap(page: Page, name: string): Promise<void> {
  const finder = page.getByRole('combobox', { name: 'Find a topic on the map' });
  await finder.fill(name);
  await expect(page.getByRole('option', { name: new RegExp(name) })).toBeVisible();
  await finder.press('Enter');
  await expect(card(page).getByRole('heading', { name })).toBeVisible();
}

test('finds a topic, opens it, and Back returns to the same view', async ({ page }) => {
  const { baseUrl } = loadFixture();
  await page.goto(`${baseUrl}/brain/map`);
  await expect(page.getByRole('heading', { name: 'Map', level: 1 })).toBeVisible();

  await findOnMap(page, ethics);
  await expect(page).toHaveURL(/focus=/);
  await expect(card(page)).toContainText(quote);

  // Move the camera away from any default the map would choose by itself.
  await page.getByRole('button', { name: 'Zoom in' }).click();
  await page.waitForTimeout(600);
  const before = await stage(page).screenshot();

  await card(page).getByRole('button', { name: 'Open topic' }).click();
  const ethicsId = await findTopicId(baseUrl, ethics);
  await expect(page).toHaveURL(new RegExp(`/brain/topics/${ethicsId}`));
  await expect(page.getByRole('heading', { name: ethics, level: 1 })).toBeVisible();

  await page.goBack();
  await expect(page).toHaveURL(new RegExp(`/brain/map\\?focus=${ethicsId}`));
  await expect(card(page).getByRole('heading', { name: ethics })).toBeVisible();
  await page.waitForTimeout(800);
  const after = await stage(page).screenshot();
  expect(meanDifference(before, after)).toBeLessThan(2);

  // The measure tells views apart: framing the whole graph is another view.
  await page.getByRole('button', { name: 'Fit to view' }).click();
  await page.waitForTimeout(800);
  expect(meanDifference(before, await stage(page).screenshot())).toBeGreaterThan(2);
});

test('Show on map opens the map on the topic', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const virtueId = await findTopicId(baseUrl, virtue);
  await page.goto(`${baseUrl}/brain/topics/${virtueId}`);
  await page.getByRole('button', { name: 'Topic actions' }).click();
  await page.getByRole('menuitem', { name: 'Show on map' }).click();
  await expect(page).toHaveURL(new RegExp(`/brain/map\\?focus=${virtueId}`));
  await expect(card(page).getByRole('heading', { name: virtue })).toBeVisible();
  await expect(page.locator('.nav-item', { hasText: 'Map' })).toHaveAttribute('aria-current', 'page');
});

test('works on a phone', async ({ page }) => {
  const { baseUrl } = loadFixture();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`${baseUrl}/brain/map`);
  await findOnMap(page, virtue);
  await card(page).getByRole('button', { name: 'Open topic' }).click();
  await expect(page).toHaveURL(/\/brain\/topics\//);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});
