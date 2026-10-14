/**
 * "Delete all my collections and reorganise my library" — end to end.
 *
 * The real backend runs the real tool loop against a local, scripted
 * OpenAI-compatible model: it reads the library, stages a delete for every
 * collection, stages the new collections and files every book. The whole
 * request must arrive as ONE review card that changes nothing until Apply, and
 * Undo must put the library back. What is asserted is the app path, not the
 * model: the script stands in for any model that does the whole task.
 */
import { createServer, type IncomingMessage } from 'node:http';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { apiGet, loadFixture, newRunId } from './support/fixture';
import { enableAssistant } from './support/assistant';
import { capturePng } from './support/visual-capture';
import type { AiProviderSettings } from '../src/app/core/dtos/ai-provider.dtos';

test.describe.configure({ mode: 'serial' });
test.use({ serviceWorkers: 'block' });

interface Seeded {
  collections: { fiction: string; history: string; misc: string };
  books: { dune: string; foundation: string; spqr: string; meditations: string };
}

type ToolCall = { name: string; args: unknown };

function completion(calls: ToolCall[] | string) {
  return typeof calls === 'string'
    ? { choices: [{ message: { role: 'assistant', content: calls }, finish_reason: 'stop' }] }
    : {
        choices: [{
          message: {
            role: 'assistant',
            content: null,
            tool_calls: calls.map((call, index) => ({
              id: `call-${index}-${call.name}`,
              type: 'function',
              function: { name: call.name, arguments: JSON.stringify(call.args) },
            })),
          },
          finish_reason: 'tool_calls',
        }],
      };
}

async function seed(request: APIRequestContext, baseUrl: string, run: string): Promise<Seeded> {
  const collection = async (name: string) => {
    const response = await request.post(`${baseUrl}/api/collections`, { data: { name, parentId: null } });
    expect(response.ok()).toBeTruthy();
    return ((await response.json()) as { id: string }).id;
  };
  const book = async (title: string, collectionId: string) => {
    const response = await request.post(`${baseUrl}/api/books`, {
      data: { type: 'physical', title, author: 'E2E', collectionId },
    });
    expect(response.ok()).toBeTruthy();
    return ((await response.json()) as { id: string }).id;
  };

  const fiction = await collection(`Fiction ${run}`);
  const history = await collection(`History ${run}`);
  const misc = await collection(`Misc ${run}`);
  return {
    collections: { fiction, history, misc },
    books: {
      dune: await book(`Dune ${run}`, fiction),
      foundation: await book(`Foundation ${run}`, fiction),
      spqr: await book(`SPQR ${run}`, history),
      meditations: await book(`Meditations ${run}`, misc),
    },
  };
}

function script(seeded: Seeded, run: string): (ToolCall[] | string)[] {
  const sf = `Science fiction ${run}`;
  const classics = `History and philosophy ${run}`;
  return [
    [{ name: 'library_overview', args: {} }],
    [
      { name: 'library_delete_collection', args: { collection: seeded.collections.fiction } },
      { name: 'library_delete_collection', args: { collection: seeded.collections.history } },
      { name: 'library_delete_collection', args: { collection: seeded.collections.misc } },
    ],
    [
      { name: 'library_create_collection', args: { name: sf } },
      { name: 'library_create_collection', args: { name: classics } },
    ],
    [
      { name: 'library_add_books_to_collection', args: { collection: sf, bookIds: [seeded.books.dune, seeded.books.foundation] } },
      { name: 'library_add_books_to_collection', args: { collection: classics, bookIds: [seeded.books.spqr, seeded.books.meditations] } },
    ],
    'I replaced your three collections with two themed ones.',
  ];
}

async function collectionNames(baseUrl: string, run: string): Promise<string[]> {
  const collections = await apiGet<{ name: string }[]>(baseUrl, '/api/collections');
  return collections.map((c) => c.name).filter((name) => name.endsWith(run)).sort();
}

for (const variant of [
  { name: 'desktop-light', viewport: { width: 1440, height: 900 }, theme: 'light' },
  { name: 'desktop-dark', viewport: { width: 1440, height: 900 }, theme: 'dark' },
  { name: 'mobile-light', viewport: { width: 390, height: 844 }, theme: 'light' },
]) {
  test(`deleting every collection and regrouping is one review, applied and undone: ${variant.name}`, async ({ browser, request }) => {
    const { baseUrl } = loadFixture();
    const run = newRunId();
    const seeded = await seed(request, baseUrl, run);
    const turns = script(seeded, run);

    let calls = 0;
    const server = createServer((incoming: IncomingMessage, response) => {
      if (incoming.url !== '/v1/chat/completions') {
        response.writeHead(404).end();
        return;
      }
      incoming.resume();
      const next = turns[Math.min(calls, turns.length - 1)];
      calls++;
      response.setHeader('Content-Type', 'application/json');
      response.end(JSON.stringify(completion(next)));
    });
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const providerUrl = `http://127.0.0.1:${(server.address() as { port: number }).port}`;

    const previous = await apiGet<AiProviderSettings>(baseUrl, '/api/settings/ai-provider');
    const put = async (body: unknown) => {
      const result = await request.put(`${baseUrl}/api/settings/ai-provider`, { data: body });
      expect(result.ok(), await result.text()).toBe(true);
    };

    const mobile = variant.viewport.width < 768;
    const context = await browser.newContext({
      viewport: variant.viewport, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 1, serviceWorkers: 'block',
    });
    await context.addInitScript((theme) => {
      try { localStorage.setItem('nostos.theme', theme); } catch { /* about:blank */ }
    }, variant.theme);
    const page = await context.newPage();

    try {
      await put({ llm: { baseUrl: `${providerUrl}/v1`, model: 'fixture-only', apiKey: 'fixture-only-key' } });
      const before = await collectionNames(baseUrl, run);
      expect(before).toEqual([`Fiction ${run}`, `History ${run}`, `Misc ${run}`]);

      await enableAssistant(page);
      await page.goto(`${baseUrl}/library`);
      await page.locator('[data-testid="assistant-trigger"]').click();
      const composer = page.locator('[data-testid="assistant-composer"]');
      await composer.fill('Delete all my collections and reorganise my library into new ones.');
      await composer.press('Enter');

      const card = page.locator('[data-testid="assistant-changeset"]').last();
      await expect(card).toHaveAttribute('data-status', 'review', { timeout: 30_000 });
      expect(calls).toBe(5);
      await expect(card).toContainText('This will delete 3 collections, create 2 collections and refile 4 books.');
      await expect(card).toContainText('Deletes 3 collections that hold 4 books between them.');
      await expect(card.locator('[data-testid="assistant-changeset-step-toggle"]')).toHaveCount(7);
      expect(await collectionNames(baseUrl, run)).toEqual(before);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await card.scrollIntoViewIfNeeded();
      await capturePng(page, `ask-nostos-review-${variant.name}`);

      await card.locator('[data-testid="assistant-changeset-details-toggle"]').click();
      await expect(card.locator('[data-testid="assistant-changeset-details"]')).toContainText(`Dune ${run}`);
      await capturePng(page, `ask-nostos-review-details-${variant.name}`);

      await card.locator('[data-testid="assistant-changeset-apply"]').click();
      await expect(card).toHaveAttribute('data-status', 'applied');
      await expect(card).toContainText('I deleted 3 collections, created 2 collections and refiled 4 books.');
      expect(await collectionNames(baseUrl, run)).toEqual([`History and philosophy ${run}`, `Science fiction ${run}`]);
      await capturePng(page, `ask-nostos-applied-${variant.name}`);

      await card.locator('[data-testid="assistant-changeset-undo"]').click();
      await expect(card).toHaveAttribute('data-status', 'undone');
      expect(await collectionNames(baseUrl, run)).toEqual(before);
    } finally {
      try {
        await put({ llm: { ...previous.llm, apiKey: '' } });
      } finally {
        server.closeAllConnections();
        await new Promise<void>((resolve) => server.close(() => resolve()));
        await context.close();
      }
    }
  });
}
