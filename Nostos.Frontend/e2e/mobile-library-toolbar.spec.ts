/**
 * Library mobile toolbar geometry.
 *
 * The UI-kit migration moved search/sort onto the canonical compact form
 * controls. On phones those controls intentionally grow to the global touch
 * rung, but the Library toolbar is a denser product-owned composition whose
 * collection/view/add controls are 38px. That left the sort select visibly
 * taller than its neighbours and bloated the second search row.
 *
 * Keep the primitives; pin only this surface's compact geometry so future
 * migrations cannot reintroduce the mismatched header.
 */
import { expect, test, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';

let fixture: ReturnType<typeof loadFixture>;

test.beforeAll(() => {
  fixture = loadFixture();
});

async function openLibrary(page: Page): Promise<void> {
  await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('header.toolbar')).toBeVisible();
  await expect(page.locator('.toolbar-right > .floating-toggle')).toBeVisible();
}

interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

test('mobile header controls share the original compact rhythm', async ({ page }) => {
  await openLibrary(page);

  const boxes = await page.evaluate(() => {
    const box = (selector: string): Box | null => {
      const el = document.querySelector(selector);
      if (!el) return null;
      const r = el.getBoundingClientRect();
      return { x: r.x, y: r.y, width: r.width, height: r.height };
    };

    return {
      collections: box('.floating-toggle'),
      sort: box('.toolbar-right .sort-select'),
      view: box('.control-group'),
      add: box('.toolbar-right .library-add-button'),
      search: box('.search-input'),
      toolbar: box('header.toolbar'),
    };
  });

  expect(boxes.collections).not.toBeNull();
  expect(boxes.sort).not.toBeNull();
  expect(boxes.view).not.toBeNull();
  expect(boxes.add).not.toBeNull();
  expect(boxes.search).not.toBeNull();
  expect(boxes.toolbar).not.toBeNull();

  // The opener is part of the same flex row, with the same gutter as the
  // neighbouring controls; a fixed-position sibling would fail this check.
  expect(boxes.collections!.x).toBeGreaterThanOrEqual(13);
  expect(boxes.sort!.x - (boxes.collections!.x + boxes.collections!.width)).toBeGreaterThanOrEqual(7);
  expect(boxes.sort!.x - (boxes.collections!.x + boxes.collections!.width)).toBeLessThanOrEqual(9);

  const firstRow = [boxes.collections!, boxes.sort!, boxes.view!, boxes.add!];
  for (const box of firstRow) {
    expect(Math.abs(box.height - 38), `first-row height was ${box.height}px`).toBeLessThanOrEqual(0.5);
    expect(Math.abs(box.y - firstRow[0].y), `first-row y was ${box.y}px`).toBeLessThanOrEqual(1);
  }

  expect(Math.abs(boxes.search!.height - 38), `search height was ${boxes.search!.height}px`).toBeLessThanOrEqual(0.5);
  expect(
    Math.abs(boxes.search!.y - (firstRow[0].y + firstRow[0].height + 10)),
    'search should sit exactly one toolbar gap below the first row',
  ).toBeLessThanOrEqual(1);

  // The second row stays full-width inside the toolbar's 14px phone gutters.
  expect(boxes.search!.x).toBeGreaterThanOrEqual(13);
  expect(boxes.search!.x + boxes.search!.width).toBeLessThanOrEqual(377);
});

test('mobile sidebar opener keeps a 44px hit area and restores focus after filter selection', async ({ page }) => {
  await openLibrary(page);
  const toggle = page.locator('.toolbar-right > .floating-toggle');

  const hitArea = await toggle.evaluate((el) => {
    const r = el.getBoundingClientRect();
    // Three pixels outside the 38px visual box must still hit the opener.
    const target = document.elementFromPoint(r.left - 2, r.top + r.height / 2);
    return el === target || el.contains(target);
  });
  expect(hitArea).toBe(true);

  await toggle.click();
  await expect(page.locator('#library-collections-sidebar')).toBeVisible();
  await page.locator('#library-collections-sidebar .nav-item').first().click();
  await expect(toggle).toBeFocused();
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
});
