import { expect, test, type Page } from '@playwright/test';

import type { Note } from '../src/app/core/dtos/note.dtos';
import { apiPost, loadFixture, newRunId } from './support/fixture';
import { cleanupBrain, snapshotTopicIds } from './support/brain-fixture';

/**
 * The wide Brain (issue #1531): the list narrows to an index and the selected
 * note opens beside it, with the selection in the URL (`?note=`), the keyboard
 * walking the list, and the note's own page untouched on a phone.
 */
const WIDTH = 1440;

test.describe('Brain list and detail on a wide screen (#1531)', () => {
  test.use({ serviceWorkers: 'block' });

  const run = newRunId();
  const query = `wide-${run}`;
  const topicName = `Wide topic ${run}`;
  let baseUrl: string;
  let bookId: string;
  let beforeTopicIds: Set<string>;
  const quotes = new Map<string, string>();

  test.beforeAll(async () => {
    ({ baseUrl } = loadFixture());
    beforeTopicIds = await snapshotTopicIds(baseUrl);
    const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
      type: 'physical',
      title: `Wide Brain ${run}`,
      author: 'Nostos QA',
    });
    bookId = book.id;
    for (const number of [1, 2, 3]) {
      const quote = `${query} quote ${number}: the line that was marked.`;
      const created = await apiPost<Note>(baseUrl, `/api/books/${book.id}/notes`, {
        selectedText: quote,
        content: `${query} thought ${number}, written beside the line.\n\n[[${topicName}]]`,
      });
      quotes.set(created.id, quote);
      // Distinct creation times, so Newest first has a stable order.
      await new Promise((resolve) => setTimeout(resolve, 20));
    }
  });

  test.afterAll(async () => {
    if (!baseUrl || !bookId) return;
    await cleanupBrain(baseUrl, { bookId, topicNames: [], beforeTopicIds });
  });

  async function openBrain(page: Page, width = WIDTH): Promise<void> {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(`${baseUrl}/brain?q=${encodeURIComponent(query)}`, { waitUntil: 'domcontentloaded' });
    await expect(page.getByRole('listbox', { name: 'Notes' })).toBeVisible();
  }

  function noteIdFromUrl(page: Page): string | null {
    return new URL(page.url()).searchParams.get('note');
  }

  test('selects, moves with the keyboard, reloads, and comes back from the book', async ({ page }) => {
    await openBrain(page);

    const rows = page.getByRole('option');
    await expect(rows).toHaveCount(3);
    const selected = page.getByRole('region', { name: 'Selected note' });
    await expect(selected).toContainText('Choose a note');

    const ids = await rows.evaluateAll((nodes) => nodes.map((node) => node.getAttribute('data-note-id')));
    expect(ids.filter((id): id is string => !!id)).toHaveLength(3);
    const [first, second] = ids as string[];

    await rows.nth(0).click();
    await expect.poll(() => noteIdFromUrl(page)).toBe(first);
    await expect(rows.nth(0)).toHaveAttribute('aria-selected', 'true');
    await expect(selected).toContainText(quotes.get(first) ?? '');

    await rows.nth(0).focus();
    await page.keyboard.press('ArrowDown');
    await expect.poll(() => noteIdFromUrl(page)).toBe(second);
    await expect(rows.nth(1)).toHaveAttribute('aria-selected', 'true');
    await expect(selected).toContainText(quotes.get(second) ?? '');
    await expect(rows.nth(1)).toBeFocused();

    await page.keyboard.press('Enter');
    await expect(selected).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(rows.nth(1)).toBeFocused();

    // Reload restores the note, the list and the filter.
    await page.reload({ waitUntil: 'domcontentloaded' });
    await expect.poll(() => noteIdFromUrl(page)).toBe(second);
    await expect(page.getByRole('searchbox')).toHaveValue(query);
    await expect(rows.nth(1)).toHaveAttribute('aria-selected', 'true');
    await expect(selected).toContainText(quotes.get(second) ?? '');

    // Leaving for the book and coming Back returns to the same note in the
    // same filtered list.
    await selected.getByRole('link', { name: /Open book/ }).click();
    await expect(page).toHaveURL(new RegExp(`/library/${bookId}`));
    await page.goBack({ waitUntil: 'domcontentloaded' });
    await expect.poll(() => noteIdFromUrl(page)).toBe(second);
    await expect(page.getByRole('searchbox')).toHaveValue(query);
    await expect(rows.nth(1)).toHaveAttribute('aria-selected', 'true');
    await expect(selected).toContainText(quotes.get(second) ?? '');
  });

  test('a deep link to a note opens it in the pane', async ({ page }) => {
    await page.setViewportSize({ width: WIDTH, height: 900 });
    const [first] = [...quotes.keys()];
    await page.goto(`${baseUrl}/brain/notes/${first}`, { waitUntil: 'domcontentloaded' });

    await expect(page).toHaveURL(new RegExp(`/brain\\?note=${first}$`));
    const selected = page.getByRole('region', { name: 'Selected note' });
    await expect(selected).toContainText(quotes.get(first) ?? '');
  });

  test('a phone keeps the list and the note’s own page, and opens ?note= there', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${baseUrl}/brain?q=${encodeURIComponent(query)}`, { waitUntil: 'domcontentloaded' });

    await expect(page.getByRole('listbox')).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Selected note' })).toHaveCount(0);
    await expect(page.locator('app-note-item')).toHaveCount(3);

    const [first] = [...quotes.keys()];
    await page.goto(`${baseUrl}/brain?note=${first}`, { waitUntil: 'domcontentloaded' });
    await expect(page).toHaveURL(new RegExp(`/brain/notes/${first}$`));
    await expect(page.locator('app-brain-note-page .note-column')).toBeVisible();
    await expect(page.locator('app-brain-note-page .note-item-quote')).toContainText(quotes.get(first) ?? '');
  });
});
