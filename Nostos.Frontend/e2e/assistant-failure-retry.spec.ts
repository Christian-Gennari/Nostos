/**
 * Ask Nostos failures render once and offer a real retry (#1556, #1560).
 *
 * The assistant turn is stubbed the same way `reader-ask-quote-highlight.spec.ts`
 * stubs one: the real Angular app and the real backend run, and only the
 * `/api/assistant/turn/stream` response is scripted. No model is called.
 *
 * The first delivery fails with a retryable provider timeout. "Try again"
 * replays the stored turn: the second request must carry the same TurnId and
 * idempotency key, which is what makes the server's mutation receipts replay
 * instead of writing again.
 */
import { expect, test, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';

test.use({ serviceWorkers: 'block' });

interface TurnBody {
  turnId: string;
  idempotencyKey: string;
  message: string;
}

async function enableAskNostos(page: Page) {
  await page.addInitScript(() => {
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
  });
  await page.route('**/api/assistant/status', (route) =>
    route.fulfill({ json: { available: true } }),
  );
}

test('a failed turn renders once and Try again replays the same TurnId', async ({ page }) => {
  const { baseUrl } = loadFixture();
  await enableAskNostos(page);

  const bodies: TurnBody[] = [];
  await page.route('**/api/assistant/turn/stream', async (route) => {
    const request = route.request().postDataJSON() as TurnBody;
    bodies.push(request);

    if (bodies.length === 1) {
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body:
          JSON.stringify({
            turnId: request.turnId,
            sequence: 1,
            kind: 'started',
          }) + '\n'
          + JSON.stringify({
            turnId: request.turnId,
            sequence: 2,
            kind: 'failed',
            failure: {
              code: 'assistant_provider_timeout',
              message: 'Ask Nostos took too long to answer. Try again.',
              retryable: true,
            },
          }) + '\n',
      });
      return;
    }

    await route.fulfill({
      contentType: 'application/x-ndjson',
      body:
        JSON.stringify({
          turnId: request.turnId,
          sequence: 1,
          kind: 'started',
        }) + '\n'
        + JSON.stringify({
          turnId: request.turnId,
          sequence: 2,
          kind: 'completed',
          response: {
            reply: 'Here is the answer, on the second delivery.',
            acknowledgement: null,
            anchorPrompt: null,
            suggestions: [],
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

  await page.goto(`${baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await page.keyboard.press('Control+j');

  const panel = page.getByTestId('assistant-panel');
  await expect(panel).toBeVisible();
  await page.getByTestId('assistant-composer').fill('What have I been reading lately?');
  await page.getByTestId('assistant-send').click();

  const failure = page.getByTestId('assistant-failure');
  await expect(failure).toHaveCount(1);
  await expect(failure).toContainText('Ask Nostos took too long to answer. Try again.');
  await expect(failure).not.toContainText('Failed ·');

  // The raw code is available, but only behind the closed Details disclosure.
  const details = failure.getByTestId('assistant-failure-details');
  await expect(details).toHaveCount(1);
  await expect(details).not.toHaveAttribute('open');
  await details.locator('summary').click();
  await expect(details).toContainText('assistant_provider_timeout');

  const retry = failure.getByTestId('assistant-retry');
  await expect(retry).toHaveText('Try again');
  await retry.click();

  await expect(page.getByTestId('assistant-entry-markdown').last()).toContainText(
    'Here is the answer, on the second delivery.',
  );
  await expect(page.getByTestId('assistant-failure')).toHaveCount(0);

  expect(bodies.length).toBe(2);
  expect(bodies[1].turnId).toBe(bodies[0].turnId);
  expect(bodies[1].idempotencyKey).toBe(bodies[0].idempotencyKey);
  expect(bodies[1].message).toBe(bodies[0].message);
});
