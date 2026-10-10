import { expect, test } from '@playwright/test';

import { apiDelete, cleanupBrain, snapshotTopicIds } from './support/brain-fixture';
import { apiPost, loadFixture } from './support/fixture';
import type { Note } from '../src/app/core/dtos/note.dtos';

async function seedReviewNote(baseUrl: string, bookTitle: string, content: string): Promise<{
  bookId: string;
  noteId: string;
}> {
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
    type: 'physical',
    title: bookTitle,
    author: 'Nostos QA',
  });
  const note = await apiPost<Note>(baseUrl, `/api/books/${book.id}/notes`, { content });
  return { bookId: book.id, noteId: note.id };
}

test('reloading optional review restores its query, Without topics filter and selected note on return', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const stamp = Date.now().toString(36);
  const query = `review reload ${stamp}`;
  let bookId: string | null = null;

  try {
    const note = await seedReviewNote(
      baseUrl,
      `Review reload source ${stamp}`,
      `A note for ${query} that remains available after a browser reload.`,
    );
    bookId = note.bookId;

    await page.goto(`${baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Notes', exact: true }).click();
    const search = page.getByRole('searchbox', { name: 'Search saved notes' });
    await search.fill(query);
    await expect(page.locator('.note-row-item')).toHaveCount(1);
    await page.locator('.brain-note-filters').getByRole('button', { name: 'Without topics' }).click();
    await expect(page.locator('.brain-note-filters').getByRole('button', { name: 'Without topics' })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.note-row-item')).toHaveCount(1);
    await page.locator('.note-row-item').click();
    await expect(page.locator('.brain-browse-detail')).toContainText(`A note for ${query}`);

    await page.getByRole('button', { name: 'Review one by one', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Reviewing one by one' })).toBeVisible();
    await expect(page.locator('.note-row-item')).toHaveCount(1);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await expect(page.getByRole('heading', { name: 'Reviewing one by one' })).toBeVisible();
    await expect(page.locator('.note-row-item')).toHaveCount(1);

    await page.getByRole('button', { name: 'Back to notes without topics' }).click();
    await expect(page.getByRole('searchbox', { name: 'Search saved notes' })).toHaveValue(query);
    await expect(page.locator('.brain-note-filters').getByRole('button', { name: 'Without topics' })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.note-row-item')).toHaveCount(1);
    await expect(page.locator('.note-row-item')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.brain-browse-detail')).toContainText(`A note for ${query}`);
  } finally {
    if (bookId) await cleanupBrain(baseUrl, { bookId, topicNames: [], beforeTopicIds });
  }
});
