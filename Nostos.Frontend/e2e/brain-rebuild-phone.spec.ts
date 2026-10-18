/**
 * The Brain on a touch screen (docs/brain.md): every place is laid out on one
 * column with no sideways scroll, every control is a 44px target, and the
 * place switch opens the sidebar as a drawer whose controls stay out of reach
 * while it is closed.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { cleanupBrain, findTopicId, snapshotTopicIds, type BrainSeed } from './support/brain-fixture';

test.use({ serviceWorkers: 'block' });

let seed: BrainSeed;
let topic: string;
let noteId: string;

test.beforeEach(async () => {
  const { baseUrl } = loadFixture();
  const run = newRunId();
  topic = `Phone${run}`;
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', { type: 'physical', title: `Phone ${run}`, author: 'Nostos QA' });
  seed = { bookId: book.id, topicNames: [topic], beforeTopicIds };
  const note = await apiPost<{ id: string }>(baseUrl, `/api/books/${book.id}/notes`, {
    content: `A note with a topic. [[${topic}]]`,
    selectedText: `A quote for the phone ${run}.`,
  });
  noteId = note.id;
  await apiPost(baseUrl, `/api/books/${book.id}/notes`, { content: `A loose note ${run}.` });
});

test.afterEach(async () => {
  await cleanupBrain(loadFixture().baseUrl, seed);
});

/** Visible controls smaller than 44px, by name and size. */
async function smallControls(page: Page): Promise<string[]> {
  return page.evaluate(() => [...document.querySelectorAll(
    'button, select, textarea, input:not([type=hidden]):not([type=checkbox]), a[appButton], [role=combobox]',
  )].flatMap((element) => {
    const rect = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    if (!rect.width || !rect.height || style.visibility === 'hidden' || style.display === 'none') return [];
    if (element.closest('[aria-hidden="true"], app-app-dock, .visually-hidden')) return [];
    if (rect.width >= 44 && rect.height >= 44) return [];
    const name = (element.getAttribute('aria-label') || element.textContent || element.tagName).trim().replace(/\s+/g, ' ');
    return [`${name.slice(0, 40)} ${Math.round(rect.width)}x${Math.round(rect.height)}`];
  }));
}

for (const viewport of [{ width: 390, height: 844 }, { width: 820, height: 1180 }]) {
  test(`every place fits a ${viewport.width}px touch screen`, async ({ browser }) => {
    const { baseUrl } = loadFixture();
    const topicId = await findTopicId(baseUrl, topic);
    const context = await browser.newContext({ viewport, hasTouch: true, isMobile: true, serviceWorkers: 'block' });
    const page = await context.newPage();
    try {
      const places = ['/brain', '/brain/passages', '/brain/unsorted', `/brain/topics/${topicId}`, `/brain/notes/${noteId}`,
        `/brain/unsorted/sort?book=${seed.bookId}`, '/brain/map'];
      const failures: string[] = [];
      for (const place of places) {
        await page.goto(`${baseUrl}${place}`);
        await expect(page.locator('app-brain-list-page, app-brain-note-page, app-brain-sort-page, app-brain-map-page').first()).toBeVisible();
        await page.waitForLoadState('networkidle');
        const small = await smallControls(page);
        if (small.length) failures.push(`${place}: ${small.join('; ')}`);
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
        if (overflow > 0) failures.push(`${place}: scrolls sideways by ${overflow}px`);
      }
      expect(failures).toEqual([]);
    } finally {
      await context.close();
    }
  });
}

test('the place switch opens the sidebar as a drawer', async ({ browser }) => {
  const { baseUrl } = loadFixture();
  const topicId = await findTopicId(baseUrl, topic);
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, serviceWorkers: 'block' });
  const page = await context.newPage();
  try {
    await page.goto(`${baseUrl}/brain/topics/${topicId}`);
    // Closed: the selected topic's actions are not reachable.
    await expect(page.getByRole('button', { name: `Rename ${topic}` })).toBeHidden();

    await page.locator('.place-switch').click();
    await expect(page.getByRole('button', { name: `Rename ${topic}` })).toBeVisible();
    await page.getByRole('link', { name: /^Unsorted/ }).click();
    await expect(page).toHaveURL(/\/brain\/unsorted/);
    await expect(page.getByRole('link', { name: /^Passages/ })).toBeHidden();
    await expect(page.getByRole('link', { name: 'Sort one by one' })).toBeVisible();
  } finally {
    await context.close();
  }
});
