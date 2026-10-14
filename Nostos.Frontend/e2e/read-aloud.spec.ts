/**
 * Read aloud in a real engine.
 *
 * Unit tests own the queue and the sentence splitter. What only a browser can
 * show is here: epub.js draws the mark on the sentence being spoken, the page
 * turns when the voice reaches the next one, a second spine section opens when
 * the first runs out, and the transport stays in reach while the chrome rests.
 *
 * The device voice is replaced by a stand-in that records what it is asked to
 * say and finishes each sentence after a short delay, so the test neither
 * depends on which voices a machine has nor makes a sound.
 *
 * Set NOSTOS_CAPTURE_EVIDENCE=1 to write the screenshots under
 * e2e/visual-evidence/read-aloud/.
 */
import { expect, test, type Page } from '@playwright/test';
import { mkdirSync, readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';

const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'read-aloud.epub'));
const EVIDENCE_DIR = path.join(__dirname, 'visual-evidence', 'read-aloud');
const CAPTURE = !!process.env['NOSTOS_CAPTURE_EVIDENCE'];

async function seedBook(baseUrl: string, title: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title,
    author: 'Jane Austen',
  });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  if (!bookId) throw new Error(`No book id in create response: ${JSON.stringify(created)}`);

  const form = new FormData();
  form.append('file', new Blob([EPUB_BYTES], { type: 'application/epub+zip' }), 'read-aloud.epub');
  const response = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!response.ok) throw new Error(`file upload -> ${response.status}: ${await response.text()}`);
  return bookId as string;
}

async function installSpeechStandIn(page: Page, theme: 'light' | 'dark'): Promise<void> {
  await page.addInitScript((themeName) => {
    try {
      localStorage.setItem('nostos.theme', themeName);
    } catch {
      // The default theme is fine.
    }
    const scope = window as any;
    scope.__spoken = [];
    scope.__speechDelay = 120;
    scope.SpeechSynthesisUtterance = class {
      rate = 1;
      lang = '';
      voice = null;
      onend: (() => void) | null = null;
      onerror: ((event: { error: string }) => void) | null = null;
      constructor(public text: string) {}
    };
    const synth = {
      current: null as any,
      speak(utterance: any) {
        // The silent request that unlocks speech inside the starting tap.
        if (!utterance.text) {
          utterance.onend?.();
          return;
        }
        scope.__spoken.push({ text: utterance.text, lang: utterance.lang, rate: utterance.rate });
        this.current = utterance;
        utterance.__timer = setTimeout(() => {
          if (this.current === utterance) this.current = null;
          utterance.onend?.();
        }, scope.__speechDelay);
      },
      cancel() {
        const utterance = this.current;
        this.current = null;
        if (!utterance) return;
        clearTimeout(utterance.__timer);
        utterance.onerror?.({ error: 'canceled' });
      },
      getVoices: () => [],
      addEventListener() {},
      removeEventListener() {},
    };
    Object.defineProperty(window, 'speechSynthesis', { value: synth, configurable: true });
  }, theme);
}

const spoken = (page: Page) =>
  page.evaluate(() => (window as any).__spoken.map((entry: { text: string }) => entry.text) as string[]);
const setSpeechDelay = (page: Page, ms: number) =>
  page.evaluate((delay) => {
    (window as any).__speechDelay = delay;
  }, ms);

/** The chrome rests hidden; a tap on the page brings it up. */
async function revealChrome(page: Page): Promise<void> {
  if (await page.getByTestId('typo-toggle').isVisible().catch(() => false)) return;
  const viewport = page.viewportSize()!;
  await page.mouse.click(viewport.width / 2, viewport.height / 2);
  await expect(page.getByTestId('typo-toggle')).toBeVisible();
}

/**
 * Starts reading. The chrome can go back to rest between being revealed and
 * the click (it does so on its own just after a book opens), so the pair is
 * retried as one step.
 */
async function startReading(page: Page): Promise<void> {
  await expect(async () => {
    await revealChrome(page);
    await page.getByTestId('read-aloud-start').click({ timeout: 2_000 });
  }).toPass({ timeout: 20_000 });
}

/** Opens View settings, retried with the reveal for the same reason as {@link startReading}. */
async function openViewSettings(page: Page): Promise<void> {
  await expect(async () => {
    await revealChrome(page);
    await page.getByTestId('typo-toggle').click({ timeout: 2_000 });
    await expect(page.getByTestId('reader-appearance')).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 20_000 });
}

async function openBook(page: Page, theme: 'light' | 'dark', run: string): Promise<void> {
  const { baseUrl } = loadFixture();
  await installSpeechStandIn(page, theme);
  void run;
  const bookId = await seedBook(baseUrl, 'Pride and Prejudice');
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.frameLocator('#epub-viewer iframe').first().locator('p').first().waitFor({ timeout: 45_000 });
  await revealChrome(page);
}

async function capture(page: Page, name: string): Promise<void> {
  if (!CAPTURE) return;
  mkdirSync(EVIDENCE_DIR, { recursive: true });
  await page.screenshot({ path: path.join(EVIDENCE_DIR, `${name}.png`) });
}

test.describe('read aloud (EPUB)', () => {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  for (const theme of ['light', 'dark'] as const) {
    test(`desktop, ${theme}: reads through the chapter and into the next one`, async ({ page }) => {
      await openBook(page, theme, `${run}-${theme}`);

      const start = page.getByTestId('read-aloud-start');
      await setSpeechDelay(page, 4000);
      await startReading(page);

      const bar = page.getByTestId('read-aloud-bar');
      await expect(bar).toBeVisible();
      await revealChrome(page);
      await expect(start).toHaveAttribute('aria-pressed', 'true');
      // The sentence being spoken is marked on the page.
      await expect(page.locator('.epubjs-read-aloud-mark')).toHaveCount(1);
      expect((await spoken(page))[0]).toBe('Chapter 1');
      expect(await page.evaluate(() => (window as any).__spoken[0].lang)).toBe('en-GB');

      // One sentence on, for a picture with a marked sentence of prose.
      await page.getByRole('button', { name: 'Next sentence' }).click();
      await expect(page.locator('.epubjs-read-aloud-mark')).toHaveCount(1);
      await expect.poll(async () => (await spoken(page)).at(-1)).toContain('truth universally acknowledged');
      await capture(page, `read-aloud-epub-1280-${theme}`);

      // Reading goes on by itself, across pages and into the second section.
      await setSpeechDelay(page, 40);
      await page.getByRole('button', { name: 'Next sentence' }).click();
      await expect
        .poll(async () => (await spoken(page)).some((text) => text.startsWith('Chapter 2')), { timeout: 60_000 })
        .toBe(true);
      const said = await spoken(page);
      // Mr. Bennet's full stop does not end a sentence.
      expect(said.some((text) => text.includes('“My dear Mr. Bennet,” said his lady'))).toBe(true);

      // The end of the book ends the session and clears the mark.
      await expect(bar).toHaveCount(0, { timeout: 30_000 });
      await expect(page.locator('.epubjs-read-aloud-mark')).toHaveCount(0);
      expect((await spoken(page)).at(-1)).toContain('since we are not to visit');
    });
  }

  test('pause, skip, speed and stop', async ({ page }) => {
    await openBook(page, 'light', `${run}-controls`);
    await setSpeechDelay(page, 60_000);
    await startReading(page);
    await expect.poll(async () => (await spoken(page)).length).toBe(1);

    const toggle = page.getByTestId('read-aloud-toggle');
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-label', 'Resume reading');

    // Skipping while paused moves the mark and says nothing.
    await page.getByRole('button', { name: 'Next sentence' }).click();
    await expect(page.locator('.epubjs-read-aloud-mark')).toHaveCount(1);
    expect((await spoken(page)).length).toBe(1);

    await toggle.click();
    await expect.poll(async () => (await spoken(page)).at(-1)).toContain('truth universally acknowledged');

    // A new speed applies to the sentence being spoken, and is shown.
    const rate = page.getByTestId('read-aloud-rate');
    await rate.click();
    await page.getByRole('menuitem', { name: '1.25×' }).click();
    await expect(rate).toHaveText('1.25×');
    await expect.poll(() => page.evaluate(() => (window as any).__spoken.at(-1).rate)).toBe(1.25);

    await page.getByTestId('read-aloud-stop').click();
    await expect(page.getByTestId('read-aloud-bar')).toHaveCount(0);
    await expect(page.locator('.epubjs-read-aloud-mark')).toHaveCount(0);
  });

  test('carries on from the new page when the person turns it', async ({ page }) => {
    await openBook(page, 'light', `${run}-turn`);
    await setSpeechDelay(page, 60_000);
    await startReading(page);
    await expect.poll(async () => (await spoken(page)).length).toBe(1);

    await revealChrome(page);
    await page.getByRole('button', { name: 'Next page' }).click();

    await expect.poll(async () => (await spoken(page)).length, { timeout: 15_000 }).toBe(2);
    expect((await spoken(page))[1]).not.toBe('Chapter 1');
    await expect(page.getByTestId('read-aloud-bar')).toBeVisible();
  });
});

test.describe('read aloud (EPUB), phone width', () => {
  test.use({ serviceWorkers: 'block', viewport: { width: 390, height: 844 }, hasTouch: true });
  const run = newRunId();

  for (const theme of ['light', 'dark'] as const) {
    test(`${theme}: the transport stays in reach while the chrome rests`, async ({ page }) => {
      await openBook(page, theme, `${run}-phone-${theme}`);
      await setSpeechDelay(page, 60_000);
      await startReading(page);

      const bar = page.getByTestId('read-aloud-bar');
      await expect(bar).toBeVisible();
      await page.getByRole('button', { name: 'Next sentence' }).click();
      await expect(page.locator('.epubjs-read-aloud-mark')).toHaveCount(1);
      await capture(page, `read-aloud-epub-390-${theme}-chrome`);

      // A tap on the page puts the chrome away; the transport stays.
      await page.mouse.click(195, 300);
      await expect(page.getByTestId('typo-toggle')).toBeHidden();
      await expect(bar).toBeVisible();
      await capture(page, `read-aloud-epub-390-${theme}`);

      // Every control is a full touch target and nothing scrolls sideways.
      for (const button of await bar.locator('button').all()) {
        const box = (await button.boundingBox())!;
        expect(Math.min(box.width, box.height)).toBeGreaterThanOrEqual(44);
      }
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth > document.documentElement.clientWidth,
        ),
      ).toBe(false);
    });
  }
});

/**
 * The server voice. The speech server is stubbed at the network edge with a
 * real (silent) MP3, so the browser's own audio element does the playing.
 */
const SILENT_MP3 = Buffer.from(
  'SUQzBAAAAAAAI1RTU0UAAAAPAAADTGF2ZjYwLjE2LjEwMAAAAAAAAAAAAAAA//NwwAAAAAAAAAAAAEluZm8AAAAPAAAAEAAAA/oAOjo6Ojo6SEhISEhIVVVVVVVVYmJiYmJib29vb29vb3x8fHx8fImJiYmJiZaWlpaWlqOjo6Ojo6OxsbGxsbG+vr6+vr7Ly8vLy8vY2NjY2NjY5eXl5eXl8vLy8vLy////////AAAAAExhdmM2MC4zMQAAAAAAAAAAAAAAACQDmgAAAAAAAAP6uxatFwAAAAAAAAAAAAAAAAD/8yDEAAAAA0gAAAAATEFNRTMuMTAwVVVVVVVVVVVVVVVMQU1FMy4xMDBVVVVVVVVVVVVV//MixCcAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVTEFNRTMuMTAwVVVVVVVVVVVVVVX/8yDETwAAA0gAAAAAVVVVVVVVVVVVVVVVVVVVVVVVVUxBTUUzLjEwMFVVVVVVVVVVVVVV//MgxHYAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVTEFNRTMuMTAwVVVVVVVVVVVVVf/zIMSdAAADSAAAAABVVVVVVVVVVVVVVVVVVVVVVVVVVUxBTUUzLjEwMFVVVVVVVVVVVVX/8yLExAAAA0gAAAAAVVVVVVVVVVVVVVVVVVVVVVVVVVVMQU1FMy4xMDBVVVVVVVVVVVVVVf/zIMTYAAADSAAAAABVVVVVVVVVVVVVVVVVVVVVVVVVTEFNRTMuMTAwVVVVVVVVVVVVVVX/8yDE2AAAA0gAAAAAVVVVVVVVVVVVVVVVVVVVVVVVVVVMQU1FMy4xMDBVVVVVVVVVVVVV//MgxNgAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVTEFNRTMuMTAwVVVVVVVVVVVVVf/zIsTXAAADSAAAAABVVVVVVVVVVVVVVVVVVVVVVVVVVUxBTUUzLjEwMFVVVVVVVVVVVVVV//MgxNgAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVf/zIMTYAAADSAAAAABVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVX/8yDE2AAAA0gAAAAAVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV//MixNcAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVX/8yDE2AAAA0gAAAAAVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV//MgxNgAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ==',
  'base64',
);

const SERVER_VOICES = [
  { id: 'bf_emma', name: 'Emma', language: 'en-GB' },
  { id: 'bm_george', name: 'George', language: 'en-GB' },
];

interface SpeechCall {
  text: string;
  voice: string | null;
  language: string | null;
}

async function stubServerVoice(
  page: Page,
  options: { managed?: boolean; fail?: { status: number; code: string } } = {},
): Promise<SpeechCall[]> {
  const calls: SpeechCall[] = [];
  await page.route('**/api/read-aloud/status*', (route) =>
    route.fulfill({
      json: {
        available: true,
        managed: options.managed ?? false,
        defaultVoice: 'bf_emma',
        voices: SERVER_VOICES,
        maxCharacters: 1200,
      },
    }),
  );
  await page.route('**/api/read-aloud/speech', (route) => {
    calls.push(route.request().postDataJSON() as SpeechCall);
    if (options.fail) {
      return route.fulfill({
        status: options.fail.status,
        contentType: 'application/problem+json',
        body: JSON.stringify({ title: options.fail.code, status: options.fail.status }),
      });
    }
    return route.fulfill({ status: 200, contentType: 'audio/mpeg', body: SILENT_MP3 });
  });
  return calls;
}

test.describe('read aloud (EPUB), server voice', () => {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  test('reads with the server voice, sentence after sentence, without the device voice', async ({ page }) => {
    const calls = await stubServerVoice(page);
    await openBook(page, 'light', `${run}-server`);
    await startReading(page);

    await expect(page.getByTestId('read-aloud-bar')).toBeVisible();
    // The audio element plays each clip through, so the reading moves on by itself.
    await expect
      .poll(() => calls.some((call) => call.text.includes('However little known')), { timeout: 30_000 })
      .toBe(true);

    expect(calls[0]).toEqual({ text: 'Chapter 1', voice: 'bf_emma', language: 'en-GB' });
    expect(calls[1].text).toContain('truth universally acknowledged');
    expect(await spoken(page)).toEqual([]);
    await expect(page.getByTestId('read-aloud-notice')).toHaveCount(0);
    await expect(page.locator('.epubjs-read-aloud-mark')).toHaveCount(1);
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`${theme}: the voice is chosen from the bar and remembered`, async ({ page }) => {
      const calls = await stubServerVoice(page, { managed: true });
      await openBook(page, theme, `${run}-voices-${theme}`);
      await setSpeechDelay(page, 60_000);
      await startReading(page);
      await expect.poll(() => calls.length).toBeGreaterThan(0);
      expect(calls[0].voice).toBe('bf_emma');

      const voice = page.getByTestId('read-aloud-voice');
      await expect(voice).toContainText('Emma');
      await voice.click();
      const menu = page.getByTestId('read-aloud-voice-menu');
      await expect(menu).toBeVisible();
      await expect(menu.getByRole('menuitem', { name: 'Emma' })).toHaveAttribute('aria-checked', 'true');
      await expect(menu).toContainText('Natural voices use your AI allowance');
      await capture(page, `read-aloud-voice-menu-1280-${theme}`);

      await menu.getByRole('menuitem', { name: 'George' }).click();
      await expect(voice).toContainText('George');
      expect(await page.evaluate(() => localStorage.getItem('nostos.readAloud.voice'))).toBe('bm_george');
      await expect.poll(() => calls.at(-1)?.voice).toBe('bm_george');
    });
  }

  test('choosing the device voice reads with it and asks the server for nothing more', async ({ page }) => {
    const calls = await stubServerVoice(page);
    await openBook(page, 'light', `${run}-device`);
    await setSpeechDelay(page, 60_000);
    await startReading(page);
    await expect.poll(() => calls.length).toBeGreaterThan(0);

    await page.getByTestId('read-aloud-voice').click();
    await page.getByTestId('read-aloud-voice-menu').getByRole('menuitem', { name: /This device/ }).click();
    await expect(page.getByTestId('read-aloud-voice')).toContainText('Device');
    const asked = calls.length;

    await expect.poll(async () => (await spoken(page)).length).toBeGreaterThan(0);
    await page.waitForTimeout(500);
    expect(calls.length).toBe(asked);
  });

  test('says why there is no natural voice when the plan does not include it', async ({ page }) => {
    await page.route('**/api/read-aloud/status*', (route) =>
      route.fulfill({
        json: {
          available: false,
          managed: true,
          defaultVoice: null,
          voices: [],
          maxCharacters: 1200,
          reason: 'read_aloud_access_denied',
        },
      }),
    );
    await openBook(page, 'light', `${run}-not-included`);
    await setSpeechDelay(page, 60_000);
    await startReading(page);

    await page.getByTestId('read-aloud-voice').click();
    await expect(page.getByTestId('read-aloud-voice-note')).toHaveText(
      'Natural voices are not included in your plan.',
    );
    await capture(page, 'read-aloud-voice-menu-unavailable-1280-light');
  });

  test('an exhausted allowance falls back to the device voice and says why', async ({ page }) => {
    const calls = await stubServerVoice(page, {
      managed: true,
      fail: { status: 429, code: 'ai_usage_limit_reached' },
    });
    await openBook(page, 'light', `${run}-allowance`);
    await setSpeechDelay(page, 60_000);
    await startReading(page);

    const notice = page.getByTestId('read-aloud-notice');
    await expect(notice).toContainText('Your AI allowance is used up');
    await expect(notice).toContainText('this device’s voice');
    // The same sentence is read by the device, and reading goes on.
    expect((await spoken(page))[0]).toBe('Chapter 1');
    await expect(page.getByTestId('read-aloud-bar')).toBeVisible();
    expect(calls.length).toBeGreaterThan(0);
    await capture(page, 'read-aloud-fallback-1280-light');
  });
});

/**
 * PDF. The document is built here, so the page text is known exactly: a
 * running header and a page number in the margins, a word hyphenated at a line
 * end, and a sentence that runs over the page break.
 */
const PDF_HEADER = 'PRIDE AND PREJUDICE';
const PDF_PAGES = [
  [
    'It is a truth universally acknowledged, that a',
    'single man in possession of a good fortune, must',
    'be in want of a wife. However little known the',
    'feelings of such a man may be on his first enter-',
    'ing a neighbourhood, this truth is so well fixed',
    'in the minds of the surrounding families, that he',
    'is considered as the rightful property of some',
  ],
  ['one or other of their daughters. Mr. Bennet made', 'no answer to this at all.'],
  ['This was invitation enough for his lady.'],
];
const PDF_SENTENCES = [
  'It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife.',
  'However little known the feelings of such a man may be on his first entering a neighbourhood, this truth is so well fixed in the minds of the surrounding families, that he is considered as the rightful property of some one or other of their daughters.',
  'Mr. Bennet made no answer to this at all.',
  'This was invitation enough for his lady.',
];

function bookPdf(pages: string[][], header: string): Buffer {
  const objects: Array<string | null> = ['<< /Type /Catalog /Pages 2 0 R >>', null];
  const kids: number[] = [];
  const fontObject = 3 + pages.length * 2;

  pages.forEach((lines, index) => {
    const pageObject = objects.length + 1;
    kids.push(pageObject);
    objects.push(
      `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 420 595] /Contents ${pageObject + 1} 0 R /Resources << /Font << /F1 ${fontObject} 0 R >> >> >>`,
    );
    const body = [
      `BT /F1 9 Tf 150 572 Td (${header}) Tj ET`,
      `BT /F1 13 Tf 18 TL 50 500 Td ${lines.map((line) => `(${line}) Tj T*`).join(' ')} ET`,
      `BT /F1 9 Tf 205 22 Td (${index + 1}) Tj ET`,
    ].join('\n');
    objects.push(`<< /Length ${body.length} >>\nstream\n${body}\nendstream`);
  });

  objects.push('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>');
  objects[1] = `<< /Type /Pages /Kids [${kids.map((n) => `${n} 0 R`).join(' ')}] /Count ${kids.length} >>`;

  let out = '%PDF-1.4\n';
  const offsets: number[] = [];
  objects.forEach((body, index) => {
    offsets.push(out.length);
    out += `${index + 1} 0 obj\n${body}\nendobj\n`;
  });
  const xref = out.length;
  out += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  for (const offset of offsets) out += `${String(offset).padStart(10, '0')} 00000 n \n`;
  out += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(out, 'latin1');
}

async function seedPdf(baseUrl: string, title: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', { type: 'ebook', title, author: 'Jane Austen' });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  if (!bookId) throw new Error(`No book id in create response: ${JSON.stringify(created)}`);

  const form = new FormData();
  form.append('file', new Blob([bookPdf(PDF_PAGES, PDF_HEADER)], { type: 'application/pdf' }), 'read-aloud.pdf');
  const response = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!response.ok) throw new Error(`file upload -> ${response.status}: ${await response.text()}`);
  return bookId as string;
}

/** Brings up the chrome of the PDF reader and starts reading, as one retried step. */
async function startReadingPdf(page: Page): Promise<void> {
  await expect(async () => {
    if (!(await page.getByTestId('typo-toggle').isVisible().catch(() => false))) {
      await page.locator('.pdf-container').click({ position: { x: 30, y: 300 } });
    }
    await page.getByTestId('read-aloud-start').click({ timeout: 2_000 });
  }).toPass({ timeout: 30_000 });
}

async function openPdf(page: Page, theme: 'light' | 'dark'): Promise<void> {
  const { baseUrl } = loadFixture();
  await installSpeechStandIn(page, theme);
  const bookId = await seedPdf(baseUrl, 'Pride and Prejudice');
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });
  await page.locator('.textLayer').first().waitFor({ timeout: 60_000 });
}

test.describe('read aloud (PDF)', () => {
  test.use({ serviceWorkers: 'block' });

  for (const theme of ['light', 'dark'] as const) {
    test(`${theme}: reads the page text as sentences and turns the pages`, async ({ page }) => {
      await openPdf(page, theme);
      await setSpeechDelay(page, 60_000);
      await startReadingPdf(page);

      await expect(page.getByTestId('read-aloud-bar')).toBeVisible();
      await expect.poll(async () => (await spoken(page)).length).toBe(1);
      // The header in the top margin is not read; the first sentence is.
      expect((await spoken(page))[0]).toBe(PDF_SENTENCES[0]);

      // The mark sits on the words: three lines of the first paragraph.
      const boxes = page.locator('.page[data-page-number="1"] .read-aloud-mark-box');
      await expect(boxes).toHaveCount(3);
      const firstLine = (await page
        .locator('.page[data-page-number="1"] .textLayer span')
        .filter({ hasText: 'It is a truth' })
        .first()
        .boundingBox())!;
      const firstBox = (await boxes.first().boundingBox())!;
      expect(Math.abs(firstBox.x - firstLine.x)).toBeLessThan(6);
      expect(Math.abs(firstBox.y - firstLine.y)).toBeLessThan(10);
      await capture(page, `read-aloud-pdf-1280-${theme}`);

      // Read on to the end: the hyphenated word is rejoined, the sentence that
      // runs over the page break is said whole and once, and the pages turn.
      await setSpeechDelay(page, 80);
      await page.getByRole('button', { name: 'Next sentence' }).click();
      await expect(page.getByTestId('read-aloud-bar')).toHaveCount(0, { timeout: 30_000 });

      const said = await spoken(page);
      expect([...new Set(said)]).toEqual(PDF_SENTENCES);
      expect(said.join(' ')).not.toContain(PDF_HEADER);
      await expect(page.locator('.read-aloud-mark-box')).toHaveCount(0);
      await expect(page.locator('.page-input')).toHaveValue('3');
    });
  }

  test('carries on from the page the person turns to', async ({ page }) => {
    await openPdf(page, 'light');
    await setSpeechDelay(page, 60_000);
    await startReadingPdf(page);
    await expect.poll(async () => (await spoken(page)).length).toBe(1);

    await page.getByRole('button', { name: 'Next page' }).click();
    await page.getByRole('button', { name: 'Next page' }).click();

    await expect.poll(async () => (await spoken(page)).at(-1), { timeout: 15_000 }).toBe(PDF_SENTENCES[3]);
    await expect(page.locator('.page[data-page-number="3"] .read-aloud-mark-box')).toHaveCount(1);
  });
});

test.describe('read aloud (PDF), phone width', () => {
  test.use({ serviceWorkers: 'block', viewport: { width: 390, height: 844 }, hasTouch: true });

  for (const theme of ['light', 'dark'] as const) {
    test(`${theme}: marks the sentence and keeps the transport in reach`, async ({ page }) => {
      await openPdf(page, theme);
      await setSpeechDelay(page, 60_000);
      await startReadingPdf(page);

      await expect(page.getByTestId('read-aloud-bar')).toBeVisible();
      await expect(page.locator('.read-aloud-mark-box').first()).toBeVisible();
      await capture(page, `read-aloud-pdf-390-${theme}`);
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth > document.documentElement.clientWidth,
        ),
      ).toBe(false);
    });
  }
});
