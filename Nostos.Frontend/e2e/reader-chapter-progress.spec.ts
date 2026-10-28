/**
 * Tap the progress bar to switch between book and chapter progress (#1269),
 * in the real readers: epub.js on a generated three-chapter EPUB and PDF.js on
 * a generated PDF with an outline. Unit tests own the range arithmetic; this
 * proves the bar, the toggle and the reset at a chapter boundary in a browser.
 *
 * `CHAPTER_PROGRESS_EVIDENCE=light|dark` runs it at phone width in that theme
 * and writes screenshots of the bottom bar in each mode to
 * `visual-evidence/1269-chapter-progress/`.
 */
import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import JSZip from 'jszip';

import { apiPost, loadFixture } from './support/fixture';

const EVIDENCE = process.env.CHAPTER_PROGRESS_EVIDENCE
  ? path.join(__dirname, 'visual-evidence', '1269-chapter-progress')
  : null;

async function threeChapterEpub(): Promise<Buffer> {
  const zip = new JSZip();
  zip.file('mimetype', 'application/epub+zip', { compression: 'STORE' });
  zip.file(
    'META-INF/container.xml',
    `<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">` +
      `<rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>`,
  );
  const chapters = ['The Arrival', 'Snow', 'Thunderbolt'];
  chapters.forEach((title, i) => {
    const paragraphs = Array.from(
      { length: 60 },
      (_, p) =>
        `<p>Chapter ${i + 1}, paragraph ${p + 1}. The days ran together the way long afternoons do on a terrace above the valley, and nobody counted them.</p>`,
    ).join('');
    zip.file(
      `OEBPS/ch${i + 1}.xhtml`,
      `<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>${title}</title></head>` +
        `<body><h1>${title}</h1>${paragraphs}</body></html>`,
    );
  });
  zip.file(
    'OEBPS/toc.ncx',
    `<?xml version="1.0"?><ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">` +
      `<head><meta name="dtb:uid" content="chapter-progress"/></head><docTitle><text>Chapters</text></docTitle><navMap>` +
      chapters
        .map((title, i) => `<navPoint id="n${i}" playOrder="${i + 1}"><navLabel><text>${title}</text></navLabel><content src="ch${i + 1}.xhtml"/></navPoint>`)
        .join('') +
      `</navMap></ncx>`,
  );
  zip.file(
    'OEBPS/content.opf',
    `<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" unique-identifier="uid" version="2.0">` +
      `<metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Chapters</dc:title>` +
      `<dc:identifier id="uid">chapter-progress</dc:identifier><dc:language>en</dc:language></metadata>` +
      `<manifest><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>` +
      chapters.map((_, i) => `<item id="ch${i + 1}" href="ch${i + 1}.xhtml" media-type="application/xhtml+xml"/>`).join('') +
      `</manifest><spine toc="ncx">` +
      chapters.map((_, i) => `<itemref idref="ch${i + 1}"/>`).join('') +
      `</spine></package>`,
  );
  return zip.generateAsync({ type: 'nodebuffer' });
}

/** A 12-page PDF whose outline starts chapters on pages 1, 5 and 9. */
function outlinedPdf(): Buffer {
  const pages = 12;
  const starts = [1, 5, 9];
  const objects: string[] = [];
  const add = (body: string) => objects.push(body); // object number = index + 1
  const pageObj = (n: number) => 4 + (n - 1) * 2;
  const fontObj = 4 + pages * 2;
  const firstOutlineItem = fontObj + 1;

  add(`<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R >>`);
  add(`<< /Type /Pages /Kids [${Array.from({ length: pages }, (_, i) => `${pageObj(i + 1)} 0 R`).join(' ')}] /Count ${pages} >>`);
  add(`<< /Type /Outlines /First ${firstOutlineItem} 0 R /Last ${firstOutlineItem + starts.length - 1} 0 R /Count ${starts.length} >>`);
  for (let n = 1; n <= pages; n++) {
    const text = `Page ${n}`;
    const body = `BT /F1 24 Tf 60 500 Td (${text}) Tj ET`;
    add(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 420 595] /Contents ${pageObj(n) + 1} 0 R /Resources << /Font << /F1 ${fontObj} 0 R >> >> >>`);
    add(`<< /Length ${body.length} >>\nstream\n${body}\nendstream`);
  }
  add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>');
  starts.forEach((start, i) => {
    const self = firstOutlineItem + i;
    const links = [i > 0 ? `/Prev ${self - 1} 0 R` : '', i < starts.length - 1 ? `/Next ${self + 1} 0 R` : '']
      .filter(Boolean)
      .join(' ');
    add(`<< /Title (Chapter ${i + 1}) /Parent 3 0 R ${links} /Dest [${pageObj(start)} 0 R /Fit] >>`);
  });

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

async function seedBook(baseUrl: string, bytes: Buffer, fileName: string, mediaType: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title: `Chapter progress ${Date.now()}`,
    author: 'E2E',
  });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([new Uint8Array(bytes)], { type: mediaType }), fileName);
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}`);
  return bookId;
}

async function revealPager(page: Page): Promise<void> {
  const bar = page.getByTestId('reader-chrome-bottom');
  await expect(async () => {
    if ((await bar.getAttribute('aria-hidden')) === 'true') {
      const view = page.viewportSize()!;
      await page.mouse.click(view.width / 2, view.height / 2);
    }
    await expect(bar).not.toHaveAttribute('aria-hidden', 'true', { timeout: 1_500 });
  }).toPass({ timeout: 20_000 });
}

/** The bar's fill as a share of its track, 0–100. */
async function fill(page: Page): Promise<number> {
  return page.locator('.reader-progress-track').evaluate((track) => {
    const fillEl = track.querySelector('.reader-progress-fill') as HTMLElement;
    return (fillEl.getBoundingClientRect().width / track.getBoundingClientRect().width) * 100;
  });
}

async function shoot(page: Page, name: string): Promise<void> {
  if (!EVIDENCE) return;
  mkdirSync(EVIDENCE, { recursive: true });
  await page.waitForTimeout(250);
  await page
    .getByTestId('reader-chrome-bottom')
    .screenshot({ path: path.join(EVIDENCE, `${name}-390-${process.env.CHAPTER_PROGRESS_EVIDENCE}.png`) });
}

test.use({ serviceWorkers: 'block' });
if (EVIDENCE) test.use({ viewport: { width: 390, height: 844 } });

test.beforeEach(async ({ page }) => {
  // Each test starts in book mode; the choice is remembered reader-wide.
  await page.addInitScript((theme) => {
    if (!sessionStorage.getItem('seeded')) {
      localStorage.removeItem('nostos.reader-progress-scope');
      if (theme) localStorage.setItem('nostos.theme', theme);
      sessionStorage.setItem('seeded', '1');
    }
  }, process.env.CHAPTER_PROGRESS_EVIDENCE ?? null);
});

test('EPUB: the bar switches to the chapter, resets at the next chapter, and is remembered', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, await threeChapterEpub(), 'chapters.epub', 'application/epub+zip');
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
  await revealPager(page);

  const toggle = page.getByTestId('reader-progress-toggle');
  await expect(toggle).toHaveAttribute('aria-pressed', 'false');
  const next = page.getByRole('button', { name: 'Next page' });
  for (let i = 0; i < 3; i++) {
    await next.click();
    await page.waitForTimeout(250);
  }
  await expect.poll(() => fill(page)).toBeGreaterThan(0);
  const book = await fill(page);

  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.progress-display')).toContainText('Chapter progress');
  // A third of the way into chapter 1 of 3 is about three times as far through the chapter.
  const chapter = await fill(page);
  expect(chapter).toBeGreaterThan(book * 2);
  await expect(page.locator('.progress-display')).toContainText(/\d+% of The Arrival/, { timeout: 3_000 });
  await shoot(page, 'epub-chapter-mode');

  // Into chapter 2 from the contents: the chapter bar starts again.
  await page.getByRole('button', { name: 'Contents', exact: true }).first().click();
  await page.locator('.toc-panel .toc-item', { hasText: 'Snow' }).click();
  await expect(page.locator('.progress-display')).toContainText(/\d+% of Snow/, { timeout: 10_000 });
  await revealPager(page);
  expect(await fill(page)).toBeLessThan(5);

  // Remembered across a reload (after the debounced progress write, so the
  // book reopens in chapter 2).
  await page.waitForTimeout(1_500);
  await page.reload();
  await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
  await revealPager(page);
  await expect(page.getByTestId('reader-progress-toggle')).toHaveAttribute('aria-pressed', 'true');

  await page.getByTestId('reader-progress-toggle').click();
  await expect(page.getByTestId('reader-progress-toggle')).toHaveAttribute('aria-pressed', 'false');
  await expect(page.locator('.progress-display')).toContainText(/\d+% • Snow/, { timeout: 3_000 });
  await shoot(page, 'epub-book-mode');
});

test('PDF: the outline places the chapter bar and the page field stays', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedBook(baseUrl, outlinedPdf(), 'outlined.pdf', 'application/pdf');
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  const input = page.locator('.page-input');
  await expect(input).toHaveValue('1', { timeout: 45_000 });
  await revealPager(page);

  const go = async (n: number) => {
    await revealPager(page);
    await input.fill(String(n));
    await input.press('Enter');
    await expect(input).toHaveValue(String(n), { timeout: 10_000 });
    await page.waitForTimeout(400);
  };

  await go(7);
  const toggle = page.getByTestId('reader-progress-toggle');
  await expect(toggle).toHaveAttribute('aria-pressed', 'false');
  expect(await fill(page)).toBeCloseTo(58, 0); // 7 of 12
  await shoot(page, 'pdf-book-mode');

  await toggle.click();
  await expect(toggle).toHaveAttribute('title', 'Chapter progress: tap for book progress');
  expect(await fill(page)).toBeCloseTo(75, 0); // page 3 of chapter 2's 4
  await expect(input).toHaveValue('7');
  await shoot(page, 'pdf-chapter-mode');

  await go(9);
  expect(await fill(page)).toBeCloseTo(25, 0); // chapter 3 starts again
});
