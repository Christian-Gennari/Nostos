/**
 * README screenshot capture — issue #144.
 *
 * Captures the six product surfaces shown in README.md from a running Nostos
 * instance at a single, consistent viewport/theme so the set reads as one tour.
 *
 * Usage: node scripts/readme-shots.mjs <baseUrl> <outDir>
 *
 * Source instance: the README is public, so the set is captured from the public
 * *showcase* instance (the curated public-domain library the landing page also
 * uses), never from a private library. Subjects are named, not guessed, and
 * overridable per instance: README_BOOK, README_STUDIO_FOLDER, README_STUDIO_DOC.
 *
 * Deliberate choices:
 *  - One viewport (1440x900 @2x) for every shot: the README declares each image
 *    width=2880 height=1800, and a mixed set reads as inconsistent.
 *  - ONE exception: the topic graph is captured at 1900x910 @2x so the frame
 *    keeps the page chrome (header + dock) and the graph's own aspect, rather
 *    than being cropped to the stage. That is a deliberate readability trade —
 *    see GRAPH_VIEWPORT below.
 *  - Light theme throughout (the marketing default; the repo logo is theme-aware
 *    but the product tour is not).
 *  - Service worker + caches are cleared before the first shot and after every
 *    navigation-ish step, because Nostos ships ngsw and a stale SW serves the
 *    PREVIOUS build's CSS. Without this a capture can silently show old styles.
 *  - Every shot asserts something about the DONE state (a node count, a title,
 *    a word count) before writing, so a blank/empty capture cannot pass quietly.
 */
import { chromium } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

const BASE = process.argv[2] ?? 'http://127.0.0.1:5330';
const OUT = process.argv[3] ?? '/tmp/nostos-readme-shots';
mkdirSync(OUT, { recursive: true });

const VIEWPORT = { width: 1440, height: 900 };

/**
 * The topic graph gets its own viewport.
 *
 * The other five shots are 1440x900 so the app's layout matches a real desktop
 * session. The graph does not need that: at 1440x900 the map stage is ~2:1 while
 * a fitted force layout is roughly square, so the stage spent ~53% of its width
 * on dead space and PR #151 worked around it by cropping to the graph's ink.
 *
 * This capture keeps the page chrome instead — header ("Brain · 53 topics ·
 * 61 references") and the dock — because that is what the README's Brain section
 * is illustrating. The trade is legibility: the labels are drawn at a constant
 * 12 CSS px (LABEL_DRAW_SIZE), so a wider image scales down harder at GitHub's
 * ~846px render column. At 1900 CSS px wide they land at ~5.3px, against ~15px
 * for the ink crop. Readable at 1:1, small in the README column.
 */
const GRAPH_VIEWPORT = { width: 1900, height: 910 };

/**
 * Subjects, per instance.
 *
 * The README is captured from the showcase library by default. On an instance
 * whose library differs, override these three names instead of editing the
 * script: the shot must be reproducible, so the subject is named, never
 * "whichever record sorted first".
 */
const SUBJECT_BOOK = process.env.README_BOOK ?? 'The Odyssey';
const STUDIO_FOLDER = process.env.README_STUDIO_FOLDER ?? 'Essays';
const STUDIO_DOC = process.env.README_STUDIO_DOC ?? 'Unhistoric Acts';
const manifest = [];

const browser = await chromium.launch({
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'],
});

/** Fresh context per shot group; drops any SW/cache the app registered. */
async function newPage(opts = {}) {
  const ctx = await browser.newContext({
    viewport: VIEWPORT,
    deviceScaleFactor: 2,
    colorScheme: 'light',
    reducedMotion: 'reduce', // stabilise animations before capture
    ...opts,
  });
  const page = await ctx.newPage();
  // Kill the PWA cache from the very first request so no stale shell can win.
  await page.addInitScript(() => {
    // no-op placeholder; caches are cleared explicitly after load below
  });
  return { ctx, page };
}

async function clearSw(page) {
  await page.evaluate(async () => {
    try {
      const regs = await navigator.serviceWorker.getRegistrations();
      for (const r of regs) await r.unregister();
    } catch {}
    try {
      for (const k of await caches.keys()) await caches.delete(k);
    } catch {}
  });
}

const settle = (page, ms = 1400) => page.waitForTimeout(ms);

/**
 * Wait until every <img> is fully decoded and every animation/transition has
 * finished.
 *
 * Without this the grid captures are NOT reproducible: two runs of the same
 * build differed by up to ~7000 pixels scattered across the cover artwork
 * (max channel delta 30/765 — invisible, but enough to churn the PNG bytes and
 * make "did my change alter this shot?" unanswerable). Cover images decode
 * progressively and the card/bloom animations settle over different numbers of
 * frames per run, so a fixed sleep samples a slightly different frame each time.
 */
async function settleFully(page) {
  await page
    .evaluate(async () => {
      // 1. every image decoded (not merely loaded)
      const imgs = [...document.querySelectorAll('img')];
      await Promise.all(
        imgs.map((i) =>
          i.complete && i.naturalWidth
            ? i.decode().catch(() => {})
            : new Promise((res) => {
                i.addEventListener('load', res, { once: true });
                i.addEventListener('error', res, { once: true });
              }),
        ),
      );
      // 2. every running animation/transition finished
      await Promise.all(
        document.getAnimations().map((a) => a.finished.catch(() => {})),
      );
    })
    .catch(() => {});
  await page.waitForTimeout(600);
}

async function shot(page, name, note, opts = {}) {
  // Settle on every shot, clipped or not: the graph shot is clipped but its
  // canvas animations are the most jitter-prone of the set.
  await settleFully(page);
  const path = `${OUT}/${name}.png`;
  await page.screenshot({ path, ...opts });
  const buf = readFileSync(path);
  const hash = createHash('md5').update(buf).digest('hex').slice(0, 12);
  manifest.push({ name, path, note, hash, bytes: buf.length });
  console.log(`  -> ${name}.png  ${hash}  ${(buf.length / 1024).toFixed(0)}KB  ${note}`);
}

// ---------------------------------------------------------------- Library
{
  const { ctx, page } = await newPage();
  await page.goto(`${BASE}/library`, { waitUntil: 'networkidle' });
  await clearSw(page);
  await page.goto(`${BASE}/library`, { waitUntil: 'networkidle' });
  await page.waitForSelector('.book-card, [class*="book-card"]', { timeout: 20000 });
  await settle(page, 2500);
  const cards = await page.locator('.book-card, [class*="book-card"]').count();
  if (cards < 6) throw new Error(`library grid too sparse: ${cards} cards`);
  const theme = await page.evaluate(() => document.documentElement.getAttribute('data-theme') ?? 'light(default)');
  await shot(page, 'library', `grid, ${cards} cards, theme=${theme}`);
  await ctx.close();
}

// ------------------------------------------------------------ Add Book
{
  const { ctx, page } = await newPage();
  await page.goto(`${BASE}/library`, { waitUntil: 'networkidle' });
  await clearSw(page);
  await page.goto(`${BASE}/library`, { waitUntil: 'networkidle' });
  await page.waitForSelector('.book-card, [class*="book-card"]', { timeout: 20000 });
  await settle(page, 2000);
  await page.locator('button:has-text("Add Book")').first().click();
  await settle(page, 1800);
  // The modal body scrolls; capture it at its top so the header/tabs read.
  const dialog = page.locator('[role="dialog"], [class*="modal"]').first();
  await dialog.waitFor({ timeout: 10000 });
  await page.evaluate(() => {
    for (const el of document.querySelectorAll('[class*="modal"] *, [role="dialog"] *')) {
      try {
        if (el.scrollHeight > el.clientHeight + 8) el.scrollTop = 0;
      } catch {}
    }
  });
  await settle(page, 900);
  await shot(page, 'add-book-modal', 'Add New Book dialog, Book Info tab');
  await ctx.close();
}

// -------------------------------------------------------- Book Details
{
  // Pick a volume that demonstrates the richest detail page WITHOUT exposing
  // anything personal: real rating, real progress, real synopsis — a cover, a
  // page count, and never a personal review. The subject is named (SUBJECT_BOOK)
  // so the frame is reproducible rather than "whichever book happened to sort
  // first".
  const { ctx, page } = await newPage();
  await page.goto(`${BASE}/library`, { waitUntil: 'networkidle' });
  await clearSw(page);
  const books = await page.evaluate(async () => {
    const r = await fetch('/api/books?pageSize=300');
    return await r.json();
  });
  const pick = books.items.find(
    (b) =>
      b.title === SUBJECT_BOOK &&
      b.pageCount &&
      b.coverUrl &&
      !b.personalReview,
  );
  if (!pick) throw new Error(`no suitable book-detail candidate named "${SUBJECT_BOOK}"`);
  await page.goto(`${BASE}/library/${pick.id}`, { waitUntil: 'networkidle' });
  await settle(page, 3000);
  const h1 = await page.evaluate(() => document.querySelector('h1')?.innerText ?? '');
  if (!h1) throw new Error('book detail h1 missing');
  await page.evaluate(() => window.scrollTo(0, 0));
  await settle(page, 600);
  await shot(page, 'book-details', `${h1} — hero, cover, rating, actions, synopsis`);
  await ctx.close();
}

// ------------------------------------------------------- Brain (list)
{
  const { ctx, page } = await newPage();
  await page.goto(`${BASE}/second-brain`, { waitUntil: 'networkidle' });
  await clearSw(page);
  await page.goto(`${BASE}/second-brain`, { waitUntil: 'networkidle' });
  await page.waitForSelector('.index-item', { timeout: 20000 });
  await settle(page, 2000);
  // Select the top topic so the pane shows the actual reading experience
  // (quotation + commentary + source), not the "select a topic" empty state.
  await page.locator('.index-item').first().click();
  await settle(page, 2500);
  const heading = await page.evaluate(() => document.querySelector('h1')?.innerText ?? '');
  const items = await page.locator('.index-item').count();
  const empty = await page.locator('text=Select a topic from the index').count();
  if (empty) throw new Error('brain list still showing empty state');
  await shot(page, 'brain-list', `${heading}, ${items} topics, topic detail open`);
  await ctx.close();
}

// ------------------------------------------------- Brain (graph / map)
{
  // Own viewport: keeps the page chrome and the graph's own aspect (see
  // GRAPH_VIEWPORT). Shot at @2x so the asset stays crisp at 1:1.
  const { ctx, page } = await newPage({ viewport: GRAPH_VIEWPORT });
  await page.goto(`${BASE}/second-brain`, { waitUntil: 'networkidle' });
  await clearSw(page);
  await page.goto(`${BASE}/second-brain`, { waitUntil: 'networkidle' });
  await page.waitForSelector('.view-mode-control .vt-opt, .view-mode-control .toggle-opt', { timeout: 20000 });
  await settle(page, 2000);
  // The Brain view toggle is a shared view-toggle component now (.vt-opt);
  // older builds rendered the same control with .toggle-opt buttons. The last
  // option is the map.
  await page.locator('.view-mode-control .vt-opt, .view-mode-control .toggle-opt').last().click();
  await page.waitForSelector('.map-stage canvas', { timeout: 20000 });
  await settle(page, 4500);

  // Fit the content to the stage. The layout keeps advancing while it settles,
  // so re-fit after the physics calms down.
  const fit = page.locator('button[aria-label="Fit to view"]');
  for (let i = 0; i < 3; i++) {
    if (await fit.count()) {
      await fit.first().click();
      await settle(page, 2200);
    }
  }
  await settle(page, 2500);

  // The map's own Fit leaves the layout fitted to its own extent only: on the
  // current build the fitted graph measures 29% of the stage width, which would
  // render the labels at ~5px in the README's column. Two steps of the map's
  // zoom bring the ink to ~77% width / 93% height with no node clipped
  // (measured with the ink-box probe below).
  const zoomIn = page.locator('button[aria-label="Zoom in"]').first();
  for (let i = 0; i < 2; i++) {
    if (await zoomIn.count()) {
      await zoomIn.click();
      await settle(page, 1600);
    }
  }
  await settle(page, 1500);

  // Read the graph through the app's own diagnostics handles. `__nostosGraph` is
  // a graphology instance, so order/size are direct properties.
  const stats = await page.evaluate(() => {
    const g = globalThis.__nostosGraph;
    return g ? { nodes: g.order ?? null, edges: g.size ?? null } : null;
  });
  if (!stats || !stats.nodes) throw new Error(`graph diagnostics empty: ${JSON.stringify(stats)}`);

  // Measure the graph's real ink (nodes, edges AND labels) off the Sigma
  // canvases. Nothing is cropped to it any more, but it is the done-state
  // assertion: a graph whose ink collapses to a corner means the physics or the
  // fit failed, and the capture would otherwise look fine.
  const inkBox = await page.evaluate(() => {
    const stage = document.querySelector('.map-stage');
    const sRect = stage.getBoundingClientRect();
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity, sampled = 0;
    for (const c of document.querySelectorAll('.map-stage canvas')) {
      if (!c.width || !c.height) continue;
      let data;
      try {
        data = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
      } catch {
        continue; // tainted canvas: skip rather than fail the capture
      }
      sampled++;
      const r = c.getBoundingClientRect();
      const sx = r.width / c.width;
      const sy = r.height / c.height;
      for (let y = 0; y < c.height; y++) {
        for (let x = 0; x < c.width; x++) {
          if (data[(y * c.width + x) * 4 + 3] > 12) {
            const px = r.left - sRect.left + x * sx;
            const py = r.top - sRect.top + y * sy;
            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;
          }
        }
      }
    }
    if (!sampled || minX === Infinity) return null;
    return { minX, maxX, minY, maxY, stage: { x: sRect.left, y: sRect.top, w: sRect.width, h: sRect.height } };
  });
  if (!inkBox) throw new Error('could not measure graph ink');

  const fillX = (inkBox.maxX - inkBox.minX) / inkBox.stage.w;
  const fillY = (inkBox.maxY - inkBox.minY) / inkBox.stage.h;
  if (fillX < 0.3 || fillY < 0.3) {
    throw new Error(`graph collapsed to a corner: stageFill=${fillX.toFixed(2)}x${fillY.toFixed(2)}`);
  }

  // The dock and header must be in frame — that is the point of this framing.
  const chrome = await page.evaluate(() => {
    const h1 = document.querySelector('h1')?.innerText ?? '';
    const vh = innerHeight;
    let dockBottom = 0;
    for (const el of document.querySelectorAll('.app-dock, .dock, nav')) {
      const r = el.getBoundingClientRect();
      if (r.height && r.width && r.top < vh && r.bottom > dockBottom) dockBottom = r.bottom;
    }
    return { h1, dockVisible: dockBottom > vh - 120 && dockBottom <= vh + 1 };
  });
  if (!chrome.h1) throw new Error('graph header missing — wrong framing');
  if (!chrome.dockVisible) throw new Error('dock not in frame — wrong framing');

  await shot(
    page,
    'brain-graph',
    `nodes=${stats.nodes} edges=${stats.edges} stageFill=${fillX.toFixed(2)}x${fillY.toFixed(2)} header="${chrome.h1}"`,
  );
  await ctx.close();
}

// ------------------------------------------------------- Writing Studio
{
  const { ctx, page } = await newPage();
  await page.goto(`${BASE}/studio`, { waitUntil: 'networkidle' });
  await clearSw(page);
  await page.goto(`${BASE}/studio`, { waitUntil: 'networkidle' });
  await settle(page, 2500);

  // The file tree starts collapsed, so documents are not in the DOM yet. Expand
  // ONLY the folder holding the target document (STUDIO_FOLDER), then collapse
  // it again before capturing: the open editor survives the collapse, and no
  // folder contents beyond the one document the shot needs stay in frame.
  const wp = page.locator('.tree-row', { hasText: STUDIO_FOLDER }).first();
  if (!(await wp.count())) throw new Error(`${STUDIO_FOLDER} folder not in tree`);
  await wp.locator('.toggle-btn').click();
  await settle(page, 1200);

  const target = page.locator('.tree-row', { hasText: STUDIO_DOC }).first();
  if (!(await target.count())) throw new Error(`${STUDIO_DOC} document not in tree`);
  await target.click();
  await settle(page, 4500);

  // Collapse the folder again so its contents are out of frame; the open
  // document stays in the editor.
  await wp.locator('.toggle-btn').click();
  await settle(page, 2000);

  // The rail has two surfaces: "For this writing" (sources kept with this
  // document — empty for the README's document) and "Library" (browse
  // topics and books while writing, which is what the README copy
  // describes). Switch to Library for the shot.
  const railLibrary = page.locator('.reference-mode-switch button', { hasText: 'Library' }).first();
  if (await railLibrary.count()) {
    await railLibrary.click();
    await settle(page, 2200);
  }
  const railText = await page.evaluate(() => (document.querySelector('.reference-mode-switch')?.parentElement?.innerText ?? '').slice(0, 400));
  if (/No sources kept/.test(railText)) throw new Error('reference rail still on the kept-sources empty state');

  const body = await page.evaluate(() => document.body.innerText);
  if (body.includes('Select a file to begin writing')) {
    throw new Error('studio still on empty state');
  }
  // Guard the privacy requirement: known private titles must never be on
  // screen, whichever instance the capture is pointed at.
  if (body.includes('philosophical aims')) {
    throw new Error('private journal title visible in frame');
  }
  const words = (body.match(/(\d[\d,]*)\s+words/i) ?? [])[1] ?? '?';
  await shot(page, 'writing-studio', `three-pane workspace, document open (${words} words)`);
  await ctx.close();
}

await browser.close();
writeFileSync(`${OUT}/manifest.json`, JSON.stringify(manifest, null, 2));
console.log(`\n${manifest.length} shots -> ${OUT}`);
