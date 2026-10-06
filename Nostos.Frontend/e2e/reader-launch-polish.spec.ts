/** Real-engine regression coverage for the #759 visual follow-up. */
import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { apiPost, loadFixture } from './support/fixture';

const EPUB = readFileSync(path.join(__dirname, 'assets', 'tiny.epub'));
const viewports = [
  { width: 320, height: 740 },
  { width: 390, height: 844 },
  { width: 768, height: 1024 },
  { width: 1440, height: 900 },
  { width: 844, height: 390 },
];

test.use({ serviceWorkers: 'block' });
let bookId: string;
test.beforeAll(async () => {
  const { baseUrl } = loadFixture();
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title: 'A long book title that still needs a visible home on a phone',
    author: 'A reader’s favourite author',
  });
  bookId = created.book?.id ?? created.bookId ?? created.id;
  const form = new FormData();
  form.append('file', new Blob([EPUB], { type: 'application/epub+zip' }), 'polish.epub');
  const uploaded = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  expect(uploaded.ok).toBe(true);
});

for (const viewport of viewports) {
  for (const theme of ['light', 'dark']) {
    test(`${viewport.width}×${viewport.height} ${theme}: margins reveal stable controls and settings fit`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await page.addInitScript((value) => localStorage.setItem('nostos.theme', value), theme);
      // Cloud adds Feedback, so this exercises the most crowded header.
      await page.route('**/api/auth/session', (route) => route.fulfill({ json: { authenticated: true, accountState: 'Active', account: { id: 'reader-polish', displayName: 'Reader', email: null } } }));
      await page.route('**/api/cloud/onboarding/**', (route) => route.fulfill({ json: { state: 'ready', ready: true, subscriptionStatus: 'Active', canCheckout: false, canCheckSubscription: false, canManageSubscription: false, canRetry: false, selectedOffer: null } }));
      await page.route('**/api/runtime/capabilities', async (route) => {
        const response = await route.fetch();
        const capabilities = await response.json();
        await route.fulfill({ response, json: {
          ...capabilities,
          deploymentMode: 'Cloud',
          feedbackUrl: 'https://nostos.page/feedback',
        } });
      });
      await page.goto(`${loadFixture().baseUrl}/read/${bookId}`);
      const frame = page.frameLocator('#epub-viewer iframe');
      await frame.locator('p').first().waitFor({ timeout: 45_000 });
      await expect(page.locator('.loading-overlay')).toHaveClass(/is-hidden/);
      const chrome = page.getByTestId('reader-chrome-top');
      await expect(chrome).toHaveAttribute('aria-hidden', 'true');
      const before = await page.locator('#epub-page').boundingBox();
      const progress = await page.locator('.progress-display').textContent();
      // A real click on the outer margin must reveal controls too.
      await page.locator('#epub-viewer').click({ position: { x: viewport.width / 2, y: 12 } });
      await expect(chrome).toBeVisible();
      expect(await page.locator('#epub-page').boundingBox()).toEqual(before);
      expect(await page.locator('.progress-display').textContent()).toBe(progress);
      await expect(page.locator('.reader-book-title')).toBeVisible();
      const heading = await page.locator('.reader-heading').boundingBox();
      const tools = await page.locator('.reader-header-tools').boundingBox();
      expect(heading!.width).toBeGreaterThan(24);
      expect(heading!.x + heading!.width).toBeLessThanOrEqual(tools!.x);
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(viewport.width);
      if (viewport.height > 500) {
        const top = await chrome.boundingBox();
        const bottom = await page.getByTestId('reader-chrome-bottom').boundingBox();
        expect(before!.y).toBeGreaterThanOrEqual(top!.y + top!.height);
        expect(before!.y + before!.height).toBeLessThanOrEqual(bottom!.y);
      }
      await page.getByRole('button', { name: 'View settings', exact: true }).click();
      const settings = page.getByRole('dialog', { name: 'View settings' });
      await expect(settings).toBeVisible();
      const box = await settings.boundingBox();
      expect(box!.x).toBeGreaterThanOrEqual(0);
      expect(box!.x + box!.width).toBeLessThanOrEqual(viewport.width);
      expect(box!.y + box!.height).toBeLessThanOrEqual(viewport.height);
      await page.getByRole('button', { name: 'Close view settings' }).click();
      await expect(settings).toHaveCount(0);
      await page.keyboard.press('Escape');
      await expect(chrome).toHaveAttribute('aria-hidden', 'true');
      expect(await page.locator('#epub-page').boundingBox()).toEqual(before);
    });
  }
}

function textPdf(): Buffer {
  const text = 'BT /F1 18 Tf 60 500 Td (A readable PDF page) Tj ET';
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 420 595] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
    `<< /Length ${text.length} >>\nstream\n${text}\nendstream`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ];
  let pdf = '%PDF-1.4\n';
  const offsets = objects.map((body, i) => {
    const offset = pdf.length;
    pdf += `${i + 1} 0 obj\n${body}\nendobj\n`;
    return offset;
  });
  const xref = pdf.length;
  pdf += `xref\n0 6\n0000000000 65535 f \n`;
  pdf += offsets.map((offset) => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf, 'latin1');
}

test('PDF: a real tap on the focusable text layer reveals controls without moving the page', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook', title: 'The printed edition', author: 'Nostos',
  });
  const id = created.book?.id ?? created.bookId ?? created.id;
  const form = new FormData();
  form.append('file', new Blob([textPdf()], { type: 'application/pdf' }), 'polish.pdf');
  expect((await fetch(`${baseUrl}/api/books/${id}/file`, { method: 'POST', body: form })).ok).toBe(true);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`${baseUrl}/read/${id}`);
  const layer = page.locator('#viewerContainer .textLayer').first();
  await expect(layer).toHaveAttribute('tabindex', '0', { timeout: 45_000 });
  const chrome = page.getByTestId('reader-chrome-top');
  await expect(chrome).toHaveAttribute('aria-hidden', 'true');
  const before = await page.locator('#viewer .page').first().boundingBox();
  await layer.click({ position: { x: 195, y: 12 } });
  await expect(chrome).toBeVisible();
  expect(await page.locator('#viewer .page').first().boundingBox()).toEqual(before);
  const header = await chrome.boundingBox();
  expect(before!.y).toBeGreaterThanOrEqual(header!.y + header!.height);
  await page.getByRole('button', { name: 'View settings', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'View settings' })).toBeVisible();
});

test('320px touch selection keeps Copy, Ask Nostos, Cancel, Note, and Highlight inside the viewport', async ({ browser }) => {
  const context = await browser.newContext({
    viewport: { width: 320, height: 740 }, isMobile: true, hasTouch: true,
    serviceWorkers: 'block',
  });
  const page = await context.newPage();
  try {
    await page.addInitScript(() => localStorage.setItem('nostos.library.preferences', JSON.stringify({
      viewMode: 'grid', sort: 'lastread', pageSize: 20, sidebarExpanded: true,
      groupByWork: true, assistantEnabled: true, assistantVoiceEnabled: true,
    })));
    await page.route('**/api/assistant/status', (route) => route.fulfill({ json: { available: true } }));
    await page.goto(`${loadFixture().baseUrl}/read/${bookId}`);
    const frame = page.frameLocator('#epub-viewer iframe');
    await frame.locator('p').first().waitFor({ timeout: 45_000 });
    await page.locator('#epub-viewer').click({ position: { x: 160, y: 12 } });
    await page.getByRole('button', { name: 'Notes & Highlights', exact: true }).click();
    await page.getByTestId('reader-highlight-toggle').click();
    await frame.locator('p').first().evaluate((paragraph) => {
      const doc = paragraph.ownerDocument;
      const range = doc.createRange();
      range.selectNodeContents(paragraph);
      const selection = doc.getSelection()!;
      selection.removeAllRanges();
      selection.addRange(range);
      doc.dispatchEvent(new Event('touchend'));
    });
    await expect(page.getByTestId('selection-bar')).toBeVisible();
    for (const action of ['copy', 'ask-nostos', 'cancel', 'add-note', 'highlight']) {
      const control = page.getByTestId(`selection-${action}`);
      await expect(control).toBeVisible();
      const box = await control.boundingBox();
      expect(box!.x).toBeGreaterThanOrEqual(0);
      expect(box!.x + box!.width).toBeLessThanOrEqual(320);
      expect(box!.y + box!.height).toBeLessThanOrEqual(740);
    }
    const note = await page.getByTestId('selection-add-note').boundingBox();
    const highlight = await page.getByTestId('selection-highlight').boundingBox();
    expect(note!.y).toBe(highlight!.y);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(320);
    await expect(page.getByTestId('assistant-trigger')).toHaveCount(0);
    await page.getByTestId('selection-ask-nostos').click();
    await expect(page.getByTestId('assistant-panel')).toBeVisible();
  } finally {
    await context.close();
  }
});
