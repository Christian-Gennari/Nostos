import { expect, test, type Page } from '@playwright/test';

import type { Note } from '../src/app/core/dtos/note.dtos';
import { apiPost, loadFixture, newRunId } from './support/fixture';
import { cleanupBrain, findTopicId, snapshotTopicIds } from './support/brain-fixture';

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

  test('a phone lists rows without a reader pane, and opens ?note= on the note’s own page', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${baseUrl}/brain?q=${encodeURIComponent(query)}`, { waitUntil: 'domcontentloaded' });

    // Since #1808 a phone lists the same plain rows as the wide Brain, but
    // there is no reader pane beside them: a row opens the note's own page.
    await expect(page.getByRole('region', { name: 'Selected note' })).toHaveCount(0);
    await expect(page.locator('[data-note-row]')).toHaveCount(3);

    const [first] = [...quotes.keys()];
    await page.goto(`${baseUrl}/brain?note=${first}`, { waitUntil: 'domcontentloaded' });
    await expect(page).toHaveURL(new RegExp(`/brain/notes/${first}$`));
    await expect(page.locator('app-brain-note-page .note-column')).toBeVisible();
    await expect(page.locator('app-brain-note-page .note-item-quote')).toContainText(quotes.get(first) ?? '');
  });

  test('keeps the list header count line clear of the order control at narrow column widths (#1814)', async ({ page }) => {
    const topicId = await findTopicId(baseUrl, topicName);
    expect(topicId).not.toBeNull();

    type Box = { left: number; right: number; top: number; bottom: number; width: number; height: number };
    const intersects = (a: Box, b: Box): boolean =>
      a.left < b.right - 0.5 && a.right > b.left + 0.5 && a.top < b.bottom - 0.5 && a.bottom > b.top + 0.5;

    async function assertDesktopHeaderClear(path: string, viewport: { width: number; height: number }): Promise<void> {
      await page.setViewportSize(viewport);
      await page.goto(`${baseUrl}${path}`, { waitUntil: 'domcontentloaded' });
      const count = page.locator('.list-header .page-count');
      const title = page.locator('.list-header .page-title');
      const order = page.locator('.list-header .order-select');
      const right = page.locator('.list-header .toolbar-right');
      const titleBox = page.locator('.list-header .toolbar-title');
      await expect(count).toBeVisible();
      await expect(title).toBeVisible();
      await expect(order).toBeVisible();

      const rects = await page.evaluate(() => {
        const box = (selector: string): Box => {
          const rect = document.querySelector(selector)!.getBoundingClientRect();
          return {
            left: rect.left,
            right: rect.right,
            top: rect.top,
            bottom: rect.bottom,
            width: rect.width,
            height: rect.height,
          };
        };
        return {
          title: box('.list-header .page-title'),
          count: box('.list-header .page-count'),
          order: box('.list-header .order-select'),
          right: box('.list-header .toolbar-right'),
          titleBox: box('.list-header .toolbar-title'),
          header: box('.list-header'),
        };
      });

      expect(rects.title.width, `${path} @ ${viewport.width}px: title collapsed`).toBeGreaterThan(20);
      expect(rects.count.width, `${path} @ ${viewport.width}px: count collapsed`).toBeGreaterThan(20);
      expect(intersects(rects.count, rects.order), `${path} @ ${viewport.width}px: count overlaps order`).toBe(false);
      expect(intersects(rects.count, rects.right), `${path} @ ${viewport.width}px: count overlaps toolbar-right`).toBe(false);
      expect(intersects(rects.title, rects.order), `${path} @ ${viewport.width}px: title overlaps order`).toBe(false);
      expect(intersects(rects.title, rects.count), `${path} @ ${viewport.width}px: title overlaps count`).toBe(false);
      expect(rects.count.right, `${path} @ ${viewport.width}px: count extends past toolbar-title`).toBeLessThanOrEqual(rects.titleBox.right + 1);
      expect(rects.count.right, `${path} @ ${viewport.width}px: count extends into order`).toBeLessThanOrEqual(rects.order.left + 0.5);
      expect(rects.right.right, `${path} @ ${viewport.width}px: controls overflow header`).toBeLessThanOrEqual(rects.header.right + 1);
      await expect(titleBox).toBeVisible();
      await expect(right).toBeVisible();
    }

    const narrowDesktop = { width: 1280, height: 900 };
    await assertDesktopHeaderClear(`/brain/topics/${topicId}`, narrowDesktop);
    await assertDesktopHeaderClear(`/brain/topics/${topicId}?order=newest`, narrowDesktop);
    await assertDesktopHeaderClear('/brain', narrowDesktop);
    await assertDesktopHeaderClear('/brain/passages', narrowDesktop);
    await assertDesktopHeaderClear('/brain/unsorted', narrowDesktop);
    await assertDesktopHeaderClear(`/brain?book=${bookId}`, narrowDesktop);

    // Narrow tablet (sidebar open -> ~532px list column).
    await assertDesktopHeaderClear(`/brain/topics/${topicId}`, { width: 780, height: 900 });

    // Phone: place switch and icon controls must not intersect.
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${baseUrl}/brain/topics/${topicId}`, { waitUntil: 'domcontentloaded' });
    await expect(page.locator('.list-header .place-switch')).toBeVisible();
    const phoneRects = await page.evaluate(() => {
      const box = (selector: string): Box => {
        const rect = document.querySelector(selector)!.getBoundingClientRect();
        return {
          left: rect.left,
          right: rect.right,
          top: rect.top,
          bottom: rect.bottom,
          width: rect.width,
          height: rect.height,
        };
      };
      return {
        place: box('.list-header .place-switch'),
        right: box('.list-header .toolbar-right'),
        header: box('.list-header'),
      };
    });
    expect(intersects(phoneRects.place, phoneRects.right)).toBe(false);
    expect(phoneRects.right.right).toBeLessThanOrEqual(phoneRects.header.right + 1);
  });
});
