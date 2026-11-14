import { expect, test } from '@playwright/test';

import { apiGet, apiPost, apiPut, loadFixture } from './support/fixture';
import { waitForLayoutSettled } from './support/waits';

/**
 * Issue #1404 on the real backend: a note saved from another tab or device is
 * not written over by a stale edit; the reader is asked, and the typed words
 * stay in the form.
 */

const EVIDENCE = 'e2e/visual-evidence/1404-note-edit-conflict';

for (const [name, viewport, dark] of [
  ['desktop-light', { width: 1280, height: 800 }, false],
  ['desktop-dark', { width: 1280, height: 800 }, true],
  ['phone-light', { width: 390, height: 844 }, false],
] as const) {
  test(`a stale note edit asks before it overwrites (${name})`, async ({ browser }) => {
    const { baseUrl } = loadFixture();
    const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
      type: 'physical',
      title: `Conflict book ${name}`,
      author: 'Nostos QA',
    });
    const note = await apiPost<{ id: string; updatedAt: string }>(baseUrl, `/api/books/${book.id}/notes`, {
      content: 'The first thought.',
    });
    expect(note.updatedAt).toBeTruthy();

    const context = await browser.newContext({
      baseURL: baseUrl,
      viewport,
      colorScheme: dark ? 'dark' : 'light',
      serviceWorkers: 'block',
    });
    const page = await context.newPage();
    await page.goto(`/brain/notes/${note.id}`);
    await page.getByRole('button', { name: 'More actions for this note' }).click();
    await page.getByRole('menuitem', { name: 'Edit note' }).click();
    const words = page.locator('textarea.note-input');
    await expect(words).toHaveValue('The first thought.');

    // The other tab saves while this one is open.
    await apiPut(baseUrl, `/api/notes/${note.id}`, { content: 'The first thought, changed in the other tab.' });

    await words.fill('The first thought, typed here.');
    await page.getByRole('button', { name: 'Save', exact: true }).click();

    const dialog = page.getByRole('alertdialog');
    await expect(dialog).toContainText('This note changed somewhere else', { timeout: 10_000 });
    await expect(words).toHaveValue('The first thought, typed here.');
    // Nothing was written over the newer words.
    const stored = await apiGet<{ content: string }>(baseUrl, `/api/notes/${note.id}`);
    expect(stored.content).toContain('changed in the other tab');
    // Let the dialog finish fading in before capturing it.
    await waitForLayoutSettled(page, '[role="alertdialog"]');
    await page.screenshot({ path: `${EVIDENCE}/conflict-${name}.png` });

    if (name === 'desktop-light') {
      await dialog.getByRole('button', { name: /Keep mine/ }).click();
      await expect
        .poll(async () => (await apiGet<{ content: string }>(baseUrl, `/api/notes/${note.id}`)).content)
        .toBe('The first thought, typed here.');
    }
    await context.close();
  });
}
