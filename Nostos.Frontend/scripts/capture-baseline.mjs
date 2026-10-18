#!/usr/bin/env node
/**
 * Design-language baseline capture.
 *
 * A CSS refactor compiles perfectly while changing every surface, so the
 * acceptance test for this work cannot be `npm run build` — it has to be a
 * pixel-and-computed-value baseline taken from the RUNNING app.
 *
 * This captures, for each surface x viewport x theme:
 *   - a PNG at DPR 1 (artifact pixels are exact; downscaling hides defects)
 *   - a JSON of computed values clustered over the painted DOM
 *
 * It also records a BUILD FINGERPRINT. This matters: a previous audit session
 * measured a bundle that predated a merge to main, and every number it produced
 * described the old code. Every later diff must state which build it measured,
 * so the fingerprint is captured with the baseline rather than reconstructed
 * later.
 *
 * Usage:
 *   node scripts/capture-baseline.mjs [--port 5214] [--out <dir>]
 *   node scripts/capture-baseline.mjs --theme sepia --surface library
 *
 * Assumes an app is already serving (npm run prod / npm start). It does NOT
 * start one: a baseline captured against a freshly-started server would race
 * the build, which is the exact failure this harness exists to prevent.
 */
import { chromium } from '@playwright/test';
import { mkdirSync, writeFileSync, existsSync, readdirSync, statSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(here, '..');

const argv = process.argv.slice(2);
const arg = (name, fallback) => {
  const i = argv.indexOf(`--${name}`);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : fallback;
};

const PORT = arg('port', '5214');
const BASE = `http://127.0.0.1:${PORT}`;
const OUT = resolve(arg('out', join(frontendRoot, 'e2e/visual-evidence/design-baseline')));
const ONLY_THEME = arg('theme', null);
const ONLY_SURFACE = arg('surface', null);

/* DATA-DRIVEN SURFACES ARE STUBBED, NOT RE-BASELINED.
 *
 * The settings page renders BACKUP HISTORY straight from the API, including the
 * stored timestamps of real backups. A weekly backup job made the newest entry
 * move (Sep 7 -> Sep 14), which moved ~3,900 pixels with byte-identical CSS.
 *
 * Re-baselining would "fix" that for one week and break again at the next backup,
 * and it would train whoever reads the failure to ignore a real regression. Worse,
 * the gate's own message said "this is a REAL styling change" - because
 * CONTENT_HASH only scans book/index/note cards, so a data change on THIS surface
 * is invisible to it.
 *
 * So the capture pins the data instead. The stub below reproduces the exact rows
 * the committed baseline was captured with, which means the fixture costs zero
 * pixel change: the CSS is still compared byte-for-byte, and only the clock is
 * frozen. Update the values only if a deliberate styling change moves this card.
 *
 * `intervalHours` and `maxBackups` are included so the schedule copy is pinned too.
 * Anything the settings page fetches that carries a timestamp belongs here. */
const BACKUP_STUB = {
  /* `/api/backup/settings` matters as much as the two below: it drives the
     Automatic Backup toggle and its description, which is different copy for
     enabled/disabled. A fresh worktree DB copy has `isEnabled: false`, so the
     capture rendered "the first backup will run within 5 minutes..." instead of
     the baseline's "created automatically every week" — 4,020 px of pure text
     drift from a database seed, not from CSS. */
  '/api/backup/settings': {
    isEnabled: true,
    provider: 'Local',
    includeBookFiles: true,
    intervalHours: 168,
    maxBackups: 3,
  },
  '/api/backup/status': {
    isEnabled: true,
    provider: 'Local',
    lastBackupAt: '2026-09-07T05:18:16.0529953Z',
    lastBackupStatus: 'Completed',
    includeBookFiles: true,
    intervalHours: 168,
    maxBackups: 3,
  },
  '/api/backup/history': [
    { id: 'stub-backup-3', createdAt: '2026-09-07T05:18:16.0529953Z',
      sizeBytes: 12682795471, provider: 'Local', status: 'Completed',
      includeBookFiles: true, errorMessage: null },
    { id: 'stub-backup-2', createdAt: '2026-08-31T05:14:37.7014082Z',
      sizeBytes: 12682795687, provider: 'Local', status: 'Completed',
      includeBookFiles: true, errorMessage: null },
    /* THREE entries, not two: (maxBackups = 3). The card's height depends on the
       row count, and the third row sits just below the fold - so its values are
       not painted, but its PRESENCE is, as a taller card. A two-entry fixture
       made the card end ~12px short of the viewport bottom and moved 8,146
       pixels of pure background. The date follows the observed weekly cadence
       (Aug 24 is the Monday before Aug 31, matching the three dates in the DB). */
    { id: 'stub-backup-1', createdAt: '2026-08-24T05:14:37.7014082Z',
      sizeBytes: 12682795687, provider: 'Local', status: 'Completed',
      includeBookFiles: true, errorMessage: null },
  ],
};

/** Surfaces worth pinning. Protected surfaces (pdf/tinymce) are out of scope. */
/* ORDER IS LOAD-BEARING: the reader is captured FIRST.
 *
 * The reader paints a live elapsed-time readout, and the harness installs a fixed
 * clock START but the clock still advances with real time. So the readout is a
 * function of how long the page has been open — which meant `reader-desktop-dark`
 * was byte-identical across three consecutive single-surface runs (and matched the
 * baseline), yet differed by ~11,240 px inside a full 6-surface run. It passed in
 * isolation and failed in the suite, which is the signature of accumulated
 * wall-clock time rather than a styling problem.
 *
 * Capturing it first minimises elapsed time and makes it reproducible. The stronger
 * fix (`clock.pauseAt`) was tried and REJECTED: it froze the app's own boot and the
 * reader came up empty (61 elements, 9 painted), which the non-vacuity guard caught.
 *
 * The reader is covered at all because it is a ROUTE, not a tab — the first pass
 * missed it, and `reader-shell.component.css` is exactly where a phantom token
 * (`var(--space-3)`, declared nowhere in the repo) had been sitting unnoticed. The
 * id is a real audio book so the route renders its player, not an empty state. */
const SURFACES = [
  { name: 'reader', route: '/read/f9c17fb2-e42d-4db5-a3d0-b0a45b73f12e', settle: 'networkidle' },
  { name: 'library', route: '/library', settle: 'networkidle' },
  { name: 'brain', route: '/second-brain', settle: 'networkidle' },
  { name: 'studio', route: '/studio', settle: 'networkidle' },
  { name: 'settings', route: '/settings', settle: 'networkidle' },
  { name: 'home', route: '/', settle: 'networkidle' },
];

const VIEWPORTS = [
  { name: 'desktop', width: 1440, height: 900 },
  { name: 'mobile', width: 390, height: 844 },
];

/* Every app theme (issue #1074). Sepia, Mist and Writer's Study are
   `data-palette` blocks over their mode, so each needs its own baseline: a token
   that moves in one of them shows nowhere in Light or Dark. */
const THEMES = ['light', 'sepia', 'mist', 'study', 'dark'];
const DARK_THEMES = new Set(['mist', 'study', 'dark']);

/** Clusters computed values over every element that actually paints. */
const SWEEP = () => {
  const buckets = {
    radius: {}, fontSize: {}, fontWeight: {}, fontFamily: {},
    bg: {}, fg: {}, borderW: {}, borderC: {}, shadow: {},
    outlineW: {}, outlineC: {}, transitionDur: {}, transitionProp: {}, easing: {},
  };
  const bump = (o, k) => { if (k) o[k] = (o[k] || 0) + 1; };
  let painted = 0;

  for (const el of document.querySelectorAll('*')) {
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) continue;
    painted++;
    const cs = getComputedStyle(el);

    bump(buckets.radius, cs.borderTopLeftRadius);
    bump(buckets.fontSize, cs.fontSize);
    bump(buckets.fontWeight, cs.fontWeight);
    bump(buckets.fontFamily, (cs.fontFamily || '').split(',')[0].replace(/["']/g, '').trim());
    bump(buckets.bg, cs.backgroundColor);
    bump(buckets.fg, cs.color);
    if (cs.boxShadow && cs.boxShadow !== 'none') bump(buckets.shadow, cs.boxShadow);

    // OUTLINE only counts when a ring is actually drawn.
    if (cs.outlineStyle !== 'none' && parseFloat(cs.outlineWidth) > 0) {
      bump(buckets.outlineW, cs.outlineWidth);
      bump(buckets.outlineC, cs.outlineColor);
    }
    // BORDER only when a style is set: with `border-style: none` the computed
    // colour is `currentColor`, which would otherwise flood this bucket with
    // text colours and read as a border that does not exist.
    if (cs.borderTopStyle !== 'none' && parseFloat(cs.borderTopWidth) > 0) {
      bump(buckets.borderW, cs.borderTopWidth);
      bump(buckets.borderC, cs.borderTopColor);
    }
    if (cs.transitionDuration && cs.transitionDuration !== '0s') {
      for (const d of cs.transitionDuration.split(',')) bump(buckets.transitionDur, d.trim());
      for (const p of cs.transitionProperty.split(',')) bump(buckets.transitionProp, p.trim());
      // Splitting on commas must ignore commas inside cubic-bezier().
      for (const e of cs.transitionTimingFunction.split(/,(?![^(]*\))/)) bump(buckets.easing, e.trim());
    }
  }

  const sorted = {};
  for (const [k, v] of Object.entries(buckets)) {
    sorted[k] = Object.entries(v).sort((a, b) => b[1] - a[1]);
  }
  return { painted, buckets: sorted };
};

/**
 * Build freshness check.
 *
 * This exists because an audit session measured a bundle that predated a merge
 * to `main` and every number it produced described the old code. It must tell us
 * "the thing the browser is running is the thing in my working tree".
 *
 * The FIRST version of this used a hard-coded sentinel selector
 * (`index-list .index-row-shell`, the specificity-padding fix from #94) and
 * asserted the served sheet contained it. That rotted within one phase: the
 * Phase 2 work replaced that selector with a token, so the marker vanished and
 * the check failed on a perfectly good build. A hard-coded marker names
 * something that will be refactored.
 *
 * So it compares CONTENT hashes instead, which is self-maintaining:
 *   1. the served stylesheet must be byte-equal to the newest built stylesheet
 *      in `dist/` (the server is running the build we think it is), and
 *   2. the newest built stylesheet must be no older than the newest source file
 *      (the build reflects the source).
 *
 * `scripts/check-freshness.mjs` proves both directions can fail.
 */
async function fingerprint(page) {
  return page.evaluate(async () => {
    const links = [...document.querySelectorAll('link[rel=stylesheet]')].map((l) => l.getAttribute('href'));
    const sheets = [];
    for (const href of links) {
      try {
        const res = await fetch(href);
        const text = await res.text();
        let h = 2166136261;
        for (let i = 0; i < text.length; i++) {
          h ^= text.charCodeAt(i);
          h = Math.imul(h, 16777619);
        }
        sheets.push({ href, bytes: text.length, hash: (h >>> 0).toString(16) });
      } catch (e) { sheets.push({ href, error: String(e) }); }
    }

    /* Content fingerprint for the DATA-driven surfaces.
       The library grid paints a list that comes from the database, and other agents
       work in this repo concurrently — `nostos.db` was observed being written during
       a capture run. A content change (a book added, covers re-fetched) moves
       hundreds of thousands of pixels while the CSS is byte-identical, which is
       indistinguishable from a real regression unless the harness records the
       content too.

       We record a hash of the visible text plus the resolved image URLs and sizes.
       That is deliberately coarser than the DOM: it must be stable against
       rendering noise and sensitive to a different book appearing. */
    const gridText = (sel) => [...document.querySelectorAll(sel)]
      .map((e) => (e.textContent || '').trim()).join('\u0001');
    const imgs = [...document.images].map((i) => `${i.currentSrc || i.src}|${i.naturalWidth}x${i.naturalHeight}`);
    const contentMaterial = [
      gridText('.book-card, .index-item, .note-card, .nav-item'),
      imgs.join('\u0002'),
      document.querySelectorAll('.book-card, .index-item, .note-card').length,
    ].join('\u0003');
    let ch = 2166136261;
    for (let i = 0; i < contentMaterial.length; i++) {
      ch ^= contentMaterial.charCodeAt(i);
      ch = Math.imul(ch, 16777619);
    }
    return {
      html: location.origin + '/',
      sheets,
      contentHash: (ch >>> 0).toString(16),
      contentCounts: {
        cards: document.querySelectorAll('.book-card, .index-item, .note-card').length,
        images: document.images.length,
      },
    };
  });
}

/**
 * Content hash for ONE surface, evaluated right before its screenshot.
 *
 * Why per-surface: the library grid paints a list that comes from `nostos.db`, and
 * other agents write to that DB concurrently — a book being added mid-run moves
 * ~100,000 pixels with byte-identical CSS, which is indistinguishable from a real
 * regression. A single concatenated hash for the whole run cannot say WHICH surface
 * moved, so a failure would still be unattributable.
 *
 * It covers what renders as text or as an image: visible text, resolved image URLs
 * with their intrinsic sizes, and element counts. It excludes anything time-based —
 * the capture installs a frozen clock, and a timestamp here would change on every
 * run and therefore carry no signal.
 */
const CONTENT_HASH = () => {
  const text = [...document.querySelectorAll(
    '.book-card, .index-item, .note-card, .nav-item, .map-node, .meta-title, .book-title')]
    .map((e) => (e.textContent || '').trim()).join('\u0001');
  const imgs = [...document.images]
    .map((i) => `${i.currentSrc || i.src}|${i.naturalWidth}x${i.naturalHeight}`).join('\u0002');
  const material = [text, imgs,
    document.querySelectorAll('.book-card, .index-item, .note-card').length,
    document.images.length].join('\u0003');
  let h = 2166136261;
  for (let i = 0; i < material.length; i++) {
    h ^= material.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return { hash: (h >>> 0).toString(16), chars: material.length,
    cards: document.querySelectorAll('.book-card, .index-item, .note-card').length,
    images: document.images.length };
};

/** Counts serve as the non-vacuity guard for the sweep itself. */
async function geometry(page) {
  return page.evaluate(() => {
    const all = document.querySelectorAll('*');
    return {
      innerWidth: window.innerWidth,
      innerHeight: window.innerHeight,
      dpr: window.devicePixelRatio,
      theme: document.documentElement.getAttribute('data-theme'),
      bodyClass: document.body.className,
      scrollHeight: document.documentElement.scrollHeight,
      elementCount: all.length,
      /**
       * A class-based canary, reported ALONGSIDE the raw element count.
       *
       * `elementCount` is not stable: the `bloom-art` directive adds `is-bloomed` to a
       * cover when its image finishes decoding, and the reader/library paths each
       * render one cover image. That lands asynchronously, so the same build reports
       * 279 or 280 elements depending on whether the decode beat the measurement —
       * measured as a 11239-pixel diff in the reader's cover region (x661..778
       * y171..342) with an IDENTICAL contentHash, i.e. the instrument moved, not the
       * styling. `elementCount` is kept for the non-vacuity floor below, but a change
       * in it should be read against `bloomed` before being called a regression: an
       * elementCount delta with an unchanged `bloomed` count and content hash is a
       * decode race, not a styling change.
       */
      bloomed: document.querySelectorAll('.is-bloomed').length,
    };
  });
}

function walkCss(dir, out = []) {
  if (!existsSync(dir)) return out;
  for (const e of readdirSync(dir)) {
    const full = join(dir, e);
    if (statSync(full).isDirectory()) walkCss(full, out);
    else if (e.endsWith('.css')) out.push(full);
  }
  return out;
}

async function main() {
  mkdirSync(OUT, { recursive: true });
  const surfaces = ONLY_SURFACE ? SURFACES.filter((s) => s.name === ONLY_SURFACE) : SURFACES;
  const themes = ONLY_THEME ? [ONLY_THEME] : THEMES;
  if (!surfaces.length) throw new Error(`no surface named ${ONLY_SURFACE}`);

  const browser = await chromium.launch();
  const report = { generatedAt: new Date().toISOString(), base: BASE, captures: [], fingerprint: null };

  try {
    for (const vp of VIEWPORTS) {
      for (const theme of themes) {
        const context = await browser.newContext({
          viewport: { width: vp.width, height: vp.height },
          deviceScaleFactor: 1,
          isMobile: vp.width < 500,
          hasTouch: vp.width < 500,
        });
        const page = await context.newPage();
        const errors = [];
        page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text().slice(0, 200)); });
        page.on('pageerror', (e) => errors.push(String(e).slice(0, 200)));

        /* Freeze time for the whole capture.
           The reader paints a live elapsed/clock readout (`.time-label-btn`), so
           its pixels depend on WHEN the capture ran — `reader-desktop-light`
           differed by ~11,300 px between two runs of identical code and identical
           content. A capture that varies with wall-clock time cannot be compared
           to a baseline at all. Installing a fixed clock (and letting it tick)
           makes the readout advance deterministically from a known instant.
           This is a harness fix, not a styling change: it does not alter what the
           app paints, only when it paints it. */
        const FROZEN_START = new Date('2026-01-01T00:00:00Z');
        if (page.clock && typeof page.clock.install === 'function') {
          await page.clock.install({ time: FROZEN_START });
          /* NOTE: `pauseAt` is DELIBERATELY not used here. It froze the app's own
             boot — the reader then rendered 61 elements / 9 painted and the
             non-vacuity guard failed the capture. `install` alone fixes the START
             time but still advances, which is handled by surface ORDER instead
             (the reader is captured first, see SURFACES). */
        }

        /* Pin the data-driven surfaces BEFORE any navigation, so the app never
           even sees the live values. Scoped here (not to `settings`) because a
           route interception is global to the context and the other surfaces
           simply never request these endpoints. See BACKUP_STUB for why this is
           the fix rather than re-baselining. */
        for (const [path, body] of Object.entries(BACKUP_STUB)) {
          await context.route(`**${path}`, (route) =>
            route.fulfill({
              status: 200,
              contentType: 'application/json',
              body: JSON.stringify(body),
            }));
        }

        /* The library's live change stream (`/api/library/changes`, 2810509b) is
           an EventSource that never ends, so no page that watches the library
           ever reaches `networkidle` and the capture hung on its first library
           surface. A still capture wants no live updates anyway: a 204 ends an
           EventSource for good (no reconnect), and the screens fall back to
           showing what they loaded, exactly as without EventSource support. */
        await context.route('**/api/library/changes', (route) => route.fulfill({ status: 204, body: '' }));

        // Seed the theme the way the app persists it, then let the app apply it.
        await page.goto(BASE, { waitUntil: 'domcontentloaded' });
        await page.evaluate((t) => localStorage.setItem('nostos.theme', t), theme);

        for (const surface of surfaces) {
          await page.goto(BASE + surface.route, { waitUntil: 'networkidle' });
          // Let the wait-field breathe out and late art resolve before sampling.
          await page.waitForTimeout(1500);

          /* Settle images BEFORE sampling. The library grid renders its covers with
             `loading="lazy" decoding="async"`, so a fixed timeout is a race: a
             capture could include covers that had decoded and another could not.
             That produced two DIFFERENT PNGs from the SAME css bundle hash
             (7f83f46e both), which made the pixel gate report phantom
             regressions and, worse, made real ones look like flake.
             Force eager + await decode + assert nothing is broken. */
          /* Polled from Node, not awaited inside the page: the page runs on the
             installed fake clock, where an in-page timer or a pending `load` can
             wait forever, and one late cover then hung the whole run. Each poll
             is a synchronous read; the waiting happens on Node's real clock. */
          await page.evaluate(() => { for (const i of document.images) i.loading = 'eager'; });
          const readImages = () => page.evaluate(() => {
            const imgs = Array.from(document.images);
            return {
              total: imgs.length,
              broken: imgs.filter((i) => i.complete && i.naturalWidth === 0).map((i) => i.currentSrc || i.src),
              pending: imgs.filter((i) => !i.complete).length,
              pendingSrc: imgs.filter((i) => !i.complete).map((i) => i.currentSrc || i.src).slice(0, 5),
            };
          });
          let imgState = await readImages();
          for (let waited = 0; imgState.pending > 0 && waited < 20000; waited += 250) {
            await new Promise((r) => setTimeout(r, 250));
            imgState = await readImages();
          }
          // Decoded, too, but bounded on Node's clock for the same reason.
          await Promise.race([
            page.evaluate(() => Promise.all(Array.from(document.images).map((i) => (i.decode ? i.decode().catch(() => {}) : null)))),
            new Promise((r) => setTimeout(r, 5000)),
          ]);
          if (imgState.pending > 0 || imgState.broken.length > 0) {
            throw new Error(`${surface.name}: images did not settle ` +
              `(pending=${imgState.pending} ${JSON.stringify(imgState.pendingSrc)}, ` +
              `broken=${imgState.broken.length} ${JSON.stringify(imgState.broken.slice(0, 5))}) — ` +
              `a race here makes every downstream comparison meaningless`);
          }

          /* Decoded is not SETTLED. The `bloom-art` directive adds `is-bloomed` to a
             cover AFTER the image loads, and that lands a frame or two later, so a
             capture could still catch the pre-bloom paint on a surface with a cover.
             That is the reader's 11239-pixel diff with an identical contentHash: the
             element count moved 279 -> 280 while the styling did not. Wait for the
             bloomed count to stop changing before measuring anything. */
          // Polled from Node for the fake-clock reason above.
          const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
          let prevBloomed = -1;
          for (let i = 0; i < 20; i++) {
            const n = await page.evaluate(() => document.querySelectorAll('.is-bloomed').length);
            if (n === prevBloomed) break;
            prevBloomed = n;
            await sleep(100);
          }
          /* The class only STARTS the reveal. `.is-bloomed` plays the global
             `bloom-in` keyframes (blur 12px -> 0, scale 1.03 -> 1, opacity 0 -> 1),
             so waiting for the class still samples mid-animation and the same build
             renders differently — measured as a 11239-pixel diff in the reader's
             cover band with geo, contentHash and sweep all identical. Wait for the
             finite animations to actually finish (an infinite one, a spinner, never
             does and must not block the capture). */
          for (let waited = 0; waited < 8000; waited += 100) {
            const running = await page.evaluate(() => (document.getAnimations?.() ?? []).filter(
              (a) => a.playState === 'running' && a.effect?.getTiming().iterations !== Infinity,
            ).length);
            if (running === 0) break;
            await sleep(100);
          }
          // Belt and braces: the keyframe duration outlives a single frame batch.
          await sleep(600);

          const applied = await page.evaluate(() => document.documentElement.getAttribute('data-theme'));
          const palette = await page.evaluate(() => document.documentElement.getAttribute('data-palette'));
          const geo = await geometry(page);
          const sweep = await page.evaluate(SWEEP);
          /* Per-surface content hash. The report-level hash is a concatenation, so
             it cannot tell WHICH surface's data moved. Recording it per capture lets
             the gate attribute a pixel difference on a data-driven surface to the
             data rather than to the CSS — these surfaces fetch from a database that
             other agents write to concurrently. */
          const contentHash = await page.evaluate(CONTENT_HASH);
          if (!report.fingerprint) report.fingerprint = await fingerprint(page);

          const tag = `${surface.name}-${vp.name}-${theme}`;
          const png = join(OUT, `${tag}.png`);
          await page.screenshot({ path: png, fullPage: false, animations: 'disabled' });

          // Non-vacuity: a surface that rendered nothing must fail loudly rather
          // than baseline as an empty page that every later diff "matches".
          if (geo.elementCount < 20 || sweep.painted < 10) {
            throw new Error(`${tag}: surface looks empty (elements=${geo.elementCount}, painted=${sweep.painted})`);
          }
          if (geo.dpr !== 1) throw new Error(`${tag}: DPR ${geo.dpr}, expected 1`);
          const modeOk = DARK_THEMES.has(theme) ? applied === 'dark' : applied === null || applied === 'light';
          const paletteOk = theme === 'light' || theme === 'dark' ? palette === null : palette === theme;
          if (!modeOk || !paletteOk) {
            throw new Error(`${tag}: theme '${theme}' not applied (data-theme=${applied}, data-palette=${palette})`);
          }

          report.captures.push({ tag, surface: surface.name, viewport: vp.name, theme, png, applied, geo, sweep, contentHash, errors });
          console.log(`  ✔ ${tag}  elements=${geo.elementCount} painted=${sweep.painted}${errors.length ? `  (${errors.length} console errors)` : ''}`);
        }
        await context.close();
      }
    }
  } finally {
    await browser.close();
  }

  // Non-vacuity on the whole run.
  if (report.captures.length !== VIEWPORTS.length * themes.length * surfaces.length) {
    throw new Error(`captured ${report.captures.length} of ${VIEWPORTS.length * themes.length * surfaces.length}`);
  }
  if (!report.fingerprint) throw new Error('no build fingerprint captured');

  // Build freshness (§1.1 of the plan): the browser's stylesheet must be the
  // newest thing we built, and the build must be at least as new as the source.
  const distDir = join(frontendRoot, 'dist/Nostos.Frontend/browser');
  const newest = (dir, pred) => {
    if (!existsSync(dir)) return null;
    let best = null;
    for (const f of readdirSync(dir)) {
      const full = join(dir, f);
      if (!pred(f) || !statSync(full).isFile()) continue;
      const m = statSync(full).mtimeMs;
      if (!best || m > best.mtime) best = { file: full, name: f, mtime: m };
    }
    return best;
  };
  const builtCss = newest(distDir, (f) => f.startsWith('styles-') && f.endsWith('.css'));
  const srcNewest = (() => {
    let best = null;
    const walk = (dir) => {
      for (const f of readdirSync(dir)) {
        const full = join(dir, f);
        const st = statSync(full);
        if (st.isDirectory()) walk(full);
        else if (!best || st.mtimeMs > best.mtime) best = { file: full, mtime: st.mtimeMs };
      }
    };
    walk(join(frontendRoot, 'src'));
    return best;
  })();

  const served = report.fingerprint.sheets[0];
  if (!builtCss) throw new Error('no built stylesheet found in dist/ — run `npm run build` before baselining');
  const servedBytes = served.bytes;
  const builtBytes = statSync(builtCss.file).size;
  if (servedBytes !== builtBytes) {
    throw new Error(
      `build freshness FAILED: the served stylesheet is ${servedBytes} bytes but the newest build is ` +
      `${builtBytes} (${builtCss.name}). The server is running a different build than the working tree — ` +
      'rebuild and restart (npm run build && PM2_HOME=/home/dev/.pm2 pm2 restart nostos).',
    );
  }
  const lagMs = srcNewest ? srcNewest.mtime - builtCss.mtime : 0;
  if (lagMs > 1000) {
    throw new Error(
      `build staleness FAILED: ${srcNewest.file.replace(frontendRoot + '/', '')} is ` +
      `${Math.round(lagMs / 1000)}s newer than the newest build (${builtCss.name}). ` +
      'The build does not reflect the source — rebuild before baselining.',
    );
  }
  console.log(`  freshness: served ${served.href?.split('/').pop()} (${servedBytes}B) == newest build ${builtCss.name}; build >= newest source`);

  const json = join(OUT, 'painted-values.json');
  writeFileSync(json, JSON.stringify(report, null, 1));

  const cssFiles = walkCss(join(frontendRoot, 'src'));
  const srcHash = createHash('sha256');
  for (const f of cssFiles.sort()) srcHash.update(f.replace(frontendRoot, '') + statSync(f).size);

  console.log(`\n  captures : ${report.captures.length}`);
  console.log(`  sheets   : ${report.fingerprint.sheets.map((s) => `${s.href?.split('/').pop()}(${s.hash})`).join(', ')}`);
  console.log(`  srcHash  : ${srcHash.digest('hex').slice(0, 12)}  (${cssFiles.length} css files)`);
  console.log(`  json     : ${json}`);
  console.log(`  pngs     : ${OUT}\n`);
}

main().catch((e) => { console.error('\n✖ ' + e.message + '\n'); process.exit(1); });
