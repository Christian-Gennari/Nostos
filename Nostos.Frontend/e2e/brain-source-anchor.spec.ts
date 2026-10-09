import { expect, test } from '@playwright/test';

import { apiGet, apiPost, loadFixture } from './support/fixture';
import { cleanupBrain, snapshotTopicIds } from './support/brain-fixture';
import type { NoteSearchHit } from '../src/app/core/dtos/note.dtos';

test('Brain Topic evidence preserves a verified PDF page destination', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const stamp = Date.now().toString(36);
  const topicName = 'Verified PDF anchor ' + stamp;
  const bookTitle = 'Verified PDF source ' + stamp;
  let bookId: string | null = null;

  try {
    const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
      type: 'physical', title: bookTitle, author: 'Nostos QA',
    });
    bookId = book.id;
    const created = await apiPost<{ id: string }>(baseUrl, `/api/books/${book.id}/notes`, {
      content: `A linked note for [[${topicName}]].`,
      captureSource: 'voice',
      sourceAnchorKind: 'pdf_page',
      sourceAnchorValue: '53',
      anchorVerified: true,
    });
    const durable = await apiGet<NoteSearchHit>(baseUrl, `/api/notes/${created.id}`);
    expect(durable.sourceAnchorKind).toBe('pdf_page');
    expect(durable.sourceAnchorValue).toBe('53');
    expect(durable.anchorVerified).toBe(true);

    await page.addInitScript(() => localStorage.setItem('nostos.theme', 'light'));
    await page.goto(`${baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
    const topic = page.locator('.index-item').filter({ hasText: topicName }).first();
    await topic.waitFor({ timeout: 30_000 });
    await topic.click();
    const source = page.locator('[data-testid="topic-evidence"] .source-badge').first();
    await expect(source).toBeVisible();
    await expect(source).toContainText('Return to passage');
    const href = await source.getAttribute('href');
    expect(href).toBeTruthy();
    const destination = new URL(href!, baseUrl);
    expect(destination.pathname).toBe(`/read/${book.id}`);
    expect(destination.searchParams.get('sourcePage')).toBe('53');
    await page.screenshot({
      path: '/tmp/804-brain-verified-anchor-evidence-after-1440x900-light.png',
      animations: 'disabled',
    });
  } finally {
    if (bookId) {
      await cleanupBrain(baseUrl, { bookId, topicNames: [], beforeTopicIds });
    }
  }
});
