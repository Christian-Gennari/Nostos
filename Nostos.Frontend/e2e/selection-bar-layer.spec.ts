/**
 * Stacking order for nostos-selection-bar (#1773):
 *
 * The selection bar must sit above page content and the dock, and under every
 * backdrop and dialog (Keep in a document, Add to collection, bulk Delete).
 * Previously the bar sat at --layer-system (200), drawing over modal backdrops
 * and covering the Keep in a document primary button on phones.
 */
import { expect, test, type Locator, type Page } from '@playwright/test';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { apiDelete } from './support/brain-fixture';
import { waitForLayoutSettled } from './support/waits';

test.use({ serviceWorkers: 'block' });

async function hitAtCenter(locator: Locator) {
  return locator.evaluate((el) => {
    const rect = el.getBoundingClientRect();
    const cx = rect.left + rect.width / 2;
    const cy = rect.top + rect.height / 2;
    const hit = document.elementFromPoint(cx, cy);
    return {
      insideTarget: !!hit && el.contains(hit),
      insideSelectionBar: !!hit?.closest('nostos-selection-bar'),
      insideModalBackdrop: !!hit?.closest('.modal-backdrop'),
      hitTag: hit?.tagName ?? null,
    };
  });
}

test.describe('Selection bar stacking order (#1773)', () => {
  const run = newRunId();
  let baseUrl = '';
  const bookIds: string[] = [];
  let collectionId: string | null = null;
  let writingId: string | null = null;

  test.beforeAll(async () => {
    ({ baseUrl } = loadFixture());
    const b1 = await apiPost<{ id: string }>(baseUrl, '/api/books', {
      type: 'physical',
      title: `Stacking One ${run}`,
      author: 'Nostos QA',
    });
    const b2 = await apiPost<{ id: string }>(baseUrl, '/api/books', {
      type: 'physical',
      title: `Stacking Two ${run}`,
      author: 'Nostos QA',
    });
    bookIds.push(b1.id, b2.id);

    await apiPost(baseUrl, `/api/books/${b1.id}/notes`, {
      selectedText: `Quote one ${run}`,
      content: `Stacking note one ${run}`,
    });
    await apiPost(baseUrl, `/api/books/${b1.id}/notes`, {
      selectedText: `Quote two ${run}`,
      content: `Stacking note two ${run}`,
    });

    const col = await apiPost<{ id: string }>(baseUrl, '/api/collections', {
      name: `Stacking Col ${run}`,
    });
    collectionId = col.id;

    const doc = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
      name: `Stacking Doc ${run}`,
      type: 'Document',
    });
    writingId = doc.id;
  });

  test.afterAll(async () => {
    if (!baseUrl) return;
    for (const id of bookIds) await apiDelete(baseUrl, `/api/books/${id}`);
    if (collectionId) await apiDelete(baseUrl, `/api/collections/${collectionId}`);
    if (writingId) await apiDelete(baseUrl, `/api/writings/${writingId}`);
  });

  for (const viewport of [
    { name: 'desktop', width: 1280, height: 800 },
    { name: 'phone', width: 390, height: 844 },
  ]) {
    test(`Brain and Library dialogs sit above the selection bar on ${viewport.name}`, async ({ page }) => {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });

      // 1. Brain: Keep in a document…
      await page.goto(`${baseUrl}/brain?q=${encodeURIComponent(run)}`, { waitUntil: 'domcontentloaded' });
      await page.getByRole('button', { name: 'Select notes' }).click();

      const noteCheckboxes = page.locator('.note-list input[type="checkbox"]');
      await expect(noteCheckboxes).toHaveCount(2);
      await noteCheckboxes.nth(0).check();
      await noteCheckboxes.nth(1).check();

      const bar = page.locator('nostos-selection-bar');
      await expect(bar).toBeVisible();

      // Bar sits above the dock and page content while no dialog is open.
      const layers = await page.evaluate(() => {
        const barEl = document.querySelector('nostos-selection-bar')!;
        const dockEl = document.querySelector('app-app-dock')!;
        return {
          barZ: Number(getComputedStyle(barEl).zIndex),
          dockZ: Number(getComputedStyle(dockEl).zIndex),
        };
      });
      expect(layers.barZ).toBeGreaterThan(layers.dockZ);

      const closeHit = await hitAtCenter(bar.locator('.selection-close'));
      expect(closeHit.insideTarget).toBe(true);

      await page.getByRole('button', { name: 'Keep in a document…' }).click();
      const handoffDialog = page.locator('[role="dialog"]');
      await expect(handoffDialog).toBeVisible();
      await waitForLayoutSettled(page, '[role="dialog"]');

      const keepButton = handoffDialog.getByRole('button', { name: 'Keep in a document' });
      const keepHit = await hitAtCenter(keepButton);
      expect(keepHit.insideTarget).toBe(true);
      expect(keepHit.insideSelectionBar).toBe(false);

      const barUnderDialog = await hitAtCenter(bar);
      expect(barUnderDialog.insideSelectionBar).toBe(false);
      expect(barUnderDialog.insideModalBackdrop).toBe(true);

      // 2. Library: Add to collection… and bulk Delete confirm
      await page.goto(`${baseUrl}/library?q=${encodeURIComponent(run)}`, { waitUntil: 'domcontentloaded' });
      await page.getByRole('button', { name: 'Select books' }).click();

      const bookCheckboxes = page.locator('input[type="checkbox"].select-check');
      await expect(bookCheckboxes).toHaveCount(2);
      await bookCheckboxes.nth(0).check();
      await bookCheckboxes.nth(1).check();

      await page.getByRole('button', { name: 'Add to collection…' }).click();
      const menu = page.locator('nostos-menu');
      await expect(menu).toBeVisible();
      await waitForLayoutSettled(page, 'nostos-menu');

      const menuHit = await hitAtCenter(menu.locator('.nostos-menu-item').first());
      expect(menuHit.insideTarget).toBe(true);
      expect(menuHit.insideSelectionBar).toBe(false);

      await page.keyboard.press('Escape');
      await expect(menu).toHaveCount(0);

      await page.locator('app-library-selection-bar').getByRole('button', { name: 'Delete' }).click();
      const deleteDialog = page.locator('[role="alertdialog"]');
      await expect(deleteDialog).toBeVisible();
      await waitForLayoutSettled(page, '[role="alertdialog"]');

      const deleteConfirm = deleteDialog.getByRole('button', { name: 'Delete permanently' });
      const deleteHit = await hitAtCenter(deleteConfirm);
      expect(deleteHit.insideTarget).toBe(true);
      expect(deleteHit.insideSelectionBar).toBe(false);

      const barUnderConfirm = await hitAtCenter(page.locator('nostos-selection-bar'));
      expect(barUnderConfirm.insideSelectionBar).toBe(false);
      expect(barUnderConfirm.insideModalBackdrop).toBe(true);
    });
  }
});
