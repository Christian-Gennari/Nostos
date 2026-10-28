/**
 * Shared body of the saved-position restore specs (#1271), run at desktop
 * width by `reader-restore-position.spec.ts` and at phone width by
 * `mobile-reader-restore-position.spec.ts`.
 *
 * The defect needs a long spine item: on open the reader box changes size
 * several times, and each re-layout re-displays the section's last seen
 * location. In a short section that costs a page; deep inside one very large
 * XHTML file it lands the reader at that file's start. The fixture is built
 * here so its shape is visible: a short opening chapter, then one chapter
 * holding several subchapters and 1,000 paragraphs, with TOC entries pointing
 * at fragments inside it.
 */
import { expect, type Page } from '@playwright/test';
import JSZip from 'jszip';

import { apiGet, apiPost, apiPut, loadFixture } from './fixture';

const PARAGRAPHS = 1000;
const SUBCHAPTERS = 5;
/** The paragraph the saved position points at, near the end of the long file. */
export const TARGET_PARAGRAPH = 900;

const xhtml = (title: string, body: string) =>
  `<?xml version="1.0" encoding="utf-8"?>\n` +
  `<html xmlns="http://www.w3.org/1999/xhtml"><head><title>${title}</title></head>` +
  `<body>${body}</body></html>`;

/**
 * The long chapter's body, and the element index (among the body's children)
 * of the target paragraph, which is what its CFI step is built from.
 */
function longChapter(): { body: string; targetChild: number } {
  const parts: string[] = [];
  let targetChild = -1;
  const per = PARAGRAPHS / SUBCHAPTERS;
  for (let p = 1; p <= PARAGRAPHS; p++) {
    if ((p - 1) % per === 0) {
      const sub = (p - 1) / per + 1;
      parts.push(`<h2 id="s${sub}">Subchapter ${sub}</h2>`);
    }
    if (p === TARGET_PARAGRAPH) targetChild = parts.length;
    parts.push(
      `<p id="p${p}">Paragraph ${p}. The sanatorium kept its hours and its temperatures, ` +
        `and the days ran together the way long afternoons do on a terrace above the valley.</p>`,
    );
  }
  return { body: parts.join(''), targetChild };
}

export async function buildLongEpub(): Promise<{ bytes: Buffer; targetCfi: string }> {
  const zip = new JSZip();
  zip.file('mimetype', 'application/epub+zip', { compression: 'STORE' });
  zip.file(
    'META-INF/container.xml',
    `<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">` +
      `<rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>`,
  );
  const { body, targetChild } = longChapter();
  zip.file('OEBPS/ch1.xhtml', xhtml('Opening', '<h1>Opening</h1><p>A short opening chapter.</p>'));
  zip.file('OEBPS/ch2.xhtml', xhtml('Long chapter', body));
  const navPoints = [
    `<navPoint id="n1" playOrder="1"><navLabel><text>Opening</text></navLabel><content src="ch1.xhtml"/></navPoint>`,
    ...Array.from({ length: SUBCHAPTERS }, (_, i) =>
      `<navPoint id="n${i + 2}" playOrder="${i + 2}"><navLabel><text>Subchapter ${i + 1}</text></navLabel>` +
        `<content src="ch2.xhtml#s${i + 1}"/></navPoint>`,
    ),
  ];
  zip.file(
    'OEBPS/toc.ncx',
    `<?xml version="1.0"?><ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">` +
      `<head><meta name="dtb:uid" content="nostos-e2e-long"/></head><docTitle><text>Long</text></docTitle>` +
      `<navMap>${navPoints.join('')}</navMap></ncx>`,
  );
  zip.file(
    'OEBPS/content.opf',
    `<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" unique-identifier="uid" version="2.0">` +
      `<metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Long chapter</dc:title>` +
      `<dc:identifier id="uid">nostos-e2e-long</dc:identifier><dc:language>en</dc:language></metadata>` +
      `<manifest><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>` +
      `<item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml"/>` +
      `<item id="ch2" href="ch2.xhtml" media-type="application/xhtml+xml"/></manifest>` +
      `<spine toc="ncx"><itemref idref="ch1"/><itemref idref="ch2"/></spine></package>`,
  );
  const bytes = await zip.generateAsync({ type: 'nodebuffer', mimeType: 'application/epub+zip' });
  // Spine step 4 is the second itemref; body is the html element's second
  // child (/4); its children are numbered 2, 4, 6, … in CFI steps.
  const targetCfi = `epubcfi(/6/4[ch2]!/4/${2 * (targetChild + 1)}/1:0)`;
  return { bytes, targetCfi };
}

export async function seedBook(baseUrl: string, bytes: Buffer): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title: `Restore ${Date.now()}`,
    author: 'E2E',
  });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([new Uint8Array(bytes)], { type: 'application/epub+zip' }), 'long.epub');
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}`);
  return bookId;
}

/** Whether the target paragraph overlaps the page epub.js is showing. */
async function targetOnVisiblePage(page: Page): Promise<{ onPage: boolean; detail: string }> {
  const stage = await page.locator('#epub-page > .epub-container').evaluate((el) => ({
    left: el.scrollLeft,
    width: el.clientWidth,
  }));
  const rect = await page
    .frameLocator('#epub-viewer iframe')
    .locator(`#p${TARGET_PARAGRAPH}`)
    .evaluate((p) => {
      const r = p.getBoundingClientRect();
      return { left: r.left, right: r.right };
    });
  const onPage = rect.right > stage.left && rect.left < stage.left + stage.width;
  return {
    onPage,
    detail: `paragraph at ${Math.round(rect.left)}–${Math.round(rect.right)}, page ${stage.left}–${stage.left + stage.width}`,
  };
}

/**
 * Seed a saved position near the end of the long chapter, open the reader,
 * optionally force resizes during the opening, wait for quiet, and check the
 * page and the saved progress.
 */
export async function expectRestoreLandsOnSavedPage(
  page: Page,
  resizesAtMs: number[] = [],
): Promise<void> {
  const { baseUrl } = loadFixture();
  const { bytes, targetCfi } = await buildLongEpub();
  const bookId = await seedBook(baseUrl, bytes);
  await apiPut(baseUrl, `/api/books/${bookId}/progress`, { location: targetCfi, percentage: 93 });

  const writes: string[] = [];
  page.on('request', (request) => {
    if (request.method() === 'PUT' && request.url().endsWith(`/api/books/${bookId}/progress`)) {
      writes.push(request.postData() ?? '');
    }
  });

  const viewport = page.viewportSize()!;
  await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  const frame = page.frameLocator('#epub-viewer iframe');
  await frame.locator('p').first().waitFor({ timeout: 45_000 });
  const opened = Date.now();
  for (const at of resizesAtMs) {
    await page.waitForTimeout(Math.max(0, at - (Date.now() - opened)));
    await page.setViewportSize({ width: viewport.width, height: viewport.height - 40 });
    await page.waitForTimeout(30);
    await page.setViewportSize(viewport);
  }

  // Two seconds of quiet after the opening churn.
  await page.waitForTimeout(2_500);
  await frame.locator(`#p${TARGET_PARAGRAPH}`).waitFor({ state: 'attached' });
  const landed = await targetOnVisiblePage(page);
  expect(landed.onPage, landed.detail).toBe(true);

  const saved = await apiGet<any>(baseUrl, `/api/books/${bookId}`);
  const book = saved?.book ?? saved;
  expect(book.lastLocation).toBe(targetCfi);
  expect(writes, 'opening the book writes no progress').toEqual([]);
}
