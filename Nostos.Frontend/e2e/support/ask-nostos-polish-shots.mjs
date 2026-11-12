#!/usr/bin/env node
/**
 * Before/after evidence for #1562 (polish the Ask Nostos conversation surface).
 *
 * Runs against the Playwright fixture (`e2e/support/launch-fixture.mjs launch`),
 * seeds a small library through the public REST API, stubs the assistant's turn
 * stream (no model is configured or called) and captures the four surfaces the
 * issue changes: the empty state (compact and expanded), an answer with
 * citations and grouped sources, and an applied changeset card.
 *
 * Usage, from Nostos.Frontend/:
 *   node e2e/support/launch-fixture.mjs launch
 *   node e2e/support/ask-nostos-polish-shots.mjs before
 *   node e2e/support/launch-fixture.mjs kill
 *
 * The phase is part of the path so the same script can run once on the base
 * branch and once on the PR head:
 *   e2e/visual-evidence/assistant-polish-1562/<phase>/
 */
import { chromium } from 'playwright';
import { readFileSync, mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '../../..');
const PHASE = process.argv[2] === 'before' ? 'before' : 'after';
const OUT = process.env.ASK_NOSTOS_SHOTS_OUT
  ? path.resolve(process.env.ASK_NOSTOS_SHOTS_OUT)
  : path.join(
      REPO_ROOT,
      'Nostos.Frontend',
      'e2e',
      'visual-evidence',
      'assistant-polish-1562',
      PHASE,
    );
const fixture = JSON.parse(readFileSync(path.join(HERE, 'fixture-state.json'), 'utf8'));
const BASE = fixture.baseUrl;

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
        excerpt: 'A man who is not yet ready for his own life is perhaps only a guest in time.',
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
    changesetId: 'cs-polish-applied',
    status: 'applied',
    steps: [
      { index: 0, summary: 'Create collection “Le Guin”', included: true, dependsOn: [] },
      { index: 1, summary: 'File 3 books into “Le Guin”', included: true, dependsOn: [0] },
    ],
    diff: {
      collections: [
        {
          id: 'c1',
          kind: 'created',
          name: 'Le Guin',
          previousName: null,
          parentName: null,
          previousParentName: null,
          parentChanged: false,
          bookCount: 3,
        },
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

function turnFor(message, books) {
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
  return citationsResponse(books);
}

function ndjson(turnId, body) {
  return JSON.stringify({ turnId, sequence: 1, kind: 'completed', response: body }) + '\n';
}

async function context(browser, { width, height, theme, mobile = false }, books) {
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
  await ctx.route('**/api/assistant/status', (route) =>
    route.fulfill({ json: { available: true } }),
  );
  await ctx.route('**/api/assistant/turn/stream', async (route) => {
    const request = route.request().postDataJSON();
    await route.fulfill({
      contentType: 'application/x-ndjson',
      body: ndjson(request.turnId, turnFor(request.message, books)),
    });
  });
  return ctx;
}

async function stable(page) {
  await page.evaluate(async () => {
    await document.fonts.ready;
  }).catch(() => {});
  await page.waitForTimeout(500);
}

async function shot(page, name) {
  const file = path.join(OUT, `${name}.png`);
  await page.screenshot({ path: file, animations: 'disabled' });
  console.log(`[shot] ${path.basename(file)}`);
}

async function openAssistant(page) {
  await page.locator('[data-testid="assistant-trigger"]').first().click();
  const panel = page.locator('[data-testid="assistant-panel"]');
  await panel.waitFor({ state: 'visible' });
  await page.waitForTimeout(550);
  return panel;
}

async function rawTurn(page, message) {
  await page.locator('[data-testid="assistant-composer"]').fill(message);
  await page.locator('[data-testid="assistant-send"]').click();
}

async function measure(page, label, selectors) {
  MEASUREMENTS[label] = await page.evaluate((wanted) => {
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
        borderRadius: s.borderRadius,
        width: Math.round(r.width),
        height: Math.round(r.height),
        top: Math.round(r.top),
        left: Math.round(r.left),
      };
    }
    return out;
  }, selectors);
}

async function capture(browser, { width, height, theme, mobile }, books) {
  const ctx = await context(browser, { width, height, theme, mobile }, books);
  const page = await ctx.newPage();
  await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
  await stable(page);
  await openAssistant(page);

  const surface = mobile ? 'phone' : 'desktop';
  await shot(page, `empty-compact-${surface}-${theme}`);
  await measure(page, `empty-compact-${surface}-${theme}`, {
    panel: '[data-testid="assistant-panel"]',
    empty: '.assistant-empty',
    emptyTitle: '.assistant-empty-title',
    door: '.capability-door',
    example: '.capability-example',
  });

  if (!mobile) {
    await page.locator('[data-testid="assistant-expand"]').click();
    await page.waitForTimeout(500);
    await shot(page, `empty-expanded-${surface}-${theme}`);
    await measure(page, `empty-expanded-${surface}-${theme}`, {
      panel: '[data-testid="assistant-panel"]',
      empty: '.assistant-empty',
      emptyTitle: '.assistant-empty-title',
      door: '.capability-door',
      example: '.capability-example',
    });
    await page.locator('[data-testid="assistant-expand"]').click();
    await page.waitForTimeout(500);
  }

  await rawTurn(page, 'What do my notes say about attention?');
  await page.getByTestId('assistant-entry-sources').first().waitFor({ timeout: 20_000 });
  await page.waitForTimeout(500);
  await shot(page, `answer-citations-${surface}-${theme}`);
  await measure(page, `answer-citations-${surface}-${theme}`, {
    composer: '.composer-shell',
    userBubble: '.entry-user',
    source: '.entry-source',
    group: '.entry-source-group',
  });
  MEASUREMENTS[`answer-citations-${surface}-${theme}`].copy = await page.evaluate(() => {
    const reply = document.querySelector('.entry-assistant .entry-markdown');
    const group = document.querySelector('.entry-source-group');
    return {
      replyText: reply?.textContent?.slice(0, 220) ?? null,
      replyHtmlStart: reply?.innerHTML.slice(0, 360) ?? null,
      groupText: group?.textContent?.replace(/\s+/g, ' ').trim() ?? null,
    };
  });

  await rawTurn(page, 'Move my Le Guin books into a new collection.');
  await page.getByTestId('assistant-changeset-undo').waitFor({ timeout: 20_000 });
  await page.waitForTimeout(400);
  await shot(page, `changeset-${surface}-${theme}`);
  await measure(page, `changeset-${surface}-${theme}`, {
    card: '.changeset',
    headline: '.changeset__headline',
  });

  await ctx.close();
}

async function main() {
  const books = await seed();
  const browser = await chromium.launch();
  try {
    for (const theme of ['light', 'dark']) {
      await capture(browser, { width: 1440, height: 900, theme, mobile: false }, books);
      await capture(browser, { width: 390, height: 844, theme, mobile: true }, books);
    }
  } finally {
    await browser.close();
  }

  writeFileSync(path.join(OUT, 'measurements.json'), JSON.stringify(MEASUREMENTS, null, 2));
  console.log(`[done] ${PHASE} -> ${path.relative(REPO_ROOT, OUT)}`);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
