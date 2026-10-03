/**
 * Why does a landscape phone still get the desktop map layout?
 *
 * Prints, for the map view at 844x390:
 *   - which media queries the browser reports as matching
 *   - the resolved display/padding/height of .content-col and its ancestors
 *   - the stage rect, so overflow is visible directly
 * Guessing at the cascade cost two rounds; this reads it.
 */
import { chromium } from '@playwright/test';

const BASE = (process.argv[2] ?? 'http://127.0.0.1:5321').replace(/\/+$/, '');
const W = Number(process.argv[3] ?? 844);
const H = Number(process.argv[4] ?? 390);

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const ctx = await browser.newContext({ viewport: { width: W, height: H }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
const page = await ctx.newPage();
await page.addInitScript(() => {
  const drop = () => document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
  const start = () => { drop(); if (document.documentElement) new MutationObserver(drop).observe(document.documentElement, { childList: true, subtree: true }); };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
});
await page.goto(`${BASE}/second-brain`, { waitUntil: 'domcontentloaded' });
await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
await page.locator('.view-mode-control .toggle-opt').last().click();
await page.locator('.sigma-container canvas').first().waitFor({ timeout: 30_000 });
await page.waitForTimeout(3000);

const info = await page.evaluate(() => {
  const matches = [];
  for (const sheet of [...document.styleSheets]) {
    let rules;
    try { rules = sheet.cssRules; } catch { continue; }
    for (const rule of [...rules]) {
      if (rule.media && rule.media.length) {
        const q = rule.media.mediaText;
        if (/width|height/.test(q)) matches.push({ q, matches: matchMedia(q).matches, rules: rule.cssRules ? rule.cssRules.length : 0 });
      }
    }
  }
  const row = (sel) => {
    const el = document.querySelector(sel);
    if (!el) return { sel, missing: true };
    const cs = getComputedStyle(el);
    const b = el.getBoundingClientRect();
    return { sel, h: Math.round(b.height), top: Math.round(b.top), bottom: Math.round(b.bottom),
             display: cs.display, padding: cs.padding, flex: cs.flex, height: cs.height, overflowY: cs.overflowY };
  };
  return {
    viewport: `${innerWidth}x${innerHeight}`,
    matchingQueries: matches.sort((a, b) => (b.matches ? 1 : 0) - (a.matches ? 1 : 0)),
    chain: ['.sigma-container', '.map-stage', '.topic-map', 'app-topic-map', '.map-wrapper', '.content-col', '.brain-layout'].map(row),
    contentColHasMap: !!document.querySelector('.content-col:has(app-topic-map)'),
  };
});

console.log('viewport:', info.viewport);
console.log('\nwidth/height media queries (matching first):');
for (const q of info.matchingQueries) console.log(`  ${q.matches ? 'MATCH  ' : 'no     '} ${q.q}  (${q.rules} rules)`);
console.log('\nresolved chain:');
for (const r of info.chain) console.log('  ' + JSON.stringify(r));
console.log('\n.content-col:has(app-topic-map) matched by selector engine:', info.contentColHasMap);
await browser.close();
