/**
 * Visual evidence for #1665: the AI topic suggestion outcomes in the sorter
 * ("Sort one by one") and in the shared topic picker.
 *
 * The real Angular app and the real backend run; only `/api/assistant/status`
 * and `/api/assistant/turn/stream` are scripted, the same way
 * `assistant-failure-retry.spec.ts` stubs a turn. No model is called.
 *
 *   ready  - the turn proposes existing topics
 *   empty  - the turn finished and proposed nothing
 *   error  - the turn failed (provider error); Try again asks once more
 */
import fs from 'node:fs';
import path from 'node:path';

import { expect, test, type Browser, type Page } from '@playwright/test';

import type { Note } from '../src/app/core/dtos/note.dtos';
import { apiGet, apiPost, loadFixture, newRunId } from './support/fixture';
import { cleanupBrain, snapshotTopicIds, type TopicRef } from './support/brain-fixture';

type Outcome = 'ready' | 'empty' | 'error';

const evidence = path.join(__dirname, 'visual-evidence', 'topic-suggestions-1665');

interface TurnBody {
  turnId: string;
}

test.describe('Topic suggestion outcomes (#1665)', () => {
  test.use({ serviceWorkers: 'block' });

  const run = newRunId();
  // More topics than the picker lists before typing (8), as in a real library:
  // a suggestion the list already shows is not repeated under "Suggested".
  const commonTopics = [
    'Attention', 'Beauty', 'Courage', 'Democracy', 'Education',
    'Friendship', 'Justice', 'Knowledge', 'Memory',
  ];
  const suggestedTopics = ['Political philosophy', 'The noble lie'];
  let baseUrl: string;
  let sortedBookId: string;
  let bookId: string;
  let noteId: string;
  let beforeTopicIds: Set<string>;
  const topicIds = new Map<string, string>();

  test.beforeAll(async () => {
    ({ baseUrl } = loadFixture());
    fs.mkdirSync(evidence, { recursive: true });
    beforeTopicIds = await snapshotTopicIds(baseUrl);

    // A sorted note gives the library its topics to suggest from.
    const sorted = await apiPost<{ id: string }>(baseUrl, '/api/books', {
      type: 'physical',
      title: `Laws ${run}`,
      author: 'Plato',
    });
    sortedBookId = sorted.id;
    const links = (names: string[]) => names.map((name) => `[[${name}]]`).join(' ');
    for (const content of [
      `What a city owes its citizens. ${links(commonTopics)}`,
      `Who should rule, and how they are raised. ${links(commonTopics)}`,
      `The guardians and the stories they are told. ${links(suggestedTopics)}`,
    ]) {
      await apiPost(baseUrl, `/api/books/${sorted.id}/notes`, { content });
    }
    const topics = await apiGet<TopicRef[]>(baseUrl, '/api/topics');
    for (const topic of topics) topicIds.set(topic.name, topic.id);

    // The note to sort: no topics yet.
    const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
      type: 'physical',
      title: 'The Republic',
      author: 'Plato',
    });
    bookId = book.id;
    const note = await apiPost<Note>(baseUrl, `/api/books/${book.id}/notes`, {
      selectedText: 'Then let us tell them the magnificent myth of the metals: that god mixed gold into those fit to rule.',
      content: 'A noble lie that holds the city together. Is a founding story ever honest?',
    });
    noteId = note.id;
  });

  test.afterAll(async () => {
    if (!baseUrl) return;
    await cleanupBrain(baseUrl, { bookId, topicNames: [], beforeTopicIds });
    await cleanupBrain(baseUrl, { bookId: sortedBookId, topicNames: [], beforeTopicIds });
  });

  async function freshPage(
    browser: Browser,
    viewport: { width: number; height: number },
    dark: boolean,
    outcome: { current: Outcome },
  ): Promise<Page> {
    const context = await browser.newContext({
      viewport,
      colorScheme: dark ? 'dark' : 'light',
      serviceWorkers: 'block',
    });
    const page = await context.newPage();
    await page.addInitScript((theme) => {
      localStorage.setItem('nostos.theme', theme);
      localStorage.setItem(
        'nostos.library.preferences',
        JSON.stringify({
          viewMode: 'grid',
          sort: 'lastread',
          pageSize: 20,
          sidebarExpanded: true,
          groupByWork: true,
          assistantEnabled: true,
          assistantVoiceEnabled: false,
        }),
      );
    }, dark ? 'dark' : 'light');
    await page.route('**/api/assistant/status', (route) => route.fulfill({ json: { available: true } }));
    await page.route('**/api/assistant/turn/stream', async (route) => {
      const body = route.request().postDataJSON() as TurnBody;
      const started = JSON.stringify({ turnId: body.turnId, sequence: 1, kind: 'started' }) + '\n';
      if (outcome.current === 'error') {
        await route.fulfill({
          contentType: 'application/x-ndjson',
          body: started + JSON.stringify({
            turnId: body.turnId,
            sequence: 2,
            kind: 'failed',
            failure: {
              code: 'assistant_provider_error',
              message: 'The AI service could not complete this turn. Try again.',
              retryable: true,
            },
          }) + '\n',
        });
        return;
      }
      const names = outcome.current === 'ready' ? suggestedTopics : [];
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body: started + JSON.stringify({
          turnId: body.turnId,
          sequence: 2,
          kind: 'completed',
          response: {
            reply: '',
            acknowledgement: null,
            anchorPrompt: null,
            suggestions: names.map((label) => ({
              kind: 'topic',
              label,
              reason: '',
              value: topicIds.get(label) ?? label,
              noteId,
            })),
            changeset: null,
            capturedNoteId: null,
            executedCapabilities: [],
            sources: [],
            evidence: [],
            error: null,
          },
        }) + '\n',
      });
    });
    return page;
  }

  const sorterShots: Array<[string, { width: number; height: number }, boolean]> = [
    ['1280-dark', { width: 1280, height: 800 }, true],
    ['1280-light', { width: 1280, height: 800 }, false],
    ['390-dark', { width: 390, height: 844 }, true],
    ['390-light', { width: 390, height: 844 }, false],
  ];

  for (const [name, viewport, dark] of sorterShots) {
    test(`sorter: ready, empty, error at ${name}`, async ({ browser }) => {
      const outcome = { current: 'ready' as Outcome };
      const page = await freshPage(browser, viewport, dark, outcome);
      const suggest = page.getByRole('button', { name: 'Suggest topics', exact: true });
      const tryAgain = page.getByRole('button', { name: 'Try again', exact: true });

      for (const state of ['ready', 'empty', 'error'] as const) {
        outcome.current = state;
        await page.goto(`${baseUrl}/brain/unsorted/sort`, { waitUntil: 'domcontentloaded' });
        await expect(page.getByText('A noble lie that holds the city together.')).toBeVisible();
        await suggest.click();
        if (state === 'ready') {
          await expect(page.getByRole('button', { name: 'The noble lie', exact: true }).last()).toBeVisible();
        } else if (state === 'empty') {
          await expect(page.getByText('No topics suggest themselves for this note.')).toBeVisible();
        } else {
          await expect(page.getByText('Could not get suggestions.')).toBeVisible();
          await expect(tryAgain).toBeVisible();
        }
        await page.screenshot({ path: path.join(evidence, `sorter-${state}-${name}.png`) });
      }

      // Try again asks once more and shows what that turn returns.
      outcome.current = 'ready';
      await tryAgain.click();
      await expect(page.getByRole('button', { name: 'The noble lie', exact: true }).last()).toBeVisible();
      await page.context().close();
    });
  }

  const pickerShots: Array<[string, { width: number; height: number }, boolean]> = [
    ['1280-dark', { width: 1280, height: 800 }, true],
    ['1280-light', { width: 1280, height: 800 }, false],
  ];

  for (const [name, viewport, dark] of pickerShots) {
    test(`topic picker: ready, empty, error at ${name}`, async ({ browser }) => {
      const outcome = { current: 'ready' as Outcome };
      const page = await freshPage(browser, viewport, dark, outcome);
      const selected = page.getByRole('region', { name: 'Selected note' });

      for (const state of ['ready', 'empty', 'error'] as const) {
        outcome.current = state;
        await page.goto(`${baseUrl}/brain/unsorted?note=${noteId}`, { waitUntil: 'domcontentloaded' });
        await expect(selected).toContainText('A noble lie that holds the city together.');
        await selected.getByTestId('topic-row-add').click();
        const picker = page.locator('app-topic-picker-popover');
        await expect(picker).toBeVisible();
        if (state === 'ready') {
          await expect(picker.locator('.picker-group', { hasText: 'Suggested' })).toBeVisible();
          // Suggestions follow the listed topics: scroll the list to them.
          await picker.getByRole('option', { name: 'The noble lie' }).scrollIntoViewIfNeeded();
          await expect(picker.getByRole('option', { name: 'The noble lie' })).toBeInViewport();
        } else if (state === 'empty') {
          await expect(picker).toContainText('No topics suggest themselves for this note.');
        } else {
          await expect(picker).toContainText('Could not get suggestions.');
          await expect(picker.getByRole('button', { name: 'Try again', exact: true })).toBeVisible();
        }
        await page.screenshot({ path: path.join(evidence, `picker-${state}-${name}.png`) });

        if (state === 'error') {
          outcome.current = 'ready';
          await picker.getByRole('button', { name: 'Try again', exact: true }).click();
          await expect(picker.getByRole('option', { name: 'The noble lie' })).toBeVisible();
        }
      }
      await page.context().close();
    });
  }
});
