/**
 * The phone rail with Ask Nostos in its centre slot.
 *
 * mobile-dock.spec.ts covers the four-slot rail (Ask Nostos off). This covers
 * the five-slot rail: every slot is still a comfortable tap target with a
 * readable label, Ask sits in the middle, and it opens the full-screen
 * assistant and gets focus back when that closes.
 */
import { expect, test } from '@playwright/test';

import { loadFixture } from './support/fixture';
import { enableAssistant } from './support/assistant';

test.use({ serviceWorkers: 'block' });

const MIN_TAP_TARGET = 44;

test('Ask Nostos takes the centre of a five-slot rail that still fits', async ({ page }) => {
  const fixture = loadFixture();
  await page.route('**/api/assistant/status', (route) =>
    route.fulfill({ json: { available: true } }),
  );
  await enableAssistant(page);
  await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  const ask = page.locator('app-app-dock [data-testid="assistant-trigger"]');
  await expect(ask).toBeVisible();
  // No second, floating launcher above the rail.
  await expect(page.getByTestId('assistant-trigger')).toHaveCount(1);

  const items = await page.locator('app-app-dock .dock-item').evaluateAll((els) =>
    els.map((el) => {
      const r = el.getBoundingClientRect();
      const label = el.querySelector('.label')!;
      return {
        label: label.textContent!.trim(),
        x: r.x,
        width: r.width,
        height: r.height,
        fontSize: parseFloat(getComputedStyle(label).fontSize),
      };
    }),
  );
  expect(items.map((item) => item.label)).toEqual(['Library', 'Brain', 'Ask', 'Studio', 'Settings']);
  for (const item of items) {
    expect(item.width, `${item.label} tap width`).toBeGreaterThanOrEqual(MIN_TAP_TARGET);
    expect(item.height, `${item.label} tap height`).toBeGreaterThanOrEqual(MIN_TAP_TARGET);
    expect(item.fontSize, `${item.label} label size`).toBeGreaterThanOrEqual(11);
  }
  const viewport = page.viewportSize()!;
  const centre = items[2].x + items[2].width / 2;
  expect(Math.abs(centre - viewport.width / 2)).toBeLessThanOrEqual(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)).toBe(false);

  await ask.click();
  const panel = page.getByTestId('assistant-panel');
  await expect(panel).toBeVisible();
  const box = (await panel.boundingBox())!;
  expect(box.width).toBeGreaterThanOrEqual(viewport.width - 1);

  await page.getByTestId('assistant-close').click();
  await expect(panel).toHaveCount(0);
  await expect(ask).toBeFocused();
});
