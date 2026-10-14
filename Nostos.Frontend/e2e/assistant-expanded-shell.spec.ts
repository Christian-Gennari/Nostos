/**
 * Desktop Ask Nostos shell regression for issue #320.
 *
 * The compact flyout remains the fast default, while an explicit header action
 * expands the SAME component into a large focus workspace. This is a rendered
 * geometry contract, so it belongs in Playwright rather than jsdom.
 */
import { expect, test, type Route } from '@playwright/test';

import { loadFixture } from './support/fixture';
import { enableAssistant } from './support/assistant';

test.use({ serviceWorkers: 'block' });

test('desktop assistant expands into a focus workspace without losing the draft', async ({ page }) => {
  const fixture = loadFixture();

  await page.route('**/api/assistant/status', async (route: Route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ available: true }),
    });
  });

  await enableAssistant(page);
  await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });

  await page.locator('[data-testid="assistant-trigger"]').click();

  const panel = page.locator('[data-testid="assistant-panel"]');
  const composer = page.locator('[data-testid="assistant-composer"]');
  const expand = page.locator('[data-testid="assistant-expand"]');
  const scrim = page.locator('.assistant-scrim');

  await expect(panel).toBeVisible();
  await expect(expand).toHaveAttribute('aria-label', 'Expand Ask Nostos');

  const compact = await panel.boundingBox();
  expect(compact).not.toBeNull();
  expect(compact!.width).toBeLessThanOrEqual(430);

  await composer.fill('Keep this draft while I make room to think.');
  await expand.click();

  await expect(panel).toHaveClass(/is-expanded/);
  await expect(expand).toHaveAttribute('aria-label', 'Collapse Ask Nostos');
  await expect(scrim).toBeVisible();
  await expect(composer).toHaveValue('Keep this draft while I make room to think.');

  const expanded = await panel.boundingBox();
  expect(expanded).not.toBeNull();
  expect(expanded!.width).toBeGreaterThan(700);
  expect(expanded!.height).toBeGreaterThan(600);

  const viewport = page.viewportSize();
  expect(viewport).not.toBeNull();
  expect(Math.abs(expanded!.x - (viewport!.width - expanded!.width) / 2)).toBeLessThanOrEqual(2);

  // The floating desktop dock overlaps the lower part of the expanded workspace.
  // At the overlap point, Ask Nostos must remain the topmost interactive surface.
  const dock = await page.locator('app-app-dock .app-dock-container').boundingBox();
  expect(dock).not.toBeNull();
  const overlapLeft = Math.max(expanded!.x, dock!.x);
  const overlapRight = Math.min(expanded!.x + expanded!.width, dock!.x + dock!.width);
  const overlapTop = Math.max(expanded!.y, dock!.y);
  const overlapBottom = Math.min(expanded!.y + expanded!.height, dock!.y + dock!.height);
  expect(overlapRight - overlapLeft).toBeGreaterThan(0);
  expect(overlapBottom - overlapTop).toBeGreaterThan(0);

  const assistantOwnsOverlap = await page.evaluate(
    ({ x, y }) => Boolean(document.elementFromPoint(x, y)?.closest('[data-testid="assistant-panel"]')),
    {
      x: overlapLeft + (overlapRight - overlapLeft) / 2,
      y: overlapTop + (overlapBottom - overlapTop) / 2,
    },
  );
  expect(assistantOwnsOverlap).toBe(true);

  await expand.click();

  await expect(panel).not.toHaveClass(/is-expanded/);
  await expect(scrim).not.toBeVisible();
  await expect(composer).toHaveValue('Keep this draft while I make room to think.');

  const compactAgain = await panel.boundingBox();
  expect(compactAgain).not.toBeNull();
  expect(compactAgain!.width).toBeLessThanOrEqual(430);
});
