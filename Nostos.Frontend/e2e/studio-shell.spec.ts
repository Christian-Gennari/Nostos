import { expect, test } from '@playwright/test';

import { apiPost, loadFixture } from './support/fixture';
import { apiDelete, cleanupBrain, seedBrain, type BrainSeed } from './support/brain-fixture';
import { apiPut } from './support/visual-capture';
import { studioPage } from './support/studio-editor';

const MANUSCRIPT = `# The Quiet Practice of Paying Attention

Attention is less a private talent than a way of receiving the world. A person attends by allowing an object, a sentence, or another person to become more than a prompt for the next thought. The practice is modest: remain long enough for the first impression to become a question. This manuscript is a place to follow that question without hurrying toward an answer. The writer returns to the page, notices what has changed, and lets the next sentence arrive at a human pace.

## A page at a time

A difficult page asks for a different pace. When the argument resists quick summary, the reader can notice where the resistance comes from: an unfamiliar term, a turn in the evidence, or the habit of wanting a conclusion before the middle has been heard. A good paragraph keeps its claim visible while leaving room for what it has not yet understood. That space is not an error to be repaired; it is where attention can become useful.

> A sentence is not finished when it sounds certain; it is finished when its attention has arrived at the thing it means.

This is one reason a notebook is useful. It gives a thought somewhere to wait without forcing the thought to become a thesis immediately. A note can be a door held open, a record of uncertainty, or a line that deserves another visit. A slower draft keeps the question visible while the answer is being made.

## Notes as a way of waiting

The work of writing begins in the small interval between recognition and explanation. If the writer moves too quickly, a sentence may become an answer to a question no one asked. A slower draft can preserve the first observation, set it beside a second one, and make a clearer claim only when both are ready. This is not a rule for every page. It is a way to keep evidence and interpretation close enough to speak to each other.

The practice can be simple:

- Keep the exact sentence that started the thought.
- Name the source and the page before the detail fades.
- Return to the paragraph after the first certainty has softened.

The habit is useful in reading as well as in writing. A careful reader marks a passage, asks what it is doing, and returns to the whole chapter before deciding what the passage proves. A careful writer does the same with a draft. The page remains open to revision, but every change has a reason that can be found again.

The writer can also follow a related idea in [the practice of close reading](https://example.com/close-reading), then come back to this page with the source still attached. The point is not to collect more material than the essay can hold. It is to keep the useful evidence within reach and let the manuscript remain the place where the final thought takes shape.`;

const VIEWPORTS = [
  { width: 1440, height: 900, label: 'desktop' },
  { width: 1280, height: 720, label: 'laptop' },
  { width: 1024, height: 768, label: 'tablet-landscape' },
] as const;

const COMPACT_VIEWPORTS = [
  { width: 820, height: 1180, label: 'tablet-portrait' },
  { width: 390, height: 844, label: 'phone' },
  { width: 844, height: 390, label: 'phone-landscape' },
] as const;

const REFERENCE_STATE_NOTES = Array.from(
  { length: 24 },
  (_, index) =>
    'Reference rail state note ' +
    (index + 1) +
    ': The active Library tab, selected book, inspected source, and scroll position should survive a rail collapse and reopen. This note keeps the selected book list long enough to scroll.',
);

async function measureShell(page: import('@playwright/test').Page) {
  return page.evaluate(() => {
    const pane = document.querySelector('.editor-pane');
    // The page and its scroller live in the app's DOM since #1037, so every
    // rectangle below is already in viewport coordinates.
    const iframe = document.querySelector<HTMLElement>('app-markdown-editor .manuscript-scroll');
    const editorBody = document.querySelector<HTMLElement>('app-markdown-editor .nostos-manuscript-body');
    const manuscriptRect = editorBody?.getBoundingClientRect();
    const manuscriptStyle = editorBody && getComputedStyle(editorBody);
    const paragraph = editorBody?.querySelector('p');
    const text = paragraph?.firstChild;
    let firstLineCharacters = 0;
    let manuscriptTextX: number | null = null;

    if (text?.nodeType === Node.TEXT_NODE) {
      const range = editorBody!.ownerDocument.createRange();
      let firstTop: number | null = null;
      for (let index = 0; index < text.textContent!.length; index++) {
        range.setStart(text, index);
        range.setEnd(text, index + 1);
        const rect = range.getBoundingClientRect();
        if (rect.width === 0 && rect.height === 0) continue;
        if (firstTop === null) {
          firstTop = rect.top;
          manuscriptTextX = rect.left;
        }
        else if (Math.abs(rect.top - firstTop) > 1) break;
        firstLineCharacters++;
      }
    }

    const toolbar = document.querySelector('app-markdown-editor .editor-toolbar-row');
    const toolbarGlyph = toolbar?.querySelector<SVGSVGElement>('button svg');
    const toolbarGlyphRectX = toolbarGlyph?.getBoundingClientRect().left ?? null;
    const glyphBounds = toolbarGlyph?.getBBox();
    const glyphTransform = toolbarGlyph?.getScreenCTM();
    const toolbarGlyphX =
      glyphBounds && glyphTransform
        ? glyphTransform.a * glyphBounds.x + glyphTransform.c * glyphBounds.y + glyphTransform.e
        : null;
    const toolbarRect = toolbar?.getBoundingClientRect();
    const toolbarRows = new Set(
      Array.from(toolbar?.children ?? []).map((control) =>
        Math.round(control.getBoundingClientRect().top + control.getBoundingClientRect().height / 2),
      ),
    ).size;
    const paneRect = pane?.getBoundingClientRect();
    const iframeRect = iframe?.getBoundingClientRect();
    const footerRect = document.querySelector('.editor-footer')?.getBoundingClientRect();
    const manuscriptTextViewportX = manuscriptTextX;

    return {
      paneWidth: Math.round(paneRect?.width ?? 0),
      manuscriptWidth: Math.round(manuscriptRect?.width ?? 0),
      manuscriptTextWidth: manuscriptRect && manuscriptStyle
        ? Math.round(manuscriptRect.width
          - parseFloat(manuscriptStyle.paddingLeft)
          - parseFloat(manuscriptStyle.paddingRight))
        : 0,
      manuscriptFontSize: manuscriptStyle?.fontSize ?? null,
      firstLineCharacters,
      toolbarGlyphX,
      toolbarGlyphRectX,
      toolbarRootX: toolbarRect?.left ?? null,
      toolbarRootWidth: toolbarRect?.width ?? null,
      manuscriptTextX: manuscriptTextViewportX,
      iframeX: iframeRect?.left ?? null,
      iframeWidth: iframeRect?.width ?? null,
      toolbarGlyphInset: toolbarGlyphX !== null && toolbarRect ? toolbarGlyphX - toolbarRect.left : null,
      manuscriptTextInset:
        manuscriptTextViewportX !== null && iframeRect
          ? manuscriptTextViewportX - iframeRect.left
          : null,
      toolbarTextDelta:
        toolbarGlyphX !== null && manuscriptTextViewportX !== null
          ? toolbarGlyphX - manuscriptTextViewportX
          : null,
      toolbarRows,
      wordCountOverlapsManuscript: Boolean(
        iframeRect && footerRect && footerRect.top < iframeRect.bottom - 1,
      ),
    };
  });
}

async function waitForEditorFonts(page: import('@playwright/test').Page): Promise<void> {
  await studioPage(page)
    .evaluate(async (body) => {
      await body.ownerDocument.fonts.ready;
    });
}

async function typeAfterLayoutChange(
  page: import('@playwright/test').Page,
  marker: string,
): Promise<void> {
  const body = studioPage(page);
  await body.locator('p').first().click();
  await page.keyboard.press('Control+End');
  await page.keyboard.insertText(` [${marker}]`);
  await expect(body).toContainText(marker);
}

async function expectSameEditorInstance(
  page: import('@playwright/test').Page,
): Promise<void> {
  // A re-created editor mounts a new page element; the same element means the
  // same editor survived the layout change.
  const isSame = await page.evaluate(() => {
    const marked = (window as any).__studioS1Editor;
    return Boolean(
      marked && marked === document.querySelector('app-markdown-editor .nostos-manuscript-body'),
    );
  });
  expect(isSame).toBe(true);
}

test('Writing Studio shell stays editor-first across desktop and compact viewports', async ({
  browser,
}) => {
  const { baseUrl } = loadFixture();
  const stamp = Date.now().toString(36);
  const title = `S1 Shell Browser Journey ${stamp} — a long title that must not crowd the save state or rail controls`;
  const referenceBookTitle = 'S1 Reference State ' + stamp;
  let writingId: string | null = null;
  let referenceFixture: BrainSeed | null = null;
  const contexts: Array<import('@playwright/test').BrowserContext> = [];

  try {
    const writing = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
      name: title,
      type: 'Document',
      parentId: null,
    });
    writingId = writing.id;
    await apiPut(baseUrl, `/api/writings/${writing.id}`, { name: title, content: MANUSCRIPT });
    referenceFixture = await seedBrain(baseUrl, referenceBookTitle, REFERENCE_STATE_NOTES, []);

    for (const viewport of VIEWPORTS) {
      const context = await browser.newContext({
        viewport: { width: viewport.width, height: viewport.height },
        deviceScaleFactor: 1,
      });
      contexts.push(context);
      const page = await context.newPage();
      await page.goto(`${baseUrl}/studio?writingId=${writing.id}`, {
        waitUntil: 'domcontentloaded',
      });

      const editorBody = studioPage(page);
      await expect(editorBody).toContainText('The Quiet Practice of Paying Attention');
      await waitForEditorFonts(page);
      await expect(page.locator('.header-doc-title')).toHaveAttribute('title', title);
      await expect(page.locator('.header-save-status')).toHaveText('Saved');
      // The page first (#1040): both rails are closed at rest and there is no
      // formatting toolbar over the page.
      await expect(page.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'false');
      await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'false');
      await expect(page.locator('app-markdown-editor .editor-toolbar')).toHaveCount(0);

      await page.waitForFunction(() => {
        const editor = document.querySelector('app-markdown-editor .nostos-manuscript-body');
        if (!editor) return false;
        (window as any).__studioS1Editor = editor;
        return true;
      });

      const atRest = await measureShell(page);
      expect(atRest.paneWidth).toBe(viewport.width);
      expect(atRest.firstLineCharacters).toBeLessThanOrEqual(115);
      if (viewport.label === 'desktop') {
        // No rails: the manuscript keeps its 820px measure and the prose >700px.
        expect(atRest.manuscriptWidth).toBe(820);
        expect(atRest.manuscriptTextWidth).toBeGreaterThan(700);
      }
      console.log(
        `[studio-shell] ${viewport.label} ${viewport.width}x${viewport.height} at-rest ${JSON.stringify(atRest)}`,
      );

      // Files slides over the page: it dims it, never resizes it.
      await page.locator('.files-toggle').click();
      await expect(page.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'true');
      await expect(page.locator('.sidebar-left')).toBeVisible();
      await expect(page.locator('.backdrop.visible')).toHaveCount(1);
      expect((await measureShell(page)).paneWidth).toBe(viewport.width);
      await page.keyboard.press('Escape');
      await expect(page.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'false');
      await expect(page.locator('.files-toggle')).toBeFocused();
      await expect(page.locator('.sidebar-left')).toBeHidden();
      await expectSameEditorInstance(page);
      await typeAfterLayoutChange(page, `${viewport.label}-files-closed`);

      // One slide-over at a time.
      await page.locator('.reference-toggle').click();
      await expect(page.locator('.sidebar-right')).toBeVisible();
      await page.locator('.backdrop.visible').click({ position: { x: 20, y: 400 } });
      await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'false');
      await expect(page.locator('.sidebar-right')).toBeHidden();
      await expectSameEditorInstance(page);
      await typeAfterLayoutChange(page, `${viewport.label}-reference-closed`);

      // Pinned, Reference sits beside the page and the page makes room.
      await page.locator('.reference-toggle').click();
      await page.locator('.reference-pin').click();
      await expect(page.locator('.backdrop.visible')).toHaveCount(0);
      await expect(page.locator('.sidebar-right')).toBeVisible();
      // The rail's width eases in; measure once it has settled.
      const railWidthNow = () =>
        page.locator('.sidebar-right').evaluate((element) => Math.round(element.getBoundingClientRect().width));
      await expect
        .poll(async () => (await measureShell(page)).paneWidth + (await railWidthNow()))
        .toBe(viewport.width);
      const pinned = await measureShell(page);
      expect(await railWidthNow()).toBeGreaterThan(0);
      expect(pinned.firstLineCharacters).toBeLessThanOrEqual(115);
      await page.locator('.files-toggle').click();
      await expect(page.locator('.sidebar-right')).toBeVisible();
      await page.keyboard.press('Escape');
      await expectSameEditorInstance(page);
      await typeAfterLayoutChange(page, `${viewport.label}-pinned`);
      console.log(
        `[studio-shell] ${viewport.label} ${viewport.width}x${viewport.height} pinned ${JSON.stringify(pinned)}`,
      );
      await page.locator('.reference-pin').click();
      await expect(page.locator('.sidebar-right')).toBeHidden();

      if (viewport.label === 'desktop') {
        const keyboardFilesToggle = page.locator('.files-toggle');
        await keyboardFilesToggle.focus();
        await page.keyboard.press('Tab');
        await page.keyboard.press('Shift+Tab');
        await expect(keyboardFilesToggle).toBeFocused();
        const keyboardFocusIndicatorVisible = await keyboardFilesToggle.evaluate((element) => {
          const style = getComputedStyle(element);
          return (
            element.matches(':focus-visible') &&
            ((style.outlineStyle !== 'none' && Number.parseFloat(style.outlineWidth) > 0) ||
              style.boxShadow !== 'none')
          );
        });
        expect(keyboardFocusIndicatorVisible).toBe(true);

        // Focus mode leaves the rails as they were.
        await page.locator('.zen-toggle').click();
        await expect(page.locator('.zen-exit')).toBeVisible();
        await expect(page.locator('body')).toHaveClass(/nostos-zen/);
        await page.keyboard.press('Escape');
        await expect(page.locator('.zen-exit')).toHaveCount(0);
        await expect(page.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'false');
        await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'false');

        // Reference keeps its place across closing and reopening the slide-over.
        await page.locator('.reference-toggle').click();
        await page.getByRole('tab', { name: 'Library', exact: true }).click();
        const libraryTabsChrome = await page.locator('.library-tabs').evaluate((element) => {
          const style = getComputedStyle(element);
          const active = element.querySelector('.tab-btn.active');
          const buttons = Array.from(element.querySelectorAll('.tab-btn'));
          return {
            trackBackground: style.backgroundColor,
            trackBottomBorder: style.borderBottomWidth,
            activeBackground: active ? getComputedStyle(active).backgroundColor : '',
            tabHeights: buttons.map((button) => button.getBoundingClientRect().height),
          };
        });
        expect(libraryTabsChrome.trackBottomBorder).toBe('0px');
        expect(libraryTabsChrome.trackBackground).toBe('rgba(0, 0, 0, 0)');
        expect(libraryTabsChrome.activeBackground).not.toBe('rgba(0, 0, 0, 0)');
        expect(libraryTabsChrome.tabHeights[0]).toBe(libraryTabsChrome.tabHeights[1]);
        await page.getByRole('tab', { name: 'Books', exact: true }).click();
        await page.getByPlaceholder('Search books...').fill(referenceBookTitle);
        const referenceBook = page
          .locator('.index-list .list-item')
          .filter({ hasText: referenceBookTitle });
        await expect(referenceBook).toBeVisible();
        await referenceBook.click();
        await expect(page.locator('.brain-detail-header .goto-btn')).toHaveText('Back to Books');
        await expect(page.locator('.reference-note-list .reference-source-row')).toHaveCount(
          REFERENCE_STATE_NOTES.length,
        );

        const referenceContent = page.locator('.brain-content');
        const referenceScroll = await referenceContent.evaluate((element) => {
          const maxScroll = element.scrollHeight - element.clientHeight;
          element.scrollTop = Math.min(180, maxScroll);
          return { top: element.scrollTop, max: maxScroll };
        });
        expect(referenceScroll.max).toBeGreaterThan(0);

        await page.locator('.reference-rail-collapse').click();
        await expect(page.locator('.sidebar-right')).toBeHidden();
        await page.locator('.reference-toggle').click();
        await expect(page.locator('.sidebar-right')).toBeVisible();
        await expect(page.getByRole('tab', { name: 'Library', exact: true })).toHaveAttribute(
          'aria-selected',
          'true',
        );
        await expect(page.getByRole('tab', { name: 'Books', exact: true })).toHaveAttribute(
          'aria-selected',
          'true',
        );
        await expect(page.locator('.brain-detail-header .goto-btn')).toHaveText('Back to Books');
        await expect.poll(() => referenceContent.evaluate((element) => element.scrollTop)).toBe(
          referenceScroll.top,
        );

        await page.locator('.reference-note-list .reference-source-row-main').first().click();
        const inspectedSource = page.locator('.inspected-source-panel');
        await expect(inspectedSource).toContainText('Reference rail state note 1');
        await page.locator('.reference-rail-collapse').click();
        await expect(page.locator('.sidebar-right')).toBeHidden();
        await page.locator('.reference-toggle').click();
        await expect(page.locator('.sidebar-right')).toBeVisible();
        await expect(inspectedSource).toContainText('Reference rail state note 1');
        await expect(page.locator('.inspected-source-context')).toHaveText(
          'Back to ' + referenceBookTitle,
        );
        await page.keyboard.press('Escape');
        await expect(page.locator('.sidebar-right')).toBeHidden();
      }
    }

    const compactContext = await browser.newContext({
      viewport: { width: 820, height: 1180 },
      hasTouch: true,
      deviceScaleFactor: 1,
    });
    contexts.push(compactContext);
    const compactPage = await compactContext.newPage();

    for (const viewport of COMPACT_VIEWPORTS) {
      await compactPage.setViewportSize({ width: viewport.width, height: viewport.height });
      await compactPage.goto(`${baseUrl}/studio?writingId=${writing.id}`, {
        waitUntil: 'domcontentloaded',
      });
      const editorBody = studioPage(compactPage);
      await expect(editorBody).toContainText('The Quiet Practice of Paying Attention');
      await waitForEditorFonts(compactPage);
      await compactPage.waitForFunction(() => {
        const editor = document.querySelector('app-markdown-editor .nostos-manuscript-body');
        if (!editor) return false;
        (window as any).__studioS1Editor = editor;
        return true;
      });
      await expect(compactPage.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'false');
      await expect(compactPage.locator('.reference-toggle')).toHaveAttribute(
        'aria-expanded',
        'false',
      );

      const drawersClosed = await measureShell(compactPage);
      expect(drawersClosed.paneWidth).toBe(viewport.width);
      expect(drawersClosed.firstLineCharacters).toBeGreaterThanOrEqual(45);
      expect(drawersClosed.toolbarRows).toBeLessThanOrEqual(1);
      if (viewport.label === 'phone') {
        // The mobile manuscript still fills the phone and uses its existing face.
        expect(drawersClosed.manuscriptWidth).toBe(viewport.width);
        expect(drawersClosed.manuscriptFontSize).toBe('17px');
      }
      console.log(
        `[studio-shell] ${viewport.label} ${viewport.width}x${viewport.height} drawers-closed ${JSON.stringify(drawersClosed)}`,
      );

      await compactPage.locator('.files-toggle').click();
      await expect(compactPage.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'true');
      await expect(compactPage.locator('.reference-toggle')).toHaveAttribute(
        'aria-expanded',
        'false',
      );
      const filesDrawerOpen = await measureShell(compactPage);
      console.log(
        `[studio-shell] ${viewport.label} ${viewport.width}x${viewport.height} files-drawer ${JSON.stringify(filesDrawerOpen)}`,
      );
      await compactPage.keyboard.press('Escape');
      await expect(compactPage.locator('.files-toggle')).toBeFocused();
      await expect(compactPage.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'false');
      if (viewport.label === 'tablet-portrait') {
        await compactPage.screenshot({
          path: '/tmp/809-studio-files-drawer-escape-after-820x1180-light.png',
          animations: 'disabled',
        });
      }
      await typeAfterLayoutChange(compactPage, `${viewport.label}-files-return`);
      await expectSameEditorInstance(compactPage);

      await compactPage.locator('.reference-toggle').click();
      await expect(compactPage.locator('.reference-toggle')).toHaveAttribute(
        'aria-expanded',
        'true',
      );
      await expect(compactPage.locator('.files-toggle')).toHaveAttribute(
        'aria-expanded',
        'false',
      );
      const referenceDrawerOpen = await measureShell(compactPage);
      const expectedReferenceDrawerWidth =
        viewport.label === 'tablet-portrait' ? 360 : viewport.width;
      await expect
        .poll(() =>
          compactPage
            .locator('.sidebar-right')
            .evaluate((element) => element.getBoundingClientRect().width),
        )
        .toBe(expectedReferenceDrawerWidth);
      console.log(
        `[studio-shell] ${viewport.label} ${viewport.width}x${viewport.height} reference-drawer ${JSON.stringify(referenceDrawerOpen)}`,
      );

      if (viewport.label === 'tablet-portrait') {
        await compactPage.getByRole('tab', { name: 'Library', exact: true }).click();
        await compactPage.getByRole('tab', { name: 'Books', exact: true }).click();
        const bookSearch = compactPage.getByRole('textbox', { name: 'Search books' });
        await bookSearch.fill(referenceBookTitle);
        await compactPage.locator('.list-item').filter({ hasText: referenceBookTitle }).click();
        await expect(compactPage.locator('.reference-note-list .reference-source-row')).toHaveCount(
          REFERENCE_STATE_NOTES.length,
        );
        const bookNotes = compactPage.locator('[data-reference-scroll="bookNotes"]');
        const bookNotesScroll = await bookNotes.evaluate((element) => {
          const maxScroll = element.scrollHeight - element.clientHeight;
          element.scrollTop = Math.min(180, maxScroll);
          return { top: element.scrollTop, max: maxScroll };
        });
        expect(bookNotesScroll.max).toBeGreaterThan(0);

        await compactPage.locator('.reference-rail-collapse').click();
        await expect(compactPage.locator('.reference-toggle')).toHaveAttribute(
          'aria-expanded',
          'false',
        );
        await compactPage.locator('.reference-toggle').click();
        await expect(compactPage.locator('.sidebar-right')).toHaveClass(/\bopen\b/);
        await expect(compactPage.getByRole('tab', { name: 'Library', exact: true })).toHaveAttribute(
          'aria-selected',
          'true',
        );
        await expect(compactPage.locator('.library-tabs').getByRole('tab', { name: 'Books' })).toHaveAttribute(
          'aria-selected',
          'true',
        );
        await expect(compactPage.locator('.brain-detail-header .goto-btn')).toHaveText('Back to Books');
        await expect
          .poll(() => bookNotes.evaluate((element) => element.scrollTop))
          .toBe(bookNotesScroll.top);

        await compactPage.locator('.reference-note-list .reference-source-row-main').first().click();
        await expect(compactPage.locator('.inspected-source-card')).toHaveCount(1);
        const nestedMore = compactPage.getByRole('button', { name: 'More source actions' });
        await nestedMore.click();
        await expect(compactPage.locator('.source-more-actions')).toBeVisible();
        await compactPage.locator('.source-more-actions button').first().focus();
        await compactPage.keyboard.press('Escape');
        await expect(compactPage.locator('.source-more-actions')).toHaveCount(0);
        await expect(compactPage.locator('.sidebar-right')).toHaveClass(/\bopen\b/);
        await expect(nestedMore).toBeFocused();
        await compactPage.keyboard.press('Escape');
        await expect(compactPage.locator('.sidebar-right')).not.toHaveClass(/\bopen\b/);
        await expect(compactPage.locator('.reference-toggle')).toBeFocused();
        await compactPage.locator('.reference-toggle').click();
        await expect(compactPage.locator('.sidebar-right')).toHaveClass(/\bopen\b/);
        await expect(compactPage.locator('.inspected-source-card')).toHaveCount(1);
        await expect(compactPage.locator('.inspected-source-context')).toHaveText(
          'Back to ' + referenceBookTitle,
        );
        await expect(compactPage.getByRole('tab', { name: 'Library', exact: true })).toHaveAttribute(
          'aria-selected',
          'true',
        );
        await expect(compactPage.locator('.library-tabs').getByRole('tab', { name: 'Books' })).toHaveAttribute(
          'aria-selected',
          'true',
        );
      }

      await compactPage.keyboard.press('Escape');
      await expect(compactPage.locator('.reference-toggle')).toBeFocused();
      await expect(compactPage.locator('.reference-toggle')).toHaveAttribute(
        'aria-expanded',
        'false',
      );
      if (viewport.label === 'tablet-portrait') {
        await compactPage.screenshot({
          path: '/tmp/809-studio-reference-drawer-escape-after-820x1180-light.png',
          animations: 'disabled',
        });
      }
      await typeAfterLayoutChange(compactPage, `${viewport.label}-reference-return`);
      await expectSameEditorInstance(compactPage);

      const drawersOpen = await measureShell(compactPage);
      expect(drawersOpen.paneWidth).toBe(viewport.width);
      expect(drawersOpen.firstLineCharacters).toBeGreaterThanOrEqual(45);
      expect(drawersOpen.wordCountOverlapsManuscript).toBe(false);
      console.log(
        `[studio-shell] ${viewport.label} ${viewport.width}x${viewport.height} drawer-returned ${JSON.stringify(drawersOpen)}`,
      );

      if (viewport.label === 'phone') {
        const header = await compactPage.evaluate(() => {
          const header = document.querySelector('.editor-header')!;
          const buttons = Array.from(
            header.querySelectorAll('.files-toggle, .reference-toggle, .typewriter-toggle, .zen-toggle'),
          ).map((button) => {
            const rect = button.getBoundingClientRect();
            return {
              x: Math.round(rect.x),
              y: Math.round(rect.y),
              width: Math.round(rect.width),
              height: Math.round(rect.height),
            };
          });
          const iframe = document.querySelector('app-markdown-editor .manuscript-scroll')!.getBoundingClientRect();
          const footer = document.querySelector('.editor-footer')!.getBoundingClientRect();
          return {
            height: Math.round(header.getBoundingClientRect().height),
            buttons,
            wordCountClearOfManuscript: footer.top >= iframe.bottom - 1,
          };
        });
        expect(header.height).toBe(52);
        expect(header.buttons).toHaveLength(4);
        expect(header.buttons.every((button) => button.width >= 44 && button.height >= 44)).toBe(
          true,
        );
        expect(new Set(header.buttons.map((button) => button.y)).size).toBe(1);
        expect(header.wordCountClearOfManuscript).toBe(true);
        console.log(`[studio-shell] phone header ${JSON.stringify(header)}`);

        // With no writing to return to, /studio is a blank page, not an empty state.
        await compactPage.evaluate(() => localStorage.removeItem('nostos.studio.lastWritingId'));
        await compactPage.goto(`${baseUrl}/studio`, { waitUntil: 'domcontentloaded' });
        await expect(compactPage.locator('.header-doc-title')).toHaveText('New page');
        await expect(compactPage.locator('.empty-state')).toHaveCount(0);
        await expect(studioPage(compactPage)).toBeVisible();
        const referenceToggle = compactPage.locator('.reference-toggle');
        await expect(referenceToggle).toHaveAttribute('aria-expanded', 'false');
        await referenceToggle.click();
        await expect(compactPage.locator('.sidebar-right')).toHaveClass(/\bopen\b/);
        const browseLibrary = compactPage.getByRole('button', { name: 'Browse Library' });
        await expect(browseLibrary).toBeVisible();
        await browseLibrary.click();
        await expect(compactPage.locator('.reference-mode-switch').getByRole('tab', { name: 'Library' })).toHaveAttribute(
          'aria-selected',
          'true',
        );
        await compactPage.locator('.reference-rail-collapse').click();
        await expect(referenceToggle).toHaveAttribute('aria-expanded', 'false');
        await referenceToggle.click();
        await expect(compactPage.locator('.sidebar-right')).toHaveClass(/\bopen\b/);
        await expect(compactPage.locator('.reference-mode-switch').getByRole('tab', { name: 'Library' })).toHaveAttribute(
          'aria-selected',
          'true',
        );
      }
    }
  } finally {
    await Promise.all(contexts.map((context) => context.close()));
    if (referenceFixture) await cleanupBrain(baseUrl, referenceFixture);
    if (writingId) await apiDelete(baseUrl, `/api/writings/${writingId}`);
  }
});
