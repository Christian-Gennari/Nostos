/**
 * The desktop dock carries Ask Nostos.
 *
 * One bar holds the destinations, the Ask Nostos launcher and the utilities;
 * the compact panel rises from that bar. These are rendered-geometry and real
 * pointer contracts (a press is pointerdown then click), so they live here
 * rather than in jsdom.
 */
import { expect, test, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';
import { enableAssistant } from './support/assistant';

test.use({ serviceWorkers: 'block' });

async function openLibraryWithAssistant(page: Page): Promise<void> {
  const fixture = loadFixture();
  await page.route('**/api/assistant/status', (route) =>
    route.fulfill({ json: { available: true } }),
  );
  await enableAssistant(page);
  await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('app-app-dock [data-testid="assistant-trigger"]')).toBeVisible();
}

test('the dock is the one Ask Nostos launcher and the panel rises from it', async ({ page }) => {
  await openLibraryWithAssistant(page);

  const trigger = page.getByTestId('assistant-trigger');
  const panel = page.getByTestId('assistant-panel');
  await expect(trigger).toHaveCount(1);
  // The corner capsule stands down while the dock carries the launcher.
  await expect(page.locator('.assistant-trigger')).toHaveCount(0);

  await trigger.click();
  await expect(panel).toBeVisible();
  await expect(trigger).toHaveAttribute('aria-expanded', 'true');

  const dock = (await page.locator('app-app-dock .app-dock-container').boundingBox())!;
  const box = (await panel.boundingBox())!;
  // Centred on the dock, and just above it.
  expect(Math.abs(box.x + box.width / 2 - (dock.x + dock.width / 2))).toBeLessThanOrEqual(2);
  const gap = dock.y - (box.y + box.height);
  expect(gap).toBeGreaterThanOrEqual(4);
  expect(gap).toBeLessThanOrEqual(24);

  // A second press closes it. Closing on the pointerdown as an "outside
  // click" would let this click reopen the panel straight away.
  await trigger.click();
  await expect(panel).toHaveCount(0);
  await expect(trigger).toHaveAttribute('aria-expanded', 'false');

  // Escape closes and hands focus back to the launcher.
  await trigger.click();
  await expect(panel).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(panel).toHaveCount(0);
  await expect(trigger).toBeFocused();
});

test('the selected destination wears the sliding pill', async ({ page }) => {
  await openLibraryWithAssistant(page);

  const library = page.locator('app-app-dock .dock-item[title="Library"]');
  const pill = page.locator('app-app-dock .dock-pill');
  await expect
    .poll(async () => {
      const [a, b] = [(await library.boundingBox())!, (await pill.boundingBox())!];
      return Math.abs(a.x - b.x) + Math.abs(a.width - b.width);
    })
    .toBeLessThanOrEqual(1);

  await page.locator('app-app-dock .dock-item[title="The Brain"]').click();
  await expect(page).toHaveURL(/\/brain/);
  const brain = page.locator('app-app-dock .dock-item[title="The Brain"]');
  await expect
    .poll(async () => {
      const [a, b] = [(await brain.boundingBox())!, (await pill.boundingBox())!];
      return Math.abs(a.x - b.x) + Math.abs(a.width - b.width);
    })
    .toBeLessThanOrEqual(1);
});

test('at tablet width the dock gives its width back', async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 768 });
  await openLibraryWithAssistant(page);

  // Unselected destinations go icon-only; the selected one keeps its label.
  await expect(page.locator('app-app-dock .dock-item[title="Library"] .label')).toBeVisible();
  await expect(page.locator('app-app-dock .dock-item[title="The Brain"] .label')).toBeHidden();
  await expect(page.locator('app-app-dock .dock-ask-key')).toBeHidden();

  // The dock stays clear of the Library sidebar.
  const dock = (await page.locator('app-app-dock .app-dock-container').boundingBox())!;
  const sidebar = (await page.locator('.sidebar-panel').boundingBox())!;
  expect(dock.x).toBeGreaterThanOrEqual(sidebar.x + sidebar.width);
  expect(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)).toBe(false);
});
