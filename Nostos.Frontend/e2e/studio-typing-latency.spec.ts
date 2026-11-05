import { expect, test } from '@playwright/test';

import { apiDelete } from './support/brain-fixture';
import { apiPost, apiPut, loadFixture } from './support/fixture';
import { studioPage, waitForStudioEditor } from './support/studio-editor';

/**
 * Issue #1392 (F-A70): typing at the end of a very long writing lagged
 * 160-200 ms per key, because every keystroke serialised the whole page to
 * Markdown, recounted its words and rescanned it for citations. This types
 * into a ~80,000-word writing in the production build and measures keydown to
 * the next paint. Set STUDIO_LATENCY_REPORT=1 to print the numbers.
 */

const WORDS = 80_000;
const KEYS = 40;
const KEY_GAP_MS = 90;
/** The Studio must stay under 50 ms a key; the gate leaves room for a slow CI runner. */
const BUDGET_MS = 100;

function longWriting(words: number): string {
  const sentence =
    'The *quiet* hours before dawn belong to the **reader**, who turns each page slowly and writes down what stays.';
  const blocks: string[] = [];
  let count = 0;
  let chapter = 1;
  while (count < words) {
    blocks.push(`## Chapter ${chapter++}`);
    for (let i = 0; i < 12 && count < words; i++) {
      const paragraph = Array.from({ length: 8 }, () => sentence).join(' ');
      blocks.push(paragraph);
      count += paragraph.split(/\s+/).length;
    }
    count += 2;
  }
  return blocks.join('\n\n');
}

function median(values: number[]): number {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.floor(sorted.length / 2)];
}

test('typing at the end of an 80,000-word writing stays responsive', async ({ page }) => {
  test.setTimeout(240_000);
  const { baseUrl } = loadFixture();
  const writing = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
    name: 'Typing latency probe',
    type: 'Document',
    parentId: null,
  });
  try {
    const content = longWriting(WORDS);
    await apiPut(baseUrl, `/api/writings/${writing.id}`, { name: 'Typing latency probe', content });

    await page.goto(`${baseUrl}/studio?writingId=${writing.id}`, { waitUntil: 'domcontentloaded' });
    const editor = await waitForStudioEditor(page, 90_000);
    await expect(editor.locator('h2').first()).toBeVisible({ timeout: 90_000 });

    await editor.click();
    await page.keyboard.press('Control+End');

    await page.evaluate(() => {
      const w = window as unknown as { __latency: number[] };
      w.__latency = [];
      document.addEventListener(
        'keydown',
        () => {
          const start = performance.now();
          requestAnimationFrame(() => {
            setTimeout(() => w.__latency.push(performance.now() - start), 0);
          });
        },
        true,
      );
    });

    for (let i = 0; i < KEYS; i++) {
      await page.keyboard.type(i % 7 === 6 ? ' ' : 'abcdefg'[i % 7]);
      await page.waitForTimeout(KEY_GAP_MS);
    }
    await page.waitForTimeout(500);

    const latency = await page.evaluate(() => (window as unknown as { __latency: number[] }).__latency);
    expect(latency.length).toBeGreaterThanOrEqual(KEYS - 2);
    const sorted = [...latency].sort((a, b) => a - b);
    const med = median(latency);
    const p95 = sorted[Math.floor(sorted.length * 0.95)];
    if (process.env['STUDIO_LATENCY_REPORT']) {
      console.log(
        `[studio-typing] words=${WORDS} keys=${latency.length} median=${med.toFixed(1)}ms p95=${p95.toFixed(1)}ms max=${sorted.at(-1)!.toFixed(1)}ms`,
      );
    }
    expect(med).toBeLessThan(BUDGET_MS);

    // What was typed is saved: autosave still writes the whole page.
    await expect
      .poll(async () => (await (await fetch(`${baseUrl}/api/writings/${writing.id}`)).json()).content, { timeout: 15_000 })
      .toContain('abcdef');
    await expect(studioPage(page)).toContainText('abcdef');
  } finally {
    await apiDelete(baseUrl, `/api/writings/${writing.id}`);
  }
});
