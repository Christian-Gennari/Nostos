/**
 * Brain reader (#1822, #1823).
 *
 * A long note's quote is clamped until More unfolds it, and a clamped quote
 * clips its overflow. The rail that tells the book's words from the reader's
 * own must therefore sit inside the quote's box, or it only shows once the
 * note is unfolded. Related notes sit below a rule as separate cards, and a
 * related quote carries the quote rail too.
 */
import { expect, test } from '@playwright/test';

import { apiPost, loadFixture, newRunId } from './support/fixture';

test.use({ serviceWorkers: 'block' });

const LONG_QUOTE =
  'Each of them is, as the proverb says, not so much a single state as a collection of states. ' +
  'For it always contains at least two states, the rich and the poor, at enmity with each other; ' +
  'each of these in turn has many subdivisions, and it is a complete mistake to treat them all as a unity. ' +
  'Treat them as a plurality, offer to hand over the property or the power or the persons of one section ' +
  'to another, and you will have allies in plenty and very few enemies. And so long as your state is run ' +
  'with the discipline we have described, it will be the greatest of states.';

test('the quote rail shows on a clamped quote, and related notes are set apart', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const run = newRunId();
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
    type: 'physical',
    title: `Rail Book ${run}`,
    author: 'Nostos QA',
  });
  const open = await apiPost<{ id: string }>(baseUrl, `/api/books/${book.id}/notes`, {
    selectedText: LONG_QUOTE,
    content: `A long enough note of my own to be clamped under its quote. [[Rail ${run}]]`,
  });
  await apiPost(baseUrl, `/api/books/${book.id}/notes`, {
    selectedText: `A related quote ${run}.`,
    content: `[[Rail ${run}]]`,
  });

  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(`${baseUrl}/brain/notes/${open.id}`, { waitUntil: 'domcontentloaded' });

    const quote = page.locator('.reader-note-item .note-item-quote');
    await expect(quote).toHaveClass(/is-clamped/);

    // The rail is drawn at the quote box's own left edge, inside the clip,
    // and the box reaches into the gutter so the text keeps the shared edge.
    const geometry = await quote.evaluate((el) => {
      const rail = getComputedStyle(el, '::before');
      const box = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      return {
        railLeft: rail.left,
        railWidth: parseFloat(rail.width),
        textLeft: box.left + parseFloat(style.paddingLeft),
        boxLeft: box.left,
      };
    });
    expect(geometry.railLeft).toBe('0px');
    expect(geometry.railWidth).toBeGreaterThan(0);
    expect(geometry.textLeft - geometry.boxLeft).toBe(16);

    const related = page.locator('section.related');
    await expect(related.locator('h2.related-title')).toHaveText(/Related notes\s*·\s*1/);
    expect(await related.evaluate((el) => getComputedStyle(el).borderTopStyle)).toBe('solid');
    await expect(related.locator('.related-row .related-text.is-quote')).toHaveCount(1);
  }
});
