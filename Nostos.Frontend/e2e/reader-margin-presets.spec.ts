/** Margin presets must resize the real EPUB engine, including above the spread cap. */
import { expect, test } from '@playwright/test';
import { mkdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { apiPost, loadFixture } from './support/fixture';

// Public-domain Dracula excerpt; provenance is recorded alongside visual evidence.
const EPUB = readFileSync(path.join(__dirname, 'assets', 'reader-margins.epub'));
const viewports = [390, 1440, 1920, 2560];
const presets = ['Narrow', 'Normal', 'Wide'] as const;
test.use({ serviceWorkers: 'block', deviceScaleFactor: 1 });
let bookId: string;
test.beforeAll(async () => {
  const { baseUrl } = loadFixture();
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook', title: 'Dracula', author: 'Bram Stoker',
  });
  bookId = created.book?.id ?? created.bookId ?? created.id;
  const form = new FormData();
  form.append('file', new Blob([EPUB], { type: 'application/epub+zip' }), 'margins.epub');
  expect((await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form })).ok).toBe(true);
});

for (const width of viewports) {
  for (const theme of ['light', 'dark']) {
    test(`${width}px ${theme}: all margin presets reflow the book and persist`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
      await page.addInitScript((value) => localStorage.setItem('nostos.theme', value), theme);
      await page.goto(`${loadFixture().baseUrl}/read/${bookId}`);
      await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
      await expect(page.locator('.loading-overlay')).toHaveClass(/is-hidden/);
      const measured: number[] = [];
      const textWidths: number[] = [];
      for (const preset of presets) {
        if (await page.getByTestId('reader-chrome-top').getAttribute('aria-hidden') === 'true') {
          await page.locator('#epub-viewer').click({ position: { x: width / 2, y: 12 } });
        }
        await page.getByRole('button', { name: 'View settings', exact: true }).click();
        const option = page.getByRole('group', { name: 'Margins', exact: true })
          .getByRole('button', { name: preset, exact: true });
        await option.click();
        await expect(option).toHaveAttribute('aria-pressed', 'true');
        await expect.poll(() => page.evaluate(() =>
          JSON.parse(localStorage.getItem('nostos.epub-typography')!).margin
        )).toBe(preset.toLowerCase());
        // Check the engine's actual layout width, not just an active button or CSS variable.
        await expect.poll(async () => {
          const outer = await page.locator('#epub-page').evaluate((el) => el.clientWidth);
          const engine = await page.locator('#epub-page .epub-container').evaluate((el) => el.clientWidth);
          return Math.abs(engine - outer);
        }).toBeLessThanOrEqual(1);
        const box = (await page.locator('#epub-page').boundingBox())!;
        measured.push(box.width);
        textWidths.push(await page.frameLocator('#epub-viewer iframe').locator('p').first()
          .evaluate((el) => el.getBoundingClientRect().width));
        expect(box.x + box.width / 2).toBeCloseTo(width / 2, 0);
        await page.getByRole('button', { name: 'Close view settings' }).click();
        await page.keyboard.press('Escape');
        await expect(page.getByTestId('reader-chrome-top')).toHaveAttribute('aria-hidden', 'true');
        await expect(page.getByTestId('reader-chrome-top')).toHaveCSS('opacity', '0');
        await expect(page.getByTestId('reader-chrome-bottom')).toHaveCSS('opacity', '0');
        if (process.env.READER_MARGIN_EVIDENCE && width === 1920 && theme === 'light') {
          const dir = path.join(__dirname, 'visual-evidence', 'reader-margin-presets');
          mkdirSync(dir, { recursive: true });
          await page.screenshot({ path: path.join(dir, `${process.env.READER_MARGIN_EVIDENCE}-${preset.toLowerCase()}.png`) });
        }
      }
      await testInfo.attach('margin-widths', { body: JSON.stringify({ width, theme, presets, measured, textWidths }), contentType: 'application/json' });
      console.log(`Margin widths ${width}px ${theme}: ${measured.map((v) => v.toFixed(2)).join(', ')}`);
      expect(measured[0] - measured[1]).toBeGreaterThan(20);
      expect(measured[1] - measured[2]).toBeGreaterThan(20);
      expect(textWidths[0] - textWidths[1]).toBeGreaterThan(10);
      expect(textWidths[1] - textWidths[2]).toBeGreaterThan(10);
      await page.reload();
      await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
      await expect.poll(async () => (await page.locator('#epub-page').boundingBox())!.width).toBeCloseTo(measured[2], 0);
    });
  }
}
