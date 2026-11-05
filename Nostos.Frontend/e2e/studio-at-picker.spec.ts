import { expect, test, type Page } from '@playwright/test';

import { apiDelete } from './support/brain-fixture';
import { apiGet, apiPost, loadFixture } from './support/fixture';
import { waitForStudioEditor } from './support/studio-editor';

/**
 * The Studio's `@` picker on the real backend (issue #1378): a note with topic
 * links goes in as plain words, an audio note is cited with its time, a
 * multi-word query finds a highlight, and Enter inserts a PDF passage or an
 * audio clip instead of doing nothing.
 */

const EVIDENCE = 'e2e/visual-evidence/1378-at-picker';

test.use({ serviceWorkers: 'block' });

async function openWriting(page: Page, baseUrl: string, writingId: string) {
  await page.goto(`${baseUrl}/studio?writingId=${writingId}`, { waitUntil: 'domcontentloaded' });
  const editor = await waitForStudioEditor(page);
  await editor.click();
  await page.keyboard.press('Control+End');
  return editor;
}

test('the @ picker inserts what is highlighted (issue #1378)', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const run = Date.now().toString(36);
  const ids: { books: string[]; writing: string | null } = { books: [], writing: null };

  try {
    const austen = await apiPost<{ id: string }>(baseUrl, '/api/books', { type: 'physical', title: `Pride and Prejudice ${run}`, author: 'Jane Austen' });
    const audio = await apiPost<{ id: string }>(baseUrl, '/api/books', { type: 'physical', title: `Emma ${run}`, author: 'Jane Austen' });
    const pdf = await apiPost<{ id: string }>(baseUrl, '/api/books', { type: 'physical', title: `Letters ${run}`, author: 'Jane Austen' });
    ids.books.push(austen.id, audio.id, pdf.id);

    await apiPost(baseUrl, `/api/books/${austen.id}/notes`, {
      content: '',
      selectedText: 'It is a truth universally acknowledged. In nine cases out of ten, a woman had better show more affection than she feels.',
    });
    await apiPost(baseUrl, `/api/books/${austen.id}/notes`, {
      content: `p. 12 [[Irony]] the famous opening line ${run}`,
      sourceAnchorKind: 'pdf_page',
      sourceAnchorValue: '12',
      anchorVerified: true,
    });
    await apiPost(baseUrl, `/api/books/${audio.id}/notes`, {
      content: `Heard on the walk ${run}`,
      sourceAnchorKind: 'audio_timestamp',
      sourceAnchorValue: '754',
      anchorVerified: true,
    });
    await apiPost(baseUrl, `/api/books/${pdf.id}/notes`, {
      kind: 'passage',
      content: '',
      passageTitle: `Chapter pages ${run}`,
      sourceAnchorKind: 'pdf_page',
      sourceAnchorValue: '3',
      passageEnd: '5',
    });

    const writing = await apiPost<{ id: string }>(baseUrl, '/api/writings', { name: `Picker ${run}`, type: 'Document', parentId: null });
    ids.writing = writing.id;
    const editor = await openWriting(page, baseUrl, writing.id);

    // A multi-word query finds the highlight, and Enter quotes it.
    await page.keyboard.type('@nine cases');
    const palette = page.getByTestId('source-palette');
    await expect(palette.locator('.sp-row').first()).toContainText('In nine cases out of ten');
    await expect(palette.locator('.sp-hint')).toContainText('↵ quote');
    await page.keyboard.press('Enter');
    await expect(editor.locator('blockquote')).toContainText('In nine cases out of ten');
    await expect(editor).not.toContainText('@nine cases');

    // A note with topic links goes in as plain words.
    await page.keyboard.press('Control+End');
    await page.keyboard.type(' @famous opening');
    await expect(palette.locator('.sp-row').first()).toContainText('the famous opening line');
    await page.keyboard.press('Enter');
    await expect(editor).toContainText(`p. 12 Irony the famous opening line ${run}`);
    await expect(editor).not.toContainText('[[');
    await expect(editor).not.toContainText('\\[');

    // An audio note is cited with its time.
    await page.keyboard.press('Control+End');
    await page.keyboard.type(' @heard on the walk');
    await expect(palette.locator('.sp-row').first()).toContainText(`Heard on the walk ${run}`);
    await page.keyboard.press('Enter');
    await expect(editor).toContainText('12:34');

    // Enter on a PDF passage inserts a reference instead of nothing.
    await page.keyboard.press('Control+End');
    await page.keyboard.type(' @chapter pages');
    await expect(palette.locator('.sp-row').first()).toContainText(`Chapter pages ${run}`);
    await expect(palette.locator('.sp-hint')).toContainText('↵ reference');
    await page.screenshot({ path: `${EVIDENCE}/pdf-passage-hint.png` });
    await page.keyboard.press('Enter');
    await expect(editor).toContainText(`Chapter pages ${run}`);
    await expect(palette).toBeHidden();

    await page.screenshot({ path: `${EVIDENCE}/inserted.png` });
  } finally {
    if (ids.writing) await apiDelete(baseUrl, `/api/writings/${ids.writing}`);
    for (const id of ids.books) await apiDelete(baseUrl, `/api/books/${id}`);
  }
});
