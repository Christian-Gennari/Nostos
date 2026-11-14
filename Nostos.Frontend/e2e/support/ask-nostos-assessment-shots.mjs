#!/usr/bin/env node
/**
 * Evidence capture for the Ask Nostos assessment (#1464).
 *
 * Runs against the existing Playwright fixture (`e2e/support/launch-fixture.mjs
 * launch`), seeds a small library through the public REST API, stubs the
 * assistant's turn (no model is configured or called) and writes 1:1
 * screenshots plus their measured styles to `docs/ask-nostos-assessment/`.
 *
 * Usage, from Nostos.Frontend/:
 *   node e2e/support/launch-fixture.mjs launch
 *   node e2e/support/ask-nostos-assessment-shots.mjs
 *
 * Nothing here is part of the test suite: the file name is not *.spec.ts.
 */
import { chromium } from 'playwright';
import { readFileSync, mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '../../..');
const OUT = path.join(REPO_ROOT, 'docs', 'ask-nostos-assessment');
const EPUB = readFileSync(path.join(HERE, '..', 'assets', 'tiny.epub'));
const fixture = JSON.parse(readFileSync(path.join(HERE, 'fixture-state.json'), 'utf8'));
const BASE = fixture.baseUrl;

mkdirSync(OUT, { recursive: true });

const SHOTS = [];
const MEASUREMENTS = {};

async function api(method, urlPath, body) {
  const res = await fetch(`${BASE}${urlPath}`, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new Error(`${method} ${urlPath} -> ${res.status}: ${await res.text()}`);
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

async function seed() {
  const existing = await api('GET', '/api/books?pageSize=1');
  if ((existing?.items?.length ?? 0) > 0) {
    console.log('[seed] library already has books, not reseeding');
    const all = await api('GET', '/api/books?pageSize=100');
    return all.items.map((b) => ({ id: b.id, title: b.title, author: b.author }));
  }

  console.log('[seed] creating books, collections, notes and a Studio document');
  const specs = [
    { type: 'ebook', title: 'The Magic Mountain', author: 'Thomas Mann', categories: 'Fiction', pageCount: 720 },
    { type: 'ebook', title: 'The Dispossessed', author: 'Ursula K. Le Guin', categories: 'Fiction', pageCount: 387 },
    { type: 'ebook', title: 'A Wizard of Earthsea', author: 'Ursula K. Le Guin', categories: 'Fiction', pageCount: 183 },
    { type: 'audiobook', title: 'The Left Hand of Darkness', author: 'Ursula K. Le Guin', narrator: 'A reader', categories: 'Fiction' },
    { type: 'ebook', title: 'How to Do Nothing', author: 'Jenny Odell', categories: 'Essays', pageCount: 256 },
    { type: 'physical', title: 'Piranesi', author: 'Susanna Clarke', categories: 'Fiction', pageCount: 245 },
    { type: 'ebook', title: 'The Odyssey', author: 'Homer', translator: 'Emily Wilson', categories: 'Classics' },
  ];
  const books = [];
  for (const spec of specs) {
    const created = await api('POST', '/api/books/', spec);
    const book = created?.book ?? created;
    books.push({ id: book.id, title: spec.title, author: spec.author });
  }

  const philosophy = await api('POST', '/api/collections/', { name: 'Philosophy' });
  const leguin = await api('POST', '/api/collections/', { name: 'Le Guin' });
  await api('POST', '/api/collections/', { name: 'To reread', parentId: philosophy.id });

  const byTitle = (t) => books.find((b) => b.title === t);
  await api('PUT', `/api/books/${byTitle('The Dispossessed').id}/collections`, {
    collectionIds: [leguin.id],
  });
  await api('PUT', `/api/books/${byTitle('A Wizard of Earthsea').id}/collections`, {
    collectionIds: [leguin.id],
  });
  await api('PUT', `/api/books/${byTitle('The Magic Mountain').id}/collections`, {
    collectionIds: [philosophy.id],
  });

  const magic = byTitle('The Magic Mountain').id;
  await api('POST', `/api/books/${magic}/notes`, {
    content:
      'Mann keeps making time do something physical: the sanatorium has no seasons, only the rhythm of meals and rest. Attention is what the place is really short of.',
  });
  await api('POST', `/api/books/${magic}/notes`, {
    content:
      'The argument with Settembrini is about whether a life should be organised around work or around receptivity. Neither of them convinces Hans, and that may be the point.',
  });
  await api('POST', `/api/books/${magic}/notes`, {
    content: '',
    selectedText:
      'A man who is not yet ready for his own life is perhaps only a guest in time.',
  });

  const folder = await api('POST', '/api/writings/', {
    name: 'Essays',
    type: 'Folder',
    parentId: null,
  });
  const doc = await api('POST', '/api/writings/', {
    name: 'The lighthouse keeper',
    type: 'Document',
    parentId: folder.id,
  });
  await api('PUT', `/api/writings/${doc.id}`, {
    name: 'The lighthouse keeper',
    content:
      '# The lighthouse keeper\n\nA draft about attention, routine and the small architecture of a day.\n',
  });

  return books;
}

const PREFERENCES = {
  viewMode: 'grid',
  sort: 'lastread',
  pageSize: 20,
  sidebarExpanded: true,
  groupByWork: true,
  assistantEnabled: true,
  assistantVoiceEnabled: false,
};

const SOURCES = [];

function citationsResponse(books) {
  const magic = books.find((b) => b.title === 'The Magic Mountain') ?? books[0];
  const odell = books.find((b) => b.title === 'How to Do Nothing') ?? books[1];
  return {
    reply:
      'Attention, not time, is the thing you keep returning to.[^1]\n\n' +
      'In *The Magic Mountain* the sanatorium removes the ordinary markers of time — ' +
      'no work, no seasons worth counting — and what is left is where a person puts their attention.[^2] ' +
      'Odell makes the same move from the other side: the scarce resource is the capacity to notice, ' +
      'and it is spent by default.[^3]',
    acknowledgement: null,
    anchorPrompt: null,
    suggestions: [],
    changeset: null,
    capturedNoteId: null,
    executedCapabilities: [],
    sources: [
      {
        bookId: magic.id,
        bookTitle: magic.title,
        bookAuthor: magic.author,
        format: 'epub',
        sourceSha256: 'a'.repeat(64),
        excerpt:
          'Mann keeps making time do something physical: the sanatorium has no seasons, only the rhythm of meals and rest.',
        locators: [{ type: 'epub', epubCfi: 'epubcfi(/6/4!/4/2/2)', epubSpineIndex: 2 }],
      },
      {
        bookId: magic.id,
        bookTitle: magic.title,
        bookAuthor: magic.author,
        format: 'epub',
        sourceSha256: 'a'.repeat(64),
        excerpt:
          'A man who is not yet ready for his own life is perhaps only a guest in time.',
        locators: [{ type: 'epub', epubCfi: 'epubcfi(/6/8!/4/2/6)', epubSpineIndex: 8 }],
      },
      {
        bookId: odell.id,
        bookTitle: odell.title,
        bookAuthor: odell.author,
        format: 'epub',
        sourceSha256: 'b'.repeat(64),
        excerpt:
          'The point is not to withdraw from the world but to notice what the world is doing while you are busy.',
        locators: [{ type: 'pdf', pdfPageIndex: 41, pdfPageLabel: '42' }],
      },
    ],
    evidence: [],
    resolvedBook: null,
    error: null,
  };
}

function appliedChangeset() {
  return {
    changesetId: 'cs-assessment-applied',
    status: 'applied',
    steps: [
      { index: 0, summary: 'Create collection “Le Guin”', included: true, dependsOn: [] },
      { index: 1, summary: 'File 3 books into “Le Guin”', included: true, dependsOn: [0] },
    ],
    diff: {
      collections: [
        { id: 'c1', kind: 'created', name: 'Le Guin', previousName: null, parentName: null, previousParentName: null, parentChanged: false, bookCount: 3 },
      ],
      memberships: [
        { bookId: 'b1', title: 'The Dispossessed', author: 'Ursula K. Le Guin', added: ['Le Guin'], removed: ['Unsorted'] },
        { bookId: 'b2', title: 'A Wizard of Earthsea', author: 'Ursula K. Le Guin', added: ['Le Guin'], removed: ['Unsorted'] },
        { bookId: 'b3', title: 'The Left Hand of Darkness', author: 'Ursula K. Le Guin', added: ['Le Guin'], removed: [] },
      ],
      bookEdits: [],
      counts: {
        collectionsCreated: 1,
        collectionsDeleted: 0,
        collectionsUpdated: 0,
        booksTouched: 3,
        membershipsAdded: 3,
        membershipsRemoved: 2,
        booksLosingMembership: 2,
        bookEdits: 0,
      },
    },
    diffHash: 'demo-diff-hash',
    actionToken: 'demo-token',
    reviewReasons: [],
    canUndo: true,
  };
}

function reviewChangeset() {
  return {
    changesetId: 'cs-assessment-review',
    status: 'review',
    steps: [
      { index: 0, summary: 'Delete 3 collections', included: true, dependsOn: [] },
      { index: 1, summary: 'Create “Fiction” and “Essays and nature”', included: true, dependsOn: [] },
      { index: 2, summary: 'File 41 books into the new collections', included: true, dependsOn: [0, 1] },
      { index: 3, summary: 'Mark 2 books finished', included: false, dependsOn: [] },
    ],
    diff: {
      collections: [
        { id: 'c2', kind: 'deleted', name: 'Old fiction', previousName: null, parentName: null, previousParentName: null, parentChanged: false, bookCount: 18 },
        { id: 'c3', kind: 'deleted', name: 'To sort', previousName: null, parentName: null, previousParentName: null, parentChanged: false, bookCount: 12 },
        { id: 'c4', kind: 'created', name: 'Fiction', previousName: null, parentName: null, previousParentName: null, parentChanged: false, bookCount: 0 },
        { id: 'c5', kind: 'created', name: 'Essays and nature', previousName: null, parentName: null, previousParentName: null, parentChanged: false, bookCount: 0 },
      ],
      memberships: [
        { bookId: 'b4', title: 'The Magic Mountain', author: 'Thomas Mann', added: ['Fiction'], removed: ['Old fiction'] },
        { bookId: 'b5', title: 'How to Do Nothing', author: 'Jenny Odell', added: ['Essays and nature'], removed: ['To sort'] },
        { bookId: 'b6', title: 'Piranesi', author: 'Susanna Clarke', added: ['Fiction'], removed: ['To sort'] },
      ],
      bookEdits: [],
      counts: {
        collectionsCreated: 2,
        collectionsDeleted: 2,
        collectionsUpdated: 0,
        booksTouched: 41,
        membershipsAdded: 41,
        membershipsRemoved: 30,
        booksLosingMembership: 30,
        bookEdits: 0,
      },
    },
    diffHash: 'demo-review-hash',
    actionToken: 'demo-token',
    reviewReasons: [
      'This would touch 41 books.',
      'It was worked out after reading book text and notes, which can carry instructions you did not write.',
    ],
    canUndo: false,
  };
}

function longReviewChangeset() {
  return {
    changesetId: 'cs-assessment-review-long',
    status: 'review',
    steps: Array.from({ length: 20 }, (_, index) => ({
      index,
      summary: `File ${index + 1} books into a new collection`,
      included: true,
      dependsOn: [],
    })),
    diff: {
      collections: [],
      memberships: [],
      bookEdits: [],
      counts: {
        collectionsCreated: 3,
        collectionsDeleted: 3,
        collectionsUpdated: 2,
        booksTouched: 41,
        membershipsAdded: 41,
        membershipsRemoved: 30,
        booksLosingMembership: 30,
        bookEdits: 6,
      },
    },
    diffHash: 'demo-review-hash',
    actionToken: 'demo-token',
    reviewReasons: [
      'This would touch 41 books.',
      'It would take 30 books out of a collection.',
      'It edits the details of 6 books.',
      'It was worked out after reading book text and notes, which can carry instructions you did not write.',
    ],
    canUndo: false,
  };
}

function turnFor(message, books) {
  if (/note|attention/i.test(message) && !/move|collection|organise|reorgan/i.test(message)) {
    return citationsResponse(books);
  }
  if (/move|collection|organise|reorgan/i.test(message)) {
    return {
      reply: 'I arranged the Le Guin books into a collection of their own.',
      acknowledgement: null,
      anchorPrompt: null,
      suggestions: [],
      changeset: appliedChangeset(),
      capturedNoteId: null,
      executedCapabilities: [],
      sources: [],
      evidence: [],
      resolvedBook: null,
      error: null,
    };
  }
  if (/big|everything|regroup/i.test(message)) {
    return {
      reply: 'Here is what I would change. Nothing has been applied.',
      acknowledgement: null,
      anchorPrompt: null,
      suggestions: [],
      changeset: reviewChangeset(),
      capturedNoteId: null,
      executedCapabilities: [],
      sources: [],
      evidence: [],
      resolvedBook: null,
      error: null,
    };
  }
  if (/next/i.test(message)) {
    return null;
  }
  return citationsResponse(books);
}

function nldjson(turnId, body) {
  return JSON.stringify({ turnId, sequence: 1, kind: 'completed', response: body }) + '\n';
}

async function rawTurn(page, message) {
  await page.locator('[data-testid="assistant-composer"]').fill(message);
  await page.locator('[data-testid="assistant-send"]').click();
}

async function openAssistant(page, { trigger = 'dock' } = {}) {
  if (trigger === 'dock') {
    await page.locator('[data-testid="assistant-trigger"]').first().click();
  } else {
    await page.keyboard.press('Control+j');
  }
  const panel = page.locator('[data-testid="assistant-panel"]');
  await panel.waitFor({ state: 'visible' });
  await settle(page);
  return panel;
}

async function context(browser, { width, height, theme, mobile = false }) {
  const ctx = await browser.newContext({
    viewport: { width, height },
    deviceScaleFactor: 1,
    isMobile: mobile,
    hasTouch: mobile,
    colorScheme: theme,
    locale: 'en-GB',
  });
  await ctx.addInitScript(
    ([prefs, t]) => {
      localStorage.setItem('nostos.library.preferences', prefs);
      localStorage.setItem('nostos.theme', t);
    },
    [JSON.stringify(PREFERENCES), theme],
  );
  return ctx;
}

/**
 * Waits until the page is visually at rest: no finite CSS animation or
 * transition is still running and the document box has held still for three
 * frames. Every capture below used to sleep a few hundred milliseconds hoping
 * the surface had finished moving; this is the condition that sleep stood for.
 */
async function settle(page, selector) {
  await page.evaluate(async (target) => {
    const root = target ? document.querySelector(target) ?? document : document;
    const running = root.getAnimations({ subtree: true }).filter((animation) => {
      const timing = animation.effect?.getComputedTiming?.();
      return typeof timing?.endTime === 'number' && Number.isFinite(timing.endTime);
    });
    await Promise.all(running.map((animation) => animation.finished.catch(() => undefined)));
    const element = target ? document.querySelector(target) : document.documentElement;
    if (!element) return;
    const nextFrame = () => new Promise((resolve) => requestAnimationFrame(() => resolve()));
    let previous = '';
    let unchanged = 0;
    for (let frame = 0; frame < 180 && unchanged < 3; frame += 1) {
      await nextFrame();
      const box = element.getBoundingClientRect();
      const signature = [box.x, box.y, box.width, box.height].join(',');
      unchanged = signature === previous ? unchanged + 1 : 0;
      previous = signature;
    }
  }, selector);
}

async function stable(page) {
  // The library keeps an SSE change stream open, so `networkidle` never
  // arrives. Fonts and a settled layout are enough for a 1:1 capture.
  await page.evaluate(async () => {
    await document.fonts.ready;
  }).catch(() => {});
  await settle(page);
}

async function shot(page, name) {
  const file = path.join(OUT, `${name}.png`);
  await page.screenshot({ path: file, animations: 'disabled' });
  SHOTS.push(path.basename(file));
  console.log(`[shot] ${path.basename(file)}`);
}

async function measure(page, label, selectors) {
  const values = await page.evaluate((wanted) => {
    const out = {};
    for (const [key, selector] of Object.entries(wanted)) {
      const el = document.querySelector(selector);
      if (!el) {
        out[key] = null;
        continue;
      }
      const s = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      out[key] = {
        selector,
        fontFamily: s.fontFamily,
        fontSize: s.fontSize,
        lineHeight: s.lineHeight,
        borderRadius: s.borderRadius,
        border: s.border,
        boxShadow: s.boxShadow,
        backgroundColor: s.backgroundColor,
        color: s.color,
        padding: s.padding,
        width: Math.round(r.width),
        height: Math.round(r.height),
      };
    }
    return out;
  }, selectors);
  MEASUREMENTS[label] = values;
}

async function main() {
  const books = await seed();
  const browser = await chromium.launch();

  // ---------------------------------------------------------------- desktop
  {
    const ctx = await context(browser, { width: 1440, height: 900, theme: 'light' });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    await ctx.route('**/api/assistant/turn/stream', async (route) => {
      const req = route.request().postDataJSON();
      const body = turnFor(req.message, books);
      if (!body) {
        await route.fulfill({
          contentType: 'application/x-ndjson',
          body:
            JSON.stringify({
              turnId: req.turnId,
              sequence: 2,
              kind: 'failed',
              failure: {
                code: 'llm_timeout',
                message: 'Ask Nostos took too long to answer. Try again.',
                retryable: true,
              },
            }) + '\n',
        });
        return;
      }
      await route.fulfill({ contentType: 'application/x-ndjson', body: nldjson(req.turnId, body) });
    });

    const page = await ctx.newPage();
    await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
    await stable(page);

    await shot(page, '01-library-dock-light');
    await measure(page, 'library-light', {
      dock: '.dock-bar',
      bookCard: '.book-card',
      bookTitle: '.book-card .book-title',
    });

    await openAssistant(page);
    await stable(page);
    await shot(page, '02-library-empty-state-light');
    await measure(page, 'assistant-empty-light', {
      panel: '.assistant-panel',
      title: '.assistant-title',
      emptyTitle: '.assistant-empty-title',
      emptyCopy: '.assistant-empty-copy',
      starter: '.starter',
      composer: '.composer-shell',
    });

    // Expanded workspace.
    await page.locator('[data-testid="assistant-expand"]').click();
    await settle(page);
    await shot(page, '03-library-empty-state-expanded-light');
    await page.locator('[data-testid="assistant-expand"]').click();
    await settle(page);

    // A turn with numbered citations.
    await rawTurn(page, 'What do my notes say about attention?');
    await page.getByTestId('assistant-entry-sources').first().waitFor({ timeout: 20_000 });
    await settle(page);
    await shot(page, '04-turn-citations-light');
    await measure(page, 'assistant-answer-light', {
      reply: '.entry-assistant .entry-text',
      source: '.entry-source',
      sourceNumber: '.entry-source-number',
      composer: '.composer-shell',
    });

    // An applied changeset with Undo.
    await rawTurn(page, 'Move my Le Guin books into a new collection.');
    await page.getByTestId('assistant-changeset-undo').waitFor({ timeout: 20_000 });
    await settle(page);
    await shot(page, '05-changed-card-light');
    await measure(page, 'changeset-applied-light', {
      card: '.changeset',
      label: '.changeset__label',
      headline: '.changeset__headline',
      action: '.changeset__actions button',
    });

    // A review changeset with its full diff, and the retryable error beneath.
    await rawTurn(page, 'Regroup everything into Fiction and Essays.');
    await page.getByTestId('assistant-changeset-details-toggle').last().waitFor({ timeout: 20_000 });
    await page.getByTestId('assistant-changeset-details-toggle').last().click();
    await settle(page);
    await shot(page, '06-review-card-light');

    await rawTurn(page, 'Which book should I read next?');
    await page.locator('.entry-error .entry-text').last().waitFor({ timeout: 20_000 });
    await settle(page);
    await shot(page, '07-error-retryable-light');
    await measure(page, 'assistant-error-light', {
      entry: '.entry-error',
      text: '.entry-error .entry-text',
      meta: '.entry-meta',
    });

    await ctx.close();
  }

  // ------------------------------------ a review taller than the panel
  {
    const ctx = await context(browser, { width: 1440, height: 900, theme: 'light' });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    await ctx.route('**/api/assistant/turn/stream', async (route) => {
      const req = route.request().postDataJSON();
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body: nldjson(req.turnId, {
          reply: "Here's what I'd change. Nothing changes until you apply it.",
          acknowledgement: null,
          anchorPrompt: null,
          suggestions: [],
          changeset: longReviewChangeset(),
          capturedNoteId: null,
          executedCapabilities: [],
          sources: [],
          evidence: [],
          resolvedBook: null,
          error: null,
        }),
      });
    });
    const page = await ctx.newPage();
    await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
    await stable(page);
    await openAssistant(page);
    await rawTurn(page, 'Regroup everything into Fiction and Essays.');
    await page.getByTestId('assistant-changeset').waitFor({ timeout: 20_000 });
    await settle(page);
    await shot(page, '06b-review-card-long-light');
    MEASUREMENTS['review-long-light'] = await page.evaluate(() => {
      const body = document.querySelector('[data-testid="assistant-body"]');
      const rect = (selector) =>
        document.querySelector(selector)?.getBoundingClientRect().toJSON() ?? null;
      return {
        bodyTop: body.getBoundingClientRect().top,
        bodyBottom: body.getBoundingClientRect().bottom,
        scrollTop: body.scrollTop,
        scrollHeight: body.scrollHeight,
        clientHeight: body.clientHeight,
        label: rect('.changeset__label'),
        headline: rect('.changeset__headline'),
        apply: rect('[data-testid="assistant-changeset-apply"]'),
      };
    });
    await ctx.close();
  }

  // ------------------------------------------------------- real error path
  {
    const ctx = await context(browser, { width: 1440, height: 900, theme: 'light' });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    const page = await ctx.newPage();
    await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
    await stable(page);
    await openAssistant(page);
    await rawTurn(page, 'What have I been reading lately?');
    await page.locator('.entry-error').waitFor({ timeout: 20_000 });
    await settle(page);
    await shot(page, '08-error-no-provider-light');
    await ctx.close();
  }

  // --------------------------------------------------------------- dark UI
  {
    const ctx = await context(browser, { width: 1440, height: 900, theme: 'dark' });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    await ctx.route('**/api/assistant/turn/stream', async (route) => {
      const req = route.request().postDataJSON();
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body: nldjson(req.turnId, citationsResponse(books)),
      });
    });
    const page = await ctx.newPage();
    await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
    await stable(page);
    await openAssistant(page);
    await shot(page, '09-library-empty-state-dark');
    await rawTurn(page, 'What do my notes say about attention?');
    await page.getByTestId('assistant-entry-sources').first().waitFor({ timeout: 20_000 });
    await settle(page);
    await shot(page, '10-turn-citations-dark');
    await measure(page, 'assistant-answer-dark', {
      panel: '.assistant-panel',
      reply: '.entry-assistant .entry-text',
      source: '.entry-source',
    });
    await ctx.close();
  }

  // --------------------------------------------------------------- anchor
  {
    const ctx = await context(browser, { width: 1440, height: 900, theme: 'light' });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    await ctx.route('**/api/assistant/turn/stream', async (route) => {
      const req = route.request().postDataJSON();
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body: nldjson(req.turnId, {
          reply: 'Which book is this for?',
          acknowledgement: null,
          anchorPrompt: {
            kind: 'book',
            question: 'Which book is this for?',
            continuationId: 'cont-assessment-1',
          },
          suggestions: [],
          changeset: null,
          capturedNoteId: null,
          executedCapabilities: [],
          sources: [],
          evidence: [],
          resolvedBook: null,
          error: null,
        }),
      });
    });
    const page = await ctx.newPage();
    await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
    await stable(page);
    await openAssistant(page);
    await rawTurn(page, 'Keep this thought: a library is a record of attention.');
    await page.getByTestId('assistant-anchor-prompt').waitFor({ timeout: 20_000 });
    await settle(page);
    await shot(page, '11-anchor-prompt-light');
    await ctx.close();
  }

  // -------------------------------------------------------------- pending
  {
    const ctx = await context(browser, { width: 1440, height: 900, theme: 'light' });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    await ctx.route('**/api/assistant/turn/stream', async (route) => {
      const req = route.request().postDataJSON();
      await new Promise((resolve) => setTimeout(resolve, 6000));
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body: nldjson(req.turnId, citationsResponse(books)),
      });
    });
    const page = await ctx.newPage();
    await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
    await stable(page);
    await openAssistant(page);
    await rawTurn(page, 'What have I been reading lately?');
    await page.getByTestId('assistant-pending').waitFor({ timeout: 20_000 });
    await settle(page);
    await shot(page, '12-pending-light');
    await ctx.close();
  }

  // --------------------------------------------------------------- reader
  {
    const ctx = await context(browser, { width: 1440, height: 900, theme: 'light' });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    await ctx.route('**/api/assistant/turn/stream', async (route) => {
      const req = route.request().postDataJSON();
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body: nldjson(req.turnId, citationsResponse(books)),
      });
    });
    const page = await ctx.newPage();
    const seeded = await api('POST', '/api/books/', {
      type: 'ebook',
      title: `The Magic Mountain (reader ${Date.now()})`,
      author: 'Thomas Mann',
    });
    const bookId = (seeded?.book ?? seeded).id;
    const form = new FormData();
    form.append('file', new Blob([EPUB], { type: 'application/epub+zip' }), 'book.epub');
    const uploaded = await fetch(`${BASE}/api/books/${bookId}/file`, { method: 'POST', body: form });
    if (!uploaded.ok) throw new Error(`upload -> ${uploaded.status}`);

    await page.goto(`${BASE}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
    await settle(page);

    await page.getByTestId('reader-chrome-reveal').focus();
    await page.keyboard.press('Enter');
    await settle(page);
    await shot(page, '13-reader-capsule-light');
    await measure(page, 'reader-capsule-light', {
      capsule: '.assistant-trigger .trigger-capsule',
      toolbar: '.reader-toolbar',
    });

    // Selection menu with the Ask Nostos action.
    await page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el) => {
      const doc = el.ownerDocument;
      const text = el.firstChild;
      if (!text) return;
      const range = doc.createRange();
      range.setStart(text, 0);
      range.setEnd(text, Math.min(48, text.textContent?.length ?? 0));
      const selection = doc.getSelection();
      selection?.removeAllRanges();
      selection?.addRange(range);
    });
    await page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el) => {
      el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, button: 0, buttons: 1 }));
      el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, button: 0 }));
    });
    await page.locator('[data-testid="selection-menu"]').waitFor({ timeout: 15_000 });
    await settle(page);
    await shot(page, '14-selection-menu-light');

    // The panel, opened from the capsule, with the reader's starter questions.
    await page.keyboard.press('Escape');
    await settle(page);
    await page.locator('[data-testid="assistant-trigger"]').first().click();
    await page.locator('[data-testid="assistant-panel"]').waitFor({ state: 'visible' });
    await settle(page);
    await shot(page, '15-reader-empty-state-light');
    await ctx.close();
  }

  // --------------------------------------------------------------- studio
  {
    const ctx = await context(browser, { width: 1440, height: 900, theme: 'light' });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    const page = await ctx.newPage();
    await page.goto(`${BASE}/studio`, { waitUntil: 'domcontentloaded' });
    await stable(page);
    await shot(page, '16-studio-dock-light');
    await openAssistant(page);
    await shot(page, '17-studio-empty-state-light');
    await ctx.close();
  }

  // --------------------------------------------------------- book detail
  {
    const ctx = await context(browser, { width: 1440, height: 900, theme: 'light' });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    const page = await ctx.newPage();
    const book = books.find((b) => b.title === 'The Magic Mountain') ?? books[0];
    await page.goto(`${BASE}/library/${book.id}`, { waitUntil: 'domcontentloaded' });
    await stable(page);
    await openAssistant(page);
    await shot(page, '18-book-detail-empty-state-light');
    await ctx.close();
  }

  // ------------------------------------------------------------ settings
  {
    const ctx = await context(browser, { width: 1440, height: 900, theme: 'light' });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    const page = await ctx.newPage();
    await page.goto(`${BASE}/settings?section=assistant`, { waitUntil: 'domcontentloaded' });
    await stable(page);
    const card = page.getByTestId('assistant-settings-card');
    await card.waitFor({ timeout: 20_000 }).catch(() => {});
    await shot(page, '19-settings-assistant-light');
    await ctx.close();
  }

  // ---------------------------------------------------------------- phone
  {
    const ctx = await context(browser, { width: 390, height: 844, theme: 'light', mobile: true });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    await ctx.route('**/api/assistant/turn/stream', async (route) => {
      const req = route.request().postDataJSON();
      const body = /move|collection/i.test(req.message)
        ? {
            reply: 'I arranged the Le Guin books into a collection of their own.',
            acknowledgement: null,
            anchorPrompt: null,
            suggestions: [],
            changeset: appliedChangeset(),
            capturedNoteId: null,
            executedCapabilities: [],
            sources: [],
            evidence: [],
            resolvedBook: null,
            error: null,
          }
        : citationsResponse(books);
      await route.fulfill({ contentType: 'application/x-ndjson', body: nldjson(req.turnId, body) });
    });
    const page = await ctx.newPage();
    await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
    await stable(page);
    await shot(page, '20-library-dock-phone-light');
    await openAssistant(page);
    await shot(page, '21-library-empty-state-phone-light');
    await rawTurn(page, 'What do my notes say about attention?');
    await page.getByTestId('assistant-entry-sources').first().waitFor({ timeout: 20_000 });
    await settle(page);
    await shot(page, '22-turn-citations-phone-light');
    await rawTurn(page, 'Move my Le Guin books into a new collection.');
    await page.getByTestId('assistant-changeset-undo').waitFor({ timeout: 20_000 });
    await settle(page);
    await shot(page, '23-changed-card-phone-light');
    await ctx.close();
  }

  {
    const ctx = await context(browser, { width: 390, height: 844, theme: 'dark', mobile: true });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    const page = await ctx.newPage();
    await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
    await stable(page);
    await openAssistant(page);
    await shot(page, '24-library-empty-state-phone-dark');
    await ctx.route('**/api/assistant/turn/stream', async (route) => {
      const req = route.request().postDataJSON();
      await route.fulfill({
        contentType: 'application/x-ndjson',
        body:
          JSON.stringify({
            turnId: req.turnId,
            sequence: 2,
            kind: 'failed',
            failure: {
              code: 'ai_usage_limit_reached',
              message: 'Your AI allowance is used up for now.',
              retryable: false,
            },
          }) + '\n',
      });
    });
    await rawTurn(page, 'What have I been reading lately?');
    await page.locator('.entry-error').waitFor({ timeout: 20_000 });
    await settle(page);
    await shot(page, '25-error-allowance-phone-dark');
    await ctx.close();
  }

  // Phone reader: capsule and the touch selection bar.
  {
    const ctx = await context(browser, { width: 390, height: 844, theme: 'dark', mobile: true });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    const page = await ctx.newPage();
    const seeded = await api('POST', '/api/books/', {
      type: 'ebook',
      title: `A Wizard of Earthsea (reader ${Date.now()})`,
      author: 'Ursula K. Le Guin',
    });
    const bookId = (seeded?.book ?? seeded).id;
    const form = new FormData();
    form.append('file', new Blob([EPUB], { type: 'application/epub+zip' }), 'book.epub');
    const uploaded = await fetch(`${BASE}/api/books/${bookId}/file`, { method: 'POST', body: form });
    if (!uploaded.ok) throw new Error(`upload -> ${uploaded.status}`);

    await page.goto(`${BASE}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
    await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
    await settle(page);
    await page.getByTestId('reader-chrome-reveal').focus();
    await page.keyboard.press('Enter');
    await settle(page);
    await shot(page, '26-reader-capsule-phone-dark');

    await page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el) => {
      const doc = el.ownerDocument;
      const text = el.firstChild;
      if (!text) return;
      const range = doc.createRange();
      range.setStart(text, 0);
      range.setEnd(text, Math.min(40, text.textContent?.length ?? 0));
      const selection = doc.getSelection();
      selection?.removeAllRanges();
      selection?.addRange(range);
    });
    await page.frameLocator('#epub-viewer iframe').locator('p').first().evaluate((el) => {
      el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, button: 0, buttons: 1 }));
      el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, button: 0 }));
    });
    await page.locator('[data-testid="selection-bar"]').waitFor({ timeout: 15_000 }).catch(() => {});
    await settle(page);
    await shot(page, '27-selection-bar-phone-dark');
    await ctx.close();
  }

  // ------------------------------------------------------ command palette
  {
    const ctx = await context(browser, { width: 1440, height: 900, theme: 'light' });
    await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
    const page = await ctx.newPage();
    await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
    await stable(page);
    await page.keyboard.press('Control+k');
    const paletteInput = page.getByPlaceholder('Search everything…');
    await paletteInput.waitFor({ timeout: 15_000 }).catch(() => {});
    await paletteInput.fill('attention').catch(() => {});
    await settle(page);
    await shot(page, '28-command-palette-light');
    await ctx.close();
  }

  await browser.close();
  writeFileSync(
    path.join(OUT, 'measurements.json'),
    JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl: BASE, shots: SHOTS, measurements: MEASUREMENTS }, null, 2),
  );
  console.log(`[done] ${SHOTS.length} screenshots + measurements.json in ${OUT}`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
