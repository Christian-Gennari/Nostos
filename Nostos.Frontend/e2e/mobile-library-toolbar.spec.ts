/**
 * Library mobile toolbar geometry.
 *
 * The first row reads navigation left (the drawer opener, labelled with the
 * shelf on screen) and the page's actions right (sort, view, add), all on the
 * touch rung. It used to pack every control against the right edge.
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

test('mobile header reads navigation left, actions right, on one touch rung', async ({ page }) => {
  await openLibrary(page);

  const boxes = await page.evaluate(() => {
    const box = (selector: string): Box | null => {
      const el = document.querySelector(selector);
      if (!el) return null;
      const r = el.getBoundingClientRect();
      if (r.width === 0 && r.height === 0) return null;
      return { x: r.x, y: r.y, width: r.width, height: r.height };
    };

    return {
      collections: box('.toolbar-right > .floating-toggle'),
      label: box('.floating-toggle .scope-label'),
      sort: box('.toolbar-right > .sort-select-phone .nostos-dropdown__trigger'),
      desktopSort: box('.toolbar-right .select-wrapper'),
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
  // The labelled sort field is the desktop control; it takes no phone room.
  expect(boxes.desktopSort).toBeNull();

  // The opener names the shelf, and sits at the left gutter: its glyph, not its
  // padding, lines up with the search field below.
  await expect(page.getByTestId('library-scope-label')).toHaveText('All Books');
  expect(boxes.collections!.x).toBeLessThan(boxes.search!.x);
  expect(boxes.collections!.x).toBeGreaterThanOrEqual(8);

  // The actions group at the right edge, in order: sort, view, add. The add
  // button ends on the search field's right edge.
  expect(boxes.sort!.x).toBeGreaterThan(boxes.label!.x + boxes.label!.width);
  expect(boxes.view!.x).toBeGreaterThan(boxes.sort!.x + boxes.sort!.width - 1);
  expect(boxes.add!.x).toBeGreaterThan(boxes.view!.x + boxes.view!.width - 1);
  expect(Math.abs(boxes.add!.x + boxes.add!.width - (boxes.search!.x + boxes.search!.width))).toBeLessThanOrEqual(1);

  // One height for the row's buttons and dropdown, and it is the touch rung:
  // 44px. The view toggle is the exception by design: its options are 44px,
  // so its track is 50px, and it sits centred on the row.
  const firstRow = [boxes.collections!, boxes.sort!, boxes.add!];
  for (const box of firstRow) {
    expect(Math.abs(box.height - 44), `first-row height was ${box.height}px`).toBeLessThanOrEqual(0.5);
  }
  const centre = (box: Box) => box.y + box.height / 2;
  for (const box of [...firstRow, boxes.view!]) {
    expect(Math.abs(centre(box) - centre(firstRow[0])), 'one row centre').toBeLessThanOrEqual(1);
  }
  expect(Math.abs(boxes.sort!.width - 44), 'the phone sort is a 44px square').toBeLessThanOrEqual(0.5);
  expect(boxes.view!.height, 'the view toggle keeps 44px options in its track').toBeGreaterThanOrEqual(44);

  expect(Math.abs(boxes.search!.height - 44), `search height was ${boxes.search!.height}px`).toBeLessThanOrEqual(0.5);
  expect(
    // Below the row's tallest control, which is the view toggle.
    Math.abs(boxes.search!.y - (boxes.view!.y + boxes.view!.height + 10)),
    'search should sit exactly one toolbar gap below the first row',
  ).toBeLessThanOrEqual(1);

  // The second row stays full-width inside the toolbar's phone gutters.
  expect(boxes.search!.x).toBeGreaterThanOrEqual(13);
  expect(boxes.search!.x + boxes.search!.width).toBeLessThanOrEqual(377);
});

test('mobile sort opens the same choices from its icon', async ({ page }) => {
  await openLibrary(page);
  const sort = page.locator('.sort-select-phone .nostos-dropdown__trigger');

  await expect(sort).toHaveAttribute('aria-label', 'Sort books');
  await sort.click();
  const listbox = page.getByRole('listbox', { name: 'Sort books' });
  await expect(listbox).toBeVisible();
  await expect(listbox.getByRole('option', { selected: true })).toHaveCount(1);
  await listbox.getByRole('option', { name: 'Title (A-Z)' }).click();
  await expect(listbox).toBeHidden();
  await sort.click();
  await expect(listbox.getByRole('option', { selected: true })).toHaveText(/Title/);
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
