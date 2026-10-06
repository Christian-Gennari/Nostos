/**
 * Real-engine smoke for unified in-book search (#761).
 *
 * Uses the production Angular build, real SelfHosted backend, epub.js and
 * PDF.js. Unit tests own edge-case breadth; this file proves the format
 * adapters and shared shell work together in an actual browser.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';

const EPUB_BYTES = readFileSync(path.join(__dirname, 'assets', 'tiny.epub'));

async function seedBook(
  baseUrl: string,
  title: string,
  bytes: Buffer,
  fileName: string,
  mediaType: string,
): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title,
    author: 'E2E',
  });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  if (!bookId) throw new Error(`No book id in create response: ${JSON.stringify(created)}`);

  const form = new FormData();
  form.append('file', new Blob([bytes], { type: mediaType }), fileName);
  const response = await fetch(`${baseUrl}/api/books/${bookId}/file`, {
    method: 'POST',
    body: form,
  });
  if (!response.ok) throw new Error(`file upload -> ${response.status}: ${await response.text()}`);
  return bookId as string;
}

function textPdf(pageTexts: string[]): Buffer {
  const objects: Array<string | null> = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    null,
  ];
  const kids: number[] = [];

  for (const text of pageTexts) {
    const pageObject = objects.length + 1;
    const streamObject = pageObject + 1;
    kids.push(pageObject);
    objects.push(
      `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 420 595] /Contents ${streamObject} 0 R /Resources << /Font << /F1 999 0 R >> >> >>`,
    );
    const body = text ? `BT /F1 18 Tf 60 500 Td (${text}) Tj ET` : '';
    objects.push(`<< /Length ${body.length} >>\nstream\n${body}\nendstream`);
  }

  const fontObject = objects.length + 1;
  objects.push('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>');
  objects[1] = `<< /Type /Pages /Kids [${kids.map((n) => `${n} 0 R`).join(' ')}] /Count ${kids.length} >>`;
  // Replace the placeholder font ref after its final object number is known.
  for (let i = 2; i < objects.length - 1; i += 2) {
    objects[i] = objects[i]!.replace('999 0 R', `${fontObject} 0 R`);
  }

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

async function waitForSearchReady(page: Page): Promise<void> {
  await expect(page.getByTestId('reader-search-toggle')).toBeEnabled({ timeout: 45_000 });
}

test.describe('unified reader search', () => {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  test('EPUB: iframe Ctrl+F opens shared search, finds text, and Escape clears the mark', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedBook(
      baseUrl,
      `Search EPUB ${run}`,
      EPUB_BYTES,
      'search.epub',
      'application/epub+zip',
    );

    await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    const frame = page.frameLocator('#epub-viewer iframe');
    const firstParagraph = frame.locator('p').first();
    await firstParagraph.waitFor({ timeout: 45_000 });
    await waitForSearchReady(page);

    const paragraph = (await firstParagraph.innerText()).replace(/\s+/g, ' ').trim();
    const query = paragraph.split(' ').find((word) => word.length >= 5) ?? paragraph.split(' ')[0];
    if (!query) throw new Error('EPUB fixture has no searchable paragraph text');

    await frame.locator('body').press('Control+f');
    const panel = page.getByTestId('reader-search-panel');
    const input = panel.locator('.reader-search-input');
    await expect(panel).toBeVisible();
    await expect(input).toBeFocused();

    await input.fill(query);
    await expect(panel.locator('.reader-search-status')).toHaveText(/\d+ of \d+/, { timeout: 30_000 });
    await expect(page.locator('[ref="epubjs-search-current"]')).toHaveCount(1);

    await page.keyboard.press('Escape');
    await expect(panel).toHaveCount(0);
    await expect(page.locator('[ref="epubjs-search-current"]')).toHaveCount(0);
  });

  test('PDF: shared search uses PDF.js counts and next wraps through real matches', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const needle = 'Reader search needle';
    const bookId = await seedBook(
      baseUrl,
      `Search PDF ${run}`,
      textPdf([`${needle} one`, `${needle} two`]),
      'search.pdf',
      'application/pdf',
    );

    await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });
    await waitForSearchReady(page);

    const toggle = page.getByTestId('reader-search-toggle');
    await page.locator('.pdf-container').click({ position: { x: 160, y: 160 } });
    await expect(page.getByTestId('reader-chrome-top')).toBeVisible();
    await toggle.click();
    const panel = page.getByTestId('reader-search-panel');
    const input = panel.locator('.reader-search-input');
    await expect(input).toBeFocused();

    await input.fill(needle);
    const status = panel.locator('.reader-search-status');
    await expect(status).toHaveText('1 of 2', { timeout: 30_000 });

    await panel.getByRole('button', { name: 'Next match' }).click();
    await expect(status).toHaveText('2 of 2');
    await panel.getByRole('button', { name: 'Next match' }).click();
    await expect(status).toHaveText('1 of 2');

    await page.keyboard.press('Escape');
    await expect(panel).toHaveCount(0);
    await expect(toggle).toBeFocused();
  });

  test('textless PDF never exposes an active search surface', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedBook(
      baseUrl,
      `Searchless PDF ${run}`,
      textPdf(['']),
      'scan.pdf',
      'application/pdf',
    );

    await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });
    // Capability detection samples this single page. Give its async text-content
    // read a short settle, then prove both click and keyboard surfaces stay shut.
    await page.waitForTimeout(800);

    const toggle = page.getByTestId('reader-search-toggle');
    await page.locator('.pdf-container').click({ position: { x: 160, y: 160 } });
    await expect(page.getByTestId('reader-chrome-top')).toBeVisible();
    await expect(toggle).toBeDisabled();
    await page.keyboard.press('Control+f');
    await expect(page.getByTestId('reader-search-panel')).toHaveCount(0);
  });
});
