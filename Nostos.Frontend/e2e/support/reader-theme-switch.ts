/**
 * In-book Light/Dark switch (issue #651), exercised in a real engine.
 *
 * The switch lives in the reader's View settings panel and drives the one
 * app-wide ThemeService. A mid-read switch must be colour-only: the EPUB keeps
 * its rendition and reading position, the PDF keeps its document and page, and
 * both surfaces follow the new theme. jsdom has no epub.js iframe or pdf.js
 * canvas, so this is checked here. Shared by the desktop and 390px specs; the
 * screenshots are the PR's visual evidence.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './fixture';

const EPUB_BYTES = readFileSync(path.join(__dirname, '..', 'assets', 'tiny.epub'));
const EVIDENCE_DIR = path.join(__dirname, '..', 'visual-evidence');

/** A minimal valid two-page PDF, built in code so no binary fixture is needed. */
function twoPagePdf(): Buffer {
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 420 595] /Contents 4 0 R /Resources << /Font << /F1 7 0 R >> >> >>',
    null,
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 420 595] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>',
    null,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ];
  const stream = (text: string) => {
    const body = `BT /F1 18 Tf 60 500 Td (${text}) Tj ET`;
    return `<< /Length ${body.length} >>\nstream\n${body}\nendstream`;
  };
  objects[3] = stream('Page one of the theme fixture');
  objects[5] = stream('Page two of the theme fixture');

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

async function seedBook(baseUrl: string, title: string, bytes: Buffer, fileName: string, type: string) {
  const created = await apiPost<any>(baseUrl, '/api/books/', { type: 'ebook', title, author: 'E2E' });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  if (!bookId) throw new Error(`No book id in create response: ${JSON.stringify(created)}`);
  const form = new FormData();
  form.append('file', new Blob([bytes], { type }), fileName);
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`file upload -> ${res.status}: ${await res.text()}`);
  return bookId as string;
}

async function openViewSettings(page: Page) {
  await page.locator('[data-testid="typo-toggle"]').click();
  await expect(page.locator('[data-testid="reader-appearance"]')).toBeVisible();
}

async function chooseTheme(page: Page, label: 'Light' | 'Dark') {
  await page.locator('[data-testid="reader-appearance"] .typo-opt', { hasText: label }).click();
  await expect(page.locator('[data-testid="reader-appearance"] .typo-opt', { hasText: label }))
    .toHaveAttribute('aria-pressed', 'true');
}

async function htmlTheme(page: Page) {
  return page.evaluate(() => document.documentElement.getAttribute('data-theme') ?? 'light');
}

async function snap(page: Page, name: string) {
  await page.screenshot({ path: path.join(EVIDENCE_DIR, `${name}.png`) });
}

export function readerThemeSwitchSpecs(viewportName: 'desktop' | 'mobile') {
  test.describe.configure({ mode: 'serial' });
  test.use({ serviceWorkers: 'block' });

  const run = newRunId();
  let baseUrl = '';
  let epubId = '';
  let pdfId = '';

  test.beforeAll(async () => {
    baseUrl = loadFixture().baseUrl;
    epubId = await seedBook(baseUrl, `Theme EPUB ${run}`, EPUB_BYTES, 'book.epub', 'application/epub+zip');
    pdfId = await seedBook(baseUrl, `Theme PDF ${run}`, twoPagePdf(), 'book.pdf', 'application/pdf');
  });

  test('EPUB: Light ↔ Dark from View settings keeps the rendition and position', async ({ page }) => {
    await page.goto(`${baseUrl}/read/${epubId}`, { waitUntil: 'domcontentloaded' });
    const frame = page.frameLocator('#epub-viewer iframe');
    await frame.locator('body').first().waitFor({ timeout: 45_000 });
    await page.waitForTimeout(800);
    const progress = page.locator('.progress-display');
    const before = (await progress.textContent())?.trim();
    const iframeSrcdoc = await page.locator('#epub-viewer iframe').evaluate((el) => (el as HTMLIFrameElement).contentDocument?.URL);

    await openViewSettings(page);
    expect(await htmlTheme(page)).toBe('light');
    await snap(page, `reader-theme-epub-panel-light-${viewportName}`);

    await chooseTheme(page, 'Dark');
    expect(await htmlTheme(page)).toBe('dark');
    await expect(frame.locator('body')).toHaveClass(/nostos-dark/);
    await snap(page, `reader-theme-epub-panel-dark-${viewportName}`);

    // Same document (no re-render), same position, and the choice is the
    // Settings one: it persists under the same key.
    expect(await page.locator('#epub-viewer iframe').evaluate((el) => (el as HTMLIFrameElement).contentDocument?.URL))
      .toBe(iframeSrcdoc);
    expect((await progress.textContent())?.trim()).toBe(before);
    expect(await page.evaluate(() => localStorage.getItem('nostos.theme'))).toBe('dark');

    await chooseTheme(page, 'Light');
    await expect(frame.locator('body')).toHaveClass(/nostos-light/);
    expect((await progress.textContent())?.trim()).toBe(before);

    await page.reload({ waitUntil: 'domcontentloaded' });
    expect(await htmlTheme(page)).toBe('light');
  });

  test('PDF: Light ↔ Dark keeps the document and page; page colours follow', async ({ page }) => {
    await page.goto(`${baseUrl}/read/${pdfId}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });
    await page.waitForTimeout(800);
    const progress = page.locator('.progress-display');
    const before = (await progress.textContent())?.trim();
    // Mark the live viewer node: a reload/re-create would drop the marker.
    await page.locator('#viewerContainer').evaluate((el) => el.setAttribute('data-e2e-marker', '1'));

    await openViewSettings(page);
    await snap(page, `reader-theme-pdf-panel-light-${viewportName}`);
    await chooseTheme(page, 'Dark');
    expect(await htmlTheme(page)).toBe('dark');
    await expect(page.locator('.pdf-container')).toHaveClass(/inverted/);
    await expect(page.locator('.typo-label', { hasText: 'Page colours' })).toBeVisible();
    await snap(page, `reader-theme-pdf-panel-dark-${viewportName}`);

    await expect(page.locator('#viewerContainer')).toHaveAttribute('data-e2e-marker', '1');
    expect((await progress.textContent())?.trim()).toBe(before);

    await chooseTheme(page, 'Light');
    await expect(page.locator('.pdf-container')).not.toHaveClass(/inverted/);
    await expect(page.locator('#viewerContainer')).toHaveAttribute('data-e2e-marker', '1');
    expect((await progress.textContent())?.trim()).toBe(before);
  });
}
