import { expect, test } from '@playwright/test';

import { apiGet, apiPost, apiPut, loadFixture } from './support/fixture';
import { studioPage, waitForStudioEditor } from './support/studio-editor';

/**
 * Issue #1363 on the real backend: a writing saved from another tab or device
 * is not written over by a stale autosave; the writer is asked, and the typed
 * text stays on the page.
 */

const EVIDENCE = 'e2e/visual-evidence/1363-edit-conflict';

for (const [name, viewport, dark] of [
  ['desktop-light', { width: 1280, height: 800 }, false],
  ['desktop-dark', { width: 1280, height: 800 }, true],
  ['phone-light', { width: 390, height: 844 }, false],
] as const) {
  test(`a stale autosave asks before it overwrites (${name})`, async ({ browser }) => {
    const { baseUrl } = loadFixture();
    const created = await apiPost<{ id: string }>(baseUrl, '/api/writings', { name: 'Conflict draft', type: 'Document' });
    const first = await apiPut<{ updatedAt: string }>(baseUrl, `/api/writings/${created.id}`, {
      name: 'Conflict draft',
      content: 'Opening paragraph.',
    });
    expect(first.updatedAt).toBeTruthy();

    const context = await browser.newContext({
      baseURL: baseUrl,
      viewport,
      colorScheme: dark ? 'dark' : 'light',
      serviceWorkers: 'block',
    });
    const page = await context.newPage();
    await page.goto(`/studio?writingId=${created.id}`);
    const editor = await waitForStudioEditor(page);
    await expect(editor).toContainText('Opening paragraph.');

    // The other tab saves while this one is open.
    await apiPut(baseUrl, `/api/writings/${created.id}`, {
      name: 'Conflict draft',
      content: 'Opening paragraph. Written in the other tab.',
    });

    await editor.click();
    await page.keyboard.press('End');
    await page.keyboard.type(' Typed here.');

    const dialog = page.getByRole('alertdialog');
    await expect(dialog).toContainText('This writing changed somewhere else', { timeout: 10_000 });
    await expect(studioPage(page)).toContainText('Typed here.');
    // Nothing was written over the newer text.
    const stored = await apiGet<{ content: string }>(baseUrl, `/api/writings/${created.id}`);
    expect(stored.content).toContain('Written in the other tab.');
    await page.screenshot({ path: `${EVIDENCE}/conflict-${name}.png` });

    if (name === 'desktop-light') {
      await dialog.getByRole('button', { name: /Keep mine/ }).click();
      await expect
        .poll(async () => (await apiGet<{ content: string }>(baseUrl, `/api/writings/${created.id}`)).content)
        .toContain('Typed here.');
    }
    await context.close();
  });
}
