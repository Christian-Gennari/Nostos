import { expect, test } from '@playwright/test';
import { createServer } from 'node:http';
import { readFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { loadFixture, apiGet, apiPost } from './support/fixture';
import { cleanupBrain, snapshotConceptIds } from './support/brain-fixture';
import { capturePng } from './support/visual-capture';
import type { AiProviderSettings } from '../src/app/core/dtos/ai-provider.dtos';
import type { Note, NoteSearchHit } from '../src/app/core/dtos/note.dtos';

// The real provider adapter talks to this local deterministic completion fixture.
// These are UI/lifecycle checks, not evidence of a live model's semantic fidelity.
for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
  for (const theme of ['light', 'dark']) {
    const size = viewport.width === 390 ? 'mobile' : 'desktop';
    test(`captured thought original survives reload and restore: ${size} ${theme}`, async ({ browser }) => {
      const { baseUrl } = loadFixture();
      const beforeConceptIds = await snapshotConceptIds(baseUrl);
      const previousProvider = await apiGet<AiProviderSettings>(baseUrl, '/api/settings/ai-provider');
      const previousMode = await apiGet(baseUrl, '/api/settings/assistant');
      expect(previousProvider.llm.hasKey, 'fixture must have no real provider credential').toBe(false);
      let providerCalls = 0;
      const server = createServer((request, response) => {
        if (request.url === '/v1/chat/completions') {
          providerCalls++;
          request.resume();
          response.setHeader('Content-Type', 'application/json');
          response.end(JSON.stringify({ choices: [{ message: { role: 'assistant',
            content: 'Attention is something I practice, even when I am unsure.' }, finish_reason: 'stop' }] }));
          return;
        }
        // Optional real before-build capture, served from a separate baseline worktree.
        const baseline = process.env['CAPTURE_BASELINE_DIST'];
        if (baseline) {
          const pathname = decodeURIComponent(new URL(request.url!, 'http://localhost').pathname);
          const file = path.resolve(baseline, `.${pathname}`);
          const asset = file.startsWith(path.resolve(baseline) + path.sep) && existsSync(file)
            && path.extname(file) ? file : path.join(baseline, 'index.html');
          const mime: Record<string, string> = { '.js': 'text/javascript', '.css': 'text/css',
            '.html': 'text/html', '.svg': 'image/svg+xml', '.png': 'image/png', '.woff2': 'font/woff2' };
          response.setHeader('Content-Type', mime[path.extname(asset)] ?? 'application/octet-stream');
          response.end(readFileSync(asset));
        } else { response.writeHead(404).end(); }
      });
      await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
      const providerUrl = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
      const put = async (url: string, body: unknown) => {
        const result = await fetch(`${baseUrl}${url}`, { method: 'PUT',
          headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
        expect(result.ok, `${url}: ${await result.text()}`).toBe(true);
      };
      const context = await browser.newContext({ viewport, isMobile: size === 'mobile',
        hasTouch: size === 'mobile', deviceScaleFactor: 1, serviceWorkers: 'block' });
      await context.addInitScript((value) => {
        try { localStorage.setItem('nostos.theme', value); } catch { /* about:blank has no storage */ }
      }, theme);
      const page = await context.newPage();
      let bookId: string | null = null;
      try {
        await put('/api/settings/ai-provider', { llm: { baseUrl: `${providerUrl}/v1`,
          model: 'fixture-only', apiKey: 'fixture-only-key' } });
        const book = await apiPost(baseUrl, '/api/books', { type: 'physical',
          title: 'Attention and uncertainty', author: 'A reader' });
        bookId = book.id;
        const raw = 'um i think attention is something i practice, even when i am unsure';
        const note = await apiPost<Note>(baseUrl, `/api/books/${book.id}/notes`, {
          content: raw, selectedText: 'We are what we repeatedly attend to.',
          processingMode: 'light_polish', captureSource: 'voice',
          sourceAnchorKind: 'physical_page', sourceAnchorValue: '12',
        });
        expect(note.processingMode).toBe('light_polish');
        expect(providerCalls).toBe(1);

        if (process.env['CAPTURE_BASELINE_DIST']) {
          const before = await context.newPage();
          await before.route('**/api/**', async (route) => {
            const url = new URL(route.request().url());
            const response = await route.fetch({ url: `${baseUrl}${url.pathname}${url.search}` });
            await route.fulfill({ response });
          });
          await before.goto(`${providerUrl}/second-brain?noteId=${note.id}`);
          await expect(before.getByTestId('brain-note-inspector')).toBeVisible();
          await capturePng(before, `captured-thoughts-note-${size}-${theme}-before`);
          await before.goto(`${providerUrl}/settings`);
          await before.getByRole('tab', { name: 'Assistant', exact: true }).click();
          await before.getByTestId('capture-processing-mode').scrollIntoViewIfNeeded();
          await capturePng(before, `captured-thoughts-settings-${size}-${theme}-before`);
          await before.close();
        }

        await page.goto(`${baseUrl}/second-brain?noteId=${note.id}`);
        await expect(page.getByTestId('note-capture-details')).toContainText('Light polish');
        const viewOriginal = page.getByRole('button', { name: 'View original', exact: true });
        const originalDialog = page.getByRole('dialog', { name: 'Original wording', exact: true });
        await expect(originalDialog).toHaveCount(0);
        await expect(page.getByTestId('note-original-text')).toHaveCount(0);
        await capturePng(page, `captured-original-modal-${size}-${theme}-inspector`);
        await viewOriginal.click();
        await expect(page.getByTestId('note-original-text')).toHaveText(raw);
        await expect(originalDialog).toBeVisible();
        const closeOriginal = originalDialog.getByRole('button', { name: 'Close original wording', exact: true });
        await expect(closeOriginal).toBeFocused();
        await page.keyboard.press('Shift+Tab');
        await expect(originalDialog.getByRole('button', { name: 'Restore original', exact: true })).toBeFocused();
        await page.keyboard.press('Tab');
        await expect(closeOriginal).toBeFocused();
        await capturePng(page, `captured-original-modal-${size}-${theme}-open`);
        await page.keyboard.press('Escape');
        await expect(originalDialog).toHaveCount(0);
        await expect(viewOriginal).toBeFocused();
        await viewOriginal.click();
        if (size === 'desktop') {
          await page.mouse.click(5, 5);
        } else {
          await closeOriginal.click();
        }
        await expect(originalDialog).toHaveCount(0);
        await expect(viewOriginal).toBeFocused();
        await page.reload();
        await expect(originalDialog).toHaveCount(0);
        await page.getByRole('button', { name: 'View original', exact: true }).click();
        await expect(page.getByTestId('note-original-text')).toHaveText(raw);
        await page.getByRole('button', { name: 'Restore original', exact: true }).click();
        await expect(page.getByTestId('note-capture-details')).toContainText('Verbatim');
        await expect(page.getByTestId('brain-note-inspector')).toBeVisible();
        const restored = await apiGet<NoteSearchHit>(baseUrl, `/api/notes/${note.id}`);
        expect(restored.content).toBe(raw);
        expect(restored.selectedText).toBe(note.selectedText);
        expect(restored.sourceAnchorValue).toBe('12');
        expect(restored.hasRawContent).toBe(true);
        expect(providerCalls).toBe(1);
        await expect(originalDialog).toBeVisible();
        await originalDialog.getByRole('button', { name: 'Close', exact: true }).click();
        await expect(originalDialog).toHaveCount(0);
        // Long transcripts scroll inside the modal while dismissal stays reachable.
        const longOriginal = Array(100).fill(raw).join('\n\n');
        await page.route(`**/api/notes/${note.id}/raw`, (route) => route.fulfill({
          json: { id: note.id, rawContent: longOriginal, content: raw, processingMode: 'verbatim' },
        }));
        await page.reload();
        await viewOriginal.click();
        await expect(page.getByTestId('note-original-text')).toHaveText(longOriginal);
        const scroll = originalDialog.locator('.modal-scroll');
        expect(await scroll.evaluate((element) => element.scrollHeight > element.clientHeight)).toBe(true);
        await expect(originalDialog.getByRole('button', { name: 'Close', exact: true })).toBeInViewport();
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await closeOriginal.click();
        await page.unroute(`**/api/notes/${note.id}/raw`);

        await page.goto(`${baseUrl}/settings`);
        await page.getByRole('tab', { name: 'Assistant', exact: true }).click();
        const dropdown = page.getByTestId('capture-processing-mode');
        await dropdown.getByRole('combobox').click();
        await dropdown.getByRole('option', { name: 'Clarify', exact: true }).click();
        await expect.poll(async () => (await apiGet(baseUrl, '/api/settings/assistant')).captureProcessingMode)
          .toBe('clarify');
        await dropdown.scrollIntoViewIfNeeded();
        await expect(page.getByText('Saved thought wording', { exact: true })).toBeVisible();
        await capturePng(page, `captured-thoughts-settings-${size}-${theme}-after`);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        const manual = await apiPost<Note>(baseUrl, `/api/books/${book.id}/notes`, { content: 'My manual words.' });
        expect(manual.processingMode).toBe('verbatim');
        expect(providerCalls).toBe(1);
        await page.goto(`${baseUrl}/second-brain?noteId=${manual.id}`);
        await expect(page.getByTestId('brain-note-inspector')).toBeVisible();
        await expect(page.getByTestId('note-capture-details')).toHaveCount(0);
      } finally {
        try {
          await put('/api/settings/ai-provider', { llm: { ...previousProvider.llm, apiKey: '' } });
          await put('/api/settings/assistant', previousMode);
          if (bookId) await cleanupBrain(baseUrl, { bookId, conceptNames: [], beforeConceptIds });
        } finally {
          server.closeAllConnections();
          await new Promise<void>((resolve) => server.close(() => resolve()));
          await context.close();
        }
      }
    });
  }
}
