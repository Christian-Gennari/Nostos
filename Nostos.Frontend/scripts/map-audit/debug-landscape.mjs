/**
 * Why does the landscape map not come up in the fixture?
 *
 * The landscape e2e waits for `.sigma-container canvas` and times out. Dump what
 * the page looks like after clicking the view-mode toggle: whether the toggle
 * exists/was clicked, whether the content column became visible, and the stage's
 * resolved size. Guessing at the cascade has cost several rounds already.
 */
import { chromium } from '@playwright/test';
import { readFileSync } from 'node:fs';

const STATE = '/home/dev/coding/projects/nostos-rebirth-mobile-map/Nostos.Frontend/e2e/test-results/fixture-state.json';
let base = 'http://127.0.0.1:5321';
try {
  base = JSON.parse(readFileSync(STATE, 'utf8')).baseUrl ?? base;
} catch { /* fall back to the dev server */ }
console.log('baseUrl:', base);

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const ctx = await browser.newContext({ viewport: { width: 844, height: 390 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
const page = await ctx.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

await page.goto(`${base}/second-brain`, { waitUntil: 'domcontentloaded' });
await page.waitForTimeout(2500);

const before = await page.evaluate(() => {
  const t = document.querySelector('.view-mode-control .toggle-opt:last-child');
  return {
    toggleExists: !!t,
    toggleVisible: t ? t.getBoundingClientRect().width > 0 : false,
    toggleBox: t ? JSON.stringify(t.getBoundingClientRect()) : null,
    indexDisplay: document.querySelector('.index-col') ? getComputedStyle(document.querySelector('.index-col')).display : 'absent',
    contentDisplay: document.querySelector('.content-col') ? getComputedStyle(document.querySelector('.content-col')).display : 'absent',
  };
});
console.log('BEFORE click:', JSON.stringify(before, null, 1));

if (before.toggleExists) {
  await page.locator('.view-mode-control .toggle-opt:last-child').click();
  await page.waitForTimeout(3000);
}

const after = await page.evaluate(() => {
  const r = (s) => {
    const el = document.querySelector(s);
    if (!el) return 'absent';
    const b = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return `${Math.round(b.width)}x${Math.round(b.height)} @${Math.round(b.top)} display=${cs.display} vis=${cs.visibility}`;
  };
  return {
    contentCol: r('.content-col'),
    contentColClasses: document.querySelector('.content-col')?.className ?? '',
    mapWrapper: r('.map-wrapper'),
    appMap: r('app-topic-map'),
    topicMap: r('.topic-map'),
    mapStage: r('.map-stage'),
    sigmaContainer: r('.sigma-container'),
    canvasCount: document.querySelectorAll('.sigma-container canvas').length,
    indexClasses: document.querySelector('.index-col')?.className ?? '',
  };
});
console.log('\nAFTER click:', JSON.stringify(after, null, 1));
console.log('\nerrors:', JSON.stringify(errors.slice(0, 5), null, 1));
await page.screenshot({ path: '/tmp/mob/landscape-debug.png' });
await browser.close();
