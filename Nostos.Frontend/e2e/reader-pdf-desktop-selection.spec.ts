/**
 * Real-engine proof that the PDF reader opens the selection menu at the text
 * when a desktop mouse selection is released, with highlight mode off — the
 * EPUB reader's #650/#657 behaviour. Production Angular build, real backend, real PDF.js.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiGet, apiPost, loadFixture, newRunId } from './support/fixture';

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

async function dragAcrossText(page: Page): Promise<{ x: number; y: number; width: number; height: number }> {
  const span = page.locator('.textLayer span', { hasText: 'Desktop selection passage' }).first();
  await span.waitFor({ timeout: 60_000 });
  const box = await span.boundingBox();
  if (!box) throw new Error('PDF text span has no box');
  const y = box.y + box.height / 2;
  await page.mouse.move(box.x + 2, y);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2, y, { steps: 8 });
  await page.mouse.move(box.x + box.width - 2, y, { steps: 8 });
  await page.mouse.up();
  return box;
}

test.describe('PDF desktop selection without highlight mode', () => {
  test.use({ serviceWorkers: 'block' });
  const run = newRunId();

  test('mouse release opens the menu at the text, a click away closes it, Highlight saves', async ({ page }) => {
    const { baseUrl } = loadFixture();
    const bookId = await seedBook(
      baseUrl,
      `PDF selection ${run}`,
      textPdf(['Desktop selection passage for the reader']),
      'selection.pdf',
      'application/pdf',
    );

    await page.goto(`${baseUrl}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });

    const menu = page.getByTestId('selection-menu');
    const text = await dragAcrossText(page);
    await expect(menu).toBeVisible();
    await expect(page.getByTestId('selection-bar')).toHaveCount(0);
    await expect(menu.locator('.selected-text')).toContainText('Desktop selection');
    await page.screenshot({ path: test.info().outputPath('pdf-selection-menu.png') });

    // Anchored just below the selected text, overlapping it horizontally.
    const menuBox = await menu.boundingBox();
    if (!menuBox) throw new Error('selection menu has no box');
    expect(menuBox.y).toBeGreaterThanOrEqual(text.y + text.height);
    expect(menuBox.y - (text.y + text.height)).toBeLessThan(24);
    expect(menuBox.x).toBeLessThan(text.x + text.width);
    expect(menuBox.x + menuBox.width).toBeGreaterThan(text.x);

    // A click away from the menu abandons the offer.
    const pageBox = await page.locator('.page').first().boundingBox();
    if (!pageBox) throw new Error('PDF page has no box');
    await page.mouse.click(pageBox.x + pageBox.width / 2, pageBox.y + pageBox.height * 0.8);
    await expect(menu).toHaveCount(0);

    await dragAcrossText(page);
    await expect(menu).toBeVisible();
    await page.getByTestId('selection-highlight').click();
    await expect(menu).toHaveCount(0);
    await expect
      .poll(async () => {
        const notes = await apiGet<any[]>(baseUrl, `/api/books/${bookId}/notes`);
        return notes.map((n) => n.selectedText);
      })
      .toEqual([expect.stringContaining('Desktop selection')]);
  });
});
