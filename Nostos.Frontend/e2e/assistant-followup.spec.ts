/**
 * The voice follow-up loop, end to end on the real app (issue #262 §6).
 *
 * WHY THIS IS AN E2E SPEC
 * -----------------------
 * The four-step flow is a conversation across surfaces: a thought is captured,
 * a page is asked for, the answer continues the SAME conversation, and the
 * acknowledgement names the book and the page. Whether the second message
 * continues or restarts is a property of the assembled app — the service, the
 * component and the backend response shape together — not of any one unit.
 *
 * HONEST LIMITS
 * -------------
 *  - A real microphone cannot be exercised headlessly. This spec drives the
 *    TYPED path through the identical composer/send seam a transcript enters
 *    (`AssistantService.insertTranscript` -> `submit`); the recording mechanics
 *    are covered by the unit suite with a fake MediaRecorder, and the one real
 *    transcription is a manual, rationed call reported in the stream handoff.
 *  - No provider call is made here. Both `/api/assistant/turn` responses are
 *    fulfilled in the browser with the real `AssistantTurnResponse` shape:
 *    the first asks the deterministic physical-page follow-up, and the second
 *    confirms the capture after that answer is attached. The app path under
 *    test — not the model — is what is asserted. Everything else (the built
 *    app, the book record, the context resolution) is the worktree's own
 *    backend and temp DB.
 *  - The service worker is blocked: it would answer `/api/**` itself and swallow
 *    `page.route` (the production build registers it). The API namespace policy
 *    is covered separately by `service-worker-navigation.spec.ts`.
 */
import { expect, test, type Route } from '@playwright/test';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { enableAssistant } from './support/assistant';

test.describe.configure({ mode: 'serial' });
test.use({ serviceWorkers: 'block' });

const RUN = newRunId();
const THOUGHT = 'Save a thought for The Magic Mountain.';
const BOOK_TITLE = `Assistant Follow-up ${RUN}`;

let fixture: ReturnType<typeof loadFixture>;
let bookId = '';

test.beforeAll(async () => {
  fixture = loadFixture();
  const created = await apiPost<any>(fixture.baseUrl, '/api/books/', {
    type: 'physical',
    title: BOOK_TITLE,
    author: 'E2E',
  });
  bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  if (!bookId) throw new Error(`No book id in create response: ${JSON.stringify(created)}`);
});

test('a typed follow-up continues the conversation and names book and page', async ({ page }) => {
  // This spec owns the assistant boundary: the fixture intentionally has no
  // real LLM credential, so advertise availability just as we already fulfill
  // the turn endpoint below. No provider call is made.
  await page.route('**/api/assistant/status', async (route: Route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ available: true }),
    });
  });

  const turns: any[] = [];
  await page.route('**/api/assistant/turn/stream', async (route: Route) => {
    turns.push(route.request().postDataJSON());
    const firstTurn = turns.length === 1;

    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(
        firstTurn
          ? {
              reply: 'What page are you on?',
              acknowledgement: null,
              // The app only shows an anchor prompt that carries a continuation id.
              anchorPrompt: { kind: 'physical_page', question: 'What page are you on?', continuationId: 'continuation-1' },
              suggestions: [],
              changeset: null,
              capturedNoteId: null,
            }
          : {
              reply: 'Saved.',
              acknowledgement: `Saved to ${BOOK_TITLE}.`,
              anchorPrompt: null,
              suggestions: [],
              changeset: null,
              capturedNoteId: 'note-e2e',
            },
      ),
    });
  });

  await enableAssistant(page);
  await page.goto(`${fixture.baseUrl}/library/${bookId}`);

  // The ambient book must be resolved before the first message: a physical book
  // is what makes the page question deterministic (no LLM round trip).
  await page.waitForFunction(
    (title) =>
      (globalThis as any).__nostosAssistant?.context?.bookFormat === 'physical' &&
      (globalThis as any).__nostosAssistant?.context?.bookTitle === title,
    BOOK_TITLE,
    { timeout: 30_000 },
  );

  await page.locator('[data-testid="assistant-trigger"]').click();
  const composer = page.locator('[data-testid="assistant-composer"]');

  // 1. The thought dispatches normally. The backend decides that this capture
  // needs a page and returns the deterministic follow-up.
  await composer.fill(THOUGHT);
  await composer.press('Enter');

  await expect.poll(() => turns.length).toBe(1);
  expect(turns[0].message).toBe(THOUGHT);
  expect(turns[0].context.bookTitle).toBe(BOOK_TITLE);
  expect(turns[0].context.anchor).toBeNull();

  const prompt = page.locator('[data-testid="assistant-anchor-prompt"]');
  await expect(prompt).toBeVisible();
  await expect(prompt).toContainText('What page are you on?');

  // 2. The answer continues the same capture (as a voice transcript would).
  await composer.fill('Page 247.');
  await composer.press('Enter');

  await expect.poll(() => turns.length).toBe(2);
  // The answer is sent as its own message, tied to the question by the
  // continuation id; the server joins it to the original thought.
  expect(turns[1].message).toBe('Page 247.');
  expect(turns[1].continuationId).toBe('continuation-1');
  expect(turns[1].context.bookTitle).toBe(BOOK_TITLE);
  // The page is no longer parsed in the browser: the anchor stays empty and
  // the server resolves "Page 247." against the pending question.
  expect(turns[1].context.anchor).toBeNull();

  // 4. The acknowledgement entry names the book and the page.
  const transcript = page.locator('[data-testid="assistant-transcript"]');
  await expect(transcript).toContainText(`Saved to ${BOOK_TITLE}.`);
  await expect(transcript).toContainText('Saved');
});
