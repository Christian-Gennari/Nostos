/**
 * The command palette as one search across Nostos.
 *
 * Seeds a book with notes that create a topic, and a document, all named
 * with one run-unique word, then finds and opens each through Ctrl+K. Cleans
 * up after itself: the shared fixture's Brain must stay empty for the
 * visual-regression empty state.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { apiDelete, cleanupBrain, findTopicId, seedBrain, type BrainSeed } from './support/brain-fixture';
import { enableAssistant } from './support/assistant';

test.use({ serviceWorkers: 'block' });
test.describe.configure({ mode: 'serial' });

const run = newRunId().replace(/[^a-z0-9]/gi, '').slice(-6).toLowerCase();
const word = `lantern${run}`;
let seed: BrainSeed;
let documentId: string;
let topicId: string;
const createdWritings: string[] = [];

test.beforeAll(async () => {
  const { baseUrl } = loadFixture();
  seed = await seedBrain(
    baseUrl,
    `Palette QA ${run}`,
    [`A ${word} hung by the door. [[${word}]]`, `Second thought on the ${word}. [[${word}]]`],
    [word],
  );
  topicId = (await findTopicId(baseUrl, word))!;
  const doc = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
    name: `${word} essay`,
    type: 'Document',
    parentId: null,
  });
  documentId = doc.id;
  createdWritings.push(doc.id);
});

test.afterAll(async () => {
  const { baseUrl } = loadFixture();
  for (const id of createdWritings) await apiDelete(baseUrl, `/api/writings/${id}`);
  await cleanupBrain(baseUrl, seed);
});

async function openPalette(page: Page, path = '/library'): Promise<void> {
  if (!page.url().includes(path)) {
    await page.goto(`${loadFixture().baseUrl}${path}`, { waitUntil: 'domcontentloaded' });
    await expect(page.locator('app-app-dock')).toBeVisible();
  }
  await page.keyboard.press('Control+k');
  await expect(page.getByTestId('palette-input')).toBeFocused();
}

const headings = (page: Page) => page.locator('.palette-heading');

test('finds a topic, its notes and a document by one word, and opens each', async ({ page }) => {
  await openPalette(page);
  await page.keyboard.type(word);
  await expect(headings(page)).toHaveText(['Topics', 'Notes', 'Documents']);
  await expect(page.getByTestId('palette-item-note')).toHaveCount(2);
  // The trailing topic tag is not part of the excerpt.
  await expect(page.getByTestId('palette-item-note').first()).not.toContainText(`[[`);

  // The highlight starts on the first row: the topic.
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(new RegExp(`/brain/topics/${topicId}`));
  await expect(page.getByTestId('command-palette')).toHaveCount(0);

  await openPalette(page, '/brain');
  await page.keyboard.type(word);
  await expect(page.getByTestId('palette-item-note')).toHaveCount(2);
  await page.keyboard.press('ArrowDown');
  await expect(page.getByTestId('palette-input')).toHaveAttribute('aria-activedescendant', 'palette-option-1');
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/brain\/notes\//);

  await openPalette(page, '/brain');
  await page.keyboard.type(word);
  await page.getByTestId('palette-item-document').click();
  await expect(page).toHaveURL(new RegExp(`studio\\?writingId=${documentId}`));
});

test('remembers what was opened, and offers it before anything is typed', async ({ page }) => {
  const { baseUrl } = loadFixture();
  await page.goto(`${baseUrl}/brain/topics/${topicId}`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('app-app-dock')).toBeVisible();
  await openPalette(page, '/brain');

  await expect(headings(page).first()).toHaveText('Recent');
  await expect(page.locator('.palette-section').first()).toContainText(word);
});

test('Escape closes and returns focus to where it was', async ({ page }) => {
  await page.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  const brain = page.locator('app-app-dock .dock-item[title="The Brain"]');
  await brain.focus();
  await page.keyboard.press('Control+k');
  await expect(page.getByTestId('palette-input')).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('command-palette')).toHaveCount(0);
  await expect(brain).toBeFocused();
});

test('Add a book opens the Library dialog from anywhere, once', async ({ page }) => {
  await openPalette(page, '/studio');
  await page.keyboard.type('add a book');
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/\/library$/);
  await expect(page.locator('app-add-book-modal [role="dialog"]')).toBeVisible();
});

test('New document creates one and opens it, without a name prompt', async ({ page }) => {
  page.on('dialog', () => {
    throw new Error('New document must not prompt');
  });
  await openPalette(page);
  await page.keyboard.type('new document');
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/studio\?writingId=/);
  const id = new URL(page.url()).searchParams.get('writingId')!;
  createdWritings.push(id);
});

test('with Ask Nostos on, the query becomes a question', async ({ page }) => {
  await page.route('**/api/assistant/status', (route) => route.fulfill({ json: { available: true } }));
  await enableAssistant(page);
  await openPalette(page);
  await page.keyboard.type(`what connects ${word}`);

  const ask = page.getByTestId('palette-item-ask');
  // The curly quotes are drawn by <q>, so they are not part of the text.
  await expect(ask).toContainText(`Ask Nostos: what connects ${word}`);
  await expect(ask.locator('q')).toHaveText(`what connects ${word}`);
  await page.keyboard.press('End');
  await page.keyboard.press('Enter');

  await expect(page.getByTestId('assistant-panel')).toBeVisible();
  await expect(page.getByTestId('assistant-composer')).toHaveValue(`what connects ${word}`);
});

test('a prefix narrows the search to one kind, and matches are marked', async ({ page }) => {
  await openPalette(page);
  await page.keyboard.type(`#${word}`);
  await expect(headings(page)).toHaveText(['Topics']);
  await expect(page.getByTestId('palette-item-topic').locator('mark')).toHaveText(word);

  await page.getByTestId('palette-input').fill('> theme');
  await expect(headings(page)).toHaveText(['Actions']);
  await expect(page.locator('.palette-item')).toHaveCount(1);
});
