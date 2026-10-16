/** Starter questions on a phone are full-width, 44px+ tap targets. */
import { expect, test } from '@playwright/test';

import { loadFixture } from './support/fixture';
import { enableAssistant } from './support/assistant';

test.use({ serviceWorkers: 'block' });

test('starters are comfortable tap targets that fit the screen', async ({ page }) => {
  await page.route('**/api/assistant/status', (route) => route.fulfill({ json: { available: true } }));
  await enableAssistant(page);
  await page.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await page.locator('app-app-dock [data-testid="assistant-trigger"]').click();

  const rows = await page.getByTestId('assistant-starter').evaluateAll((els) =>
    els.map((el) => el.getBoundingClientRect()).map((r) => ({ width: r.width, height: r.height, right: r.right })),
  );
  expect(rows.length).toBeGreaterThanOrEqual(2);
  const viewport = page.viewportSize()!;
  for (const row of rows) {
    expect(row.height).toBeGreaterThanOrEqual(44);
    expect(row.right).toBeLessThanOrEqual(viewport.width);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)).toBe(false);
});
