/**
 * A saved highlight stays on its words when the text re-wraps under it.
 *
 * epub.js's overlay measured a highlight once and redrew it only when the
 * frame changed size, so a reflow at the same size — a web font finishing its
 * load, a typography change — left the highlight on the wrong words until a
 * reload. Highlights are now painted by the text itself.
 */
import { expect, Page, test } from '@playwright/test';
import { mkdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { apiPost, loadFixture } from './support/fixture';

const EPUB = readFileSync(path.join(__dirname, 'assets', 'reader-margins.epub'));
test.use({ serviceWorkers: 'block' });

/** A fresh copy per test, so one run's reading position and highlights do not carry into the next. */
async function seedBook(): Promise<string> {
  const { baseUrl } = loadFixture();
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook', title: 'Dracula', author: 'Bram Stoker',
  });
  const bookId = created.book?.id ?? created.bookId ?? created.id;
  const form = new FormData();
  form.append('file', new Blob([EPUB], { type: 'application/epub+zip' }), 'margins.epub');
  expect((await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form })).ok).toBe(true);
  return bookId;
}

/**
 * epub.js can replace the section's frame, or re-lay it out, shortly after it
 * first appears. Waits until the same document has stayed on screen at the
 * same size across a poll, so a step never acts on a frame being discarded.
 */
async function waitForSettledPage(page: Page): Promise<void> {
  await expect.poll(() => page.evaluate(() => {
    const frame = document.querySelector<HTMLIFrameElement>('#epub-viewer iframe');
    const doc = frame?.contentDocument as (Document & { __nostosSettle?: string }) | null | undefined;
    if (!frame || !doc?.body) return false;
    const box = frame.getBoundingClientRect();
    const size = `${Math.round(box.width)}x${Math.round(box.height)}`;
    const same = doc.__nostosSettle === size;
    doc.__nostosSettle = size;
    return same && box.width > 0;
  }), { intervals: [400], timeout: 20_000 }).toBe(true);
}

/**
 * The words the page paints in a highlighter pen. A registered range is
 * painted on its own line boxes wherever they are, so no stored geometry
 * exists that could disagree with the text.
 */
async function paintedHighlights(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const frame = document.querySelector<HTMLIFrameElement>('#epub-viewer iframe');
    const win = frame?.contentWindow as (Window & { CSS: { highlights?: Map<string, Set<Range>> } }) | null;
    const painted: string[] = [];
    for (const [name, highlight] of win?.CSS.highlights?.entries() ?? []) {
      if (!name.startsWith('nostos-mark-pen-')) continue;
      for (const range of highlight) painted.push(range.toString().trim());
    }
    return painted;
  });
}

for (const theme of ['light', 'dark'] as const) {
  test(`390px ${theme}: a highlight follows its words through a same-size reflow`, async ({ browser }) => {
    const context = await browser.newContext({
      viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, serviceWorkers: 'block',
    });
    const page = await context.newPage();
    const bookId = await seedBook();
    try {
      await page.addInitScript((value) => localStorage.setItem('nostos.theme', value), theme);
      await page.goto(`${loadFixture().baseUrl}/read/${bookId}`);
      const frame = page.frameLocator('#epub-viewer iframe');
      await frame.locator('p').first().waitFor({ timeout: 45_000 });
      await expect(page.locator('.loading-overlay')).toHaveClass(/is-hidden/);
      await waitForSettledPage(page);

      // A sentence from the middle of the opening paragraph that wraps over
      // several lines, like the one in the report, and stays on this page
      // after the reflow.
      const words = await frame.locator('p').first().evaluate((paragraph) => {
        const doc = paragraph.ownerDocument;
        const text = paragraph.firstChild as Text;
        const content = text.data;
        const start = content.indexOf('Buda-Pesth');
        const end = content.indexOf('.', start) + 1;
        const range = doc.createRange();
        range.setStart(text, start);
        range.setEnd(text, end);
        const selection = doc.getSelection()!;
        selection.removeAllRanges();
        selection.addRange(range);
        doc.dispatchEvent(new Event('touchend'));
        return range.toString().trim();
      });
      await page.getByTestId('selection-highlight').click();
      await expect(page.getByTestId('selection-bar')).toBeHidden();
      await expect.poll(() => paintedHighlights(page)).toEqual([words]);

      await waitForSettledPage(page);
      const frameSize = async () => {
        const box = await page.locator('#epub-viewer iframe').boundingBox();
        return box ? `${Math.round(box.width)}x${Math.round(box.height)}` : null;
      };
      const frameBox = await frameSize();

      // Re-wrap every line without touching the frame: the shape of a font
      // swapping in after the page was laid out.
      await frame.locator('head').evaluate((head) => {
        const style = head.ownerDocument.createElement('style');
        style.textContent = 'p { letter-spacing: 0.05em !important; word-spacing: 0.1em !important; }';
        head.appendChild(style);
      });
      expect(await frameSize()).toBe(frameBox);

      expect(await paintedHighlights(page)).toEqual([words]);

      if (process.env.HIGHLIGHT_REFLOW_EVIDENCE) {
        const dir = path.join(__dirname, 'visual-evidence', 'reader-highlight-reflow');
        mkdirSync(dir, { recursive: true });
        await page.screenshot({ path: path.join(dir, `${process.env.HIGHLIGHT_REFLOW_EVIDENCE}-390-${theme}.png`) });
      }
    } finally {
      await context.close();
    }
  });
}

/**
 * Where the saved highlight is painted, measured against where its words are
 * now. `matches` compares the painted range's line boxes with a fresh range
 * built over the same text in the document on screen, so a highlight left in
 * a discarded section, painted twice, or on the wrong words all fail.
 */
interface HighlightState {
  copies: number;
  live: boolean;
  matches: boolean;
  lines: number;
  visible: boolean;
  tint: 'full' | 'tinted' | 'none';
}

async function highlightState(page: Page, words: string): Promise<HighlightState> {
  return page.evaluate((expected) => {
    const squash = (text: string) => text.replace(/\s+/g, ' ').trim();
    const state = { copies: 0, live: false, matches: false, lines: 0, visible: false, tint: 'none' as 'full' | 'tinted' | 'none' };
    for (const frame of document.querySelectorAll<HTMLIFrameElement>('#epub-viewer iframe')) {
      const win = frame.contentWindow as (Window & { CSS: { highlights?: Map<string, Set<Range>> } }) | null;
      const doc = frame.contentDocument;
      if (!win || !doc) continue;
      const painted: Range[] = [];
      for (const [name, highlight] of win.CSS.highlights?.entries() ?? []) {
        if (!name.startsWith('nostos-mark-pen-')) continue;
        for (const range of highlight) if (squash(range.toString()) === expected) painted.push(range);
      }
      state.copies += painted.length;
      const range = painted[0];
      if (!range) continue;
      state.live = range.startContainer.ownerDocument === doc && range.startContainer.isConnected;

      const walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT);
      let fresh: Range | null = null;
      for (let node = walker.nextNode() as Text | null; node; node = walker.nextNode() as Text | null) {
        const flat = node.data.replace(/\s/g, ' ');
        const at = flat.indexOf(expected.slice(0, 10));
        if (at < 0) continue;
        fresh = doc.createRange();
        fresh.setStart(node, at);
        fresh.setEnd(node, range.endOffset);
        break;
      }
      const boxes = (r: Range) => [...r.getClientRects()].filter((b) => b.width > 0)
        .map((b) => [b.left, b.top, b.width, b.height].map(Math.round).join(','));
      const now = boxes(range);
      state.lines = now.length;
      state.matches = !!fresh && now.length > 0 && now.join(';') === boxes(fresh).join(';');

      const frameBox = frame.getBoundingClientRect();
      state.visible = [...range.getClientRects()].some((b) => {
        const left = frameBox.left + b.left;
        return b.width > 0 && left >= 0 && left + b.width <= innerWidth + 1;
      });
      const css = doc.getElementById('nostos-text-marks')?.textContent ?? '';
      const rule = css.match(/::highlight\(nostos-mark-pen-sage\)\{background-color:([^;]+);/)?.[1] ?? '';
      state.tint = !rule ? 'none' : /^#[0-9a-f]{8}$/i.test(rule) ? 'tinted' : 'full';
    }
    return state;
  }, words);
}

/** The chrome starts hidden; a tap near the top shows it. */
async function openViewSettings(page: Page, width: number): Promise<void> {
  if (await page.getByTestId('reader-chrome-top').getAttribute('aria-hidden') === 'true') {
    await page.locator('#epub-viewer').click({ position: { x: width / 2, y: 12 } });
  }
  await page.getByRole('button', { name: 'View settings', exact: true }).click();
}

async function closeViewSettings(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Close view settings' }).click();
  await page.keyboard.press('Escape');
}

function radio(page: Page, group: string, option: string) {
  return page.getByRole('radiogroup', { name: group, exact: true }).getByRole('radio', { name: option, exact: true });
}

type Step = { name: string; run: (page: Page, width: number) => Promise<void>; theme?: 'light' | 'dark' };

const inSettings = (name: string, act: (page: Page) => Promise<void>, theme?: 'light' | 'dark'): Step => ({
  name,
  theme,
  run: async (page, width) => {
    await openViewSettings(page, width);
    await act(page);
    await closeViewSettings(page);
  },
});

const pick = (group: string, option: string) => (page: Page) => radio(page, group, option).click();
const press = (label: string, times: number) => async (page: Page) => {
  for (let i = 0; i < times; i++) await page.getByRole('button', { name: label, exact: true }).click();
};

function steps(start: 'light' | 'dark'): Step[] {
  const other = start === 'light' ? 'dark' : 'light';
  const label = (theme: string) => theme[0].toUpperCase() + theme.slice(1);
  return [
    inSettings('margins-narrow', pick('Margins', 'Narrow')),
    inSettings('margins-wide', pick('Margins', 'Wide')),
    inSettings('margins-normal', pick('Margins', 'Normal')),
    inSettings('typeface-publisher', pick('Typeface', 'Publisher')),
    inSettings('typeface-sans', pick('Typeface', 'Sans')),
    inSettings('typeface-mono', pick('Typeface', 'Mono')),
    inSettings('typeface-libron', pick('Typeface', 'Libron')),
    inSettings('text-larger-x3', press('Larger text', 3)),
    inSettings('text-smaller-x5', press('Smaller text', 5)),
    inSettings('line-height-2.0', pick('Line height', '2.0')),
    inSettings('line-height-1.4', pick('Line height', '1.4')),
    inSettings('everything-at-once', async (page) => {
      await radio(page, 'Margins', 'Wide').click();
      await radio(page, 'Typeface', 'Sans').click();
      await page.getByRole('button', { name: 'Larger text', exact: true }).click();
      await radio(page, 'Line height', '1.8').click();
    }),
    inSettings(`theme-${other}`, pick('Theme', label(other)), other),
    inSettings(`theme-${start}`, pick('Theme', label(start)), start),
    inSettings('reset-text', (page) => page.getByRole('button', { name: 'Reset text settings' }).click()),
    {
      name: 'page-turn-forward-and-back',
      run: async (page) => {
        await page.keyboard.press('ArrowRight');
        await page.waitForTimeout(400);
        await page.keyboard.press('ArrowLeft');
      },
    },
    {
      name: 'viewport-rotated-and-back',
      run: async (page) => {
        const size = page.viewportSize()!;
        await page.setViewportSize({ width: size.height, height: size.width });
        await page.waitForTimeout(800);
        await page.setViewportSize(size);
      },
    },
    {
      name: 'reload',
      run: async (page) => {
        await page.reload();
        await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
      },
    },
  ];
}

const sizes = [
  { label: 'phone', width: 390, height: 844, mobile: true },
  { label: 'desktop', width: 1280, height: 800, mobile: false },
];

for (const size of sizes) {
  for (const theme of ['light', 'dark'] as const) {
    test(`${size.label} ${theme}: a highlight stays on its words through every view setting`, async ({ browser }) => {
      test.setTimeout(180_000);
      const context = await browser.newContext({
        viewport: { width: size.width, height: size.height },
        isMobile: size.mobile, hasTouch: size.mobile, serviceWorkers: 'block',
      });
      const page = await context.newPage();
      const bookId = await seedBook();
      try {
        await page.addInitScript((value) => {
          if (!sessionStorage.getItem('seeded')) {
            localStorage.setItem('nostos.theme', value);
            sessionStorage.setItem('seeded', '1');
          }
        }, theme);
        await page.goto(`${loadFixture().baseUrl}/read/${bookId}`);
        const frame = page.frameLocator('#epub-viewer iframe');
        await frame.locator('p').first().waitFor({ timeout: 45_000 });
        await expect(page.locator('.loading-overlay')).toHaveClass(/is-hidden/);
        await waitForSettledPage(page);

        const words = await frame.locator('p').first().evaluate((paragraph) => {
          const doc = paragraph.ownerDocument;
          const text = paragraph.firstChild as Text;
          const start = text.data.indexOf('Buda-Pesth');
          const end = text.data.indexOf('.', start) + 1;
          const range = doc.createRange();
          range.setStart(text, start);
          range.setEnd(text, end);
          doc.getSelection()!.removeAllRanges();
          doc.getSelection()!.addRange(range);
          doc.dispatchEvent(new Event(doc.defaultView!.matchMedia('(pointer: coarse)').matches ? 'touchend' : 'mouseup'));
          return range.toString().replace(/\s+/g, ' ').trim();
        });
        await page.getByTestId('selection-highlight').click();
        await expect(page.getByTestId('selection-bar')).toBeHidden();

        const report: Record<string, HighlightState> = {};
        const check = async (name: string, expectedTheme: 'light' | 'dark') => {
          await expect.poll(async () => {
            const state = await highlightState(page, words);
            return state.copies === 1 && state.live && state.matches;
          }, { message: `${name}: highlight painted once, live, on its words`, timeout: 15_000 }).toBe(true);
          // Let any trailing re-layout land, then require it to still hold.
          await page.waitForTimeout(700);
          const state = await highlightState(page, words);
          expect(state, name).toMatchObject({ copies: 1, live: true, matches: true });
          expect(state.tint, `${name}: pen strength follows the theme`).toBe(expectedTheme === 'dark' ? 'tinted' : 'full');
          report[name] = state;
          if (process.env.HIGHLIGHT_SWEEP_EVIDENCE && state.visible) {
            const dir = path.join(process.env.HIGHLIGHT_SWEEP_EVIDENCE, `${size.label}-${theme}`);
            mkdirSync(dir, { recursive: true });
            await page.screenshot({ path: path.join(dir, `${name}.png`) });
          }
        };

        let current: 'light' | 'dark' = theme;
        await check('saved', current);
        for (const step of steps(theme)) {
          await step.run(page, size.width);
          if (step.theme) current = step.theme;
          await check(step.name, current);
        }
        console.log(`${size.label} ${theme}`, JSON.stringify(report));
      } finally {
        await context.close();
      }
    });
  }
}

/** Turns pages until the section on screen has `heading` as its first heading. */
async function turnTo(page: Page, key: 'ArrowRight' | 'ArrowLeft', heading: string): Promise<void> {
  await expect.poll(async () => {
    const shown = await page.evaluate(() =>
      [...document.querySelectorAll<HTMLIFrameElement>('#epub-viewer iframe')]
        .map((frame) => frame.contentDocument?.querySelector('h1')?.textContent?.trim() ?? ''));
    if (shown.includes(heading)) return true;
    await page.keyboard.press(key);
    await page.waitForTimeout(300);
    return false;
  }, { intervals: [100], timeout: 30_000 }).toBe(true);
  await waitForSettledPage(page);
}

test('a highlight is painted in its own chapter only, across chapter changes and a reload', async ({ browser }) => {
  test.setTimeout(120_000);
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, serviceWorkers: 'block' });
  const page = await context.newPage();
  const { baseUrl } = loadFixture();
  const created = await apiPost<any>(baseUrl, '/api/books/', { type: 'ebook', title: 'Pride and Prejudice', author: 'Jane Austen' });
  const bookId = created.book?.id ?? created.bookId ?? created.id;
  const form = new FormData();
  form.append('file', new Blob([readFileSync(path.join(__dirname, 'assets', 'read-aloud.epub'))], { type: 'application/epub+zip' }), 'p.epub');
  expect((await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form })).ok).toBe(true);
  try {
    await page.goto(`${baseUrl}/read/${bookId}`);
    await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
    await waitForSettledPage(page);
    // Focus the page so arrow keys turn it.
    await page.locator('#epub-viewer').click({ position: { x: 195, y: 400 } });
    await turnTo(page, 'ArrowRight', 'Chapter 2');

    const words = await page.evaluate(() => {
      const frame = [...document.querySelectorAll<HTMLIFrameElement>('#epub-viewer iframe')]
        .find((f) => f.contentDocument?.querySelector('h1')?.textContent?.trim() === 'Chapter 2')!;
      const doc = frame.contentDocument!;
      const text = doc.querySelector('p')!.firstChild as Text;
      const start = text.data.indexOf('He had always');
      const range = doc.createRange();
      range.setStart(text, start);
      range.setEnd(text, text.data.indexOf('.', start) + 1);
      doc.getSelection()!.removeAllRanges();
      doc.getSelection()!.addRange(range);
      doc.dispatchEvent(new Event('mouseup'));
      return range.toString().replace(/\s+/g, ' ').trim();
    });
    await page.getByTestId('selection-highlight').click();
    await expect(page.getByTestId('selection-bar')).toBeHidden();
    await expect.poll(async () => (await highlightState(page, words)).matches).toBe(true);

    // Chapter 1 is a different section: the mark must not be painted there.
    await turnTo(page, 'ArrowLeft', 'Chapter 1');
    const chapterOnePens = await page.evaluate(() => {
      let count = 0;
      for (const frame of document.querySelectorAll<HTMLIFrameElement>('#epub-viewer iframe')) {
        if (frame.contentDocument?.querySelector('h1')?.textContent?.trim() !== 'Chapter 1') continue;
        const win = frame.contentWindow as Window & { CSS: { highlights?: Map<string, Set<Range>> } };
        for (const [name, highlight] of win.CSS.highlights?.entries() ?? []) {
          if (name.startsWith('nostos-mark-pen-')) count += highlight.size;
        }
      }
      return count;
    });
    expect(chapterOnePens).toBe(0);

    // Back in chapter 2 the section is a new document; the mark is painted into it again.
    await turnTo(page, 'ArrowRight', 'Chapter 2');
    await expect.poll(async () => highlightState(page, words)).toMatchObject({ copies: 1, live: true, matches: true });

    await page.reload();
    await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
    await waitForSettledPage(page);
    await expect.poll(async () => highlightState(page, words)).toMatchObject({ copies: 1, live: true, matches: true });
  } finally {
    await context.close();
  }
});
