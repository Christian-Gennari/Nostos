/**
 * Regression: the Collections drawer opener is phone-only.
 * The icon button primitive also sets display, so verify the computed style
 * across the 768/769px breakpoint, not merely the presence of CSS selectors.
 */
import { expect, test } from '@playwright/test';
import { loadFixture } from './support/fixture';

test('Library Collections opener takes no desktop toolbar space, but remains usable on mobile', async ({ page }) => {
  const fixture = loadFixture();
  await page.addInitScript(() => {
    localStorage.setItem('nostos.library.preferences', JSON.stringify({
      viewMode: 'grid',
      sort: 'lastread',
      pageSize: 20,
      sidebarExpanded: false,
      groupByWork: true,
      assistantEnabled: false,
      assistantVoiceEnabled: true,
    }));
  });
  await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('header.toolbar')).toBeVisible();

  const opener = page.locator('.toolbar-right > button.floating-toggle');
  const sort = page.locator('.toolbar-right .select-wrapper');
  const view = page.locator('.toolbar-right .control-group');
  const add = page.locator('.toolbar-right .library-add-button');

  for (const width of [1440, 1280, 900, 769]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(opener, `desktop at ${width}px`).toHaveCSS('display', 'none');
    await expect(opener).toBeHidden();
    await expect(sort).toBeVisible();
    await expect(view).toBeVisible();
    await expect(add).toBeVisible();
  }

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(opener).toHaveCSS('display', 'inline-flex');
  await expect(opener).toBeVisible();
  await expect(opener).toHaveAttribute('aria-label', 'Open collections sidebar');
  await opener.click();
  await expect(opener).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('#library-collections-sidebar')).toBeVisible();
});
