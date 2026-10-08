import { expect, test } from '@playwright/test';

import { apiPost, loadFixture } from './support/fixture';
import { apiDelete } from './support/brain-fixture';
import { apiPut } from './support/visual-capture';

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

async function measureShell(page: import('@playwright/test').Page) {
  return page.evaluate(() => {
    const pane = document.querySelector('.editor-pane');
    const iframe = document.querySelector<HTMLIFrameElement>('.tox-edit-area__iframe');
    const editorBody = iframe?.contentDocument?.body;
    const paragraph = editorBody?.querySelector('p');
    const text = paragraph?.firstChild;
    let firstLineCharacters = 0;

    if (text?.nodeType === Node.TEXT_NODE) {
      const range = editorBody!.ownerDocument.createRange();
      let firstTop: number | null = null;
      for (let index = 0; index < text.textContent!.length; index++) {
        range.setStart(text, index);
        range.setEnd(text, index + 1);
        const rect = range.getBoundingClientRect();
        if (rect.width === 0 && rect.height === 0) continue;
        if (firstTop === null) firstTop = rect.top;
        else if (Math.abs(rect.top - firstTop) > 1) break;
        firstLineCharacters++;
      }
    }

    const toolbar = document.querySelector('.tox-toolbar__primary');
    const toolbarRows = new Set(
      Array.from(toolbar?.querySelectorAll('.tox-toolbar__group') ?? []).map((group) =>
        Math.round(group.getBoundingClientRect().top),
      ),
    ).size;
    const paneRect = pane?.getBoundingClientRect();
    const iframeRect = iframe?.getBoundingClientRect();
    const footerRect = document.querySelector('.editor-footer')?.getBoundingClientRect();

    return {
      paneWidth: Math.round(paneRect?.width ?? 0),
      firstLineCharacters,
      toolbarRows,
      wordCountOverlapsManuscript: Boolean(
        iframeRect && footerRect && footerRect.top < iframeRect.bottom - 1,
      ),
    };
  });
}

async function waitForEditorFonts(page: import('@playwright/test').Page): Promise<void> {
  await page
    .frameLocator('.tox-edit-area__iframe')
    .locator('body')
    .evaluate(async (body) => {
      await body.ownerDocument.fonts.ready;
    });
}

async function typeAfterLayoutChange(
  page: import('@playwright/test').Page,
  marker: string,
): Promise<void> {
  const body = page.frameLocator('.tox-edit-area__iframe').locator('body');
  await body.locator('p').first().click();
  await page.keyboard.press('Control+End');
  await page.keyboard.insertText(` [${marker}]`);
  await expect(body).toContainText(marker);
}

async function expectSameEditorInstance(
  page: import('@playwright/test').Page,
): Promise<void> {
  const isSame = await page.evaluate(() => {
    const tinyMce = (window as any).tinymce;
    return Boolean(
      tinyMce?.__studioS1Editor &&
        tinyMce.__studioS1Editor === tinyMce.get('markdown-tinymce-editor'),
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
  let writingId: string | null = null;
  const contexts: Array<import('@playwright/test').BrowserContext> = [];

  try {
    const writing = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
      name: title,
      type: 'Document',
      parentId: null,
    });
    writingId = writing.id;
    await apiPut(baseUrl, `/api/writings/${writing.id}`, { name: title, content: MANUSCRIPT });

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

      const editorBody = page.frameLocator('.tox-edit-area__iframe').locator('body');
      await expect(editorBody).toContainText('The Quiet Practice of Paying Attention');
      await waitForEditorFonts(page);
      await expect(page.locator('.header-doc-title')).toHaveAttribute('title', title);
      await expect(page.locator('.header-save-status')).toHaveText('Saved');
      await expect(page.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'true');
      await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'true');

      await page.waitForFunction(() => {
        const tinyMce = (window as any).tinymce;
        if (!tinyMce?.get('markdown-tinymce-editor')) return false;
        tinyMce.__studioS1Editor = tinyMce.get('markdown-tinymce-editor');
        return true;
      });

      const bothOpen = await measureShell(page);
      expect(bothOpen.firstLineCharacters).toBeLessThanOrEqual(75);
      expect(bothOpen.toolbarRows).toBeLessThanOrEqual(1);
      console.log(
        `[studio-shell] ${viewport.label} ${viewport.width}x${viewport.height} both-open ${JSON.stringify(bothOpen)}`,
      );

      await page.locator('.files-toggle').click();
      await expect(page.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'false');
      await expect(page.locator('.sidebar-left')).toBeHidden();
      await expect(page.locator('.sidebar-right')).toBeVisible();
      await expectSameEditorInstance(page);
      await typeAfterLayoutChange(page, `${viewport.label}-files-hidden`);

      await page.locator('.files-toggle').click();
      await expect(page.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'true');
      await expect(page.locator('.sidebar-left')).toBeVisible();
      await expectSameEditorInstance(page);
      await typeAfterLayoutChange(page, `${viewport.label}-files-shown`);

      await page.locator('.reference-toggle').click();
      await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'false');
      await expect(page.locator('.sidebar-right')).toBeHidden();
      await expect(page.locator('.sidebar-left')).toBeVisible();
      await expectSameEditorInstance(page);
      await typeAfterLayoutChange(page, `${viewport.label}-reference-hidden`);

      await page.locator('.reference-toggle').click();
      await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'true');
      await expect(page.locator('.sidebar-right')).toBeVisible();
      await expectSameEditorInstance(page);
      await typeAfterLayoutChange(page, `${viewport.label}-reference-shown`);

      await page.locator('.files-toggle').click();
      await expect(page.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'false');
      await expectSameEditorInstance(page);
      await typeAfterLayoutChange(page, `${viewport.label}-both-hidden`);
      await page.locator('.reference-toggle').click();
      await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'false');
      await expectSameEditorInstance(page);
      await typeAfterLayoutChange(page, `${viewport.label}-both-collapsed`);

      const bothCollapsed = await measureShell(page);
      expect(bothCollapsed.paneWidth).toBe(viewport.width);
      expect(bothCollapsed.firstLineCharacters).toBeLessThanOrEqual(75);
      expect(bothCollapsed.toolbarRows).toBeLessThanOrEqual(1);
      console.log(
        `[studio-shell] ${viewport.label} ${viewport.width}x${viewport.height} both-collapsed ${JSON.stringify(bothCollapsed)}`,
      );

      await page.locator('.files-toggle').click();
      await typeAfterLayoutChange(page, `${viewport.label}-restore-files`);
      await page.locator('.reference-toggle').click();
      await typeAfterLayoutChange(page, `${viewport.label}-restore-reference`);

      if (viewport.label === 'tablet-landscape') {
        await page.locator('.files-toggle').click();
        await typeAfterLayoutChange(page, 'reload-files-preference');
        await expect(page.locator('.header-save-status')).toHaveText('Saved', {
          timeout: 20_000,
        });
        await page.reload({ waitUntil: 'domcontentloaded' });
        await expect(page.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'false');
        await expect(page.locator('.reference-toggle')).toHaveAttribute('aria-expanded', 'true');
        await expect(editorBody).toContainText('Attention is less a private talent');
        await page.locator('.files-toggle').click();
        await expect(page.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'true');
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
      const editorBody = compactPage.frameLocator('.tox-edit-area__iframe').locator('body');
      await expect(editorBody).toContainText('The Quiet Practice of Paying Attention');
      await waitForEditorFonts(compactPage);
      await compactPage.waitForFunction(() => {
        const tinyMce = (window as any).tinymce;
        if (!tinyMce?.get('markdown-tinymce-editor')) return false;
        tinyMce.__studioS1Editor = tinyMce.get('markdown-tinymce-editor');
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
      console.log(
        `[studio-shell] ${viewport.label} ${viewport.width}x${viewport.height} drawers-closed ${JSON.stringify(drawersClosed)}`,
      );

      await compactPage.locator('.files-toggle').click();
      await expect(compactPage.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'true');
      await expect(compactPage.locator('.reference-toggle')).toHaveAttribute(
        'aria-expanded',
        'false',
      );
      await compactPage.locator('.files-rail-collapse').click();
      await expect(compactPage.locator('.files-toggle')).toBeFocused();
      await expect(compactPage.locator('.files-toggle')).toHaveAttribute('aria-expanded', 'false');
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
      await compactPage.locator('.reference-rail-collapse').click();
      await expect(compactPage.locator('.reference-toggle')).toBeFocused();
      await expect(compactPage.locator('.reference-toggle')).toHaveAttribute(
        'aria-expanded',
        'false',
      );
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
          const iframe = document.querySelector('.tox-edit-area__iframe')!.getBoundingClientRect();
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

        await compactPage.goto(`${baseUrl}/studio`, { waitUntil: 'domcontentloaded' });
        await expect(compactPage.locator('.empty-new-document')).toBeVisible();
        await expect(compactPage.locator('.empty-browse-files')).toBeVisible();
      }
    }
  } finally {
    await Promise.all(contexts.map((context) => context.close()));
    if (writingId) await apiDelete(baseUrl, `/api/writings/${writingId}`);
  }
});
