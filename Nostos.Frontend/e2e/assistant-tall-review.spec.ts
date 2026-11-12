/**
 * A review taller than the panel must arrive with its title and Apply in view
 * (#1557).
 *
 * The assistant turn is stubbed the same way `reader-ask-quote-highlight.spec.ts`
 * stubs one: the real Angular app and the real backend run, and only the
 * `/api/assistant/turn/stream` response is scripted. No model is called.
 *
 * The card has 20 steps and several changed collections, so its content is
 * taller than the 485 px desktop transcript and the phone's full-screen
 * transcript. Before this fix the transcript followed to the end, putting
 * "Review before applying", the headline and the first steps above the
 * scrollport. The check is geometric, not `toBeInViewport`: content clipped by
 * the transcript's own overflow is still inside the browser viewport.
 */
import { expect, test, type Locator, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';

test.use({ serviceWorkers: 'block' });

interface Changeset {
  changesetId: string;
  status: string;
  steps: { index: number; summary: string; included: boolean; dependsOn: number[] }[];
  diff: {
    collections: {
      id: string;
      kind: string;
      name: string;
      previousName: string | null;
      parentName: string | null;
      previousParentName: string | null;
      parentChanged: boolean;
      bookCount: number;
    }[];
    memberships: {
      bookId: string;
      title: string;
      author: string | null;
      added: string[];
      removed: string[];
    }[];
    bookEdits: never[];
    counts: {
      collectionsCreated: number;
      collectionsDeleted: number;
      collectionsUpdated: number;
      booksTouched: number;
      membershipsAdded: number;
      membershipsRemoved: number;
      booksLosingMembership: number;
      bookEdits: number;
    };
  };
  diffHash: string;
  actionToken: string;
  reviewReasons: string[];
  canUndo: boolean;
}

/** A review taller than the transcript at both 1440 and 390 px. */
function longReview(): Changeset {
  return {
    changesetId: 'cs-1557-review-long',
    status: 'review',
    steps: Array.from({ length: 20 }, (_, index) => ({
      index,
      summary: `File ${index + 1} books into ${index === 19 ? 'To reread' : 'Fiction'}`,
      included: true,
      dependsOn: [],
    })),
    diff: {
      collections: [
        {
          id: 'c1', kind: 'created', name: 'Fiction', previousName: null, parentName: null,
          previousParentName: null, parentChanged: false, bookCount: 0,
        },
        {
          id: 'c2', kind: 'deleted', name: 'Old fiction', previousName: null, parentName: null,
          previousParentName: null, parentChanged: false, bookCount: 18,
        },
      ],
      memberships: [
        { bookId: 'b1', title: 'The Magic Mountain', author: 'Thomas Mann', added: ['Fiction'], removed: ['Old fiction'] },
        { bookId: 'b2', title: 'The Dispossessed', author: 'Ursula K. Le Guin', added: ['Fiction'], removed: ['Old fiction'] },
        { bookId: 'b3', title: 'Piranesi', author: 'Susanna Clarke', added: ['Fiction'], removed: ['Old fiction'] },
      ],
      bookEdits: [],
      counts: {
        collectionsCreated: 1,
        collectionsDeleted: 1,
        collectionsUpdated: 0,
        booksTouched: 20,
        membershipsAdded: 20,
        membershipsRemoved: 18,
        booksLosingMembership: 18,
        bookEdits: 0,
      },
    },
    diffHash: 'demo-review-hash',
    actionToken: 'demo-token',
    reviewReasons: [
      'This would touch 20 books.',
      'It deletes a collection that holds 18 books.',
      'It was worked out after reading book text and notes, which can carry instructions you did not write.',
    ],
    canUndo: false,
  };
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

async function sendReviewTurn(page: Page): Promise<void> {
  await page.route('**/api/assistant/turn/stream', async (route) => {
    const request = route.request().postDataJSON() as { turnId: string };
    await route.fulfill({
      contentType: 'application/x-ndjson',
      body:
        JSON.stringify({
          turnId: request.turnId,
          sequence: 1,
          kind: 'completed',
          response: {
            reply: "Here's what I'd change. Nothing changes until you apply it.",
            acknowledgement: null,
            anchorPrompt: null,
            suggestions: [],
            changeset: longReview(),
            capturedNoteId: null,
            executedCapabilities: [],
            sources: [],
            evidence: [],
            resolvedBook: null,
            error: null,
          },
        }) + '\n',
    });
  });

  await page.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await page.keyboard.press('Control+j');
  await expect(page.getByTestId('assistant-panel')).toBeVisible();
  await page.getByTestId('assistant-composer').fill('Regroup everything into Fiction and Essays.');
  await page.getByTestId('assistant-send').click();
  await expect(page.getByTestId('assistant-changeset')).toBeVisible();
}

/** True when the element is painted inside the transcript's own scrollport. */
async function withinTranscript(page: Page, locator: Locator): Promise<boolean> {
  const box = await locator.boundingBox();
  const body = await page.getByTestId('assistant-body').boundingBox();
  if (!box || !body) return false;
  return box.y >= body.y - 1 && box.y + box.height <= body.y + body.height + 1;
}

async function distanceToEnd(page: Page): Promise<number> {
  return page.getByTestId('assistant-body').evaluate(
    (body) => body.scrollHeight - body.scrollTop - body.clientHeight,
  );
}

test.describe('a 20-step review at 1440 px', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('arrives with its title, headline and first step in view', async ({ page }) => {
    await enableAskNostos(page);
    await sendReviewTurn(page);

    const card = page.getByTestId('assistant-changeset');
    const label = card.getByText('Review before applying', { exact: true });
    const headline = page.getByTestId('assistant-changeset-headline');
    const firstStep = card.getByTestId('assistant-changeset-step-toggle').first();
    const apply = page.getByTestId('assistant-changeset-apply');

    await expect.poll(() => withinTranscript(page, label)).toBe(true);
    await expect.poll(() => withinTranscript(page, headline)).toBe(true);
    await expect.poll(() => withinTranscript(page, firstStep)).toBe(true);
    await expect.poll(() => withinTranscript(page, apply)).toBe(true);

    // The card's top is the top of the transcript, not its end.
    const labelOffset = await page.evaluate(() => {
      const label = document.querySelector('.changeset__label')!;
      const body = document.querySelector('[data-testid="assistant-body"]')!;
      return label.getBoundingClientRect().top - body.getBoundingClientRect().top;
    });
    expect(labelOffset).toBeLessThan(40);
    expect(await distanceToEnd(page)).toBeGreaterThan(0);

    // "Show every change" grows the card; the header stays where it was.
    await card.getByTestId('assistant-changeset-details-toggle').click();
    await expect(card.getByTestId('assistant-changeset-details')).toBeVisible();
    await expect.poll(() => withinTranscript(page, label)).toBe(true);
    await expect.poll(() => withinTranscript(page, firstStep)).toBe(true);
    await expect.poll(() => withinTranscript(page, apply)).toBe(true);
    expect(await distanceToEnd(page)).toBeGreaterThan(0);
  });
});

test.describe('a 20-step review at 390 px', () => {
  test.use({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });

  test('arrives with its title, headline and first step in view', async ({ page }) => {
    await enableAskNostos(page);
    await sendReviewTurn(page);

    const card = page.getByTestId('assistant-changeset');
    const label = card.getByText('Review before applying', { exact: true });
    const headline = page.getByTestId('assistant-changeset-headline');
    const firstStep = card.getByTestId('assistant-changeset-step-toggle').first();
    const apply = page.getByTestId('assistant-changeset-apply');

    await expect.poll(() => withinTranscript(page, label)).toBe(true);
    await expect.poll(() => withinTranscript(page, headline)).toBe(true);
    await expect.poll(() => withinTranscript(page, firstStep)).toBe(true);
    await expect.poll(() => withinTranscript(page, apply)).toBe(true);

    const labelOffset = await page.evaluate(() => {
      const label = document.querySelector('.changeset__label')!;
      const body = document.querySelector('[data-testid="assistant-body"]')!;
      return label.getBoundingClientRect().top - body.getBoundingClientRect().top;
    });
    expect(labelOffset).toBeLessThan(40);
    expect(await distanceToEnd(page)).toBeGreaterThan(0);

    await card.getByTestId('assistant-changeset-details-toggle').click();
    await expect(card.getByTestId('assistant-changeset-details')).toBeVisible();
    await expect.poll(() => withinTranscript(page, label)).toBe(true);
    await expect.poll(() => withinTranscript(page, firstStep)).toBe(true);
    await expect.poll(() => withinTranscript(page, apply)).toBe(true);
    expect(await distanceToEnd(page)).toBeGreaterThan(0);
  });
});
