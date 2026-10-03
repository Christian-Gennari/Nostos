/**
 * Measure the two mobile defects vision flagged, as numbers.
 *
 * 1. CLIPPED LABELS. Sigma draws labels into a 2D canvas sized exactly to the
 *    stage, so there is no DOM box to inspect. A label that runs past the stage
 *    edge is simply cut off — so the test is: does label ink touch the canvas
 *    border? Sample the outermost 1px ring of the label layer.
 *
 * 2. DEAD SPACE. The map card ends well above the bottom navigation, so the
 *    phone shows a large empty band instead of graph. Measure the gap between
 *    the stage bottom and the top of the dock / viewport bottom.
 */
import { chromium } from '@playwright/test';
import { PNG } from 'pngjs';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const BASE = (process.argv[2] ?? 'http://127.0.0.1:5321').replace(/\/+$/, '');
const OUT = process.argv[3] ?? '.map-audit-out/mobile-detail';
mkdirSync(OUT, { recursive: true });

const DEVICES = [
  { name: 'iphone-se', w: 375, h: 667, dsf: 2 },
  { name: 'iphone-14', w: 390, h: 844, dsf: 3 },
  { name: 'pixel-7', w: 412, h: 915, dsf: 2.6 },
  { name: 'ipad-mini', w: 744, h: 1133, dsf: 2 },
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
const out = [];

for (const dev of DEVICES) {
  const ctx = await browser.newContext({
    viewport: { width: dev.w, height: dev.h }, deviceScaleFactor: dev.dsf, isMobile: true, hasTouch: true,
  });
  const page = await ctx.newPage();
  await guardOverlay(page);
  await page.goto(`${BASE}/second-brain`, { waitUntil: 'domcontentloaded' });
  await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
  await page.locator('.view-mode-control .toggle-opt').last().click();
  await page.locator('.sigma-container canvas').first().waitFor({ timeout: 30_000 });
  await page.waitForTimeout(3500);

  // --- Dead space + element geometry (CSS px) ---
  const geom = await page.evaluate(() => {
    const r = (s) => { const el = document.querySelector(s); if (!el) return null; const b = el.getBoundingClientRect();
      return { top: Math.round(b.top), bottom: Math.round(b.bottom), h: Math.round(b.height), w: Math.round(b.width), x: Math.round(b.x) }; };
    const dock = document.querySelector('.bottom-dock, .app-dock, nav');
    const dockRect = dock ? dock.getBoundingClientRect() : null;
    const stage = document.querySelector('.sigma-container');
    const stageRect = stage ? stage.getBoundingClientRect() : null;
    const card = document.querySelector('.topic-map');
    const cardRect = card ? card.getBoundingClientRect() : null;
    const vh = window.innerHeight;
    return {
      viewportH: vh,
      viewportW: window.innerWidth,
      backBar: r('.map-back, .back-to-index'),
      card: cardRect ? { top: Math.round(cardRect.top), bottom: Math.round(cardRect.bottom), h: Math.round(cardRect.height), x: Math.round(cardRect.x), w: Math.round(cardRect.width) } : null,
      stage: stageRect ? { top: Math.round(stageRect.top), bottom: Math.round(stageRect.bottom), h: Math.round(stageRect.height), x: Math.round(stageRect.x), w: Math.round(stageRect.width) } : null,
      dock: dockRect ? { top: Math.round(dockRect.top), bottom: Math.round(dockRect.bottom), h: Math.round(dockRect.height) } : null,
      // vertical bands of unused space
      gapStageToDock: dockRect && stageRect ? Math.round(dockRect.top - stageRect.bottom) : null,
      gapStageToViewport: stageRect ? Math.round(vh - stageRect.bottom) : null,
      // horizontal: stage inset from viewport edges (card padding)
      gutterLeft: stageRect ? Math.round(stageRect.x) : null,
      gutterRight: stageRect ? Math.round(window.innerWidth - (stageRect.x + stageRect.width)) : null,
    };
  });

  // --- Label clipping: does label ink touch the canvas border? ---
  const clip = await page.evaluate(async () => {
    const layer = document.querySelector('.sigma-labels');
    if (!layer) return { error: 'no .sigma-labels layer' };
    const r = layer.getBoundingClientRect();
    // Read pixels is impossible on a 2D canvas only if it is tainted; this one is not.
    const c = document.createElement('canvas');
    c.width = layer.width; c.height = layer.height;
    const cx = c.getContext('2d');
    cx.drawImage(layer, 0, 0);
    const img = cx.getImageData(0, 0, c.width, c.height);
    const { data, width: W, height: H } = img;
    const isInk = (x, y) => {
      const i = (y * W + x) * 4;
      return data[i + 3] > 24; // any visible alpha
    };
    const edges = { left: 0, right: 0, top: 0, bottom: 0 };
    for (let y = 0; y < H; y++) { if (isInk(0, y)) edges.left++; if (isInk(W - 1, y)) edges.right++; }
    for (let x = 0; x < W; x++) { if (isInk(x, 0)) edges.top++; if (isInk(x, H - 1)) edges.bottom++; }
    // Also: ink in the outermost 2px ring, to catch near-misses.
    let ringInk = 0, totalInk = 0;
    for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
      if (!isInk(x, y)) continue;
      totalInk++;
      if (x <= 1 || y <= 1 || x >= W - 2 || y >= H - 2) ringInk++;
    }
    return { cssW: Math.round(r.width), cssH: Math.round(r.height), pxW: W, pxH: H, edges, ringInk, totalInk };
  });

  await page.screenshot({ path: path.join(OUT, `${dev.name}.png`) });
  out.push({ device: dev.name, geom, clip });
  await ctx.close();
}

writeFileSync(path.join(OUT, 'mobile-detail.json'), JSON.stringify(out, null, 2));

for (const r of out) {
  console.log(`\n=== ${r.device} (viewport ${r.geom.viewportW}x${r.geom.viewportH}) ===`);
  console.log(`  stage   : ${JSON.stringify(r.geom.stage)}`);
  console.log(`  card    : ${JSON.stringify(r.geom.card)}`);
  console.log(`  dock    : ${JSON.stringify(r.geom.dock)}`);
  console.log(`  DEAD SPACE  stage->dock: ${r.geom.gapStageToDock}px   stage->viewport: ${r.geom.gapStageToViewport}px`);
  console.log(`  gutters left/right: ${r.geom.gutterLeft}/${r.geom.gutterRight}px`);
  console.log(`  label layer ${r.clip.cssW}x${r.clip.cssH} -> ${r.clip.pxW}x${r.clip.pxH}px`);
  console.log(`  CLIPPING ink at edges: ${JSON.stringify(r.clip.edges)}   ring ink ${r.clip.ringInk}/${r.clip.totalInk}`);
}
await browser.close();
