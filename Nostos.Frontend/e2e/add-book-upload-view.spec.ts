/**
 * Visual evidence for the focused upload view (#1275).
 *
 * A real file is sent through the production Angular build to a real backend,
 * with the browser's upload pipe slowed over CDP so the view is still on screen
 * when the shot is taken. Desktop dark and phone 390 are captured at 1:1; the
 * confirmation itself is covered by the component specs.
 */
import { expect, test, type CDPSession, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';

import { loadFixture } from './support/fixture';

const SOURCE_PATH = '/tmp/claude-1000/oc1275/add-book-upload-view.txt';
const SOURCE_BYTES = 8 * 1024 * 1024;
const UPLOAD_BYTES_PER_SECOND = 256 * 1024;

function ensureSourceFile(): void {
  mkdirSync(path.dirname(SOURCE_PATH), { recursive: true });
  const block = Buffer.alloc(1024 * 1024);
  let state = 1275;
  for (let index = 0; index < block.length; index += 1) {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    block[index] = state >>> 24;
  }

  const parts: Buffer[] = [];
  for (let written = 0; written < SOURCE_BYTES; written += block.length) {
    block[0] = (block[0] + 1) & 0xff;
    parts.push(Buffer.from(block));
  }
  writeFileSync(SOURCE_PATH, Buffer.concat(parts, SOURCE_BYTES));
}

async function throttle(page: Page): Promise<CDPSession> {
  const session = await page.context().newCDPSession(page);
  await session.send('Network.enable');
  await session.send('Network.emulateNetworkConditions', {
    offline: false,
    latency: 20,
    downloadThroughput: 20 * 1024 * 1024,
    uploadThroughput: UPLOAD_BYTES_PER_SECOND,
  });
  return session;
}

async function openUploadView(page: Page, baseUrl: string, title: string): Promise<void> {
  await page.goto(`${baseUrl}/library`, { waitUntil: 'domcontentloaded' });

  const add = page.locator('.library-add-button');
  await expect(add).toBeVisible();
  await add.click();

  const dialog = page.getByRole('dialog').first();
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Add manually' }).click();
  await dialog.locator('input[name="title"]').fill(title);
  await dialog
    .locator('input[type="file"][accept*=".epub"]')
    .first()
    .setInputFiles(SOURCE_PATH);
  await dialog.getByRole('button', { name: 'Add to Library' }).click();

  const view = page.getByTestId('add-book-upload-view');
  await expect(view).toBeVisible();
  await expect(view).toContainText(title);
  await expect(view).toContainText('add-book-upload-view.txt');

  await expect
    .poll(
      async () => {
        const text = (await view.textContent()) ?? '';
        const match = /Uploading… (\d+)%/.exec(text);
        return match ? Number(match[1]) : 0;
      },
      { timeout: 90_000 },
    )
    .toBeGreaterThanOrEqual(10);
}

test.describe('add-book upload view (#1275)', () => {
  test.use({ serviceWorkers: 'block' });

  test('captures desktop dark and phone 390', async ({ page, context }) => {
    test.setTimeout(240_000);
    const { baseUrl } = loadFixture();
    ensureSourceFile();

    const evidence = path.join(__dirname, 'visual-evidence');
    mkdirSync(evidence, { recursive: true });

    await page.setViewportSize({ width: 1280, height: 900 });
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.addInitScript(() => localStorage.setItem('nostos.theme', 'dark'));
    await throttle(page);
    await openUploadView(page, baseUrl, `Upload view desktop ${Date.now()}`);
    await page.screenshot({
      path: path.join(evidence, 'add-book-upload-view-desktop-dark.png'),
    });

    const phone = await context.newPage();
    await phone.setViewportSize({ width: 390, height: 844 });
    await phone.emulateMedia({ colorScheme: 'dark' });
    await phone.addInitScript(() => localStorage.setItem('nostos.theme', 'dark'));
    await throttle(phone);
    await openUploadView(phone, baseUrl, `Upload view phone ${Date.now()}`);
    await phone.screenshot({
      path: path.join(evidence, 'add-book-upload-view-phone-390.png'),
    });
  });
});
