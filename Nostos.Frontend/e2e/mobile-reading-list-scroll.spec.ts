import { expect, test } from '@playwright/test';

import { apiDelete } from './support/brain-fixture';
import { apiPost, loadFixture } from './support/fixture';

// The workspace shell clips its content, so the To read page has to scroll
// itself. Before it did, a phone could not reach the Someday list at all.
test('the To read page scrolls to its last Someday entry on a phone', async ({ page }) => {
  const fixture = loadFixture();
  const stamp = Date.now().toString(36);
  const ids: string[] = [];

  await page.setViewportSize({ width: 390, height: 844 });
  try {
    for (let index = 0; index < 12; index++) {
      const entry = await apiPost<{ id: string }>(fixture.baseUrl, '/api/reading-list', {
        title: `Scroll wish ${stamp} ${String(index + 1).padStart(2, '0')}`,
        readNext: index === 0,
      });
      ids.push(entry.id);
    }

    await page.goto(fixture.baseUrl + '/library/to-read', { waitUntil: 'domcontentloaded' });
    // Someday is newest first, so the first wish added is last on the page.
    const last = page.locator('.someday-row', { hasText: `Scroll wish ${stamp} 02` });
    await last.waitFor({ state: 'attached' });

    await page.mouse.move(195, 500);
    for (let step = 0; step < 30; step++) await page.mouse.wheel(0, 400);

    await expect(last).toBeInViewport();
  } finally {
    for (const id of ids) await apiDelete(fixture.baseUrl, `/api/reading-list/${id}`).catch(() => {});
  }
});
