/**
 * #1384: a PDF passage can be quoted in part. The passage viewer draws the
 * page and lays pdf.js's text layer over it, so words can be selected and
 * "Quote in writing" puts them into the draft. Real backend, real pdf.js.
 */
import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { openReferenceRailOnLoad, waitForStudioEditor } from './support/studio-editor';

const PDF_BYTES = readFileSync(path.join(__dirname, 'assets', 'reader-notes.pdf'));
const EVIDENCE_DIR = path.join(__dirname, 'visual-evidence', '1384-pdf-passage-text-layer');

test.use({ serviceWorkers: 'block' });

test('select words in a PDF passage and quote them in writing', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const run = newRunId();

  const created = await apiPost<any>(baseUrl, '/api/books/', { type: 'ebook', title: `PDF passage ${run}`, author: 'E2E' });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([PDF_BYTES], { type: 'application/pdf' }), 'book.pdf');
  const upload = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  expect(upload.ok).toBe(true);

  const passage = await apiPost<{ id: string }>(baseUrl, `/api/books/${bookId}/notes`, {
    content: '',
    kind: 'passage',
    passageTitle: 'Opening lines',
    sourceAnchorKind: 'pdf_page',
    sourceAnchorValue: '1',
    passageEnd: '2',
  });
  const writing = await apiPost<{ id: string }>(baseUrl, '/api/writings', { name: `Quote ${run}`, type: 'Document', parentId: null });
  await apiPost(baseUrl, `/api/writings/${writing.id}/notes`, { noteId: passage.id });

  await openReferenceRailOnLoad(page);
  await page.goto(`${baseUrl}/studio?writingId=${writing.id}`, { waitUntil: 'domcontentloaded' });
  const editor = await waitForStudioEditor(page);

  await page.locator('.kept-sources-content .reference-source-row-main').first().click();
  await page.getByTestId('studio-quote-from-passage').click();

  const layer = page.getByTestId('passage-viewer-text-layer');
  const firstWords = layer.locator('span:not(.markedContent)').first();
  await expect(firstWords).toBeAttached();
  const quoteButton = page.getByTestId('passage-viewer-quote');
  await expect(quoteButton).toBeDisabled();

  // A real triple click selects a line of the page.
  await firstWords.click({ clickCount: 3 });
  await expect(quoteButton).toBeEnabled();
  const selected = (await page.evaluate(() => window.getSelection()?.toString() ?? '')).replace(/\s+/g, ' ').trim();
  expect(selected.length).toBeGreaterThan(5);
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'selected-desktop.png') });

  await quoteButton.click();
  await expect(quoteButton).toContainText('Quoted');
  await expect(editor).toContainText(selected.slice(0, 20));
  await page.screenshot({ path: path.join(EVIDENCE_DIR, 'quoted-desktop.png') });

  // Page navigation still works, and the new page has its own words to pick.
  await page.getByTestId('passage-viewer-next').click();
  await expect(page.getByTestId('passage-viewer-page')).toContainText('Page 2 of 2');
  await expect(layer.locator('span:not(.markedContent)').first()).toBeAttached();
  await expect(quoteButton).toBeDisabled();
});
