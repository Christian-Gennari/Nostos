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

test('mobile header controls share one height and one rhythm', async ({ page }) => {
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

  // One height for the row's buttons and dropdown, and it is the touch rung:
  // 44px. (38px until the dropdown took its touch size; see
  // library.component.css.) The view toggle is the exception by design: its
  // options are 44px, so its track is 50px, and it sits centred on the row.
  const firstRow = [boxes.collections!, boxes.sort!, boxes.add!];
  for (const box of firstRow) {
    expect(Math.abs(box.height - 44), `first-row height was ${box.height}px`).toBeLessThanOrEqual(0.5);
    expect(Math.abs(box.y - firstRow[0].y), `first-row y was ${box.y}px`).toBeLessThanOrEqual(1);
  }
  const centre = (box: Box) => box.y + box.height / 2;
  expect(boxes.view!.height, 'the view toggle keeps 44px options in its track').toBeGreaterThanOrEqual(44);
  expect(
    Math.abs(centre(boxes.view!) - centre(firstRow[0])),
    'the view toggle is centred on the row',
  ).toBeLessThanOrEqual(1);

  expect(Math.abs(boxes.search!.height - 44), `search height was ${boxes.search!.height}px`).toBeLessThanOrEqual(0.5);
  expect(
    // Below the row's tallest control, which is the view toggle.
    Math.abs(boxes.search!.y - (boxes.view!.y + boxes.view!.height + 10)),
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
    // Just outside the visible box must still hit the opener.
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
