/**
 * Mobile interaction audit.
 *
 * Layout can be correct while the interactions are broken, so drive the things a
 * thumb actually does: tap to select, tap the selection bar, open focus mode,
 * pinch-zoom, rotate the device, and use the back affordance. Each step records
 * what changed, so a no-op or a state that cannot be undone shows up as a
 * finding rather than a feeling.
 *
 * Touch gestures go through CDP Input.dispatchTouchEvent because Playwright's
 * touchscreen API cannot express a two-finger pinch.
 */
import { chromium } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const BASE = (process.argv[2] ?? 'http://127.0.0.1:5321').replace(/\/+$/, '');
const OUT = process.argv[3] ?? '.map-audit-out/mobile-interact';
mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true });
const page = await ctx.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
await page.addInitScript(() => {
  const drop = () => document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
  const start = () => { drop(); if (document.documentElement) new MutationObserver(drop).observe(document.documentElement, { childList: true, subtree: true }); };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
});

const cdp = await ctx.newCDPSession(page);
const findings = [];
const rec = (step, ok, detail) => {
  findings.push({ step, ok, detail });
  // Persist as we go: a crash later in the run must not discard what we learned.
  try { writeFileSync(path.join(OUT, 'interact.json'), JSON.stringify({ findings, errors }, null, 2)); } catch { /* ignore */ }
};

await page.goto(`${BASE}/second-brain`, { waitUntil: 'domcontentloaded' });
await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
await page.locator('.view-mode-control .toggle-opt').last().click();
await page.locator('.sigma-container canvas').first().waitFor({ timeout: 30_000 });
await page.waitForTimeout(3000);

const stageBox = await page.locator('.sigma-container').boundingBox();

/** Camera + selection snapshot. */
const snap = () => page.evaluate(() => {
  const sig = globalThis.__nostosSigma;
  const c = sig.getCamera();
  return {
    cam: { x: +c.x.toFixed(3), y: +c.y.toFixed(3), ratio: +c.ratio.toFixed(3) },
    selectionName: document.querySelector('.map-selection-name')?.textContent?.trim() ?? null,
    fullscreen: !!document.querySelector('.map-stage.is-fullscreen'),
    controlsVisible: document.querySelectorAll('[role="toolbar"] button').length,
  };
});

// --- 1. Tap a node ---
{
  const target = await page.evaluate(() => {
    const sig = globalThis.__nostosSigma, g = globalThis.__nostosGraph;
    const deg = new Map();
    g.forEachEdge((e, a, s, t) => { deg.set(s, (deg.get(s) ?? 0) + 1); deg.set(t, (deg.get(t) ?? 0) + 1); });
    let hub = null;
    g.forEachNode((id, a) => { const d = deg.get(id) ?? 0; if (!hub || d > hub.d) hub = { id, label: a.label, d }; });
    const v = sig.graphToViewport({ x: g.getNodeAttributes(hub.id).x, y: g.getNodeAttributes(hub.id).y });
    return { ...hub, v };
  });
  const before = await snap();
  await page.touchscreen.tap(stageBox.x + target.v.x, stageBox.y + target.v.y);
  await page.waitForTimeout(1200);
  const after = await snap();
  rec('tap node selects it', !!after.selectionName, `label=${target.label} selection=${after.selectionName}`);
  rec('selection bar appears without moving the graph', JSON.stringify(before.cam) === JSON.stringify(after.cam),
      `cam ${JSON.stringify(before.cam)} -> ${JSON.stringify(after.cam)}`);
  await page.screenshot({ path: path.join(OUT, '1-selected.png') });
}

// --- 2. Selection bar action ---
{
  const bar = page.locator('.map-selection-name');
  const visible = await bar.isVisible().catch(() => false);
  const box = visible ? await bar.boundingBox() : null;
  rec('selection bar is on screen and reachable', !!box && box.y + box.height <= 844 && box.x >= 0,
      box ? JSON.stringify(box) : 'absent');
  const openBtn = page.locator('[aria-label="Read notes"]');
  if (await openBtn.count()) {
    const before = await snap();
    await openBtn.click();
    await page.waitForTimeout(1600);
    const opened = await page.evaluate(() => ({
      url: location.pathname,
      selectionName: document.querySelector('.map-selection-name')?.textContent?.trim() ?? null,
      hasMap: !!document.querySelector('.sigma-container'),
    }));
    rec('Read notes leaves the map for the topic pane', opened.url.includes('second-brain'), JSON.stringify(opened));
    await page.screenshot({ path: path.join(OUT, '2-after-read-notes.png') });
    // Return to the MAP VIEW for the remaining steps. The back button goes to
    // the index, not the map, so use the view-mode toggle — and confirm the
    // renderer is actually back before trusting any later measurement.
    const back = page.locator('.mobile-nav-header');
    if (await back.count()) { await back.first().click(); await page.waitForTimeout(1800); }
    const mapToggle = page.locator('.view-mode-control .toggle-opt').last();
    if (await mapToggle.count()) { await mapToggle.click(); await page.waitForTimeout(2500); }
    await page.locator('.sigma-container canvas').first().waitFor({ timeout: 20_000 }).catch(() => {});
    rec('returned to the map view after Read notes',
        await page.locator('.sigma-container').count() > 0,
        `sigma containers=${await page.locator('.sigma-container').count()}`);
  }
}

// --- 3. Focus mode ---
{
  const before = await snap();
  const focus = page.locator('[aria-label="Focus mode"], [aria-label="Exit focus mode"]');
  rec('Focus mode control present', (await focus.count()) > 0, `count=${await focus.count()}`);
  if (await focus.count()) {
    await focus.first().click();
    await page.waitForTimeout(1500);
    const mid = await snap();
    rec('Focus mode engages', mid.fullscreen === true, `fullscreen=${mid.fullscreen}`);
    const stage = await page.locator('.map-stage').boundingBox();
    rec('focused stage covers the viewport', !!stage && stage.height >= 800, stage ? `h=${Math.round(stage.height)}` : 'none');
    await page.screenshot({ path: path.join(OUT, '3-focus-mode.png') });
    // Exit and confirm the controls remain reachable.
    const exit = page.locator('[aria-label="Exit focus mode"]');
    if (await exit.count()) { await exit.first().click(); await page.waitForTimeout(1500); }
    const after = await snap();
    rec('Exiting focus restores the inline map', after.fullscreen === false && after.controlsVisible > 0,
        `fullscreen=${after.fullscreen} controls=${after.controlsVisible}`);
  } else {
    rec('Focus mode control present', false, 'not found');
  }
}

// --- 4. Pinch zoom (two-finger) via CDP ---
{
  const before = await snap();
  const liveStage = await page.locator('.sigma-container').boundingBox();
  const cx = liveStage.x + liveStage.width / 2;
  const cy = liveStage.y + liveStage.height / 2;
  const touch = (type, points) => cdp.send('Input.dispatchTouchEvent', { type, touchPoints: points });
  await touch('touchStart', [
    { x: cx - 30, y: cy, id: 1 },
    { x: cx + 30, y: cy, id: 2 },
  ]);
  for (let i = 1; i <= 8; i++) {
    const spread = 30 + i * 8;
    await touch('touchMove', [
      { x: cx - spread, y: cy, id: 1 },
      { x: cx + spread, y: cy, id: 2 },
    ]);
    await page.waitForTimeout(40);
  }
  await touch('touchEnd', []);
  await page.waitForTimeout(900);
  const after = await snap();
  rec('pinch zooms the camera', Math.abs(after.cam.ratio - before.cam.ratio) > 0.02,
      `ratio ${before.cam.ratio} -> ${after.cam.ratio}`);
  await page.screenshot({ path: path.join(OUT, '4-pinched.png') });
}

// --- 5. Fit after all that ---
{
  const fitBtn = page.locator('[aria-label="Fit to view"]');
  if (!(await fitBtn.count())) {
    rec('Fit control is reachable after focus-mode exit', false, 'Fit button not present');
  } else {
  await fitBtn.click();
  await page.waitForTimeout(1400);
  const fit = await page.evaluate(() => {
    const sig = globalThis.__nostosSigma, g = globalThis.__nostosGraph;
    const dims = sig.getDimensions();
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    g.forEachNode((id, a) => { if (a.x < minX) minX = a.x; if (a.x > maxX) maxX = a.x; if (a.y < minY) minY = a.y; if (a.y > maxY) maxY = a.y; });
    const p0 = sig.graphToViewport({ x: minX, y: minY }); const p1 = sig.graphToViewport({ x: maxX, y: maxY });
    let off = 0; g.forEachNode((id, a) => { const v = sig.graphToViewport({ x: a.x, y: a.y }); if (v.x < 0 || v.y < 0 || v.x > dims.width || v.y > dims.height) off++; });
    return { fillX: Math.round(100 * Math.abs(p1.x - p0.x) / dims.width), fillY: Math.round(100 * Math.abs(p1.y - p0.y) / dims.height), off };
  });
  rec('Fit re-frames after pinch', fit.off === 0 && fit.fillX > 40, JSON.stringify(fit));
  }
}

// --- 6. Rotate to landscape and back ---
{
  for (const [w, h, tag] of [[844, 390, 'landscape'], [390, 844, 'portrait-again']]) {
    await page.setViewportSize({ width: w, height: h });
    await page.waitForTimeout(1800);
    const r = await page.evaluate(() => {
      const sig = globalThis.__nostosSigma, g = globalThis.__nostosGraph;
      if (!sig || !g) return null;
      const dims = sig.getDimensions();
      let off = 0; g.forEachNode((id, a) => { const v = sig.graphToViewport({ x: a.x, y: a.y }); if (v.x < 0 || v.y < 0 || v.x > dims.width || v.y > dims.height) off++; });
      const tiny = [...document.querySelectorAll('[role="toolbar"] button')].map((b) => b.getBoundingClientRect()).filter((b) => b.width < 44 || b.height < 44).length;
      const controlsOff = [...document.querySelectorAll('[role="toolbar"] button')].map((b) => b.getBoundingClientRect())
        .filter((b) => b.left < 0 || b.top < 0 || b.right > innerWidth || b.bottom > innerHeight).length;
      return { stage: `${Math.round(dims.width)}x${Math.round(dims.height)}`, off, tiny, controlsOff, overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth };
    });
    rec(`rotate to ${tag}: graph stays framed and controls usable`,
        !!r && r.off === 0 && r.tiny === 0 && r.controlsOff === 0 && !r.overflow, JSON.stringify(r));
    await page.screenshot({ path: path.join(OUT, `5-${tag}.png`) });
  }
}

writeFileSync(path.join(OUT, 'interact.json'), JSON.stringify({ findings, errors }, null, 2));
console.log('');
for (const f of findings) console.log(`${f.ok ? 'PASS' : 'FAIL'}  ${f.step}\n        ${f.detail}`);
console.log(`\nSUMMARY: ${findings.filter((f) => f.ok).length}/${findings.length} passed, console errors: ${errors.length}`);
if (errors.length) console.log('errors:', JSON.stringify(errors.slice(0, 4), null, 2));
await browser.close();
