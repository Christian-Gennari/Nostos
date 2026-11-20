#!/usr/bin/env node
/**
 * Before/after evidence for the Ask Nostos panel copy (#1748).
 *
 * Runs against the Playwright fixture (`e2e/support/launch-fixture.mjs launch`),
 * stubs the assistant's turn, the changeset actions and one note read (no model
 * is called), and writes 1:1 panel screenshots for each state the issue lists.
 * Run it once per build (the fixture serves whatever is in its wwwroot) and
 * name the run with --label.
 *
 *   node e2e/support/ask-copy-shots.mjs --label after --out <dir>
 *
 * Not part of the test suite: the file name is not *.spec.ts.
 */
import { chromium } from 'playwright';
import { readFileSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(readFileSync(path.join(HERE, 'fixture-state.json'), 'utf8'));
const BASE = fixture.baseUrl;

const args = process.argv.slice(2);
const option = (name, fallback) => {
  const at = args.indexOf(`--${name}`);
  return at >= 0 ? args[at + 1] : fallback;
};
const LABEL = option('label', 'after');
const OUT = path.resolve(option('out', 'ask-copy-shots'));
mkdirSync(OUT, { recursive: true });

const REVIEW_SENTENCE = "Here's what I'd change. Nothing changes until you apply it.";

const PREFERENCES = {
  viewMode: 'grid',
  sort: 'lastread',
  pageSize: 20,
  sidebarExpanded: true,
  groupByWork: true,
  assistantEnabled: true,
  assistantVoiceEnabled: false,
};

const COUNTS = {
  collectionsCreated: 1,
  collectionsDeleted: 0,
  collectionsUpdated: 0,
  booksTouched: 2,
  membershipsAdded: 2,
  membershipsRemoved: 0,
  booksLosingMembership: 0,
  bookEdits: 0,
};

function changeset(status) {
  return {
    changesetId: 'cs-copy',
    status,
    steps: [
      { index: 0, summary: 'Create collection “Le Guin”', included: true, dependsOn: [] },
      { index: 1, summary: 'Add 2 books to “Le Guin”', included: true, dependsOn: [0] },
    ],
    diff: {
      collections: [
        { id: 'c1', kind: 'created', name: 'Le Guin', previousName: null, parentName: null, previousParentName: null, parentChanged: false, bookCount: 2 },
      ],
      memberships: [
        { bookId: 'b1', title: 'The Dispossessed', author: 'Ursula K. Le Guin', added: ['Le Guin'], removed: [] },
        { bookId: 'b2', title: 'A Wizard of Earthsea', author: 'Ursula K. Le Guin', added: ['Le Guin'], removed: [] },
      ],
      bookEdits: [],
      counts: COUNTS,
    },
    diffHash: 'hash-copy',
    actionToken: 'tok-copy',
    reviewReasons: status === 'review' ? ['Changes 2 books.'] : [],
    canUndo: status === 'applied',
  };
}

function noteEvidence(index, noteId, label, excerpt) {
  return {
    handle: { kind: 'note', noteId },
    label,
    bookTitle: 'Walden',
    bookAuthor: 'Henry David Thoreau',
    excerpt,
    locators: [],
  };
}

function topicEvidence(topicId, label) {
  return { handle: { kind: 'topic', topicId }, label, bookTitle: null, excerpt: `${label} runs through several notes.`, locators: [] };
}

const SCENARIOS = {
  'review-applied': {
    message: 'Regroup the Le Guin books into a collection.',
    response: {
      reply: REVIEW_SENTENCE,
      changeset: changeset('review'),
    },
    steps: ['apply'],
  },
  'review-undone': {
    message: 'Regroup the Le Guin books into a collection.',
    response: {
      reply: REVIEW_SENTENCE,
      changeset: changeset('review'),
    },
    steps: ['apply', 'undo'],
  },
  'result-chips': {
    message: 'Start a draft titled "On attention" and add Piranesi to my To read list.',
    response: {
      reply: 'I started the draft and added Piranesi.',
      executedCapabilities: ['writings_create', 'reading_list_add'],
    },
  },
  'anchor-question': {
    message: 'Save this thought: a library is a record of attention.',
    response: {
      reply: 'Which book is this for?',
      anchorPrompt: { kind: 'book', question: 'Which book is this for?', continuationId: 'cont-copy' },
    },
  },
  'saved-quote': {
    message: 'Save the quote where Harker says he found his smattering of German very useful.',
    response: {
      reply: 'Saved the quote.',
      capturedNoteId: 'note-quote',
    },
  },
  'cited-sources': {
    message: 'What did I note about attention?',
    response: {
      reply:
        'You wrote that attention is chosen [^1], that a day is spent on what you notice [^2], and that solitude protects it [^3].',
      evidence: [
        noteEvidence(1, 'n1', 'Note · Walden', 'Attention is chosen, hour by hour.'),
        noteEvidence(2, 'n2', 'Note · Walden', 'I spend the day on what I notice.'),
        noteEvidence(3, 'n3', 'Note · Walden', 'Solitude protects attention.'),
        topicEvidence('t1', 'Attention'),
        topicEvidence('t2', 'Solitude'),
      ],
    },
  },
};

const turnResponse = (overrides) => ({
  reply: '',
  acknowledgement: null,
  anchorPrompt: null,
  suggestions: [],
  changeset: null,
  capturedNoteId: null,
  executedCapabilities: [],
  sources: [],
  evidence: [],
  ...overrides,
});

async function settle(page) {
  await page.evaluate(async () => {
    await document.fonts.ready;
    const nextFrame = () => new Promise((resolve) => requestAnimationFrame(() => resolve()));
    for (let frame = 0; frame < 12; frame += 1) await nextFrame();
  });
}

async function runScenario(browser, name, scenario, { width, height, theme, mobile }) {
  const ctx = await browser.newContext({
    viewport: { width, height },
    deviceScaleFactor: 1,
    isMobile: mobile,
    hasTouch: mobile,
    colorScheme: theme,
    locale: 'en-GB',
    serviceWorkers: 'block',
  });
  await ctx.addInitScript(
    ([prefs, t]) => {
      localStorage.setItem('nostos.library.preferences', prefs);
      localStorage.setItem('nostos.theme', t);
    },
    [JSON.stringify(PREFERENCES), theme],
  );
  await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
  await ctx.route('**/api/assistant/turn/stream', async (route) => {
    const request = route.request().postDataJSON();
    await route.fulfill({
      contentType: 'application/x-ndjson',
      body: JSON.stringify({ turnId: request.turnId, sequence: 1, kind: 'completed', response: turnResponse(scenario.response) }) + '\n',
    });
  });
  await ctx.route('**/api/assistant/changesets/*/apply', (r) =>
    r.fulfill({ json: { success: true, errorCode: null, errorMessage: null, conflicts: [], changeset: changeset('applied') } }));
  await ctx.route('**/api/assistant/changesets/*/undo', (r) =>
    r.fulfill({ json: { success: true, errorCode: null, errorMessage: null, conflicts: [], changeset: { ...changeset('undone'), canUndo: false } } }));
  await ctx.route('**/api/notes/note-quote', (r) =>
    r.fulfill({
      json: {
        id: 'note-quote',
        bookId: 'b-dracula',
        bookTitle: 'Dracula',
        content: '',
        selectedText: 'I found my smattering of German very useful here; indeed, I don’t know how I should be able to get on without it.',
        snippet: null,
        topicNames: [],
        createdAt: '2026-11-20T00:00:00Z',
      },
    }));

  const page = await ctx.newPage();
  await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
  await page.locator('[data-testid="assistant-trigger"]').first().click();
  const panel = page.locator('[data-testid="assistant-panel"]');
  await panel.waitFor({ state: 'visible' });
  await page.locator('[data-testid="assistant-composer"]').fill(scenario.message);
  await page.locator('[data-testid="assistant-send"]').click();
  await page.locator('.entry-assistant, [data-testid="assistant-anchor-prompt"]').first().waitFor();

  for (const step of scenario.steps ?? []) {
    await page.locator(`[data-testid="assistant-changeset-${step}"]`).click();
    await page.waitForTimeout(250);
  }
  if (name === 'saved-quote') await page.locator('[data-testid="assistant-captured-quote"]').waitFor({ timeout: 4000 }).catch(() => {});
  await settle(page);

  const file = path.join(OUT, `${name}-${LABEL}-${width}-${theme}.png`);
  await panel.screenshot({ path: file, animations: 'disabled' });
  console.log(`[shot] ${path.basename(file)}`);
  await ctx.close();
}

async function main() {
  const browser = await chromium.launch({ executablePath: process.env.PW_CHROMIUM || undefined });
  try {
    for (const [name, scenario] of Object.entries(SCENARIOS)) {
      await runScenario(browser, name, scenario, { width: 1280, height: 800, theme: 'dark', mobile: false });
    }
    // The card flow again at phone width, light theme.
    for (const name of ['review-applied', 'review-undone']) {
      await runScenario(browser, name, SCENARIOS[name], { width: 390, height: 844, theme: 'light', mobile: true });
    }
  } finally {
    await browser.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
