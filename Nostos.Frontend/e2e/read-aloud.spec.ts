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
    test(`${theme}: the voice is chosen in View settings and remembered`, async ({ page }) => {
      const calls = await stubServerVoice(page, { managed: true });
      await openBook(page, theme, `${run}-voices-${theme}`);

      await openViewSettings(page);
      const row = page.getByTestId('read-aloud-voice');
      await expect(row).toBeVisible();
      await expect(row.getByRole('button', { name: 'Emma' })).toHaveAttribute('aria-pressed', 'true');
      await expect(row).toContainText('Natural voices use your AI allowance');
      await capture(page, `read-aloud-voices-1280-${theme}`);

      await row.getByRole('button', { name: 'George' }).click();
      await expect(row.getByRole('button', { name: 'George' })).toHaveAttribute('aria-pressed', 'true');
      expect(await page.evaluate(() => localStorage.getItem('nostos.readAloud.voice'))).toBe('bm_george');

      await page.getByTestId('typo-toggle').click();
      await startReading(page);
      await expect.poll(() => calls.length).toBeGreaterThan(0);
      expect(calls[0].voice).toBe('bm_george');
    });
  }

  test('choosing Device reads with the device voice and asks the server for nothing', async ({ page }) => {
    const calls = await stubServerVoice(page);
    await openBook(page, 'light', `${run}-device`);

    await openViewSettings(page);
    await page.getByTestId('read-aloud-voice').getByRole('button', { name: 'Device' }).click();
    await page.getByTestId('typo-toggle').click();
    await startReading(page);

    await expect.poll(async () => (await spoken(page)).length).toBeGreaterThan(0);
    expect(calls).toEqual([]);
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
