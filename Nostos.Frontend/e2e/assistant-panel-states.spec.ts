/**
 * The Ask Nostos panel's states, walked end to end (#1563).
 *
 * One real Angular app and the real backend run; only the assistant's turn
 * stream is stubbed, exactly like `assistant-failure-retry.spec.ts` and
 * `reader-ask-quote-highlight.spec.ts`. No model is called.
 *
 * The walk covers what the unit tests pin piece by piece: the empty state's
 * three doors prefill without sending, an answer with numbered citations and a
 * grouped source row, a changeset that arrives as a review with its title and
 * Apply in view, a retryable failure whose Try again replays the same TurnId,
 * and a capture that committed before the provider failed — the saved note
 * reads above the one failure block. It runs at desktop and phone width.
 */
import { expect, test, type Locator, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';

test.use({ serviceWorkers: 'block' });

interface TurnBody {
  turnId: string;
  idempotencyKey: string;
  message: string;
}

const ANSWER_MESSAGE = 'What did I note about attention?';
const REVIEW_MESSAGE = 'Regroup everything into Fiction and Essays.';
const FAILURE_MESSAGE = 'Summarise the rope entries.';
const SAVED_FAILURE_MESSAGE = 'Remember this: the harbour bell rings twice at dusk.';
const FAILURE_SENTENCE = 'Ask Nostos took too long to answer. Try again.';
const SAVED_FAILURE_SENTENCE = 'The AI service could not complete this turn. Try again.';

/** Two passages from one book, so the source row is grouped and numbered. */
const ANSWER_EVIDENCE = [
  {
    handle: {
      kind: 'book_text',
      bookId: 'e2e-book-salt-meridian',
      sourceSha256: 'e2e-sha-salt-meridian',
      ordinal: 0,
    },
    label: 'The Salt Meridian',
    bookTitle: 'The Salt Meridian',
    bookAuthor: 'E2E',
    format: 'pdf',
    excerpt: 'Attention is the scarce resource, and the notes keep returning to it.',
    locators: [{ type: 'pdf', pdfPageIndex: 41, pdfPageLabel: '42' }],
  },
  {
    handle: {
      kind: 'book_text',
      bookId: 'e2e-book-salt-meridian',
      sourceSha256: 'e2e-sha-salt-meridian',
      ordinal: 1,
    },
    label: 'The Salt Meridian',
    bookTitle: 'The Salt Meridian',
    bookAuthor: 'E2E',
    format: 'pdf',
    excerpt: 'The evening walk ends at the lantern room stairs.',
    locators: [{ type: 'pdf', pdfPageIndex: 56, pdfPageLabel: '57' }],
  },
];

/** A review taller than the transcript on purpose: six steps plus its reasons. */
function reviewChangeset() {
  return {
    changesetId: 'cs-e2e-review',
    status: 'review',
    steps: Array.from({ length: 6 }, (_, index) => ({
      index,
      summary: `File ${index + 1} books into ${index < 4 ? 'Fiction' : 'Essays'}`,
      included: true,
      dependsOn: [],
    })),
    diff: {
      collections: [
        {
          id: 'c1',
          kind: 'created',
          name: 'Fiction',
          previousName: null,
          parentName: null,
          previousParentName: null,
          parentChanged: false,
          bookCount: 0,
        },
        {
          id: 'c2',
          kind: 'created',
          name: 'Essays',
          previousName: null,
          parentName: null,
          previousParentName: null,
          parentChanged: false,
          bookCount: 0,
        },
      ],
      memberships: [
        { bookId: 'b1', title: 'The Magic Mountain', author: 'Thomas Mann', added: ['Fiction'], removed: [] },
        { bookId: 'b2', title: 'The Dispossessed', author: 'Ursula K. Le Guin', added: ['Fiction'], removed: [] },
        { bookId: 'b3', title: 'Piranesi', author: 'Susanna Clarke', added: ['Essays'], removed: [] },
      ],
      bookEdits: [],
      counts: {
        collectionsCreated: 2,
        collectionsDeleted: 0,
        collectionsUpdated: 0,
        booksTouched: 6,
        membershipsAdded: 6,
        membershipsRemoved: 0,
        booksLosingMembership: 0,
        bookEdits: 0,
      },
    },
    diffHash: 'e2e-review-hash',
    actionToken: 'e2e-action-token',
    reviewReasons: [
      'Changes 6 books.',
      'It was worked out from book text or notes, so you decide.',
    ],
    canUndo: false,
  };
}

function completed(turnId: string, response: Record<string, unknown>, sequence = 2): string {
  return (
    JSON.stringify({ turnId, sequence: 1, kind: 'started' }) +
    '\n' +
    JSON.stringify({ turnId, sequence, kind: 'completed', response }) +
    '\n'
  );
}

function failed(
  turnId: string,
  code: string,
  message: string,
  retryable: boolean,
  response?: Record<string, unknown>,
  sequence = 2,
): string {
  return (
    JSON.stringify({ turnId, sequence: 1, kind: 'started' }) +
    '\n' +
    JSON.stringify({
      turnId,
      sequence,
      kind: 'failed',
      failure: { code, message, retryable },
      ...(response ? { response } : {}),
    }) +
    '\n'
  );
}

async function enableAskNostos(page: Page): Promise<void> {
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

/** The dock carries the launcher on every workspace route, at both widths. */
async function openAssistant(page: Page): Promise<void> {
  const launcher = page.locator('[data-assistant-launcher]').first();
  await expect(launcher).toBeVisible();
  await launcher.click();
  await expect(page.getByTestId('assistant-panel')).toBeVisible();
}

async function send(page: Page, message: string): Promise<void> {
  await page.getByTestId('assistant-composer').fill(message);
  await page.getByTestId('assistant-send').click();
}

/** True when the element is painted inside the transcript's own scrollport. */
async function withinTranscript(page: Page, locator: Locator): Promise<boolean> {
  const box = await locator.boundingBox();
  const body = await page.getByTestId('assistant-body').boundingBox();
  if (!box || !body) return false;
  return box.y >= body.y - 1 && box.y + box.height <= body.y + body.height + 1;
}

/**
 * Drives the panel through every state with one stubbed turn stream.
 * `savedFailureTurnId` is returned so the caller can assert the failure block
 * came after the acknowledgement.
 */
async function walkPanelStates(page: Page): Promise<TurnBody[]> {
  const bodies: TurnBody[] = [];
  const deliveries = new Map<string, number>();

  await page.route('**/api/assistant/turn/stream', async (route) => {
    const request = route.request().postDataJSON() as TurnBody;
    bodies.push(request);
    const delivery = (deliveries.get(request.message) ?? 0) + 1;
    deliveries.set(request.message, delivery);

    if (request.message === ANSWER_MESSAGE) {
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body: completed(request.turnId, {
          reply:
            'Attention is the scarce resource.[^1] Your lantern walk is where it shows most clearly.[^2]',
          acknowledgement: null,
          anchorPrompt: null,
          suggestions: [],
          changeset: null,
          capturedNoteId: null,
          executedCapabilities: [],
          sources: [],
          evidence: ANSWER_EVIDENCE,
          error: null,
          resolvedBook: null,
        }),
      });
      return;
    }

    if (request.message === REVIEW_MESSAGE) {
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body: completed(request.turnId, {
          reply: "Here's what I'd change. Nothing changes until you apply it.",
          acknowledgement: null,
          anchorPrompt: null,
          suggestions: [],
          changeset: reviewChangeset(),
          capturedNoteId: null,
          executedCapabilities: [],
          sources: [],
          evidence: [],
          error: null,
          resolvedBook: null,
        }),
      });
      return;
    }

    if (request.message === FAILURE_MESSAGE) {
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body:
          delivery === 1
            ? failed(request.turnId, 'assistant_provider_timeout', FAILURE_SENTENCE, true)
            : completed(request.turnId, {
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
                resolvedBook: null,
              }),
      });
      return;
    }

    if (request.message === SAVED_FAILURE_MESSAGE) {
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body: failed(request.turnId, 'assistant_provider_error', SAVED_FAILURE_SENTENCE, true, {
          reply: '',
          acknowledgement: 'Saved to The Salt Meridian.',
          anchorPrompt: null,
          suggestions: [],
          changeset: null,
          capturedNoteId: 'e2e-note-1558',
          executedCapabilities: [],
          sources: [],
          evidence: [],
          error: null,
          resolvedBook: null,
        }),
      });
      return;
    }

    await route.fulfill({
      contentType: 'application/x-ndjson',
      body: completed(request.turnId, {
        reply: 'Nothing to do.',
        acknowledgement: null,
        anchorPrompt: null,
        suggestions: [],
        changeset: null,
        capturedNoteId: null,
        executedCapabilities: [],
        sources: [],
        evidence: [],
        error: null,
        resolvedBook: null,
      }),
    });
  });

  await page.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await openAssistant(page);

  // 1. Empty state: every door prefills, and none sends.
  const panel = page.getByTestId('assistant-panel');
  const composer = page.getByTestId('assistant-composer');
  await expect(page.getByTestId('assistant-empty')).toBeVisible();

  for (const [door, fill] of [
    ['assistant-example-library-next', 'What should I read next from my library?'],
    ['assistant-example-capture-thought', 'Save this thought: '],
    ['assistant-example-organise-collection', 'Move my Le Guin books into a collection'],
  ] as const) {
    await page.getByTestId(door).click();
    await expect(composer).toHaveValue(fill);
    expect(bodies.length, `${door} must prefill without sending`).toBe(0);
  }
  await expect(page.getByTestId('assistant-empty')).toBeVisible();
  await composer.fill('');

  // 2. An answer with numbered citations and one grouped, numbered source row.
  await send(page, ANSWER_MESSAGE);
  const answer = page.getByTestId('assistant-entry-markdown').last();
  await expect(answer).toContainText('Attention is the scarce resource.');
  await expect(answer.locator('sup.assistant-cite')).toHaveCount(2);

  const group = page.getByTestId('assistant-source-group').last();
  await expect(group).toContainText('The Salt Meridian · 2 passages');
  await expect(group.locator('.entry-source-number')).toHaveText('1–2');
  await group.click();
  await expect(page.getByTestId('assistant-source-item')).toHaveCount(2);
  await expect(page.getByTestId('assistant-source-item').first()).toContainText('p. 42');
  await expect(page.getByTestId('assistant-source-item').last()).toContainText('p. 57');

  // 3. A changeset review arrives with its title and Apply in the transcript.
  await send(page, REVIEW_MESSAGE);
  const card = page.getByTestId('assistant-changeset').last();
  await expect(card).toBeVisible();
  await expect(card).toContainText('Review before applying');
  await expect(card.getByTestId('assistant-changeset-headline')).toContainText(
    'This will create 2 collections',
  );
  const apply = card.getByTestId('assistant-changeset-apply');
  await expect(apply).toHaveText('Apply changes');
  await expect.poll(() => withinTranscript(page, card.getByText('Review before applying', { exact: true }))).toBe(true);
  await expect.poll(() => withinTranscript(page, card.getByTestId('assistant-changeset-headline'))).toBe(true);
  await expect.poll(() => withinTranscript(page, apply)).toBe(true);

  // 4. One failure, one sentence, one Try again; the replay keeps the TurnId.
  await send(page, FAILURE_MESSAGE);
  const failure = page.getByTestId('assistant-failure');
  await expect(failure).toHaveCount(1);
  await expect(failure).toContainText(FAILURE_SENTENCE);
  await expect(panel).not.toContainText('Failed ·');
  const details = failure.getByTestId('assistant-failure-details');
  await expect(details).not.toHaveAttribute('open');
  await expect(details).toContainText('assistant_provider_timeout');

  const failedTurn = bodies[bodies.length - 1];
  const retry = failure.getByTestId('assistant-retry');
  await expect(retry).toHaveText('Try again');
  await retry.click();
  await expect(page.getByTestId('assistant-entry-markdown').last()).toContainText(
    'Here is the answer, on the second delivery.',
  );
  await expect(page.getByTestId('assistant-failure')).toHaveCount(0);

  const firstFailureDelivery = bodies.filter((body) => body.message === FAILURE_MESSAGE);
  expect(firstFailureDelivery.length).toBe(2);
  expect(firstFailureDelivery[1].turnId).toBe(failedTurn.turnId);
  expect(firstFailureDelivery[1].idempotencyKey).toBe(failedTurn.idempotencyKey);

  // 5. A capture that committed before the failure reads above the failure.
  await send(page, SAVED_FAILURE_MESSAGE);
  await expect(page.getByTestId('assistant-failure')).toHaveCount(1);
  const transcript = page.getByTestId('assistant-transcript');
  const savedFailure = page.getByTestId('assistant-failure');
  await expect(savedFailure).toContainText(SAVED_FAILURE_SENTENCE);
  const text = (await transcript.textContent()) ?? '';
  expect(text).toContain('Saved to The Salt Meridian.');
  expect(text).toContain('Saved note');
  expect(text.indexOf('Saved to The Salt Meridian.')).toBeLessThan(
    text.indexOf(SAVED_FAILURE_SENTENCE),
  );
  expect(text.indexOf('Saved note')).toBeLessThan(text.indexOf(SAVED_FAILURE_SENTENCE));
  await expect(savedFailure.getByTestId('assistant-retry')).toHaveText('Try again');

  return bodies;
}

test.describe('Ask Nostos panel states at 1440 px', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('walks empty, answer, review, failure, retry and saved-then-failed', async ({ page }) => {
    await enableAskNostos(page);
    await walkPanelStates(page);
  });
});

test.describe('Ask Nostos panel states at 390 px', () => {
  test.use({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });

  test('walks the same states on a phone', async ({ page }) => {
    await enableAskNostos(page);
    await walkPanelStates(page);
  });
});
