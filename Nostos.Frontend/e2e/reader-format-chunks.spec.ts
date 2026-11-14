/**
 * Each reader format loads on its own (#1320). Real backend, production build.
 *
 * The first three specs read the network log: an EPUB open carries no PDF code
 * and an audiobook open carries neither engine, and that stays true after the
 * app root's idle prefetch of the passage viewer. The rest hold a format's
 * chunk back for longer than `ready` waits, as a slow connection does, and
 * check what the shell asked for before the reader existed still happens.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, apiPut, loadFixture, newRunId } from './support/fixture';
import { buildLongEpub, TARGET_PARAGRAPH } from './support/reader-restore-position';
import { waitForLayoutSettled, waitForRenditionSize } from './support/waits';

test.use({ serviceWorkers: 'block' });

const PDF_CODE = 'ngx-extended-pdf-viewer';
const EPUB_CODE = 'EPUBJS';

const PDF_BYTES = readFileSync(path.join(__dirname, 'assets', 'reader-notes.pdf'));
const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'reader-margins.epub'));
/** Silent MPEG-1 Layer III frames: enough for the upload and for the player to mount. */
const MP3_BYTES = Buffer.concat(
  Array.from({ length: 200 }, () => Buffer.concat([Buffer.from([0xff, 0xfb, 0x90, 0x64]), Buffer.alloc(413)])),
);

async function seed(
  baseUrl: string,
  type: string,
  bytes: Buffer,
  fileName: string,
  mediaType: string,
): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', { type, title: `Chunks ${newRunId()}`, author: 'E2E' });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([new Uint8Array(bytes)], { type: mediaType }), fileName);
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}: ${await res.text()}`);
  return bookId;
}

/**
 * Every script the page loaded, with its size and whether it holds each engine.
 *
 * Reading a response body is asynchronous, so the list grows after the response
 * event fires: `whenIdle()` resolves once every script seen so far has been
 * read, which is what the "no PDF code" claims need to be true.
 */
function scriptLog(page: Page) {
  const scripts: { url: string; bytes: number; pdf: boolean; epub: boolean; passage: boolean }[] = [];
  let inFlight = 0;
  const idleWaiters: (() => void)[] = [];
  page.on('response', (response) => {
    if (!response.url().endsWith('.js')) return;
    inFlight += 1;
    response
      .text()
      .catch(() => '')
      .then((body) => {
        scripts.push({
          url: response.url().split('/').pop()!,
          bytes: Buffer.byteLength(body),
          pdf: body.includes(PDF_CODE),
          epub: body.includes(EPUB_CODE),
          passage: body.includes('passage-viewer-title'),
        });
      })
      .finally(() => {
        inFlight -= 1;
        if (inFlight === 0) idleWaiters.splice(0).forEach((resolve) => resolve());
      });
  });
  const whenIdle = () =>
    new Promise<void>((resolve) => (inFlight === 0 ? resolve() : idleWaiters.push(resolve)));
  return Object.assign(scripts, { whenIdle });
}

/** Holds the chunk carrying `marker` until `release` is called. */
async function holdChunk(page: Page, marker: string) {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => (release = resolve));
  let held = false;
  await page.route(/\/chunk-[^/]*\.js$/, async (route) => {
    const response = await route.fetch();
    const body = await response.body();
    if (body.includes(marker)) {
      held = true;
      await gate;
    }
    await route.fulfill({ response, body });
  });
  return { release, wasHeld: () => held };
}

async function watchLayoutShift(page: Page) {
  await page.addInitScript(() => {
    (window as any).__shift = 0;
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries() as any[]) (window as any).__shift += entry.value;
    }).observe({ type: 'layout-shift', buffered: true });
  });
}
const layoutShift = (page: Page) => page.evaluate(() => (window as any).__shift as number);

const openFrame = (page: Page) => page.frameLocator('#epub-viewer iframe').locator('p').first();

function report(name: string, scripts: ReturnType<typeof scriptLog>): void {
  const total = scripts.reduce((sum, script) => sum + script.bytes, 0);
  console.log(`[chunks] ${name}: ${scripts.length} scripts, ${(total / 1000).toFixed(1)} kB raw`);
}

test('an EPUB open loads no PDF code, even after the idle prefetch', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seed(baseUrl, 'ebook', EPUB_BYTES, 'book.epub', 'application/epub+zip');
  const scripts = scriptLog(page);

  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await openFrame(page).waitFor({ timeout: 45_000 });
  await expect.poll(() => scripts.some((script) => script.passage), { timeout: 15_000 }).toBe(true);
  await scripts.whenIdle();

  report('EPUB', scripts);
  expect(scripts.filter((script) => script.epub).length).toBeGreaterThan(0);
  expect(scripts.filter((script) => script.pdf)).toEqual([]);
});

test('an audiobook open loads neither the PDF viewer nor epub.js', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seed(baseUrl, 'audiobook', MP3_BYTES, 'book.mp3', 'audio/mpeg');
  const scripts = scriptLog(page);

  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.locator('app-audio-reader').waitFor({ timeout: 45_000 });
  await expect.poll(() => scripts.some((script) => script.passage), { timeout: 15_000 }).toBe(true);
  await scripts.whenIdle();

  report('audiobook', scripts);
  expect(scripts.filter((script) => script.pdf || script.epub)).toEqual([]);
});

test('a PDF open loads no epub.js', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seed(baseUrl, 'ebook', PDF_BYTES, 'book.pdf', 'application/pdf');
  const scripts = scriptLog(page);

  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });
  await scripts.whenIdle();

  report('PDF', scripts);
  expect(scripts.filter((script) => script.pdf).length).toBeGreaterThan(0);
  expect(scripts.filter((script) => script.epub)).toEqual([]);
});

test('a PDF passage outside the reader loads the viewer only when it is opened', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seed(baseUrl, 'ebook', PDF_BYTES, 'book.pdf', 'application/pdf');
  await apiPost(baseUrl, `/api/books/${bookId}/notes`, {
    content: '',
    kind: 'passage',
    passageTitle: 'First page',
    sourceAnchorKind: 'pdf_page',
    sourceAnchorValue: '1',
    passageEnd: '1',
  });
  const scripts = scriptLog(page);

  await page.goto(`${baseUrl}/library/${bookId}`, { waitUntil: 'domcontentloaded' });
  const read = page.getByText('Read passage');
  await read.waitFor({ timeout: 30_000 });
  await expect.poll(() => scripts.some((script) => script.passage), { timeout: 15_000 }).toBe(true);
  expect(scripts.filter((script) => script.pdf)).toEqual([]);

  await read.click();
  await expect(page.getByTestId('passage-viewer-pdf-page').first()).toBeVisible({ timeout: 30_000 });
  await expect.poll(() => scripts.filter((script) => script.pdf).length).toBeGreaterThan(0);
});

test('a page link waits for a PDF viewer that arrives late, with no layout shift', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seed(baseUrl, 'ebook', PDF_BYTES, 'book.pdf', 'application/pdf');
  await watchLayoutShift(page);
  const chunk = await holdChunk(page, PDF_CODE);

  await page.goto(`${baseUrl}/read/${bookId}?sourcePage=2`, { waitUntil: 'domcontentloaded' });
  const spinner = page.locator('nostos-loading-indicator.reader-loading');
  await spinner.waitFor({ timeout: 30_000 });
  // The chunk is on the wire and the reader cannot mount until it arrives.
  await expect.poll(() => chunk.wasHeld(), { timeout: 30_000 }).toBe(true);
  await expect(page.locator('app-pdf-reader')).toHaveCount(0);
  await page.screenshot({ path: path.join(__dirname, 'visual-evidence', 'reader-format-chunk-pdf-held.png') });

  chunk.release();
  await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });
  await expect(spinner).toHaveCount(0);

  await expect
    .poll(() => page.locator('#viewerContainer .page[data-page-number="2"]').evaluate((el) => {
      const box = el.getBoundingClientRect();
      return box.top < window.innerHeight / 2 && box.bottom > 0;
    }), { timeout: 20_000 })
    .toBe(true);
  const shift = await layoutShift(page);
  console.log(`[chunks] PDF chunk held until released: layout shift ${shift.toFixed(4)}`);
  expect(shift).toBeLessThan(0.01);
});

test('a saved place and search work on an EPUB reader that arrives late', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const { bytes, targetCfi } = await buildLongEpub();
  const bookId = await seed(baseUrl, 'ebook', bytes, 'long.epub', 'application/epub+zip');
  await apiPut(baseUrl, `/api/books/${bookId}/progress`, { location: targetCfi, percentage: 93 });
  await watchLayoutShift(page);
  const chunk = await holdChunk(page, EPUB_CODE);

  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  const spinner = page.locator('nostos-loading-indicator.reader-loading');
  await spinner.waitFor({ timeout: 30_000 });
  // The chunk is on the wire and the reader cannot mount until it arrives.
  await expect.poll(() => chunk.wasHeld(), { timeout: 30_000 }).toBe(true);
  const before = (await spinner.boundingBox())!;

  chunk.release();
  const overlay = page.locator('app-epub-reader .loading-overlay nostos-loading-indicator');
  await overlay.waitFor({ state: 'attached', timeout: 30_000 });
  const after = await overlay.boundingBox();
  if (after) {
    const jump = Math.hypot(before.x - after.x, before.y - after.y);
    console.log(`[chunks] spinner moved ${jump.toFixed(1)} px between shell and EPUB reader`);
  }

  // The saved paragraph is the one on the page, as in the un-held restore spec.
  const paragraph = page.frameLocator('#epub-viewer iframe').locator(`#p${TARGET_PARAGRAPH}`);
  await paragraph.waitFor({ state: 'attached', timeout: 45_000 });
  await expect
    .poll(async () => {
      const stage = await page.locator('#epub-page > .epub-container').evaluate((el) => ({
        left: el.scrollLeft,
        width: el.clientWidth,
      }));
      const rect = await paragraph.evaluate((el) => {
        const box = el.getBoundingClientRect();
        return { left: box.left, right: box.right };
      });
      return rect.right > stage.left && rect.left < stage.left + stage.width;
    }, { timeout: 20_000 })
    .toBe(true);

  // The restore has re-laid the page out; no shift may have come with it.
  await waitForRenditionSize(page);
  await waitForLayoutSettled(page, '#epub-page');
  const shift = await layoutShift(page);
  console.log(`[chunks] EPUB chunk held until released: layout shift ${shift.toFixed(4)}`);
  expect(shift).toBeLessThan(0.01);

  await page.keyboard.press('Control+f');
  const input = page.locator('.reader-search-input');
  await expect(input).toBeVisible({ timeout: 20_000 });
  await input.fill('terrace');
  await expect(page.locator('.reader-search-panel')).toContainText(/\d+ of \d+/, { timeout: 30_000 });
});

test('read aloud and the Aa panel work on an EPUB reader that arrives late', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seed(baseUrl, 'ebook', EPUB_BYTES, 'book.epub', 'application/epub+zip');
  const chunk = await holdChunk(page, EPUB_CODE);

  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.locator('nostos-loading-indicator.reader-loading').waitFor({ timeout: 30_000 });
  await expect.poll(() => chunk.wasHeld(), { timeout: 30_000 }).toBe(true);
  chunk.release();
  await openFrame(page).waitFor({ timeout: 45_000 });
  await waitForRenditionSize(page);
  await waitForLayoutSettled(page, '#epub-viewer');

  const paragraph = (await openFrame(page).boundingBox())!;
  await page.mouse.click(paragraph.x + 40, paragraph.y + 8);
  await page.getByTestId('typo-toggle').click();
  await expect(page.locator('[data-testid="typo-panel"] [aria-labelledby="typo-typeface"] .seg-opt')).toHaveCount(4);
  await page.keyboard.press('Escape');

  await page.getByTestId('reader-more').click();
  await expect(page.getByTestId('read-aloud-start')).toBeEnabled();
});

test('an audiobook keeps a time link for a player that arrives late', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seed(baseUrl, 'audiobook', MP3_BYTES, 'book.mp3', 'audio/mpeg');
  const chunk = await holdChunk(page, 'audio-loading');

  await page.goto(`${baseUrl}/read/${bookId}?sourceTime=2`, { waitUntil: 'domcontentloaded' });
  await page.locator('nostos-loading-indicator.reader-loading').waitFor({ timeout: 30_000 });
  await expect.poll(() => chunk.wasHeld(), { timeout: 30_000 }).toBe(true);
  chunk.release();

  await page.locator('app-audio-reader').waitFor({ timeout: 30_000 });
  await expect(page.locator('nostos-loading-indicator.reader-loading')).toHaveCount(0);
});
