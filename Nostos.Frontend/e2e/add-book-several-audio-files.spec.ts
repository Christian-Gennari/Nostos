/**
 * Several audio files chosen together become ONE audiobook (#1373).
 *
 * Real MP3 frames go through the production Angular build to a real backend.
 * The files are chosen out of order, with a "10" before "2", and the book that
 * results must hold them as tracks in natural file-name order. Desktop light and
 * phone 390 dark are captured at 1:1.
 */
import { expect, test, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';

import { loadFixture } from './support/fixture';

const DIR = '/tmp/claude-1000/oc1373';
const FRAME_BYTES = 208; // MPEG-2 Layer III, 64 kbps, 22.05 kHz

function mp3(frames: number): Buffer {
  const frame = Buffer.alloc(FRAME_BYTES);
  Buffer.from([0xff, 0xf3, 0x80, 0xc4]).copy(frame);
  return Buffer.concat(Array.from({ length: frames }, () => frame));
}

function writeFiles(): string[] {
  mkdirSync(DIR, { recursive: true });
  const files: Array<[string, number]> = [
    ['Chapter 10.mp3', 400],
    ['Chapter 1.mp3', 200],
    ['Chapter 2.mp3', 300],
  ];
  return files.map(([name, frames]) => {
    const file = path.join(DIR, name);
    writeFileSync(file, mp3(frames));
    return file;
  });
}

async function chooseFiles(page: Page, baseUrl: string, files: string[]) {
  await page.goto(`${baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await page.locator('.library-add-button').click();
  const dialog = page.getByRole('dialog').first();
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Add manually' }).click();
  await dialog.locator('input[type="file"][multiple]').setInputFiles(files);
  await expect(dialog).toContainText('3 audio files');
  return dialog;
}

test.describe('add-book several audio files (#1373)', () => {
  test.use({ serviceWorkers: 'block' });

  test('three MP3 files become one audiobook in natural order', async ({ page, context }) => {
    const { baseUrl } = loadFixture();
    const files = writeFiles();
    const evidence = path.join(__dirname, 'visual-evidence', '1373-several-audio-files');
    mkdirSync(evidence, { recursive: true });

    const phone = await context.newPage();
    await phone.setViewportSize({ width: 390, height: 844 });
    await phone.emulateMedia({ colorScheme: 'dark' });
    await phone.addInitScript(() => localStorage.setItem('nostos.theme', 'dark'));
    await chooseFiles(phone, baseUrl, files);
    await phone.screenshot({ path: path.join(evidence, 'chosen-phone-390-dark.png') });
    await phone.close();

    await page.setViewportSize({ width: 1280, height: 900 });
    await page.emulateMedia({ colorScheme: 'light' });
    await page.addInitScript(() => localStorage.setItem('nostos.theme', 'light'));
    const dialog = await chooseFiles(page, baseUrl, files);
    const title = `Several files ${Date.now()}`;
    await dialog.locator('input[name="title"]').fill(title);
    await page.screenshot({ path: path.join(evidence, 'chosen-desktop-light.png') });

    const uploaded = page.waitForResponse(
      (r) => /\/api\/books\/[^/]+\/tracks$/.test(r.url()) && r.request().method() === 'POST',
    );
    await dialog.getByRole('button', { name: 'Add to Library' }).click();
    const response = await uploaded;
    expect(response.status()).toBe(200);
    const id = /\/api\/books\/([^/]+)\/tracks$/.exec(response.url())![1];

    const book = await (await page.request.get(`${baseUrl}/api/books/${id}`)).json();
    expect(book.type).toBe('audiobook');
    expect(book.tracks.map((t: { title: string }) => t.title)).toEqual([
      'Chapter 1',
      'Chapter 2',
      'Chapter 10',
    ]);
    expect(book.tracks.map((t: { number: number }) => t.number)).toEqual([1, 2, 3]);
  });
});
