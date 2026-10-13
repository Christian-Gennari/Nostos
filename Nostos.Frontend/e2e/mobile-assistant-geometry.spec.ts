/**
 * The assistant trigger's mobile geometry in the EPUB reader (issue #262 §2).
 *
 * The collapsed capsule is fixed to the bottom-right and, in the reader, must
 * sit in the empty right flank of `.reader-toolbar` without stealing the
 * page-turn gesture or covering a line of text. That is a rendered-layout
 * property: jsdom reports no layout, so the numbers can only come from a real
 * engine at the real 390px width.
 *
 * This is a permanent guard for the defect 261-S1 measured by hand (a full-width
 * capsule overlapped the "Next page" control by 1782px²). It asserts:
 *   1. the tap box is at least 44x44;
 *   2. it intersects no `.reader-toolbar` control;
 *   3. it intersects no EPUB text rect.
 *
 * File name starts with `mobile` so `playwright.config.ts` runs it in the
 * 390px mobile-chromium project.
 */
import { expect, test, type Page, type Route } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { enableAssistant } from './support/assistant';

test.describe.configure({ mode: 'serial' });
test.use({ serviceWorkers: 'block' });

const RUN = newRunId();
const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'tiny.epub'));

interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** The overlap area of two viewport rects; 0 when they do not intersect. */
function intersectionArea(a: Rect, b: Rect): number {
  const width = Math.max(0, Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x));
  const height = Math.max(0, Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y));
  return width * height;
}

let fixture: ReturnType<typeof loadFixture>;
let bookId = '';

test.beforeAll(async () => {
  fixture = loadFixture();
  const created = await apiPost<any>(fixture.baseUrl, '/api/books/', {
    type: 'ebook',
    title: `Assistant Geometry ${RUN}`,
    author: 'E2E',
  });
  bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  if (!bookId) throw new Error(`No book id in create response: ${JSON.stringify(created)}`);

  const form = new FormData();
  form.append('file', new Blob([EPUB_BYTES], { type: 'application/epub+zip' }), 'book.epub');
  const res = await fetch(`${fixture.baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`file upload -> ${res.status}: ${await res.text()}`);
});

async function triggerRect(page: Page): Promise<Rect> {
  const box = await page.locator('[data-testid="assistant-trigger"]').boundingBox();
  if (!box) throw new Error('the assistant trigger has no box');
  return box;
}

test('the collapsed trigger stays 44px+ and clear of the toolbar and epub text', async ({ page }) => {
  // Geometry is independent of a real model provider. The production fixture
  // intentionally has none configured, so make the assistant available without
  // introducing a live LLM dependency into this layout test.
  await page.route('**/api/assistant/status', async (route: Route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ available: true }),
    });
  });

  await enableAssistant(page);
  await page.goto(`${fixture.baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.locator('#epub-viewer iframe').waitFor({ state: 'attached', timeout: 45_000 });
  // Let the reader place its page and settle before measuring.
  await page.waitForTimeout(1_000);

  const trigger = await triggerRect(page);
  console.log(`[geometry] trigger ${Math.round(trigger.width)}x${Math.round(trigger.height)} at (${Math.round(trigger.x)},${Math.round(trigger.y)})`);
  expect(trigger.width, 'trigger tap-box width').toBeGreaterThanOrEqual(44);
  expect(trigger.height, 'trigger tap-box height').toBeGreaterThanOrEqual(44);

  const controls = await page
    .locator('.reader-toolbar button, .reader-toolbar a, .reader-toolbar [role="button"]')
    .evaluateAll((nodes) =>
      // Only controls that are on screen. The reader opens in immersive mode
      // with its toolbars faded out (#768); the trigger is then the only thing
      // in that corner, and a hidden "Next page" under it is not an overlap.
      nodes
        .filter((node) => {
          const bar = node.closest('.reader-toolbar');
          return !bar || getComputedStyle(bar).opacity !== '0';
        })
        .map((node) => {
        const r = node.getBoundingClientRect();
        return {
          x: r.x,
          y: r.y,
          width: r.width,
          height: r.height,
          label: (node.getAttribute('aria-label') ?? node.textContent ?? '').trim(),
        };
      }),
    );

  let worstControl = 0;
  let worstControlLabel = '';
  for (const control of controls) {
    const area = intersectionArea(trigger, control);
    if (area > worstControl) {
      worstControl = area;
      worstControlLabel = control.label;
    }
  }
  console.log(`[geometry] toolbar controls: ${controls.length}; worst overlap ${Math.round(worstControl)}px2 (${worstControlLabel || 'none'})`);
  expect(worstControl, `trigger overlaps toolbar control "${worstControlLabel}"`).toBe(0);

  // When the reader's toolbars ARE shown they sit over that corner, and the
  // page controls must be what a tap reaches, not the trigger underneath.
  const viewport = page.viewportSize();
  if (!viewport) throw new Error('no viewport');
  await page.touchscreen.tap(Math.round(viewport.width / 2), Math.round(viewport.height / 2));
  await expect(page.locator('.reader-layout.chrome-visible')).toBeVisible();
  await page.waitForTimeout(400);
  const topAtNextPage = await page.evaluate(() => {
    const next = [...document.querySelectorAll('.reader-toolbar button')].find((button) =>
      (button.getAttribute('aria-label') ?? '').includes('Next page'),
    );
    if (!next) return 'no Next page control';
    const r = next.getBoundingClientRect();
    const top = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
    return top?.closest('[data-testid="assistant-trigger"]') ? 'assistant-trigger' : 'reader';
  });
  expect(topAtNextPage, 'a tap on "Next page" must reach the reader').toBe('reader');
  await page.touchscreen.tap(Math.round(viewport.width / 2), Math.round(viewport.height / 2));
  await expect(page.locator('.reader-layout.chrome-visible')).toHaveCount(0);

  const iframe = await page.locator('#epub-viewer iframe').boundingBox();
  if (!iframe) throw new Error('the epub iframe has no box');
  const texts = await page
    .frameLocator('#epub-viewer iframe')
    .locator('p, h1, h2, h3, li, span, div')
    .evaluateAll((nodes) =>
      nodes
        .filter((node) => (node.textContent ?? '').trim().length > 0)
        .map((node) => {
          const r = node.getBoundingClientRect();
          return { x: r.x, y: r.y, width: r.width, height: r.height };
        })
        .filter((r) => r.width > 0 && r.height > 0),
    );

  let worstText = 0;
  for (const text of texts) {
    const pageRect: Rect = {
      x: text.x + iframe.x,
      y: text.y + iframe.y,
      width: text.width,
      height: text.height,
    };
    worstText = Math.max(worstText, intersectionArea(trigger, pageRect));
  }
  console.log(`[geometry] epub text rects: ${texts.length}; worst overlap ${Math.round(worstText)}px2`);
  expect(worstText, 'trigger overlaps epub text').toBe(0);
});

test('the open assistant fills the visible viewport and stays stable when the composer focuses', async ({ page }) => {
  await page.route('**/api/assistant/status', async (route: Route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ available: true }),
    });
  });

  await enableAssistant(page);
  await page.goto(`${fixture.baseUrl}/library/${bookId}`, { waitUntil: 'domcontentloaded' });
  const trigger = page.locator('[data-testid="assistant-trigger"]');
  await expect(trigger).toBeVisible();
  await trigger.click();

  const panel = page.locator('[data-testid="assistant-panel"]');
  const composer = page.locator('[data-testid="assistant-composer"]');
  await expect(panel).toBeVisible();

  // Entering the dedicated phone surface must not summon the keyboard on its
  // own; the user can read the conversation or start voice capture first.
  await expect(composer).not.toBeFocused();

  async function visibleGeometry() {
    return page.evaluate(() => {
      const panel = document.querySelector('[data-testid="assistant-panel"]');
      if (!(panel instanceof HTMLElement)) return null;
      const rect = panel.getBoundingClientRect();
      const viewport = window.visualViewport;
      return {
        panelTop: rect.top,
        panelHeight: rect.height,
        panelWidth: rect.width,
        viewportTop: viewport?.offsetTop ?? 0,
        viewportHeight: viewport?.height ?? window.innerHeight,
        viewportWidth: viewport?.width ?? window.innerWidth,
      };
    });
  }

  // Visibility is reported before the short reveal animation has necessarily
  // reached its final transform. Measure the settled shell, not an animation
  // frame whose scale temporarily moves every edge inward.
  await expect
    .poll(async () => {
      const geometry = await visibleGeometry();
      if (!geometry) return Number.POSITIVE_INFINITY;
      return Math.max(
        Math.abs(geometry.panelTop - geometry.viewportTop),
        Math.abs(geometry.panelHeight - geometry.viewportHeight),
        Math.abs(geometry.panelWidth - geometry.viewportWidth),
      );
    })
    .toBeLessThanOrEqual(2);

  const opened = await visibleGeometry();
  expect(opened).not.toBeNull();

  // Regression for the old 56dvh -> 84dvh :focus-within jump.
  const beforeFocus = await panel.boundingBox();
  await composer.focus();
  const afterFocus = await panel.boundingBox();
  expect(beforeFocus).not.toBeNull();
  expect(afterFocus).not.toBeNull();
  expect(Math.abs(afterFocus!.height - beforeFocus!.height)).toBeLessThanOrEqual(2);

  // Headless Chromium has no software keyboard, so shrink the visible browser
  // viewport to the kind of height a keyboard leaves behind. The panel should
  // follow that visible area rather than translate itself by a guessed offset.
  await page.setViewportSize({ width: 390, height: 520 });

  await expect
    .poll(async () => {
      const geometry = await visibleGeometry();
      if (!geometry) return Number.POSITIVE_INFINITY;
      return Math.abs(geometry.panelHeight - geometry.viewportHeight);
    })
    .toBeLessThanOrEqual(2);

  const shrunken = await visibleGeometry();
  expect(shrunken).not.toBeNull();
  expect(Math.abs(shrunken!.panelTop - shrunken!.viewportTop)).toBeLessThanOrEqual(2);

  const composerBox = await composer.boundingBox();
  expect(composerBox).not.toBeNull();
  expect(composerBox!.y + composerBox!.height).toBeLessThanOrEqual(
    shrunken!.viewportTop + shrunken!.viewportHeight + 1,
  );
});

