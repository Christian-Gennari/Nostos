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
async function undersizedVisibleControls(page: Page, rootSelector: string): Promise<string[]> {
  return page.evaluate((selector) => {
    const root = document.querySelector(selector);
    if (!root) return ['missing root ' + selector];
    const candidates = Array.from(root.querySelectorAll(
      'button, select, textarea, input:not([type="hidden"]):not([type="checkbox"]), [role="tab"], a[appButton], a.source-badge, a.brain-source-link, a.empty-link',
    ));
    return candidates.flatMap((element) => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      if (!rect.width || !rect.height || style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0' || element.closest('[aria-hidden="true"]')) return [];
      if (rect.width >= 44 && rect.height >= 44) return [];
      const label = element.getAttribute('aria-label') || (element.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 50);
      return [`${label || element.tagName}: ${rect.width.toFixed(1)}x${rect.height.toFixed(1)}`];
    });
  }, rootSelector);
}

const dockViewports = [
  { width: 1440, height: 900 },
  { width: 1280, height: 720 },
  { width: 1024, height: 768 },
  { width: 820, height: 1180 },
  { width: 390, height: 844 },
  { width: 844, height: 390 },
];

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

async function ensureReferenceDrawerOpen(page: Page): Promise<void> {
  const toggle = page.locator('.reference-toggle');
  const drawer = page.locator('.sidebar-right');
  await expect(toggle).toBeVisible();
  if ((await toggle.getAttribute('aria-expanded')) === 'false') {
    await toggle.click();
  }
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  await expect(drawer).toHaveClass(/\bopen\b/);
  const viewport = page.viewportSize();
  if (viewport) {
    await expect
      .poll(() =>
        drawer.evaluate((element) => Math.round(element.getBoundingClientRect().right)),
      )
      .toBe(viewport.width);
  }
}

async function expectDarkEditor(page: Page): Promise<void> {
  const editorDocument = page.frameLocator('.tox-edit-area iframe').locator('html');
  await expect(editorDocument).toHaveAttribute('data-theme', 'dark');
  await expect
    .poll(() => editorDocument.evaluate((element) => getComputedStyle(element).backgroundColor))
    .toBe('rgb(18, 19, 24)');
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
  const dockFailures: string[] = [];
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
  await expect
    .poll(() =>
      page
        .locator('.sidebar-right')
        .evaluate((element) => Math.round(element.getBoundingClientRect().width)),
    )
    .toBe(360);
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
  for (const viewport of dockViewports) {
    await page.setViewportSize(viewport);
    await ensureReferenceDrawerOpen(page);
    await expect(topicIndex).toBeVisible();
    await topicIndex.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
    });
    const topicIndexEnd = await page.evaluate(() => {
      const row = document
        .querySelector('[data-reference-scroll="topics"] .list-item:last-child')!
        .getBoundingClientRect();
      const dock = document.querySelector('app-app-dock')!.getBoundingClientRect();
      return { rowBottom: row.bottom, dockTop: dock.top };
    });
    if (topicIndexEnd.rowBottom > topicIndexEnd.dockTop) {
      dockFailures.push(`Reference Topics at ${viewport.width}x${viewport.height}: ${topicIndexEnd.rowBottom}px > dock ${topicIndexEnd.dockTop}px`);
    }
    if (viewport.width === 820 || viewport.width === 390 || viewport.width === 844) {
      await captureAfter(
        page,
        'reference-topics-scroll-end-after-' + viewport.width + 'x' + viewport.height + '-light.png',
      );
    }
  }
  await page.setViewportSize({ width: 1280, height: 720 });
  await ensureReferenceDrawerOpen(page);
  await topicIndex.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
  });
  const topicScroll = await topicIndex.evaluate((element) => element.scrollTop);
  await captureAfter(page, 'reference-topics-scroll-end-after-1280x720-light.png');
  await page.locator('.list-item').filter({ hasText: topicNames[39] }).click();
  await expect(page.getByRole('button', { name: 'Back to Topics' })).toBeVisible();
  await expect(page.locator('.reference-note-list .reference-source-row')).toHaveCount(1);
  const topicNotes = page.locator('[data-reference-scroll="topicNotes"]');
  for (const viewport of dockViewports) {
    await page.setViewportSize(viewport);
    await ensureReferenceDrawerOpen(page);
    await expect(topicNotes).toBeVisible();
    await topicNotes.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
    });
    const topicNotesEnd = await page.evaluate(() => {
      const row = document
        .querySelector('[data-reference-scroll="topicNotes"] .reference-source-row:last-child')!
        .getBoundingClientRect();
      const dock = document.querySelector('app-app-dock')!.getBoundingClientRect();
      return { rowBottom: row.bottom, dockTop: dock.top };
    });
    if (topicNotesEnd.rowBottom > topicNotesEnd.dockTop) {
      dockFailures.push(`Reference topic notes at ${viewport.width}x${viewport.height}: ${topicNotesEnd.rowBottom}px > dock ${topicNotesEnd.dockTop}px`);
    }
    if (viewport.width === 820 || viewport.width === 844) {
      await captureAfter(
        page,
        'reference-topic-notes-scroll-end-after-' + viewport.width + 'x' + viewport.height + '-light.png',
      );
    }
  }
  await page.setViewportSize({ width: 1280, height: 720 });
  await ensureReferenceDrawerOpen(page);
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
  await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'false');
  await page.locator('.reference-toggle').click();
  await expect(page.locator('.sidebar-right')).toHaveClass(/\bopen\b/);
  await expect
    .poll(() =>
      page
        .locator('.sidebar-right')
        .evaluate((element) => Math.round(element.getBoundingClientRect().width)),
    )
    .toBe(360);
  await expect(page.locator('.list-item').first()).toBeVisible();
  await expect(page.locator('.header-save-status')).toHaveText('Saved', { timeout: 15_000 });
  await captureAfter(page, 'reference-book-index-tablet-820x1180-light.png');

  const bookIndex = page.locator('[data-reference-scroll="books"]');
  await bookSearch.fill('');
  await expect(bookIndex.locator('.list-item').first()).toBeVisible();
  for (const viewport of dockViewports) {
    await page.setViewportSize(viewport);
    await ensureReferenceDrawerOpen(page);
    await expect(bookIndex).toBeVisible();
    await bookIndex.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
    });
    const bookIndexEnd = await page.evaluate(() => {
      const row = document
        .querySelector('[data-reference-scroll="books"] .list-item:last-child')!
        .getBoundingClientRect();
      const dock = document.querySelector('app-app-dock')!.getBoundingClientRect();
      return { rowBottom: row.bottom, dockTop: dock.top };
    });
    if (bookIndexEnd.rowBottom > bookIndexEnd.dockTop) {
      dockFailures.push(`Reference Books at ${viewport.width}x${viewport.height}: ${bookIndexEnd.rowBottom}px > dock ${bookIndexEnd.dockTop}px`);
    }
    if (viewport.width === 820 || viewport.width === 390 || viewport.width === 844) {
      await captureAfter(
        page,
        'reference-books-scroll-end-after-' + viewport.width + 'x' + viewport.height + '-light.png',
      );
    }
  }
  await page.setViewportSize({ width: 820, height: 1180 });
  await ensureReferenceDrawerOpen(page);
  await bookIndex.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
  });
  await captureAfter(page, 'reference-books-scroll-end-after-820x1180-light.png');
  await bookSearch.fill('S2 Reference Book');
  await bookIndex.evaluate((element) => {
    element.scrollTop = 420;
  });
  const bookScroll = await bookIndex.evaluate((element) => element.scrollTop);
  await page.locator('.list-item').filter({ hasText: mainBookTitle }).click();
  await expect(page.getByRole('button', { name: 'Back to Books' })).toBeVisible();
  await expect(page.locator('.reference-note-list .reference-source-row')).toHaveCount(40);
  const bookNotes = page.locator('[data-reference-scroll="bookNotes"]');
  for (const viewport of dockViewports) {
    await page.setViewportSize(viewport);
    await ensureReferenceDrawerOpen(page);
    await expect(bookNotes).toBeVisible();
    await bookNotes.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
    });
    const bookNotesEnd = await page.evaluate(() => {
      const row = document
        .querySelector('[data-reference-scroll="bookNotes"] .reference-source-row:last-child')!
        .getBoundingClientRect();
      const dock = document.querySelector('app-app-dock')!.getBoundingClientRect();
      return { rowBottom: row.bottom, dockTop: dock.top };
    });
    if (bookNotesEnd.rowBottom > bookNotesEnd.dockTop) {
      dockFailures.push(`Reference book notes at ${viewport.width}x${viewport.height}: ${bookNotesEnd.rowBottom}px > dock ${bookNotesEnd.dockTop}px`);
    }
    if (viewport.width === 820 || viewport.width === 844) {
      await captureAfter(
        page,
        'reference-book-notes-scroll-end-after-' + viewport.width + 'x' + viewport.height + '-light.png',
      );
    }
  }
  await page.setViewportSize({ width: 820, height: 1180 });
  await ensureReferenceDrawerOpen(page);
  await bookNotes.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
  });
  await captureAfter(page, 'reference-book-notes-scroll-end-after-820x1180-light.png');
  await page.locator('.reference-rail-collapse').click();
  await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'false');
  await typeAndRemoveProbe(page, 'S2BookNotes');
  await page.setViewportSize({ width: 1024, height: 768 });
  await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('.sidebar-right')).toBeVisible();
  await page.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '280px';
  });
  await expect
    .poll(() =>
      page
        .locator('.sidebar-right')
        .evaluate((element) => Math.round(element.getBoundingClientRect().width)),
    )
    .toBe(280);
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
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '360px';
  });
  await captureAfter(page, 'reference-source-note-only-desktop-1440x900-light.png');

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
  await page.locator('.files-toggle').click();
  await expect(page.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator('.sidebar-left')).toBeHidden();
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
  expect(dockFailures, 'Reference topic/book lists clear the dock').toEqual([]);
  expect(browserErrors).toEqual([]);
  expect(apiFailures).toEqual([]);
});

test('kept-source density and responsive Reference rows hold at 0, 3, 8, and 40', async ({
  page,
  browser,
}) => {
  const dockFailures: string[] = [];
  const touchTargetFailures: string[] = [];
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

  // Every dark capture starts Studio after setting the persisted theme, so the
  // editor iframe gets its theme during TinyMCE initialization as well as in
  // the shell. Changing only the outer document attribute leaves the iframe in
  // its prior theme and is not a valid product journey.
  const darkContext = await browser.newContext({
    viewport: { width: 1280, height: 720 },
  });
  const darkPage = await darkContext.newPage();
  await darkPage.addInitScript(() => localStorage.setItem('nostos.theme', 'dark'));
  await openWriting(darkPage, { width: 1280, height: 720 });
  await expectDarkEditor(darkPage);
  await expect(darkPage.locator('.kept-sources-content .reference-source-row')).toHaveCount(40);
  await darkPage.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '280px';
  });
  await captureAfter(darkPage, 'reference-for-writing-forty-laptop-1280x720-dark.png');

  await darkPage.getByRole('tab', { name: 'Library' }).click();
  await darkPage.locator('.library-tabs').getByRole('tab', { name: 'Books' }).click();
  await darkPage.getByRole('textbox', { name: 'Search books' }).fill('S2 Reference Book');
  await darkPage.locator('.list-item').filter({ hasText: mainBookTitle }).click();
  await expect(darkPage.locator('.reference-note-list .reference-source-row')).toHaveCount(40);
  await darkPage.setViewportSize({ width: 1024, height: 768 });
  await darkPage.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '280px';
  });
  await expect(darkPage.locator('.reference-note-list .reference-source-row').first().locator(
    '.library-keep-action.active',
  )).toBeVisible();
  await darkPage.waitForTimeout(350);
  await expectDarkEditor(darkPage);
  await captureAfter(darkPage, 'reference-book-notes-narrow-1024x768-dark.png');
  const darkNoteOnlyRow = darkPage
    .locator('.reference-note-list .reference-source-row-main')
    .filter({ hasText: noteOnlyContent.slice(95, 130) })
    .first();
  await darkNoteOnlyRow.click();
  await expectDarkEditor(darkPage);
  await expect(darkPage.locator('.source-action-primary-row .nostos-button--primary')).toHaveText(
    'Insert note',
  );
  await darkPage.setViewportSize({ width: 1440, height: 900 });
  await darkPage.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '360px';
  });
  await captureAfter(darkPage, 'reference-source-note-only-desktop-1440x900-dark.png');

  await darkPage.getByRole('button', { name: 'Back to ' + mainBookTitle }).click();
  await darkPage.getByRole('button', { name: 'Back to Books' }).click();
  await darkPage.locator('.list-item').filter({ hasText: mainBookTitle }).click();
  const darkQuoteRow = darkPage
    .locator('.reference-note-list .reference-source-row-main')
    .filter({ hasText: longQuote.slice(0, 32) })
    .first();
  await darkQuoteRow.click();
  await expectDarkEditor(darkPage);
  await expect(darkPage.locator('.source-action-primary-row .nostos-button--primary')).toHaveText(
    'Insert quote',
  );
  await darkPage.setViewportSize({ width: 1024, height: 768 });
  await darkPage.locator('.sidebar-right').evaluate((element) => {
    (element as HTMLElement).style.width = '280px';
  });
  await captureAfter(darkPage, 'reference-source-long-quote-desktop-1024x768-dark.png');
  await darkContext.close();

  const touchContext = await browser.newContext({
    viewport: { width: 390, height: 844 },
    isMobile: true,
    hasTouch: true,
    deviceScaleFactor: 1,
  });
  const touchPage = await touchContext.newPage();
  await touchPage.addInitScript(() => localStorage.setItem('nostos.theme', 'light'));
  await openWriting(touchPage, { width: 390, height: 844 });
  const referenceToggle = touchPage.locator('.reference-toggle');
  await referenceToggle.click();
  await expect(touchPage.locator('.sidebar-right')).toHaveClass(/open/);
  await expect
    .poll(() =>
      touchPage
        .locator('.sidebar-right')
        .evaluate((element) => Math.round(element.getBoundingClientRect().width)),
    )
    .toBe(390);
  await expect(touchPage.locator('.kept-sources-content .reference-source-row')).toHaveCount(
    40,
  );
  const writingSourceList = touchPage.locator('.kept-sources-content');
  for (const viewport of dockViewports) {
    await touchPage.setViewportSize(viewport);
    await ensureReferenceDrawerOpen(touchPage);
    await expect(writingSourceList).toBeVisible();
    await writingSourceList.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
    });
    const writingListEnd = await touchPage.evaluate(() => {
      const row = document
        .querySelector('.kept-sources-content .reference-source-row:last-child')!
        .getBoundingClientRect();
      const dock = document.querySelector('app-app-dock')!.getBoundingClientRect();
      return { rowBottom: row.bottom, dockTop: dock.top };
    });
    if (writingListEnd.rowBottom > writingListEnd.dockTop) {
      dockFailures.push(`For this writing at ${viewport.width}x${viewport.height}: ${writingListEnd.rowBottom}px > dock ${writingListEnd.dockTop}px`);
    }
    if (viewport.width === 390 || viewport.width === 820 || viewport.width === 844) {
      await captureAfter(
        touchPage,
        'reference-for-writing-scroll-end-after-' +
          viewport.width + 'x' + viewport.height + '-light.png',
      );
    }
  }
  for (const viewport of [
    { width: 390, height: 844 },
    { width: 820, height: 1180 },
  ]) {
    await touchPage.setViewportSize(viewport);
    const referenceTabHeights = await touchPage
      .locator('.reference-mode-switch .tab-btn')
      .evaluateAll((tabs) => tabs.map((tab) => tab.getBoundingClientRect().height));
    expect(referenceTabHeights.length).toBe(2);
    if (!referenceTabHeights.every((height) => height >= 44)) {
      touchTargetFailures.push(`Reference tab heights ${viewport.width}x${viewport.height}: ${JSON.stringify(referenceTabHeights)}`);
    }
    const smallControls = await undersizedVisibleControls(touchPage, '.studio-layout');
    if (smallControls.length) touchTargetFailures.push(`Studio For this writing ${viewport.width}x${viewport.height}: ${JSON.stringify(smallControls)}`);
  }
  await ensureReferenceDrawerOpen(touchPage);
  await touchPage.getByRole('tab', { name: 'Library', exact: true }).click();
  await touchPage.locator('.library-tabs').getByRole('tab', { name: 'Books', exact: true }).click();
  const touchBookSearch = touchPage.getByRole('textbox', { name: 'Search books' });
  await touchBookSearch.fill(mainBookTitle);
  await expect(touchPage.locator('.list-item').filter({ hasText: mainBookTitle })).toBeVisible();
  for (const viewport of [
    { width: 390, height: 844 },
    { width: 820, height: 1180 },
  ]) {
    await touchPage.setViewportSize(viewport);
    await ensureReferenceDrawerOpen(touchPage);
    const smallControls = await undersizedVisibleControls(touchPage, '.studio-layout');
    if (smallControls.length) touchTargetFailures.push(`Studio Library Books ${viewport.width}x${viewport.height}: ${JSON.stringify(smallControls)}`);
  }
  await touchPage.locator('.list-item').filter({ hasText: mainBookTitle }).click();
  await expect(touchPage.locator('.reference-note-list .reference-source-row')).toHaveCount(40);
  for (const viewport of [
    { width: 390, height: 844 },
    { width: 820, height: 1180 },
  ]) {
    await touchPage.setViewportSize(viewport);
    await ensureReferenceDrawerOpen(touchPage);
    const smallControls = await undersizedVisibleControls(touchPage, '.studio-layout');
    if (smallControls.length) touchTargetFailures.push(`Studio book notes ${viewport.width}x${viewport.height}: ${JSON.stringify(smallControls)}`);
  }
  const touchLongQuoteRow = touchPage
    .locator('.reference-note-list .reference-source-row-main')
    .filter({ hasText: longQuote.slice(0, 32) })
    .first();
  await touchLongQuoteRow.click();
  await expect(touchPage.locator('.inspected-source-card')).toContainText(longQuote);
  for (const viewport of [
    { width: 390, height: 844 },
    { width: 820, height: 1180 },
  ]) {
    await touchPage.setViewportSize(viewport);
    await ensureReferenceDrawerOpen(touchPage);
    const smallControls = await undersizedVisibleControls(touchPage, '.studio-layout');
    if (smallControls.length) touchTargetFailures.push(`Studio quote inspector ${viewport.width}x${viewport.height}: ${JSON.stringify(smallControls)}`);
  }
  await touchPage.getByRole('tab', { name: 'For this writing', exact: true }).click();
  await expect(touchPage.locator('.kept-sources-content .reference-source-row')).toHaveCount(40);
  await touchPage.setViewportSize({ width: 390, height: 844 });

  const keptSources = touchPage.locator('.kept-sources-content');
  await keptSources.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
  });
  const keptSourcesEnd = await touchPage.evaluate(() => {
    const row = document
      .querySelector('.kept-sources-content .reference-source-row:last-child')!
      .getBoundingClientRect();
    const dock = document.querySelector('app-app-dock')!.getBoundingClientRect();
    return { rowBottom: row.bottom, dockTop: dock.top };
  });
  if (keptSourcesEnd.rowBottom > keptSourcesEnd.dockTop) {
    dockFailures.push(`For this writing at 390x844: ${keptSourcesEnd.rowBottom}px > dock ${keptSourcesEnd.dockTop}px`);
  }
  await captureAfter(
    touchPage,
    'reference-for-writing-scroll-end-after-390x844-light.png',
  );
  const actionTarget = await touchPage
    .locator('.reference-row-action')
    .first()
    .evaluate((element) => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      return { width: rect.width, height: rect.height, cssWidth: style.width, cssHeight: style.height };
    });
  if (actionTarget.cssWidth !== '44px' || actionTarget.cssHeight !== '44px') {
    touchTargetFailures.push(`Reference row action CSS size: ${JSON.stringify(actionTarget)}`);
  }
  // Chromium can report 43.99997px from its transformed layout rectangle even
  // though the computed control box is exactly 44px. Compare at rendered-pixel
  // precision while still guarding the actual CSS target above.
  if (Math.round(actionTarget.width) < 44 || Math.round(actionTarget.height) < 44) {
    touchTargetFailures.push(`Reference row action box: ${JSON.stringify(actionTarget)}`);
  }

  await keptSources.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
  });
  await captureAfter(touchPage, 'mobile-reference-inspection-mobile-390x844-light.png');

  await touchPage.addInitScript(() => localStorage.setItem('nostos.theme', 'dark'));
  await openWriting(touchPage, { width: 390, height: 844 });
  await expectDarkEditor(touchPage);
  await touchPage.locator('.reference-toggle').click();
  await captureAfter(touchPage, 'mobile-reference-inspection-mobile-390x844-dark.png');
  await touchContext.close();

  await openWriting(page, { width: 844, height: 390 });
  await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'false');
  await page.locator('.reference-toggle').click();
  await expect(page.locator('.sidebar-right')).toHaveClass(/\bopen\b/);
  await expect
    .poll(() =>
      page
        .locator('.sidebar-right')
        .evaluate((element) => Math.round(element.getBoundingClientRect().width)),
    )
    .toBe(844);
  await expect(page.locator('.kept-sources-content .reference-source-row')).toHaveCount(40);
  const longQuoteRow = page
    .locator('.kept-sources-content .reference-source-row-main')
    .filter({ hasText: longQuote.slice(0, 32) })
    .first();
  await expect(longQuoteRow).toBeVisible();
  await longQuoteRow.click();
  await expect(page.locator('.inspected-source-card')).toContainText(longQuote);
  const inspectorPanel = page.locator('.inspected-source-panel');
  const inspectorActionRow = page.locator('.source-action-primary-row');
  const referenceDrawer = page.locator('.sidebar-right');
  for (const viewport of dockViewports) {
    await page.setViewportSize(viewport);
    if (!(await referenceDrawer.evaluate((element) => element.classList.contains('open')))) {
      await page.locator('.reference-toggle').click();
    }
    await expect(referenceDrawer).toHaveClass(/\bopen\b/);
    await expect
      .poll(() =>
        referenceDrawer.evaluate((element) =>
          Math.round(element.getBoundingClientRect().right),
        ),
      )
      .toBe(viewport.width);
    await expect(inspectorPanel).toBeVisible();
    await expect(inspectorActionRow).toBeVisible();
    const drawerBounds = await referenceDrawer.boundingBox();
    expect(drawerBounds).not.toBeNull();
    expect(drawerBounds!.x, `Reference drawer starts on-screen at ${viewport.width}x${viewport.height}`).toBeGreaterThanOrEqual(0);
    expect(
      drawerBounds!.x + drawerBounds!.width,
      `Reference drawer ends on-screen at ${viewport.width}x${viewport.height}`,
    ).toBeLessThanOrEqual(viewport.width);
    if (viewport.width === 844 && viewport.height === 390) {
      expect(drawerBounds!.width, 'Reference drawer fills short landscape').toBeGreaterThanOrEqual(
        viewport.width * 0.9,
      );
      expect(drawerBounds!.x, 'Reference drawer starts at the short-landscape edge').toBeLessThanOrEqual(
        1,
      );
    }
    await inspectorPanel.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
    });
    const inspectorEnd = await page.evaluate(() => {
      const action = document
        .querySelector('.source-action-primary-row')!
        .getBoundingClientRect();
      const dock = document.querySelector('app-app-dock')!.getBoundingClientRect();
      return { actionBottom: action.bottom, dockTop: dock.top, actionHeight: action.height };
    });
    expect(
      inspectorEnd.actionHeight,
      `Reference action row is visible at ${viewport.width}x${viewport.height}`,
    ).toBeGreaterThan(0);
    if (inspectorEnd.actionBottom > inspectorEnd.dockTop) {
      dockFailures.push(`Reference inspector actions at ${viewport.width}x${viewport.height}: ${inspectorEnd.actionBottom}px > dock ${inspectorEnd.dockTop}px`);
    }
    if (viewport.width === 1280) {
      await captureAfter(
        page,
        'reference-inspector-long-quote-actions-scroll-end-after-1280x720-light.png',
      );
    }
    if (viewport.width === 844) {
      await captureAfter(
        page,
        'reference-inspector-long-quote-actions-scroll-end-after-844x390-light.png',
      );
    }
  }
  expect(dockFailures, 'All Reference lists and inspector clear the dock').toEqual([]);
  expect(touchTargetFailures, 'Reference coarse-pointer controls meet 44px').toEqual([]);
});

test('source insertion stays unavailable through editor startup failure, then inserts and autosaves after retry', async ({ browser }) => {
  const context = await browser.newContext({ serviceWorkers: 'block' });
  const page = await context.newPage();
  const disposition = { value: 'hold' as 'hold' | 'abort' | 'continue' };
  let releaseTinyMce!: () => void;
  let signalTinyMceRequest!: () => void;
  const tinyMceRequested = new Promise<void>((resolve) => { signalTinyMceRequest = resolve; });
  const tinyMceGate = new Promise<void>((resolve) => { releaseTinyMce = resolve; });
  const writing = await apiGet<WritingContentDto>(baseUrl, '/api/writings/' + writingId);
  const initialContent = writing.content ?? '';

  await page.route('**/tinymce/tinymce.min.js', async (route) => {
    if (disposition.value === 'hold') {
      signalTinyMceRequest();
      await tinyMceGate;
    }
    if (disposition.value === 'abort') return route.abort();
    return route.continue();
  });
  await page.addInitScript(() => localStorage.setItem('nostos.theme', 'light'));
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(baseUrl + '/studio?writingId=' + writingId, { waitUntil: 'domcontentloaded' });
  await tinyMceRequested;
  await expect(page.locator('.header-doc-title')).toHaveText(writingTitle);
  await page.getByRole('tab', { name: 'Library' }).click();
  await page.locator('.library-tabs').getByRole('tab', { name: 'Books' }).click();
  await page.locator('.list-item').filter({ hasText: mainBookTitle }).click();
  const quoteRow = page
    .locator('.reference-note-list .reference-source-row-main')
    .filter({ hasText: longQuote.slice(0, 32) })
    .first();
  await quoteRow.click();
  const insertQuote = page.getByRole('button', { name: 'Insert quote', exact: true });
  await expect(insertQuote).toBeDisabled();
  await expect(page.locator('#source-action-disabled-reason')).toContainText(
    'The editor is not ready for insertion yet',
  );
  await page.screenshot({ path: '/tmp/809-studio-insert-editor-loading-1440x900-light.png', animations: 'disabled' });

  disposition.value = 'abort';
  releaseTinyMce();
  await expect(page.locator('textarea.editor-starting')).toHaveCount(0, { timeout: 15_000 });
  await expect(insertQuote).toBeDisabled();
  await expect(page.locator('#source-action-disabled-reason')).toContainText(
    'The editor is not ready for insertion yet',
  );
  await page.screenshot({ path: '/tmp/809-studio-insert-editor-error-1440x900-light.png', animations: 'disabled' });

  disposition.value = 'continue';
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.locator('.tox-tinymce').waitFor({ timeout: 45_000 });
  await expect(page.locator('.header-doc-title')).toHaveText(writingTitle);
  await page.getByRole('tab', { name: 'Library' }).click();
  await page.locator('.library-tabs').getByRole('tab', { name: 'Books' }).click();
  await page.locator('.list-item').filter({ hasText: mainBookTitle }).click();
  const retriedQuoteRow = page
    .locator('.reference-note-list .reference-source-row-main')
    .filter({ hasText: longQuote.slice(0, 32) })
    .first();
  await retriedQuoteRow.click();
  const retryInsert = page.getByRole('button', { name: 'Insert quote', exact: true });
  await expect(retryInsert).toBeEnabled();
  const editorBody = page.frameLocator('.tox-edit-area iframe').locator('body');
  await editorBody.click();
  await page.keyboard.press('Control+End');
  await retryInsert.click();
  const insertedQuote = editorBody.locator('blockquote').last();
  await expect(insertedQuote).toContainText(longQuote);
  await expect
    .poll(async () => (await apiGet<WritingContentDto>(baseUrl, '/api/writings/' + writingId)).content)
    .toContain(longQuote);
  const persisted = await apiGet<WritingContentDto>(baseUrl, '/api/writings/' + writingId);
  expect(persisted.content).not.toBe(initialContent);
  await page.screenshot({ path: '/tmp/809-studio-insert-recovered-1440x900-light.png', animations: 'disabled' });
  await page.addInitScript(() => localStorage.setItem('nostos.theme', 'dark'));
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.locator('.tox-tinymce').waitFor({ timeout: 45_000 });
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect(page.frameLocator('.tox-edit-area iframe').locator('body')).toContainText(longQuote);
  await page.screenshot({ path: '/tmp/809-studio-insert-recovered-1440x900-dark.png', animations: 'disabled' });
  await context.close();
});
