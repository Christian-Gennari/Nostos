/**
 * Horizontal alignment for nostos-selection-bar (#1816):
 *
 * The dock centres on `--dock-anchor-x` (set on `.workspace-layout` per mode
 * and sidebar state), so the selection bar must share that exact anchor rather
 * than centring on the whole window (`50%`). Both bars must keep their
 * horizontal centres within 1px across Library, Brain list and Brain Kinds
 * with the sidebar open and collapsed, while phones (<=768px) stay centred on
 * the viewport.
 */
import { expect, test, type Page } from '@playwright/test';

import { cleanupBrain, snapshotTopicIds } from './support/brain-fixture';
import { apiPost, loadFixture, newRunId } from './support/fixture';
import { waitForLayoutSettled } from './support/waits';

test.use({ serviceWorkers: 'block' });

const DESKTOP_WIDTHS = [1024, 1440, 1871] as const;

async function measureCentres(page: Page): Promise<{ barCenter: number; dockCenter: number }> {
  await waitForLayoutSettled(page, 'nostos-selection-bar');
  await waitForLayoutSettled(page, 'app-app-dock');
  return page.evaluate(() => {
    const barEl = document.querySelector('nostos-selection-bar');
    const dockEl = document.querySelector('app-app-dock');
    if (!barEl || !dockEl) {
      throw new Error(`Missing element(s): bar=${!!barEl}, dock=${!!dockEl}`);
    }
    const barRect = barEl.getBoundingClientRect();
    const dockRect = dockEl.getBoundingClientRect();
    return {
      barCenter: barRect.left + barRect.width / 2,
      dockCenter: dockRect.left + dockRect.width / 2,
    };
  });
}

async function assertAlignedAcrossDesktopWidths(
  page: Page,
  shellSelector: string,
): Promise<void> {
  for (const width of DESKTOP_WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    await expect(page.locator(shellSelector)).not.toHaveClass(/sidebar-collapsed/);

    const open = await measureCentres(page);
    expect(Math.abs(open.barCenter - open.dockCenter)).toBeLessThanOrEqual(1);

    await page.getByRole('button', { name: 'Collapse sidebar' }).click();
    await expect(page.locator(shellSelector)).toHaveClass(/sidebar-collapsed/);

    const collapsed = await measureCentres(page);
    expect(Math.abs(collapsed.barCenter - collapsed.dockCenter)).toBeLessThanOrEqual(1);
    expect(open.barCenter - collapsed.barCenter).toBeGreaterThan(80);

    await page.getByRole('button', { name: 'Expand sidebar' }).click();
    await expect(page.locator(shellSelector)).not.toHaveClass(/sidebar-collapsed/);
  }
}

test.describe('Selection bar dock anchor (#1816)', () => {
  const run = newRunId();
  let baseUrl = '';
  let bookId = '';
  let beforeTopicIds = new Set<string>();

  test.beforeAll(async () => {
    ({ baseUrl } = loadFixture());
    beforeTopicIds = await snapshotTopicIds(baseUrl);
    const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
      type: 'physical',
      title: `Anchor Book ${run}`,
      author: 'Nostos QA',
    });
    bookId = book.id;
    await apiPost(baseUrl, `/api/books/${book.id}/notes`, {
      selectedText: `Anchor quote ${run}`,
      content: `Anchor note ${run} [[Anchor Topic ${run}]]`,
    });
  });

  test.afterAll(async () => {
    if (!baseUrl || !bookId) return;
    await cleanupBrain(baseUrl, { bookId, topicNames: [], beforeTopicIds });
  });

  test('Library aligns selection bar and dock at 1024, 1440 and 1871px (open and collapsed)', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`${baseUrl}/library?q=${encodeURIComponent(run)}`, {
      waitUntil: 'domcontentloaded',
    });

    await page.getByRole('button', { name: 'Select books' }).click();
    const checkbox = page.locator('input[type="checkbox"].select-check').first();
    await expect(checkbox).toBeVisible();
    await checkbox.check();
    await expect(page.locator('nostos-selection-bar')).toBeVisible();

    await assertAlignedAcrossDesktopWidths(page, '.layout-wrapper');
  });

  test('Brain list aligns selection bar and dock at 1024, 1440 and 1871px (open and collapsed)', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`${baseUrl}/brain?q=${encodeURIComponent(run)}`, {
      waitUntil: 'domcontentloaded',
    });

    await page.getByRole('button', { name: 'Select notes' }).click();
    const checkbox = page.locator('.note-list input[type="checkbox"]').first();
    await expect(checkbox).toBeVisible();
    await checkbox.check();
    await expect(page.locator('nostos-selection-bar')).toBeVisible();

    await assertAlignedAcrossDesktopWidths(page, '.brain-layout');
  });

  test('Brain Kinds aligns selection bar and dock at 1024, 1440 and 1871px (open and collapsed)', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`${baseUrl}/brain/kinds?q=${encodeURIComponent(run)}`, {
      waitUntil: 'domcontentloaded',
    });

    const checkbox = page.locator('.topic-rows input[type="checkbox"].row-check').first();
    await expect(checkbox).toBeVisible();
    await checkbox.check();
    await expect(page.locator('nostos-selection-bar')).toBeVisible();

    await assertAlignedAcrossDesktopWidths(page, '.brain-layout');
  });

  test('Phone (390px) keeps selection bar and dock centred on the viewport', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });

    await page.goto(`${baseUrl}/library?q=${encodeURIComponent(run)}`, {
      waitUntil: 'domcontentloaded',
    });
    await page.getByRole('button', { name: 'Select books' }).click();
    await page.locator('input[type="checkbox"].select-check').first().check();
    await expect(page.locator('nostos-selection-bar')).toBeVisible();
    const lib = await measureCentres(page);
    expect(Math.abs(lib.barCenter - lib.dockCenter)).toBeLessThanOrEqual(1);
    expect(Math.abs(lib.barCenter - 195)).toBeLessThanOrEqual(1);

    await page.goto(`${baseUrl}/brain?q=${encodeURIComponent(run)}`, {
      waitUntil: 'domcontentloaded',
    });
    await page.getByRole('button', { name: 'Select notes' }).click();
    await page.locator('.note-list input[type="checkbox"]').first().check();
    await expect(page.locator('nostos-selection-bar')).toBeVisible();
    const brain = await measureCentres(page);
    expect(Math.abs(brain.barCenter - brain.dockCenter)).toBeLessThanOrEqual(1);
    expect(Math.abs(brain.barCenter - 195)).toBeLessThanOrEqual(1);

    await page.goto(`${baseUrl}/brain/kinds?q=${encodeURIComponent(run)}`, {
      waitUntil: 'domcontentloaded',
    });
    await page.locator('.topic-rows input[type="checkbox"].row-check').first().check();
    await expect(page.locator('nostos-selection-bar')).toBeVisible();
    const kinds = await measureCentres(page);
    expect(Math.abs(kinds.barCenter - kinds.dockCenter)).toBeLessThanOrEqual(1);
    expect(Math.abs(kinds.barCenter - 195)).toBeLessThanOrEqual(1);
  });
});
