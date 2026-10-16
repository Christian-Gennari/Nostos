/**
 * Audiobook passages (issue #1019): a clip between two times, marked with
 * More > Mark passage here at each end, its ends nudged on the save sheet,
 * and played in the passage viewer within its bounds and no further.
 *
 * clips.mp3 is a 20-second tone with two ID3 chapters (0:00 "Chapter One",
 * 0:08 "Chapter Two"), made with ffmpeg for this spec.
 */
import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { apiPost, loadFixture, newRunId } from './support/fixture';

test.use({ serviceWorkers: 'block', launchOptions: { args: ['--autoplay-policy=no-user-gesture-required'] } });

const MP3 = readFileSync(path.join(__dirname, 'assets', 'clips.mp3'));

async function seedAudiobook(baseUrl: string): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'audiobook',
    title: `Clips ${newRunId()}`,
    author: 'E2E',
  });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  const form = new FormData();
  form.append('file', new Blob([MP3], { type: 'audio/mpeg' }), 'clips.mp3');
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload -> ${res.status}`);
  return bookId;
}

async function openAt(page: Page, baseUrl: string, bookId: string, seconds: number) {
  await page.goto(`${baseUrl}/read/${bookId}?sourceTime=${seconds}`, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('reader-more')).toBeVisible({ timeout: 30_000 });
  // The player has loaded and turned to the time.
  await page.waitForTimeout(1500);
}

async function markHere(page: Page, label: RegExp) {
  await page.getByTestId('reader-more').click();
  await expect(page.getByTestId('reader-mark-passage-here')).toHaveText(label);
  await page.getByTestId('reader-mark-passage-here').click();
}

test('a clip is marked at two times, nudged, and played in the viewer within its bounds', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const bookId = await seedAudiobook(baseUrl);

  // The later time first; the clip runs in book order all the same.
  await openAt(page, baseUrl, bookId, 15);
  await markHere(page, /Mark passage here/);
  await expect(page.getByTestId('passage-pill')).toBeVisible();

  await openAt(page, baseUrl, bookId, 9);
  await markHere(page, /Complete passage here/);
  await expect(page.getByTestId('passage-sheet-title-input')).toHaveValue('Chapter Two');
  await expect(page.getByTestId('passage-sheet-size')).toHaveText('0:09–0:15 · 0:06');
  // A nudge stops at the end of the book (0:20)...
  await page.getByTestId('passage-sheet-end-later').click();
  await expect(page.getByTestId('passage-sheet-size')).toHaveText('0:09–0:20 · 0:11');
  // ...and never takes the end before the start.
  await page.getByRole('button', { name: 'End 15 seconds earlier' }).click();
  await expect(page.getByTestId('passage-sheet-size')).toHaveText('0:09–0:10 · 0:01');
  await page.getByTestId('passage-sheet-end-later').click();
  await expect(page.getByTestId('passage-sheet-size')).toHaveText('0:09–0:20 · 0:11');
  await page.getByTestId('passage-sheet-save').click();
  await expect(page.getByTestId('passage-sheet-save')).toHaveCount(0);

  const saved = (await (await fetch(`${baseUrl}/api/books/${bookId}/notes`)).json()).find(
    (note: any) => note.kind === 'passage',
  );
  expect(saved.sourceAnchorKind).toBe('audio_timestamp');
  expect(Number(saved.sourceAnchorValue)).toBeCloseTo(9, 0);
  expect(Number(saved.passageEnd)).toBeCloseTo(20, 0);

  await page.getByRole('button', { name: 'Notes & Highlights' }).click();
  await expect(page.locator('.passage-size').first()).toHaveText('0:09–0:20 · 0:11');
  await page.getByTestId('passage-card-read-button').click();

  const play = page.getByTestId('passage-viewer-clip-play');
  await expect(play).toBeEnabled({ timeout: 15_000 });
  await expect(page.getByTestId('passage-viewer-clip-time')).toHaveText('0:00/ 0:11');
  await play.click();
  await expect(play).toHaveAttribute('aria-label', 'Pause clip');
  await expect(page.getByTestId('passage-viewer-clip-time')).not.toHaveText('0:00/ 0:11', { timeout: 5_000 });

  // It stops at the end of the clip, not the end of the book.
  await page.getByTestId('passage-viewer-clip-scrubber').evaluate((el: HTMLInputElement) => {
    el.value = String(Number(el.max) - 1);
    el.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await expect(play).toHaveAttribute('aria-label', 'Play clip', { timeout: 5_000 });
  await expect(page.getByTestId('passage-viewer-clip-time')).toHaveText('0:11/ 0:11');
});
