/**
 * Mobile topic-map audit.
 *
 * "The layout is super weird and buggy on mobile" is a set of uncounted claims.
 * This enumerates what a phone actually sees: geometry of every map surface, the
 * fitted graph, controls, touch targets, overflow, and the fullscreen state.
 *
 * Runs a matrix of real device viewports rather than one, because bugs in this
 * area have been shape-specific (a fit that only failed when the stage was
 * taller than it was wide).
 */
import { chromium } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const BASE = (process.argv[2] ?? 'http://127.0.0.1:5321').replace(/\/+$/, '');
const OUT = process.argv[3] ?? '.map-audit-out/mobile';
mkdirSync(OUT, { recursive: true });

const DEVICES = [
  { name: 'iphone-se', w: 375, h: 667, dsf: 2 },
  { name: 'iphone-14', w: 390, h: 844, dsf: 3 },
  { name: 'pixel-7', w: 412, h: 915, dsf: 2.6 },
  { name: 'ipad-mini', w: 744, h: 1133, dsf: 2 },
  { name: 'phone-landscape', w: 844, h: 390, dsf: 3 },
];

const browser = await chromium.launch({
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'],
});

/**
 * A dev server can leave a stale HMR error overlay in the DOM; it covers the
 * page and swallows every pointer event, so a click times out for a reason that
 * has nothing to do with the app. Remove it on every page.
 */
async function guardOverlay(page) {
  await page.addInitScript(() => {
    const drop = () => document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
    const start = () => {
      drop();
      if (document.documentElement) new MutationObserver(drop).observe(document.documentElement, { childList: true, subtree: true });
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
  });
}

const results = [];

for (const dev of DEVICES) {
  const ctx = await browser.newContext({
    viewport: { width: dev.w, height: dev.h },
    deviceScaleFactor: dev.dsf,
    isMobile: true,
    hasTouch: true,
  });
  const page = await ctx.newPage();
  await guardOverlay(page);
  const errors = [];
  page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

  await page.goto(`${BASE}/second-brain`, { waitUntil: 'domcontentloaded' });
  await page.locator('.index-item').first().waitFor({ timeout: 30_000 });

  // Index (list) state first — the user lands here.
  const indexState = await page.evaluate(() => {
    const col = document.querySelector('.content-col, .map-wrapper, .topic-map');
    const doc = document.documentElement;
    return {
      docScrollWidth: doc.scrollWidth,
      clientWidth: doc.clientWidth,
      innerWidth: window.innerWidth,
      innerHeight: window.innerHeight,
      visualHeight: window.visualViewport ? Math.round(window.visualViewport.height) : null,
      hasContentCol: !!col,
    };
  });

  // Switch to map view, exactly as a user would tap it.
  const toggle = page.locator('.view-mode-control .toggle-opt').last();
  const toggleBox = await toggle.boundingBox().catch(() => null);
  let toggleOk = false;
  if (toggleBox) {
    try { await toggle.click({ timeout: 5000 }); toggleOk = true; } catch { toggleOk = false; }
  }
  await page.waitForTimeout(3500);

  const mapState = await page.evaluate(() => {
    const q = (s) => document.querySelector(s);
    const r = (el) => {
      if (!el) return null;
      const b = el.getBoundingClientRect();
      return { x: Math.round(b.x), y: Math.round(b.y), w: Math.round(b.width), h: Math.round(b.height),
               bottom: Math.round(b.bottom), right: Math.round(b.right) };
    };
    const sigma = q('.sigma-container');
    const controls = [...document.querySelectorAll('[role="toolbar"] button')].map((b) => {
      const bb = b.getBoundingClientRect();
      return { label: (b.getAttribute('aria-label') || b.title || b.textContent || '').trim().slice(0, 24),
               w: Math.round(bb.width), h: Math.round(bb.height), x: Math.round(bb.x), y: Math.round(bb.y) };
    });
    const graph = globalThis.__nostosGraph;
    const sig = globalThis.__nostosSigma;
    let fit = null;
    if (graph && sig && graph.order) {
      let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
      graph.forEachNode((id, a) => {
        if (a.x < minX) minX = a.x; if (a.x > maxX) maxX = a.x;
        if (a.y < minY) minY = a.y; if (a.y > maxY) maxY = a.y;
      });
      const dims = sig.getDimensions();
      const cam = sig.getCamera();
      let off = 0;
      graph.forEachNode((id, a) => {
        const v = sig.graphToViewport({ x: a.x, y: a.y });
        if (v.x < 0 || v.y < 0 || v.x > dims.width || v.y > dims.height) off++;
      });
      // How much of the stage the graph spans, in stage pixels.
      const a0 = sig.graphToViewport({ x: minX, y: minY });
      const a1 = sig.graphToViewport({ x: maxX, y: maxY });
      fit = {
        stageW: Math.round(dims.width), stageH: Math.round(dims.height),
        camera: { x: +cam.x.toFixed(3), y: +cam.y.toFixed(3), ratio: +cam.ratio.toFixed(3) },
        spanX: Math.round(Math.abs(a1.x - a0.x)), spanY: Math.round(Math.abs(a1.y - a0.y)),
        fillXPct: Math.round(100 * Math.abs(a1.x - a0.x) / dims.width),
        fillYPct: Math.round(100 * Math.abs(a1.y - a0.y) / dims.height),
        offscreen: off,
        nodeCount: graph.order,
      };
    }
    const squished = [];
    document.querySelectorAll('.sigma-container, .topic-map, .map-wrapper, [role="toolbar"], .map-search, .map-legend').forEach((el) => {
      const b = el.getBoundingClientRect();
      if (b.width < 40 || b.height < 20) squished.push({ cls: el.className.toString().slice(0, 50), w: Math.round(b.width), h: Math.round(b.height) });
    });
    return {
      docScrollWidth: document.documentElement.scrollWidth,
      clientWidth: document.documentElement.clientWidth,
      innerWidth: window.innerWidth,
      innerHeight: window.innerHeight,
      visualHeight: window.visualViewport ? Math.round(window.visualViewport.height) : null,
      stage: r(sigma),
      map: r(q('.topic-map')),
      wrapper: r(q('.map-wrapper')),
      controls: controls,
      controlsTiny: controls.filter((c) => c.w < 40 || c.h < 40),
      controlsOffscreen: controls.filter((c) => c.x < 0 || c.y < 0 || c.x + c.w > window.innerWidth || c.y + c.h > window.innerHeight),
      selectionBar: r(q('[role="toolbar"]')),
      search: r(q('.map-search')),
      legend: r(q('.map-legend')),
      squished,
      fit,
      horizontalOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
    };
  });

  await page.screenshot({ path: path.join(OUT, `${dev.name}-map.png`) });

  results.push({ device: dev, toggleOk, indexState, map: mapState, errors });
  await ctx.close();
}

writeFileSync(path.join(OUT, 'mobile-audit.json'), JSON.stringify(results, null, 2));

// Console summary.
for (const r of results) {
  const m = r.map;
  console.log(`\n=== ${r.device.name} (${r.device.w}x${r.device.h}) ===`);
  console.log(`  toggle clicked: ${r.toggleOk}   console errors: ${r.errors.length}`);
  console.log(`  viewport: ${m.innerWidth}x${m.innerHeight}  visual: ${m.visualHeight}`);
  console.log(`  stage: ${JSON.stringify(m.stage)}`);
  console.log(`  controls: ${m.controls.length}  tiny(<40): ${m.controlsTiny.length}  offscreen: ${m.controlsOffscreen.length}`);
  if (m.controlsTiny.length) console.log(`    tiny: ${JSON.stringify(m.controlsTiny.map((c) => c.label + ' ' + c.w + 'x' + c.h))}`);
  if (m.controlsOffscreen.length) console.log(`    offscreen: ${JSON.stringify(m.controlsOffscreen.map((c) => c.label))}`);
  console.log(`  horiz overflow: ${m.horizontalOverflow}  (scroll ${m.docScrollWidth} vs client ${m.clientWidth})`);
  if (m.fit) console.log(`  FIT: stage ${m.fit.stageW}x${m.fit.stageH} span ${m.fit.spanX}x${m.fit.spanY} fill ${m.fit.fillXPct}%x${m.fit.fillYPct}% offscreen ${m.fit.offscreen}/${m.fit.nodeCount} cam ${JSON.stringify(m.fit.camera)}`);
  if (m.squished.length) console.log(`  SQUISHED: ${JSON.stringify(m.squished)}`);
  if (r.errors.length) console.log(`  ERRORS: ${JSON.stringify(r.errors.slice(0, 3))}`);
}

await browser.close();
