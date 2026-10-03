/**
 * In landscape, does the Index rail stay visible and squeeze the map?
 *
 * Historical probe. Map view now closes the rail on EVERY viewport (`.index-col`
 * gets `map-hidden` and the layout collapses to one track), so the answer for
 * map view is "no" by construction — the squeeze this measured can no longer
 * happen. Kept because it also samples the list-view layout at the same sizes.
 *
 * The class it originally watched was `mobile-hidden`, whose rules WERE
 * width-keyed — so on an 844x390 phone (wider than the 768px breakpoint) the
 * class applied with no CSS honouring it and the rail kept ~40% of the screen.
 * `.index-col.mobile-hidden` is now the topic-DETAIL case only.
 */
import { chromium } from '@playwright/test';

const base = process.argv[2] ?? 'http://127.0.0.1:5321';
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });

for (const [name, w, h] of [['landscape-844', 844, 390], ['landscape-932', 932, 430], ['portrait-390', 390, 844]]) {
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: true, hasTouch: true });
  const page = await ctx.newPage();
  await page.goto(`${base}/second-brain`, { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => { try { localStorage.removeItem('vite-error-overlay'); } catch {} });
  await page.waitForTimeout(2200);
  const toggle = page.locator('.view-mode-control .toggle-opt:last-child');
  if (await toggle.count()) { await toggle.click(); await page.waitForTimeout(2200); }

  const m = await page.evaluate(() => {
    const box = (s) => {
      const el = document.querySelector(s);
      if (!el) return null;
      const b = el.getBoundingClientRect();
      return { w: Math.round(b.width), h: Math.round(b.height), x: Math.round(b.x), display: getComputedStyle(el).display };
    };
    return {
      index: box('.index-col'),
      indexClasses: document.querySelector('.index-col')?.className ?? 'absent',
      content: box('.content-col'),
      stage: box('.sigma-container'),
      vw: window.innerWidth,
    };
  });
  const idx = m.index;
  const pct = idx ? Math.round((idx.w / m.vw) * 100) : 0;
  console.log(`\n=== ${name} (${m.vw}px) ===`);
  console.log(`  index-col : ${idx ? `${idx.w}x${idx.h} display=${idx.display}  classes="${m.indexClasses}"` : 'absent'}`);
  console.log(`  index share of viewport width: ${pct}%`);
  console.log(`  stage     : ${m.stage ? `${m.stage.w}x${m.stage.h}` : 'absent'}`);
  await ctx.close();
}
await browser.close();
