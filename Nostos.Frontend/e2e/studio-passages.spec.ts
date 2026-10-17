/**
 * Passages in Studio (issue #1021): a passage kept with a writing is a row
 * with its title, its primary action is Quote from passage, and words
 * selected in the passage viewer go into the draft as a quotation that names
 * the passage. The whole passage is never inserted.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiDelete } from './support/brain-fixture';
import { apiGet, apiPost, loadFixture, newRunId } from './support/fixture';
import { apiPut } from './support/visual-capture';
import type { WritingContentDto } from '../src/app/core/dtos/writing.dtos';
import { pinReferenceRail, studioPage, STUDIO_PAGE_SELECTOR } from './support/studio-editor';

// These journeys work with sources in view: Reference pinned beside the page
// (#1040), as a writer who prefers that would set it.
test.beforeEach(async ({ context }) => {
  await pinReferenceRail(context);
});

test.use({ serviceWorkers: 'block' });

const PARAGRAPHS = [
  '“Even this must have a preface—that is, a literary preface,” laughed Ivan, “and I am a poor hand at making one.',
  'Man prefers peace, and even death, to freedom of choice in the knowledge of good and evil.',
  'My story is laid in Spain, in Seville, in the most terrible time of the Inquisition, when fires were lighted every day to the glory of God.',
];

let baseUrl = '';
let bookId = '';
let writingId = '';
let passageId = '';

test.beforeEach(async () => {
  baseUrl = loadFixture().baseUrl;
  const run = newRunId();
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
    type: 'physical',
    title: `The Brothers Karamazov ${run}`,
    author: 'Fyodor Dostoyevsky',
  });
  bookId = book.id;
  const text = Array.from({ length: 6 }, () => PARAGRAPHS.join('\n\n')).join('\n\n');
  const passage = await apiPost<{ id: string }>(baseUrl, `/api/books/${book.id}/notes`, {
    content: "Ivan's poem.",
    kind: 'passage',
    passageTitle: 'The Grand Inquisitor',
    sourceAnchorKind: 'epub_cfi',
    sourceAnchorValue: 'epubcfi(/6/80!/4/2/1:0)',
    passageEnd: 'epubcfi(/6/80!/4/112/1:285)',
    selectedText: text,
    passageHtml: text.split('\n\n').map((p) => `<p>${p}</p>`).join(''),
  });
  passageId = passage.id;

  const writing = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
    name: `On freedom ${run}`,
    type: 'Document',
  });
  writingId = writing.id;
  await apiPut(baseUrl, `/api/writings/${writing.id}`, {
    name: `On freedom ${run}`,
    content: '# On freedom\n\nThe Inquisitor says it plainly.',
  });
  await apiPost(baseUrl, `/api/writings/${writing.id}/notes`, { noteId: passage.id });
});

test.afterEach(async () => {
  await apiDelete(baseUrl, `/api/writings/${writingId}`);
  await apiDelete(baseUrl, `/api/books/${bookId}`);
});

async function openWriting(page: Page) {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`${baseUrl}/studio?writingId=${writingId}`, { waitUntil: 'domcontentloaded' });
  await page.locator(STUDIO_PAGE_SELECTOR).waitFor({ timeout: 45_000 });
  await expect(studioPage(page)).toContainText('The Inquisitor says it plainly', {
    timeout: 45_000,
  });
  const toggle = page.locator('.reference-toggle');
  if ((await toggle.getAttribute('aria-expanded')) === 'false') await toggle.click();
}

test('a kept passage is quoted by selecting words in the passage viewer', async ({ page }) => {
  await openWriting(page);

  // Put the cursor at the end of the draft, where the quotation will go.
  const editor = studioPage(page);
  await editor.click();
  await page.keyboard.press('Control+End');

  const row = page.getByTestId('studio-passage-row');
  await expect(row).toHaveText('The Grand Inquisitor');
  await row.click();

  await expect(page.locator('.inspected-source-card [data-testid="passage-card-title"]')).toHaveText('The Grand Inquisitor');
  // The opening alone is never offered as the quotation.
  await expect(page.getByRole('button', { name: 'Insert quote' })).toHaveCount(0);
  await page.getByTestId('studio-quote-from-passage').click();

  const text = page.getByTestId('passage-viewer-text');
  await expect(text).toContainText('Man prefers peace');
  const quote = page.getByTestId('passage-viewer-quote');
  await expect(quote).toBeDisabled();

  await text.evaluate((root) => {
    const walker = root.ownerDocument.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) {
      const node = walker.currentNode as Text;
      const at = node.data.indexOf('Man prefers peace');
      if (at < 0) continue;
      const range = root.ownerDocument.createRange();
      range.setStart(node, at);
      range.setEnd(node, at + 'Man prefers peace, and even death, to freedom of choice'.length);
      const selection = root.ownerDocument.getSelection()!;
      selection.removeAllRanges();
      selection.addRange(range);
      return;
    }
    throw new Error('quotation not found');
  });
  await expect(quote).toBeEnabled();
  await quote.click();
  await expect(page.getByText('Quoted in your writing')).toBeVisible();

  await page.getByTestId('passage-viewer-close').click();
  await expect(editor).toContainText('Man prefers peace, and even death, to freedom of choice');
  await expect(editor).toContainText('“The Grand Inquisitor”');
  await expect(editor).not.toContainText('My story is laid in Spain');

  await expect
    .poll(async () => (await apiGet<WritingContentDto>(baseUrl, `/api/writings/${writingId}`)).content ?? '', {
      timeout: 15_000,
    })
    .toContain('> Man prefers peace, and even death, to freedom of choice');
});
