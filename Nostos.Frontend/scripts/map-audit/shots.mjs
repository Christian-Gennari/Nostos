/**
 * Capture the topic map at 1:1 for visual review, in both themes.
 * Also hides only the floating controls so the graph itself is unobstructed.
 */
import { chromium } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

const BASE = (process.argv[2] ?? 'http://127.0.0.1:4200').replace(/\/+$/, '');
const OUT = process.argv[3] ?? '.map-audit-out/shots';
mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });

for (const theme of ['light', 'dark']) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
  const page = await ctx.newPage();
  await page.addInitScript(() => {
    const drop = () => document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
    const start = () => { drop(); if (document.documentElement) new MutationObserver(drop).observe(document.documentElement, { childList: true, subtree: true }); };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
  });
  await page.goto(`${BASE}/second-brain`, { waitUntil: 'domcontentloaded' });
  if (theme === 'dark') {
    await page.evaluate(() => localStorage.setItem('nostos.theme', 'dark'));
    await page.reload({ waitUntil: 'domcontentloaded' });
  }
  await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
  await page.locator('.view-mode-control .toggle-opt:last-child').click();
  await page.locator('.sigma-container canvas').first().waitFor({ timeout: 30_000 });
  await page.waitForTimeout(3200);

  // Full page: shows the graph in its real context.
  await page.screenshot({ path: path.join(OUT, `map-${theme}-page.png`) });
  // Stage only at 1:1: how the graph itself reads.
  await page.locator('.sigma-container').screenshot({ path: path.join(OUT, `map-${theme}-stage.png`) });

  // Selection state: the other user-reported case.
  const t = await page.evaluate(() => {
    const sig = globalThis.__nostosSigma, g = globalThis.__nostosGraph;
    let best = null;
    g.forEachNode((id, a) => { if (!best || a.size > best.size) best = { id, label: a.label, a }; });
    const p = sig.graphToViewport({ x: best.a.x, y: best.a.y });
    return { label: best.label, vp: { x: p.x, y: p.y } };
  });
  const box = await page.locator('.sigma-container').boundingBox();
  await page.mouse.click(box.x + t.vp.x, box.y + t.vp.y);
  await page.waitForTimeout(1600);
  await page.screenshot({ path: path.join(OUT, `map-${theme}-selected-page.png`) });

  console.log(theme, 'selected node:', t.label);
  await ctx.close();
}

await browser.close();
console.log('done ->', OUT);
