#!/usr/bin/env node
/**
 * Before/after evidence for #1557 (a tall review card's title and Apply stay
 * in view). Runs against the existing Playwright fixture and stubs only the
 * assistant's turn stream — no model is configured or called.
 *
 * Usage, from Nostos.Frontend/:
 *   node e2e/support/launch-fixture.mjs launch
 *   node e2e/support/assistant-tall-review-shots.mjs before
 *
 * `before` is the #1566 branch head (the stacked PR's base), `after` is this
 * PR's head. Each run writes four 1:1 shots: desktop 1440 and phone 390, in
 * light and dark.
 */
import { chromium } from 'playwright';
import { readFileSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const FRONTEND_DIR = path.resolve(HERE, '../..');
const REPO_ROOT = path.resolve(FRONTEND_DIR, '..');
const OUT = path.join(REPO_ROOT, 'Nostos.Frontend', 'e2e', 'visual-evidence', '1557-tall-review');
const fixture = JSON.parse(readFileSync(path.join(HERE, 'fixture-state.json'), 'utf8'));
const BASE = fixture.baseUrl;

const prefix = process.argv[2];
if (prefix !== 'before' && prefix !== 'after') {
  console.error('usage: assistant-tall-review-shots.mjs <before|after>');
  process.exit(2);
}

mkdirSync(OUT, { recursive: true });

const PREFERENCES = {
  viewMode: 'grid',
  sort: 'lastread',
  pageSize: 20,
  sidebarExpanded: true,
  groupByWork: true,
  assistantEnabled: true,
  assistantVoiceEnabled: false,
};

function longReview() {
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
        { id: 'c1', kind: 'created', name: 'Fiction', previousName: null, parentName: null, previousParentName: null, parentChanged: false, bookCount: 0 },
        { id: 'c2', kind: 'deleted', name: 'Old fiction', previousName: null, parentName: null, previousParentName: null, parentChanged: false, bookCount: 18 },
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

function completedTurn(turnId) {
  return (
    JSON.stringify({
      turnId,
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
    }) + '\n'
  );
}

async function capture(browser, { width, height, theme, mobile }) {
  const context = await browser.newContext({
    viewport: { width, height },
    deviceScaleFactor: 1,
    isMobile: mobile,
    hasTouch: mobile,
    colorScheme: theme,
    locale: 'en-GB',
  });
  await context.addInitScript(
    ([prefs, chosenTheme]) => {
      localStorage.setItem('nostos.library.preferences', prefs);
      localStorage.setItem('nostos.theme', chosenTheme);
    },
    [JSON.stringify(PREFERENCES), theme],
  );
  await context.route('**/api/assistant/status', (route) =>
    route.fulfill({ json: { available: true } }),
  );
  await context.route('**/api/assistant/turn/stream', async (route) => {
    const request = route.request().postDataJSON();
    await route.fulfill({
      contentType: 'application/x-ndjson',
      body: completedTurn(request.turnId),
    });
  });

  const page = await context.newPage();
  await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => document.fonts.ready).catch(() => {});
  await page.waitForTimeout(400);

  await page.keyboard.press('Control+j');
  await page.getByTestId('assistant-panel').waitFor({ state: 'visible' });
  await page.getByTestId('assistant-composer').fill('Regroup everything into Fiction and Essays.');
  await page.getByTestId('assistant-send').click();
  await page.getByTestId('assistant-changeset').waitFor({ timeout: 20_000 });
  await page.waitForTimeout(700);

  const name = `${prefix}-${width}-${theme}.png`;
  await page.screenshot({ path: path.join(OUT, name), animations: 'disabled' });
  console.log(`[shot] ${path.relative(REPO_ROOT, path.join(OUT, name))}`);

  const measurements = await page.evaluate(() => {
    const body = document.querySelector('[data-testid="assistant-body"]');
    const rect = (selector) =>
      document.querySelector(selector)?.getBoundingClientRect().toJSON() ?? null;
    return {
      bodyTop: body?.getBoundingClientRect().top ?? null,
      bodyBottom: body?.getBoundingClientRect().bottom ?? null,
      scrollTop: body?.scrollTop ?? null,
      scrollHeight: body?.scrollHeight ?? null,
      clientHeight: body?.clientHeight ?? null,
      label: rect('.changeset__label'),
      headline: rect('.changeset__headline'),
      firstStep: rect('.changeset__step'),
      apply: rect('[data-testid="assistant-changeset-apply"]'),
    };
  });
  console.log(`[measure] ${name} ${JSON.stringify(measurements)}`);

  await context.close();
}

const browser = await chromium.launch();
try {
  await capture(browser, { width: 1440, height: 900, theme: 'light', mobile: false });
  await capture(browser, { width: 1440, height: 900, theme: 'dark', mobile: false });
  await capture(browser, { width: 390, height: 844, theme: 'light', mobile: true });
  await capture(browser, { width: 390, height: 844, theme: 'dark', mobile: true });
} finally {
  await browser.close();
}
