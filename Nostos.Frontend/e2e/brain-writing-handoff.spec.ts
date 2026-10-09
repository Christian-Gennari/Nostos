import { expect, test, type Locator } from '@playwright/test';

import { apiGet, apiPost, loadFixture } from './support/fixture';
import { apiDelete, cleanupBrain, snapshotTopicIds } from './support/brain-fixture';
import { apiPut } from './support/visual-capture';
import type { Note } from '../src/app/core/dtos/note.dtos';
import type {
  WritingContentDto,
  WritingDto,
  WritingSourceDto,
} from '../src/app/core/dtos/writing.dtos';

async function seedNote(baseUrl: string, title: string, content: string): Promise<{
  bookId: string;
  noteId: string;
}> {
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
    type: 'physical',
    title,
    author: 'Nostos QA',
  });
  const note = await apiPost<Note>(baseUrl, `/api/books/${book.id}/notes`, { content });
  return { bookId: book.id, noteId: note.id };
}

async function openHandoff(
  page: import('@playwright/test').Page,
  baseUrl: string,
  noteId: string
) {
  await page.goto(`${baseUrl}/second-brain?noteId=${noteId}`, { waitUntil: 'domcontentloaded' });
  const trigger = page.getByRole('button', { name: 'Keep with writing…', exact: true });
  await expect(trigger).toBeVisible();
  await trigger.click();

  const dialog = page.getByRole('dialog', { name: 'Keep with writing', exact: true });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('status', { name: 'Loading writings' })).toHaveCount(0);
  await expect(dialog.locator('#brain-writing-handoff-description')).toHaveText(
    'Keep a reference to this note with a writing. The note stays in Brain; nothing is copied into your text.'
  );
  return { dialog, trigger };
}

async function chooseWriting(dialog: Locator, title: string): Promise<void> {
  const filter = dialog.getByRole('searchbox', { name: 'Filter existing writings' });
  const hasFilter = (await filter.count()) > 0;
  if (hasFilter) await filter.fill(title);

  const option = dialog.getByTitle(title, { exact: true });
  await option.click();
  if (hasFilter) {
    await filter.fill('');
    await option.scrollIntoViewIfNeeded();
  }
}

test('existing writing handoff persists once and duplicate retry has one truthful notice', async ({
  page,
  browser,
}) => {
  const { baseUrl } = loadFixture();
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const stamp = Date.now().toString(36);
  const bookTitle = `Handoff Existing ${stamp}`;
  const writingTitle = `Handoff Destination ${stamp}`;
  const manuscript = '# Existing draft\n\nThis paragraph must not change during a source handoff.';
  const noteText = 'A note kept as a source reference, never copied into the manuscript.';
  const browserErrors: string[] = [];
  const writingApiFailures: string[] = [];
  page.on('pageerror', (error) => browserErrors.push(error.message));
  page.on('response', (response) => {
    if (response.url().includes('/api/writings') && response.status() >= 400) {
      writingApiFailures.push(`${response.status()} ${response.url()}`);
    }
  });

  let bookId: string | null = null;
  const createdWritingIds: string[] = [];
  try {
    const note = await seedNote(baseUrl, bookTitle, noteText);
    bookId = note.bookId;
    const writing = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
      name: writingTitle,
      type: 'Document',
      parentId: null,
    });
    createdWritingIds.push(writing.id);
    await apiPut(baseUrl, `/api/writings/${writing.id}`, {
      name: writingTitle,
      content: manuscript,
    });
    for (let index = 0; index < 9; index++) {
      const extraTitle = index === 0
        ? `Other destination with an intentionally long title ${stamp} for truncation checks`
        : `Other destination ${stamp} ${index + 1}`;
      const extra = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
        name: extraTitle,
        type: 'Document',
        parentId: null,
      });
      createdWritingIds.push(extra.id);
    }

    const { dialog, trigger } = await openHandoff(page, baseUrl, note.noteId);
    await expect(dialog.getByRole('button', { name: 'Keep with writing' })).toBeDisabled();

    // The shell moves focus inside; native radios provide the destination arrow-key model.
    await expect(dialog.getByRole('button', { name: 'Close writing picker' })).toBeFocused();
    await page.keyboard.press('Tab');
    const newWritingRadio = dialog.getByRole('radio', { name: /New writing/ });
    await expect(newWritingRadio).toBeFocused();
    await page.keyboard.press('ArrowDown');
    const focusedRadio = dialog.locator('input[type="radio"]:focus');
    await expect(focusedRadio).toBeChecked();

    await chooseWriting(dialog, writingTitle);
    await expect(dialog.getByRole('button', { name: 'Keep with writing' })).toBeEnabled();
    const closeButton = dialog.getByRole('button', { name: 'Close writing picker' });
    const keepButton = dialog.getByRole('button', { name: 'Keep with writing' });
    await keepButton.focus();
    await page.keyboard.press('Tab');
    await expect(closeButton).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(keepButton).toBeFocused();

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => {
      document.documentElement.setAttribute('data-theme', 'light');
      localStorage.setItem('nostos.theme', 'light');
    });
    const modalMetrics = await page.evaluate(() => {
      const card = document.querySelector('.handoff-dialog')!.getBoundingClientRect();
      const footer = document.querySelector('.handoff-actions')!.getBoundingClientRect();
      const list = document.querySelector('.destination-list')!.getBoundingClientRect();
      const listElement = document.querySelector('.destination-list') as HTMLElement;
      const headerElement = document.querySelector('.handoff-header')!;
      const shellHead = document.querySelector('.modal-head')!.getBoundingClientRect();
      const header = headerElement.getBoundingClientRect();
      return {
        card: { top: card.top, bottom: card.bottom },
        header: {
          top: header.top,
          bottom: header.bottom,
          ownBorderBottom: getComputedStyle(headerElement).borderBottomWidth,
          shellBottom: shellHead.bottom,
        },
        footer: { top: footer.top, bottom: footer.bottom },
        list: { top: list.top, bottom: list.bottom },
        listScrolls: listElement.scrollHeight > listElement.clientHeight,
        bodyOverflow: getComputedStyle(document.querySelector('.handoff-dialog .modal-scroll')!).overflowY,
      };
    });
    expect(modalMetrics.header.top).toBeGreaterThanOrEqual(modalMetrics.card.top);
    expect(modalMetrics.header.ownBorderBottom).toBe('0px');
    expect(Math.abs(modalMetrics.header.shellBottom - modalMetrics.header.bottom)).toBeLessThanOrEqual(1);
    expect(modalMetrics.footer.bottom).toBeLessThanOrEqual(modalMetrics.card.bottom);
    expect(modalMetrics.header.bottom).toBeLessThanOrEqual(modalMetrics.list.top);
    expect(modalMetrics.footer.top).toBeGreaterThanOrEqual(modalMetrics.list.bottom);
    expect(modalMetrics.listScrolls).toBe(true);
    expect(modalMetrics.bodyOverflow).toBe('hidden');

    const destinationList = dialog.locator('.destination-list');
    await expect(dialog.locator('.existing-destination')).toHaveCount(10);
    const dockViewports = [
      { width: 1440, height: 900 },
      { width: 1280, height: 720 },
      { width: 1024, height: 768 },
      { width: 820, height: 1180 },
      { width: 390, height: 844 },
      { width: 844, height: 390 },
    ];
    const dockFailures: string[] = [];
    for (const viewport of dockViewports) {
      await page.setViewportSize(viewport);
      await destinationList.evaluate((element) => {
        element.scrollTop = element.scrollHeight;
      });
      const listEnd = await page.evaluate(() => {
        const rowElement = document.querySelector(
          '.destination-list .existing-destination:last-child',
        ) as HTMLElement;
        const row = rowElement.getBoundingClientRect();
        const list = document.querySelector('.destination-list')!.getBoundingClientRect();
        const footer = document.querySelector('.handoff-actions')!.getBoundingClientRect();
        const dock = document.querySelector('app-app-dock')!.getBoundingClientRect();
        const probeX = row.left + row.width / 2;
        const probeY = Math.min(row.bottom - 2, dock.top + 2);
        const hit = document.elementFromPoint(probeX, probeY);
        return {
          rowBottom: row.bottom,
          listBottom: list.bottom,
          footerTop: footer.top,
          dockTop: dock.top,
          rowOverlapsDock: row.top < dock.bottom && row.bottom > dock.top &&
            row.left < dock.right && row.right > dock.left,
          rowHitAtDockBoundary: rowElement.contains(hit),
        };
      });
      if (viewport.width === 820 || viewport.width === 844) {
        await page.screenshot({
          path: '/tmp/804-brain-handoff-list-scroll-end-after-' +
            viewport.width + 'x' + viewport.height + '-light.png',
          animations: 'disabled',
        });
      }
        if (listEnd.rowBottom > listEnd.footerTop) {
          dockFailures.push(
            `Handoff final destination at ${viewport.width}x${viewport.height}: ${listEnd.rowBottom}px > footer ${listEnd.footerTop}px`,
          );
        }
        if (listEnd.rowBottom > listEnd.listBottom + 1) {
          dockFailures.push(
            `Handoff final destination at ${viewport.width}x${viewport.height}: ${listEnd.rowBottom}px > list ${listEnd.listBottom}px`,
          );
        }
        if (listEnd.rowOverlapsDock) {
          if (!listEnd.rowHitAtDockBoundary) {
            dockFailures.push(
              `Handoff row hit target at dock boundary is blocked at ${viewport.width}x${viewport.height}`,
            );
          }
      }
      if (viewport.width === 820 || viewport.width === 844) {
        await page.screenshot({
          path: '/tmp/804-brain-handoff-list-scroll-end-after-' +
            viewport.width + 'x' + viewport.height + '-light.png',
          animations: 'disabled',
        });
      }
    }
    expect(dockFailures, 'Handoff destination list remains reachable above the dock').toEqual([]);

    const touchContext = await browser.newContext({
      viewport: { width: 820, height: 1180 },
      isMobile: true,
      hasTouch: true,
      deviceScaleFactor: 1,
    });
    try {
      const touchPage = await touchContext.newPage();
      const touchHandoff = await openHandoff(touchPage, baseUrl, note.noteId);
      const touchFailures: string[] = [];
      for (const viewport of [
        { width: 390, height: 844 },
        { width: 820, height: 1180 },
        { width: 844, height: 390 },
      ]) {
        await touchPage.setViewportSize(viewport);
        await expect
          .poll(() => touchPage.evaluate(() => matchMedia('(pointer: coarse)').matches))
          .toBe(true);
        const undersizedTargets = await touchHandoff.dialog.evaluate((element) =>
          Array.from(element.querySelectorAll<HTMLElement>(
            'button, .destination-option, .destination-filter, .open-after',
          )).flatMap((target) => {
            const rect = target.getBoundingClientRect();
            const style = getComputedStyle(target);
            if (!rect.width || !rect.height || style.display === 'none' || style.visibility === 'hidden') return [];
            if (rect.width >= 44 && rect.height >= 44) return [];
            const label = target.getAttribute('aria-label') || target.textContent?.trim().replace(/\s+/g, ' ').slice(0, 60) || target.className;
            return [`${label}: ${rect.width.toFixed(1)}x${rect.height.toFixed(1)}`];
          }),
        );
        touchFailures.push(...undersizedTargets.map((target) => `Handoff ${viewport.width}x${viewport.height}: ${target}`));
      }
      expect(touchFailures, 'Handoff controls meet 44px on coarse-pointer viewports').toEqual([]);
    } finally {
      await touchContext.close();
    }

    await page.setViewportSize({ width: 1440, height: 900 });
    await destinationList.evaluate((element) => {
      element.scrollTop = 0;
    });
    await page.screenshot({
      path: '/tmp/writing-handoff-existing-desktop-light.png',
      animations: 'disabled',
    });
    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'dark'));
    await page.screenshot({
      path: '/tmp/writing-handoff-existing-desktop-dark.png',
      animations: 'disabled',
    });
    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'light'));
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({
      path: '/tmp/writing-handoff-existing-mobile-light.png',
      animations: 'disabled',
    });
    const mobileMetrics = await page.evaluate(() => {
      const card = document.querySelector('.handoff-dialog')!.getBoundingClientRect();
      const footer = document.querySelector('.handoff-actions')!.getBoundingClientRect();
      const header = document.querySelector('.handoff-header')!;
      const headerRect = header.getBoundingClientRect();
      const shellHeadRect = document.querySelector('.modal-head')!.getBoundingClientRect();
      return {
        width: document.documentElement.clientWidth,
        height: document.documentElement.clientHeight,
        card: { top: card.top, bottom: card.bottom },
        footer: { top: footer.top, bottom: footer.bottom },
        headerBorderBottom: getComputedStyle(header).borderBottomWidth,
        headerToShellBottom: shellHeadRect.bottom - headerRect.bottom,
        horizontalOverflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      };
    });
    expect(mobileMetrics.card.top).toBe(0);
    expect(mobileMetrics.footer.bottom).toBeLessThanOrEqual(mobileMetrics.height + 1);
    expect(mobileMetrics.headerBorderBottom).toBe('0px');
    expect(Math.abs(mobileMetrics.headerToShellBottom)).toBeLessThanOrEqual(1);
    expect(mobileMetrics.horizontalOverflow).toBeLessThanOrEqual(1);

    await page.setViewportSize({ width: 1280, height: 600 });
    await page.screenshot({
      path: '/tmp/writing-handoff-short-desktop-light.png',
      animations: 'disabled',
    });
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'light'));

    await dialog.getByRole('button', { name: 'Keep with writing' }).click();
    await expect(page.locator('.toast-success')).toContainText(`Kept 1 source with “${writingTitle}”`);
    const persisted = await apiGet<WritingSourceDto[]>(baseUrl, `/api/writings/${writing.id}/notes`);
    expect(persisted).toHaveLength(1);
    expect(persisted[0].id).toBe(note.noteId);

    const writingAfterKeep = await apiGet<WritingContentDto>(baseUrl, `/api/writings/${writing.id}`);
    expect(writingAfterKeep.content).toBe(manuscript);

    // Re-entry is a genuine retry against the stored membership, not a toast-only assertion.
    const retry = await openHandoff(page, baseUrl, note.noteId);
    await chooseWriting(retry.dialog, writingTitle);
    await retry.dialog.getByRole('button', { name: 'Keep with writing' }).click();
    await expect(page.locator('.toast-info')).toContainText(
      `This source is already kept with “${writingTitle}”`
    );
    await expect(page.locator('.toast-success')).toHaveCount(0);
    await page.screenshot({
      path: '/tmp/writing-handoff-duplicate-desktop-light.png',
      animations: 'disabled',
    });
    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'dark'));
    await page.screenshot({
      path: '/tmp/writing-handoff-duplicate-desktop-dark.png',
      animations: 'disabled',
    });
    await expect(
      await apiGet<WritingSourceDto[]>(baseUrl, `/api/writings/${writing.id}/notes`)
    ).toHaveLength(1);

    await expect(trigger).toBeAttached();
    expect(browserErrors).toEqual([]);
    expect(writingApiFailures).toEqual([]);
  } finally {
    for (const id of createdWritingIds) await apiDelete(baseUrl, `/api/writings/${id}`);
    if (bookId) {
      await cleanupBrain(baseUrl, { bookId, topicNames: [], beforeTopicIds });
    }
  }
});

test('new writing is created once, referenced, and never receives note prose', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const stamp = Date.now().toString(36);
  const bookTitle = `Handoff New ${stamp}`;
  const writingTitle = `New Handoff Writing ${stamp}`;
  const noteText = 'Keep this note connected by source membership only.';
  const browserErrors: string[] = [];
  page.on('pageerror', (error) => browserErrors.push(error.message));

  let bookId: string | null = null;
  let writingId: string | null = null;
  let existingWritingId: string | null = null;
  try {
    const note = await seedNote(baseUrl, bookTitle, noteText);
    bookId = note.bookId;
    const existingWriting = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
      name: `Existing handoff option ${stamp}`,
      type: 'Document',
      parentId: null,
    });
    existingWritingId = existingWriting.id;
    const { dialog } = await openHandoff(page, baseUrl, note.noteId);
    await dialog.locator('.new-destination').click();
    const titleInput = dialog.getByRole('textbox', { name: 'Title' });
    await expect(titleInput).toBeFocused();
    await titleInput.fill(writingTitle);
    await expect(dialog.getByRole('button', { name: 'Keep with writing' })).toBeEnabled();

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => {
      document.documentElement.setAttribute('data-theme', 'light');
      localStorage.setItem('nostos.theme', 'light');
    });
    await page.screenshot({
      path: '/tmp/writing-handoff-new-writing-desktop-light.png',
      animations: 'disabled',
    });
    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'dark'));
    await page.screenshot({
      path: '/tmp/writing-handoff-new-writing-desktop-dark.png',
      animations: 'disabled',
    });
    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'light'));
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({
      path: '/tmp/writing-handoff-new-writing-mobile-light.png',
      animations: 'disabled',
    });

    await page
      .getByRole('checkbox', { name: 'Open writing after keeping' })
      .check();
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => localStorage.setItem('nostos.theme', 'light'));
    await titleInput.press('Enter');
    await expect(page.locator('.toast-success')).toContainText(
      `Kept 1 source with “${writingTitle}”`
    );
    await expect(page).toHaveURL(/\/studio/);
    const controlsUnderToast = await page.locator('.toast-success').evaluate((toast) => {
      const toastBox = toast.getBoundingClientRect();
      return Array.from(document.querySelectorAll('button'))
        .filter((button) => !toast.contains(button))
        .filter((button) => {
          const rect = button.getBoundingClientRect();
          return (
            rect.width > 0 &&
            rect.height > 0 &&
            rect.left < toastBox.right &&
            rect.right > toastBox.left &&
            rect.top < toastBox.bottom &&
            rect.bottom > toastBox.top
          );
        })
        .map((button) => button.getAttribute('aria-label') || button.textContent?.trim());
    });
    console.log(`[acceptance follow-up] D2 open toast overlap: ${JSON.stringify(controlsUnderToast)}`);
    await page.screenshot({
      path: '/tmp/809-studio-toast-after-open-1440x900-light.png',
      animations: 'disabled',
    });
    const writings = await apiGet<WritingDto[]>(baseUrl, '/api/writings');
    const created = writings.find((writing) => writing.name === writingTitle);
    expect(created).toBeTruthy();
    writingId = created!.id;

    const sourceList = await apiGet<WritingSourceDto[]>(
      baseUrl,
      `/api/writings/${writingId}/notes`
    );
    expect(sourceList).toHaveLength(1);
    expect(sourceList[0].id).toBe(note.noteId);
    const persistedWriting = await apiGet<WritingContentDto>(baseUrl, `/api/writings/${writingId}`);
    expect(persistedWriting.content ?? '').not.toContain(noteText);
    expect(browserErrors).toEqual([]);
  } finally {
    if (!writingId) {
      const writings = await apiGet<WritingDto[]>(baseUrl, '/api/writings').catch(() => []);
      const created = writings.find((writing) => writing.name === writingTitle);
      if (created) writingId = created.id;
    }
    if (writingId) await apiDelete(baseUrl, `/api/writings/${writingId}`);
    if (existingWritingId) await apiDelete(baseUrl, `/api/writings/${existingWritingId}`);
    if (bookId) {
      await cleanupBrain(baseUrl, { bookId, topicNames: [], beforeTopicIds });
    }
  }
});

test('cancel, Escape and backdrop close without changing membership and restore focus', async ({
  page,
}) => {
  const { baseUrl } = loadFixture();
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const stamp = Date.now().toString(36);
  const bookTitle = `Handoff Cancel ${stamp}`;
  const writingTitle = `Cancel Destination ${stamp}`;

  let bookId: string | null = null;
  let writingId: string | null = null;
  try {
    const note = await seedNote(baseUrl, bookTitle, 'Canceling must leave this note untouched.');
    bookId = note.bookId;
    const writing = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
      name: writingTitle,
      type: 'Document',
      parentId: null,
    });
    writingId = writing.id;

    let opened = await openHandoff(page, baseUrl, note.noteId);
    await opened.dialog.locator('.existing-destination').filter({ hasText: writingTitle }).click();
    await opened.dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(opened.dialog).toHaveCount(0);
    await expect(opened.trigger).toBeFocused();
    await expect(
      await apiGet<WritingSourceDto[]>(baseUrl, `/api/writings/${writing.id}/notes`)
    ).toHaveLength(0);

    opened = await openHandoff(page, baseUrl, note.noteId);
    await page.keyboard.press('Escape');
    await expect(opened.dialog).toHaveCount(0);
    await expect(opened.trigger).toBeFocused();

    opened = await openHandoff(page, baseUrl, note.noteId);
    await page.mouse.click(8, 8);
    await expect(opened.dialog).toHaveCount(0);
    await expect(opened.trigger).toBeFocused();

    expect(
      await apiGet<WritingSourceDto[]>(baseUrl, `/api/writings/${writing.id}/notes`)
    ).toHaveLength(0);
  } finally {
    if (writingId) await apiDelete(baseUrl, `/api/writings/${writingId}`);
    if (bookId) {
      await cleanupBrain(baseUrl, { bookId, topicNames: [], beforeTopicIds });
    }
  }
});
