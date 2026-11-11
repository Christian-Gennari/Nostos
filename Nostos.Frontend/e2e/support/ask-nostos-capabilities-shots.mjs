#!/usr/bin/env node
/**
 * Before/after evidence for the Ask Nostos capability copy (#1559).
 *
 * Runs against the Playwright fixture (`e2e/support/launch-fixture.mjs launch`),
 * seeds a small library through the public REST API, and captures the surfaces
 * the issue changes: the empty state (compact and expanded), Settings →
 * Assistant, and the reader selection action, on desktop and phone, light and
 * dark. No model is configured or called.
 *
 * Usage, from Nostos.Frontend/:
 *   node e2e/support/launch-fixture.mjs launch
 *   node e2e/support/ask-nostos-capabilities-shots.mjs before
 *   node e2e/support/launch-fixture.mjs kill
 *
 * The phase is part of the path so the same script can run once on the base
 * branch and once on the PR head:
 *   e2e/visual-evidence/assistant-capabilities-1559/<phase>/
 */
import { chromium } from 'playwright';
import { readFileSync, mkdirSync } from 'node:fs';
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
      'assistant-capabilities-1559',
      PHASE,
    );
const EPUB = readFileSync(path.join(HERE, '..', 'assets', 'reader-margins.epub'));
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
  await ctx.route('**/api/assistant/status', (route) =>
    route.fulfill({ json: { available: true } }),
  );
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

async function emptyStates(browser, { width, height, theme, mobile }) {
  const ctx = await context(browser, { width, height, theme, mobile });
  const page = await ctx.newPage();
  await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
  await stable(page);
  await openAssistant(page);
  await shot(page, `empty-compact-${mobile ? 'phone' : 'desktop'}-${theme}`);

  if (!mobile) {
    await page.locator('[data-testid="assistant-expand"]').click();
    await page.waitForTimeout(500);
    await shot(page, `empty-expanded-desktop-${theme}`);
  }
  await ctx.close();
}

async function settings(browser, { width, height, theme, mobile }) {
  const ctx = await context(browser, { width, height, theme, mobile });
  const page = await ctx.newPage();
  await page.goto(`${BASE}/settings?section=assistant`, { waitUntil: 'domcontentloaded' });
  await stable(page);
  await page.getByTestId('assistant-settings-card').waitFor({ timeout: 20_000 }).catch(() => {});
  await shot(page, `settings-assistant-${mobile ? 'phone' : 'desktop'}-${theme}`);
  await ctx.close();
}

async function selection(browser, { width, height, theme, mobile }) {
  const ctx = await context(browser, { width, height, theme, mobile });
  const page = await ctx.newPage();

  const seeded = await api('POST', '/api/books/', {
    type: 'ebook',
    title: `The Odyssey (capture ${Date.now()})`,
    author: 'Homer',
  });
  const bookId = (seeded?.book ?? seeded).id;
  const form = new FormData();
  form.append('file', new Blob([EPUB], { type: 'application/epub+zip' }), 'book.epub');
  const uploaded = await fetch(`${BASE}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!uploaded.ok) throw new Error(`upload -> ${uploaded.status}`);

  await page.goto(`${BASE}/read/${bookId}`, { waitUntil: 'domcontentloaded' });
  await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
  await page.waitForTimeout(800);

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

  if (mobile) {
    await page.locator('[data-testid="selection-bar"]').waitFor({ timeout: 15_000 });
    await page.waitForTimeout(400);
    await shot(page, `selection-${mobile ? 'phone' : 'desktop'}-${theme}`);
  } else {
    await page.locator('[data-testid="selection-menu"]').waitFor({ timeout: 15_000 });
    await page.waitForTimeout(400);
    await shot(page, `selection-${mobile ? 'phone' : 'desktop'}-${theme}`);
  }
  await ctx.close();
}

async function main() {
  await seed();
  const browser = await chromium.launch();

  for (const theme of ['light', 'dark']) {
    await emptyStates(browser, { width: 1440, height: 900, theme, mobile: false });
    await settings(browser, { width: 1440, height: 900, theme, mobile: false });
    await selection(browser, { width: 1440, height: 900, theme, mobile: false });

    await emptyStates(browser, { width: 390, height: 844, theme, mobile: true });
    await settings(browser, { width: 390, height: 844, theme, mobile: true });
    await selection(browser, { width: 390, height: 844, theme, mobile: true });
  }

  await browser.close();
  console.log(`[done] ${PHASE} -> ${path.relative(REPO_ROOT, OUT)}`);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
