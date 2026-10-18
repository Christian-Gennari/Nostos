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
let run: string;

test.beforeEach(async () => {
  const { baseUrl } = loadFixture();
  run = newRunId();
  topic = `A long phone topic name ${run}`;
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

/**
 * Visible controls whose tap area is smaller than 44px, by name and size. A
 * control may draw small and answer a larger tap through an absolutely placed
 * `::after`; that area counts. Links inside a note's words are text, not
 * controls, and are left out.
 */
async function smallControls(page: Page): Promise<string[]> {
  return page.evaluate(() => [...document.querySelectorAll(
    'button, select, textarea, input:not([type=hidden]):not([type=checkbox]), a[href], [role=combobox]',
  )].flatMap((element) => {
    const rect = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    if (!rect.width || !rect.height || style.visibility === 'hidden' || style.display === 'none') return [];
    if (element.closest('[aria-hidden="true"], app-app-dock, .visually-hidden, .note-item-thought, .sort-thought')) return [];
    let width = rect.width;
    let height = rect.height;
    const after = getComputedStyle(element, '::after');
    if (after.content !== 'none' && after.position === 'absolute') {
      const px = (value: string) => parseFloat(value) || 0;
      width = Math.max(width, rect.width - px(after.left) - px(after.right));
      height = Math.max(height, rect.height - px(after.top) - px(after.bottom));
    }
    if (width >= 43.5 && height >= 43.5) return [];
    const name = (element.getAttribute('aria-label') || element.textContent || element.tagName).trim().replace(/\s+/g, ' ');
    return [`${name.slice(0, 40)} ${Math.round(width)}x${Math.round(height)}`];
  }));
}

for (const viewport of [{ width: 390, height: 844 }, { width: 820, height: 1180 }]) {
  test(`every place fits a ${viewport.width}px touch screen`, async ({ browser }) => {
    const { baseUrl } = loadFixture();
    const topicId = await findTopicId(baseUrl, topic);
    const context = await browser.newContext({ viewport, hasTouch: true, isMobile: true, serviceWorkers: 'block' });
    const page = await context.newPage();
    try {
      const q = `q=${encodeURIComponent(run)}`;
      const places: Array<[string, string]> = [
        [`/brain?${q}`, 'app-note-item'],
        [`/brain/unsorted?${q}`, 'app-note-item'],
        [`/brain/topics/${topicId}`, 'app-note-item'],
        [`/brain/notes/${noteId}`, 'app-note-item'],
        [`/brain/unsorted/sort?book=${seed.bookId}`, '.sort-quote, .sort-thought'],
        ['/brain/map', '.map-stage'],
      ];
      const failures: string[] = [];
      for (const [place, ready] of places) {
        await page.goto(`${baseUrl}${place}`);
        await expect(page.locator(ready).first()).toBeVisible();
        const small = await smallControls(page);
        if (small.length) failures.push(`${place}: ${small.join('; ')}`);
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
        if (overflow > 0) failures.push(`${place}: scrolls sideways by ${overflow}px`);
      }

      // Editing: the highlighter pens and the form's buttons too.
      await page.goto(`${baseUrl}/brain/notes/${noteId}`);
      await page.getByRole('button', { name: 'More actions for this note' }).click();
      await page.getByRole('menuitem', { name: 'Edit note' }).click();
      await expect(page.getByRole('group', { name: 'Highlight colour' })).toBeVisible();
      const editing = await smallControls(page);
      if (editing.length) failures.push(`editing: ${editing.join('; ')}`);
      expect(failures).toEqual([]);
    } finally {
      await context.close();
    }
  });
}

test('a tap on a topic name opens it, never the cross beside it', async ({ browser }) => {
  const { baseUrl } = loadFixture();
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, serviceWorkers: 'block' });
  const page = await context.newPage();
  try {
    await page.goto(`${baseUrl}/brain/notes/${noteId}`);
    const name = page.locator('.note-item-topic-name').first();
    await expect(name).toBeVisible();
    const box = (await name.boundingBox())!;
    const hit = await page.evaluate(([x, y]) => document.elementFromPoint(x, y)?.closest('a, button')?.className ?? '',
      [box.x + box.width - 2, box.y + box.height / 2]);
    expect(hit).toContain('note-item-topic-name');
  } finally {
    await context.close();
  }
});

test('the phone toolbar stays on one row with a long topic name', async ({ browser }) => {
  const { baseUrl } = loadFixture();
  const topicId = await findTopicId(baseUrl, topic);
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, serviceWorkers: 'block' });
  const page = await context.newPage();
  try {
    await page.goto(`${baseUrl}/brain/topics/${topicId}`);
    const place = (await page.locator('.place-switch').boundingBox())!;
    const order = (await page.getByRole('button', { name: 'Topic actions' }).boundingBox())!;
    expect(Math.abs(place.y + place.height / 2 - (order.y + order.height / 2))).toBeLessThan(4);
  } finally {
    await context.close();
  }
});

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
