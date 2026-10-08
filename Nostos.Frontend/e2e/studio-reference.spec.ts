import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

import { apiDelete, snapshotTopicIds } from './support/brain-fixture';
import { apiGet, apiPost, loadFixture } from './support/fixture';
import { apiPut } from './support/visual-capture';
import type { Note } from '../src/app/core/dtos/note.dtos';
import type { WritingContentDto, WritingSourceDto } from '../src/app/core/dtos/writing.dtos';

const screenshotDir =
  process.env.STUDIO_REFERENCE_SCREENSHOT_DIR ?? '/tmp/nostos-studio-reference-after';

let baseUrl = '';
let writingId = '';
let writingTitle = '';
let longWritingId = '';
let longWritingTitle = '';
let mainBookId = '';
let mainBookTitle = '';
let topicNames: string[] = [];
let noteIds: string[] = [];
let createdBookIds: string[] = [];
let createdWritingIds: string[] = [];
let createdTopicIdsBefore = new Set<string>();
let longQuote = '';
let noteOnlyContent = '';

function captureAfter(page: Page, filename: string): Promise<Buffer> {
  mkdirSync(screenshotDir, { recursive: true });
  return page.screenshot({
    path: path.join(screenshotDir, filename),
    animations: 'disabled',
    fullPage: false,
  });
}

async function typeAndRemoveProbe(page: Page, marker: string): Promise<void> {
  const editorBody = page.frameLocator('.tox-edit-area iframe').locator('body');
  await editorBody.waitFor({ state: 'visible' });
  const before = await editorBody.evaluate((element) => element.textContent ?? '');
  await editorBody.click();
  await page.keyboard.press('Control+End');
  await page.keyboard.type(marker);
  await expect(editorBody).toContainText(marker);
  for (let index = 0; index < marker.length; index++) {
    await page.keyboard.press('Backspace');
  }
  await expect
    .poll(() => editorBody.evaluate((element) => element.textContent ?? ''))
    .toBe(before);
  await expect(page.locator('.header-save-status')).toHaveText('Saved', { timeout: 15_000 });
}

async function openWriting(page: Page, viewport = { width: 1440, height: 900 }): Promise<void> {
  await page.setViewportSize(viewport);
  await page.goto(baseUrl + '/studio?writingId=' + writingId, {
    waitUntil: 'domcontentloaded',
  });
  await page.locator('.tox-tinymce').waitFor({ timeout: 45_000 });
  await expect(page.frameLocator('.tox-edit-area iframe').locator('body')).toContainText(
    'The Quiet Practice of Paying Attention',
    { timeout: 45_000 },
  );
  await expect(page.locator('.header-doc-title')).toHaveText(writingTitle);
}

test.beforeAll(async () => {
  const fixture = loadFixture();
  baseUrl = fixture.baseUrl;
  createdTopicIdsBefore = await snapshotTopicIds(baseUrl);

  const stamp = Date.now().toString(36);
  writingTitle = 'S2 Essay — The Quiet Practice of Paying Attention ' + stamp;
  mainBookTitle =
    'S2 Reference Book — A Collected Practice of Attention and Intellectual Hospitality ' +
    'for Patient Readers ' +
    stamp;

  const rootFolder = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
    name: 'S2 Archive — ' + 'Nested long folder '.repeat(4) + stamp,
    type: 'Folder',
    parentId: null,
  });
  createdWritingIds.push(rootFolder.id);
  const nestedFolder = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
    name: 'Essays and marginalia — ' + 'Long nested title '.repeat(4),
    type: 'Folder',
    parentId: rootFolder.id,
  });
  createdWritingIds.push(nestedFolder.id);
  const writing = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
    name: writingTitle,
    type: 'Document',
    parentId: nestedFolder.id,
  });
  writingId = writing.id;
  createdWritingIds.push(writing.id);

  const paragraphs = [
    'Attention is less a private talent than a way of receiving the world. A person attends by allowing an object, a sentence, or another person to become more than a prompt for the next thought. The practice is modest: remain long enough for the first impression to become a question.',
    'A difficult page asks for a different pace. When the argument resists quick summary, the reader can notice where the resistance comes from: an unfamiliar term, a turn in the evidence, or the habit of wanting a conclusion before the middle has been heard.',
    'This is one reason a notebook is useful. It gives a thought somewhere to wait without forcing the thought to become a thesis immediately. A note can be a door held open, a record of uncertainty, or a line that deserves another visit.',
    'The work of reading continues after the book is closed. Memory edits, joins, and sometimes corrects the first account. Returning to a marked passage is not repetition for its own sake; it is a way to ask whether the question has become more exact.',
    'A writing can keep those returns nearby while leaving the page free to change. The source remains available as evidence, but the sentence in the manuscript must still earn its place through its own rhythm, context, and claim.',
    'Each return to the source should keep the question visible without letting a citation substitute for thought. A useful reference records the page, the claim, and what remains uncertain. The writer can compare the draft with the original language, decide what belongs in the argument, and leave the rest nearby for another reading. This return leaves room for revision.',
  ];
  const manuscript = [
    '# The Quiet Practice of Paying Attention',
    '',
    paragraphs[0],
    '',
    '## A page at a time',
    '',
    paragraphs[1],
    '',
    '> A sentence is not finished when it sounds certain; it is finished when its attention has arrived at the thing it means.',
    '',
    paragraphs[2],
    '',
    '## Returning without hurry',
    '',
    paragraphs[3],
    '',
    '- Keep the question beside the evidence.',
    '- Let a quotation remain in its original context.',
    '- Revise the claim when the page asks for it.',
    '',
    paragraphs[4],
    '',
    paragraphs[5],
    '',
    'A related note can be found in [the reading journal](https://example.com/reading-journal), where the question began.',
  ].join('\n');
  await apiPut(baseUrl, '/api/writings/' + writing.id, {
    name: writingTitle,
    content: manuscript,
  });

  longWritingTitle = 'S2 Long Manuscript — ' + 'A Draft with Many Passages '.repeat(2) + stamp;
  const longWriting = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
    name: longWritingTitle,
    type: 'Document',
    parentId: nestedFolder.id,
  });
  longWritingId = longWriting.id;
  createdWritingIds.push(longWriting.id);
  const longManuscript = Array.from({ length: 36 }, (_, index) =>
    [
      '## Passage ' + String(index + 1).padStart(2, '0') + ' — Returning to the Argument',
      paragraphs[index % paragraphs.length],
      paragraphs[(index + 1) % paragraphs.length],
    ].join('\n\n'),
  ).join('\n\n');
  if (longManuscript.length < 13_000) {
    throw new Error('The S2 long-manuscript fixture must exceed 13,000 characters.');
  }
  await apiPut(baseUrl, '/api/writings/' + longWriting.id, {
    name: longWritingTitle,
    content: longManuscript,
  });

  const mainBook = await apiPost<{ id: string }>(baseUrl, '/api/books', {
    type: 'physical',
    title: mainBookTitle,
    author: 'Nostos S2 Fixture',
  });
  mainBookId = mainBook.id;
  createdBookIds.push(mainBook.id);

  topicNames = Array.from({ length: 40 }, (_, index) =>
    index === 39
      ? 'S2 Topic 39 — ' + 'A Very Long Topic Name for Patient Reading '.repeat(3)
      : 'S2 Topic ' + String(index).padStart(2, '0') + ' — Attention and the Reader',
  );
  const quoteSentence =
    'Attention asks the reader to remain with the sentence before asking it to explain itself. ';
  longQuote = quoteSentence.repeat(8).slice(0, 467);
  noteOnlyContent = (
    '[[' +
    topicNames[1] +
    ']] A note without a quotation can still keep a practical thought close to its source. ' +
    'It records the claim, the hesitation, and the small detail that may matter when the draft returns to this page. '
  ).padEnd(576, ' ').slice(0, 576);

  for (let index = 0; index < 40; index++) {
    const content =
      index === 1
        ? noteOnlyContent
        : '[[' +
          topicNames[index] +
          ']] ' +
          (index === 0
            ? 'The note asks how attention changes when a reader returns to the same passage.'
            : 'A saved observation about reading, evidence, patience, and the sentence that remains after the page is closed.');
    const note = await apiPost<Note>(baseUrl, '/api/books/' + mainBook.id + '/notes', {
      content,
      ...(index === 0 ? { selectedText: longQuote } : {}),
    });
    noteIds.push(note.id);
  }

  for (let index = 0; index < 20; index++) {
    const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
      type: 'physical',
      title:
        'S2 Reference Book ' +
        String(index + 1).padStart(2, '0') +
        ' — ' +
        'Collected Volume of Reading and Reflection '.repeat(2) +
        stamp,
      author: 'Nostos S2 Fixture',
    });
    createdBookIds.push(book.id);
    await apiPost<Note>(baseUrl, '/api/books/' + book.id + '/notes', {
      content:
        '[[S2 Book Topic ' +
        String(index + 1).padStart(2, '0') +
        ' — ' +
        'A long subject for source browsing '.repeat(2) +
        ']] A separate note keeps the book index populated for scroll restoration.',
    });
  }
});

test.afterAll(async () => {
  for (const id of [...createdWritingIds].reverse()) {
    await apiDelete(baseUrl, '/api/writings/' + id);
  }
  for (const id of createdBookIds) {
    await apiDelete(baseUrl, '/api/books/' + id);
  }
  try {
    const topics = await apiGet<{ id: string }[]>(baseUrl, '/api/topics');
    for (const topic of topics) {
      if (!createdTopicIdsBefore.has(topic.id)) {
        await apiDelete(baseUrl, '/api/topics/' + topic.id);
      }
    }
  } catch (error) {
    console.warn('[studio-reference] topic cleanup failed:', error);
  }
});

test('Reference browsing, inspection, keep, and insertion preserve the writing', async ({ page }) => {
  const browserErrors: string[] = [];
  const apiFailures: string[] = [];
  page.on('pageerror', (error) => browserErrors.push(error.message));
  page.on('console', (message) => {
    if (message.type() === 'error') browserErrors.push(message.text());
  });
  page.on('response', (response) => {
    if (
      response.url().includes('/api/') &&
      /writings|books|topics/.test(response.url()) &&
      response.status() >= 400
    ) {
      apiFailures.push(response.status() + ' ' + response.url());
    }
  });

  await page.addInitScript(() => localStorage.setItem('nostos.theme', 'light'));
  await openWriting(page);
  const initialWriting = await apiGet<WritingContentDto>(
    baseUrl,
    '/api/writings/' + writingId,
  );
  const manuscriptWordCount = page.locator('.editor-desk-telemetry .word-count-num');
  await expect
    .poll(async () => Number(await manuscriptWordCount.textContent()))
    .toBeGreaterThan(340);
  expect(Number(await manuscriptWordCount.textContent())).toBeLessThanOrEqual(360);
  await expect(page.locator('.kept-sources-content .reference-empty-state')).toContainText(
    'Keep notes here from Library',
  );
  await expect(page.getByRole('button', { name: 'Browse Library' })).toBeVisible();
  await captureAfter(page, 'reference-for-writing-empty-desktop-1440x900-light.png');

  await page.goto(baseUrl + '/studio?writingId=' + longWritingId, {
    waitUntil: 'domcontentloaded',
  });
  await page.locator('.tox-tinymce').waitFor({ timeout: 45_000 });
  await expect(page.locator('.header-doc-title')).toHaveText(longWritingTitle);
  const longEditorBody = page.frameLocator('.tox-edit-area iframe').locator('body');
  await expect(longEditorBody).toContainText('Passage 36', { timeout: 45_000 });
  const longEditorLength = await longEditorBody.evaluate((element) => (element.textContent ?? '').length);
  expect(longEditorLength).toBeGreaterThan(13_000);
  await openWriting(page);

  await page.getByRole('button', { name: 'Browse Library' }).click();
  await typeAndRemoveProbe(page, 'S2Mode');
  const topicSearch = page.getByRole('textbox', { name: 'Search topics' });
  await topicSearch.fill('S2 Topic');
  await page.setViewportSize({ width: 1280, height: 720 });
  await expect(page.locator('.list-item').first()).toBeVisible();
  await expect(page.locator('.header-save-status')).toHaveText('Saved', { timeout: 15_000 });
  await captureAfter(page, 'reference-library-topics-desktop-1280x720-light.png');

  const topicIndex = page.locator('[data-reference-scroll="topics"]');
  await topicIndex.evaluate((element) => {
    element.scrollTop = 520;
  });
  const topicScroll = await topicIndex.evaluate((element) => element.scrollTop);
  await page.locator('.list-item').filter({ hasText: topicNames[39] }).click();
  await expect(page.getByRole('button', { name: 'Back to Topics' })).toBeVisible();
  await expect(page.locator('.reference-note-list .reference-source-row')).toHaveCount(1);
  await typeAndRemoveProbe(page, 'S2Topic');
  await page.getByRole('button', { name: 'Back to Topics' }).click();
  await expect(topicSearch).toHaveValue('S2 Topic');
  await expect
    .poll(() => topicIndex.evaluate((element) => element.scrollTop))
    .toBeGreaterThanOrEqual(topicScroll - 2);

  await page.locator('.library-tabs').getByRole('tab', { name: 'Books' }).click();
  await typeAndRemoveProbe(page, 'S2Books');
  const bookSearch = page.getByRole('textbox', { name: 'Search books' });
  await bookSearch.fill('S2 Reference Book');
  await page.setViewportSize({ width: 820, height: 1180 });
  await page.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '280px';
  });
  await expect(page.locator('.list-item').first()).toBeVisible();
  await expect(page.locator('.header-save-status')).toHaveText('Saved', { timeout: 15_000 });
  await captureAfter(page, 'reference-book-index-tablet-820x1180-light.png');

  const bookIndex = page.locator('[data-reference-scroll="books"]');
  await bookIndex.evaluate((element) => {
    element.scrollTop = 420;
  });
  const bookScroll = await bookIndex.evaluate((element) => element.scrollTop);
  await page.locator('.list-item').filter({ hasText: mainBookTitle }).click();
  await expect(page.getByRole('button', { name: 'Back to Books' })).toBeVisible();
  await expect(page.locator('.reference-note-list .reference-source-row')).toHaveCount(40);
  await typeAndRemoveProbe(page, 'S2BookNotes');
  await page.setViewportSize({ width: 1024, height: 768 });
  await page.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '280px';
  });
  await expect(page.locator('.header-save-status')).toHaveText('Saved', { timeout: 15_000 });
  await captureAfter(page, 'reference-book-notes-narrow-1024x768-light.png');

  const noteOnlyRow = page
    .locator('.reference-note-list .reference-source-row-main')
    .filter({ hasText: noteOnlyContent.slice(95, 130) })
    .first();
  await noteOnlyRow.focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('.inspected-source-panel')).toBeVisible();
  await expect(page.locator('.inspected-source-card')).toHaveCount(1);
  await expect(page.locator('.reference-source-row-main')).toHaveCount(0);
  await expect(page.locator('.source-action-primary-row .nostos-button--primary')).toHaveText(
    'Insert note',
  );
  await expect(page.locator('.inspected-source-open')).toContainText('Open source');
  await page.evaluate(() => {
    document.documentElement.setAttribute('data-theme', 'dark');
    localStorage.setItem('nostos.theme', 'dark');
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '360px';
  });
  await captureAfter(page, 'reference-source-note-only-desktop-1440x900-dark.png');

  await page.getByRole('button', { name: 'Back to ' + mainBookTitle }).click();
  await expect(page.locator('.reference-note-list .reference-source-row')).toHaveCount(40);
  await page.getByRole('button', { name: 'Back to Books' }).click();
  await expect(bookSearch).toHaveValue('S2 Reference Book');
  await expect
    .poll(() => bookIndex.evaluate((element) => element.scrollTop))
    .toBeGreaterThanOrEqual(bookScroll - 2);

  await page.locator('.list-item').filter({ hasText: mainBookTitle }).click();
  const quoteRow = page
    .locator('.reference-note-list .reference-source-row-main')
    .filter({ hasText: longQuote.slice(0, 32) })
    .first();
  await quoteRow.click();
  await expect(page.locator('.source-action-primary-row .nostos-button--primary')).toHaveText(
    'Insert quote',
  );
  const primary = page.locator('.source-action-primary-row .nostos-button--primary');
  await expect(primary).toBeEnabled();
  const quoteBeforeKeep = await apiGet<WritingContentDto>(
    baseUrl,
    '/api/writings/' + writingId,
  );

  const more = page.getByRole('button', { name: 'More source actions' });
  await more.focus();
  await page.keyboard.press('Enter');
  await expect(more).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('.source-more-actions button')).toHaveText([
    'Insert note',
    'Insert as reference',
  ]);
  await page.keyboard.press('Tab');
  await expect(page.locator('.source-more-actions button').first()).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(page.locator('.source-more-actions')).toHaveCount(0);
  await expect(more).toBeFocused();

  const keepWithWriting = page.locator('.source-action-primary-row .nostos-button--secondary');
  await expect(keepWithWriting).toHaveAttribute('aria-label', 'Keep with this writing');
  await keepWithWriting.click();
  await expect(page.locator('.source-kept-state')).toContainText('Kept with this writing');
  const persistedKept = await apiGet<WritingSourceDto[]>(
    baseUrl,
    '/api/writings/' + writingId + '/notes',
  );
  expect(persistedKept.map((source) => source.id)).toEqual([noteIds[0]]);
  expect(
    (await apiGet<WritingContentDto>(baseUrl, '/api/writings/' + writingId)).content,
  ).toBe(quoteBeforeKeep.content);
  await page.setViewportSize({ width: 1024, height: 768 });
  await page.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '280px';
  });
  await page.waitForTimeout(350);
  await captureAfter(page, 'reference-source-long-quote-desktop-1024x768-light.png');

  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.locator('.tox-tinymce').waitFor({ timeout: 45_000 });
  await expect(page.locator('.kept-sources-content .reference-source-row')).toHaveCount(1);
  const keptRow = page.locator('.kept-sources-content .reference-source-row-main');
  await keptRow.focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('.inspected-source-card')).toHaveCount(1);
  await expect(page.locator('.reference-source-row')).toHaveCount(0);
  await expect(page.locator('.source-kept-state')).toContainText('Kept with this writing');

  const sourceMenu = page.getByRole('button', { name: 'More source actions' });
  await sourceMenu.click();
  await expect(page.locator('.source-more-actions button')).toHaveText([
    'Insert note',
    'Insert as reference',
    'Remove from this writing',
  ]);
  await sourceMenu.click();

  const manuscriptBeforeInsert = await apiGet<WritingContentDto>(
    baseUrl,
    '/api/writings/' + writingId,
  );
  const editorBody = page.frameLocator('.tox-edit-area iframe').locator('body');
  await editorBody.click();
  await page.keyboard.press('Control+End');
  await page.getByRole('button', { name: 'Insert quote', exact: true }).click();
  const lastQuote = editorBody.locator('blockquote').last();
  await expect(lastQuote).toContainText(longQuote);
  await expect(lastQuote).toContainText(mainBookTitle);
  await expect
    .poll(async () => (await apiGet<WritingContentDto>(baseUrl, '/api/writings/' + writingId)).content)
    .toContain(longQuote);

  await page.getByRole('tab', { name: 'Library' }).click();
  await typeAndRemoveProbe(page, 'S2AfterInsert');
  await page.locator('.library-tabs').getByRole('tab', { name: 'Books' }).click();
  await typeAndRemoveProbe(page, 'S2AfterBooks');
  await page.locator('.list-item').filter({ hasText: mainBookTitle }).click();
  const unkeptNoteOnly = page
    .locator('.reference-note-list .reference-source-row-main')
    .filter({ hasText: noteOnlyContent.slice(95, 130) })
    .first();
  await unkeptNoteOnly.click();
  await page.getByRole('button', { name: 'Insert note', exact: true }).click();
  await expect(editorBody).toContainText(noteOnlyContent.slice(95, 130));
  await expect
    .poll(async () => (await apiGet<WritingContentDto>(baseUrl, '/api/writings/' + writingId)).content)
    .toContain(noteOnlyContent.slice(95, 130));
  expect(
    await apiGet<WritingSourceDto[]>(baseUrl, '/api/writings/' + writingId + '/notes'),
  ).toHaveLength(1);
  expect(manuscriptBeforeInsert.content).not.toContain(longQuote);
  expect(browserErrors).toEqual([]);
  expect(apiFailures).toEqual([]);
});

test('kept-source density and responsive Reference rows hold at 0, 3, 8, and 40', async ({
  page,
  browser,
}) => {
  const addSources = async (start: number, end: number) => {
    for (let index = start; index < end; index++) {
      await apiPost(baseUrl, '/api/writings/' + writingId + '/notes', {
        noteId: noteIds[index],
      });
    }
  };

  const reloadWriting = async (viewport: { width: number; height: number }) => {
    await openWriting(page, viewport);
    await expect(page.locator('.kept-sources-content .reference-source-row')).toHaveCount(
      await apiGet<WritingSourceDto[]>(baseUrl, '/api/writings/' + writingId + '/notes').then(
        (sources) => sources.length,
      ),
    );
  };

  const initialSources = await apiGet<WritingSourceDto[]>(
    baseUrl,
    '/api/writings/' + writingId + '/notes',
  );
  if (initialSources.length === 0) {
    await apiPost(baseUrl, '/api/writings/' + writingId + '/notes', {
      noteId: noteIds[0],
    });
  }
  const seededSources = await apiGet<WritingSourceDto[]>(
    baseUrl,
    '/api/writings/' + writingId + '/notes',
  );
  expect(seededSources).toHaveLength(1);
  await addSources(1, 3);
  await reloadWriting({ width: 1024, height: 768 });
  await expect(page.locator('.kept-sources-content .reference-source-row')).toHaveCount(3);
  await page.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '280px';
  });
  await captureAfter(page, 'reference-for-writing-few-desktop-1024x768-light.png');

  await addSources(3, 8);
  await reloadWriting({ width: 1440, height: 900 });
  await expect(page.locator('.kept-sources-content .reference-source-row')).toHaveCount(8);
  await captureAfter(page, 'reference-for-writing-many-desktop-1440x900-light.png');

  await addSources(8, 40);
  await reloadWriting({ width: 1280, height: 720 });
  await expect(page.locator('.kept-sources-content .reference-source-row')).toHaveCount(40);
  await page.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '280px';
  });
  const narrowRowGeometry = await page.locator('.reference-source-row').first().evaluate((row) => {
    const main = row.querySelector('.reference-source-row-main')!.getBoundingClientRect();
    const action = row.querySelector('.reference-row-action')!.getBoundingClientRect();
    const preview = row.querySelector('.reference-source-preview')!;
    return {
      rowCount: document.querySelectorAll('.reference-source-row').length,
      mainRight: main.right,
      actionLeft: action.left,
      previewScrollWidth: preview.scrollWidth,
      previewClientWidth: preview.clientWidth,
      horizontalOverflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    };
  });
  expect(narrowRowGeometry.rowCount).toBe(40);
  expect(narrowRowGeometry.mainRight).toBeLessThanOrEqual(narrowRowGeometry.actionLeft);
  expect(narrowRowGeometry.previewScrollWidth).toBeLessThanOrEqual(
    narrowRowGeometry.previewClientWidth + 1,
  );
  expect(narrowRowGeometry.horizontalOverflow).toBeLessThanOrEqual(1);
  await captureAfter(page, 'reference-for-writing-forty-laptop-1280x720-light.png');
  await page.evaluate(() => {
    document.documentElement.setAttribute('data-theme', 'dark');
    localStorage.setItem('nostos.theme', 'dark');
  });
  await captureAfter(page, 'reference-for-writing-forty-laptop-1280x720-dark.png');

  const touchContext = await browser.newContext({
    viewport: { width: 390, height: 844 },
    isMobile: true,
    hasTouch: true,
    deviceScaleFactor: 1,
  });
  const touchPage = await touchContext.newPage();
  await touchPage.addInitScript(() => localStorage.setItem('nostos.theme', 'light'));
  await openWriting(touchPage, { width: 390, height: 844 });
  const referenceToggle = touchPage.getByRole('button', {
    name: 'Toggle reference sidebar',
  });
  await referenceToggle.click();
  await expect(touchPage.locator('.sidebar-right')).toHaveClass(/open/);
  await expect(touchPage.locator('.kept-sources-content .reference-source-row')).toHaveCount(
    40,
  );
  const actionTarget = await touchPage
    .locator('.reference-row-action')
    .first()
    .boundingBox();
  expect(actionTarget?.width).toBeGreaterThanOrEqual(44);
  expect(actionTarget?.height).toBeGreaterThanOrEqual(44);

  await touchPage.locator('.kept-sources-content .reference-source-row-main').first().click();
  await expect(touchPage.locator('.inspected-source-card')).toHaveCount(1);
  await captureAfter(touchPage, 'mobile-reference-inspection-mobile-390x844-light.png');
  await touchPage.evaluate(() => {
    document.documentElement.setAttribute('data-theme', 'dark');
    localStorage.setItem('nostos.theme', 'dark');
  });
  await captureAfter(touchPage, 'mobile-reference-inspection-mobile-390x844-dark.png');
  await touchContext.close();

  await openWriting(page, { width: 844, height: 390 });
  await expect(page.locator('.kept-sources-content .reference-source-row')).toHaveCount(40);
  await page.locator('.kept-sources-content .reference-source-row-main').first().click();
  await expect(page.locator('.inspected-source-card')).toHaveCount(1);
  await captureAfter(page, 'mobile-reference-inspection-landscape-844x390-light.png');
});
