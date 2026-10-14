/**
 * The transcript follows the newest turn (issue #300).
 *
 * WHY THIS IS AN E2E SPEC
 * -----------------------
 * "Is the reply on screen without touching the scrollbar?" is a question about
 * layout: the panel's real height, the transcript's real height, and the
 * browser's real scroll position. jsdom reports 0 for all three, so the unit
 * suite can only assert the arithmetic against stated numbers
 * (`assistant.component.spec.ts`, "following the newest turn"). The numbers
 * here are measured in a real browser on the built app.
 *
 * HONEST LIMITS
 * -------------
 *  - The LLM is not called. `/api/assistant/status` and `/api/assistant/turn`
 *    are fulfilled in the browser with the real response shapes, so the spec
 *    says nothing about whether a gateway key exists on the machine running it.
 *    What it exercises is the app's own scroll behaviour.
 *  - The reply is one long paragraph, which is what makes the pane scrollable;
 *    replies arrive whole today, so there is no streaming position to follow.
 *  - Desktop pane only. The same component drives the mobile sheet, and the
 *    software-keyboard resize path cannot be exercised headlessly.
 *  - The service worker is blocked: it would answer `/api/**` itself and
 *    swallow `page.route` (the production build registers it).
 */
import { expect, test, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';
import { enableAssistant } from './support/assistant';

test.use({ serviceWorkers: 'block' });

let fixture: ReturnType<typeof loadFixture>;

/** Long enough that one reply alone overflows the pane's ~470px of height. */
const FILLER = Array.from(
  { length: 12 },
  (_, index) =>
    `Point ${index + 1}: the transcript keeps whichever scroll position the reader gave it, ` +
    'so bringing the newest turn into view is the surface\u2019s job and not the reader\u2019s.',
).join(' ');

let turns = 0;
/** When set, the next reply waits here until the test opens it. */
let replyGate: Promise<void> | null = null;
let openReplyGate: () => void = () => {};

test.beforeEach(() => {
  turns = 0;
  replyGate = null;
  openReplyGate = () => {};
});

test.beforeAll(() => {
  fixture = loadFixture();
});

async function mockAssistant(page: Page): Promise<void> {
  await page.route('**/api/assistant/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ available: true }),
    }),
  );

  await page.route('**/api/assistant/turn/stream', async (route) => {
    turns += 1;
    const reply = `Reply ${turns}. ${FILLER}`;
    if (replyGate) await replyGate;
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        reply,
        acknowledgement: null,
        anchorPrompt: null,
        suggestions: [],
        changeset: null,
        capturedNoteId: null,
      }),
    });
  });
}

interface Pane {
  scrollTop: number;
  clientHeight: number;
  scrollHeight: number;
}

function transcript(page: Page) {
  return page.locator('[data-testid="assistant-transcript"]');
}

async function pane(page: Page): Promise<Pane> {
  return page.locator('[data-testid="assistant-body"]').evaluate((element) => ({
    scrollTop: element.scrollTop,
    clientHeight: element.clientHeight,
    scrollHeight: element.scrollHeight,
  }));
}

function distanceToEnd(view: Pane): number {
  return view.scrollHeight - view.scrollTop - view.clientHeight;
}

async function openAssistant(page: Page): Promise<void> {
  await page.locator('[data-testid="assistant-trigger"]').click();
  await expect(page.locator('[data-testid="assistant-panel"]')).toBeVisible();
}

async function ask(page: Page, text: string): Promise<void> {
  const composer = page.locator('[data-testid="assistant-composer"]');
  await composer.fill(text);
  await composer.press('Enter');
}

/** A real wheel scroll over the transcript, as a reader's hand would do it. */
async function wheelOverPane(page: Page, deltaY: number): Promise<void> {
  await page.locator('[data-testid="assistant-body"]').hover();
  await page.mouse.wheel(0, deltaY);
}

/** Two long turns: enough transcript that the pane scrolls for real. */
async function buildHistory(page: Page): Promise<void> {
  await ask(page, 'What are you reading?');
  await expect(transcript(page)).toContainText('Reply 1.');
  await ask(page, 'And what did I highlight last week?');
  await expect(transcript(page)).toContainText('Reply 2.');
}

test.beforeEach(async ({ page }) => {
  await mockAssistant(page);
  await enableAssistant(page);
  await page.goto(`${fixture.baseUrl}/library`);
});

test('an arriving reply is on screen without a manual scroll', async ({ page }) => {
  await openAssistant(page);

  await ask(page, 'What are you reading?');
  await expect(transcript(page)).toContainText('Reply 1.');

  const view = await pane(page);
  // The pane really does scroll: without this the position below is 0 for free
  // and the assertion would pass on a pane that never needed following.
  expect(view.scrollHeight - view.clientHeight).toBeGreaterThan(200);
  expect(distanceToEnd(view)).toBeLessThanOrEqual(1);
});

test('a reply that lands while an older turn is being read does not move the view', async ({
  page,
}) => {
  await openAssistant(page);
  await buildHistory(page);

  replyGate = new Promise<void>((resolve) => {
    openReplyGate = resolve;
  });
  await ask(page, 'One more thing, take your time.');
  await expect(page.locator('[data-testid="assistant-pending"]')).toBeVisible();

  // The wait is long, so the reader goes back into the conversation.
  await wheelOverPane(page, -400);
  await expect.poll(async () => (await pane(page)).scrollTop).toBeGreaterThan(0);

  const before = await pane(page);
  expect(distanceToEnd(before)).toBeGreaterThan(50);

  openReplyGate();
  await expect(transcript(page)).toContainText('Reply 3.');

  const after = await pane(page);
  expect(Math.abs(after.scrollTop - before.scrollTop)).toBeLessThanOrEqual(2);
});

test('the reader own message returns to the end from wherever they had scrolled', async ({
  page,
}) => {
  await openAssistant(page);
  await buildHistory(page);

  await wheelOverPane(page, -400);
  await expect.poll(async () => (await pane(page)).scrollTop).toBeGreaterThan(0);
  expect(distanceToEnd(await pane(page))).toBeGreaterThan(50);

  await ask(page, 'Answer this one.');
  await expect.poll(async () => distanceToEnd(await pane(page))).toBeLessThanOrEqual(1);

  await expect(transcript(page)).toContainText('Reply 3.');
});

test('reopening the surface lands on the newest turn', async ({ page }) => {
  await openAssistant(page);
  await buildHistory(page);

  await wheelOverPane(page, -400);
  await expect.poll(async () => (await pane(page)).scrollTop).toBeGreaterThan(0);

  await page.keyboard.press('Escape');
  await expect(page.locator('[data-testid="assistant-panel"]')).toBeHidden();

  await openAssistant(page);
  await expect.poll(async () => distanceToEnd(await pane(page))).toBeLessThanOrEqual(1);
});
