#!/usr/bin/env node
/**
 * Selection-state probe — the interaction checks a static screenshot cannot make.
 *
 * The token change replaces a global dark override that only WON by
 * out-specifying Angular's rewritten component rule. That defect was invisible
 * at rest (the bare `.active` selector resolved correctly) and only appeared on
 * hover and focus-within. So a screenshot of the resting page proves nothing:
 * all three states have to be driven and read back, in BOTH themes.
 *
 * Runs against the served app. Real pointer, real keyboard focus.
 *
 * Usage: node scripts/probe-selection.mjs [--port 5214]
 */
import { chromium } from '@playwright/test';

const argv = process.argv.slice(2);
const arg = (n, d) => { const i = argv.indexOf(`--${n}`); return i >= 0 && argv[i + 1] ? argv[i + 1] : d; };
const BASE = `http://127.0.0.1:${arg('port', '5214')}`;

const readBg = (sel) => {
  const el = document.querySelector(sel);
  if (!el) return null;
  // The sidebar paints its pill on ::before, the Brain row paints its own bg.
  const cs = getComputedStyle(el, '::before');
  const own = getComputedStyle(el);
  return {
    own: own.backgroundColor,
    before: cs.backgroundColor,
    color: own.color,
  };
};

const browser = await chromium.launch();
const failures = [];
try {
  for (const theme of ['light', 'dark']) {
    const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
    const page = await ctx.newPage();
    await page.goto(BASE + '/library', { waitUntil: 'domcontentloaded' });
    await page.evaluate((t) => localStorage.setItem('nostos.theme', t), theme);

    // ---- Brain topic index: .index-item.active, hover + focus-within ----
    await page.goto(BASE + '/second-brain', { waitUntil: 'networkidle' });
    await page.waitForTimeout(1200);

    const activeSel = '.index-item.active';
    const haveActive = await page.locator(activeSel).count();
    if (!haveActive) {
      console.log(`  [${theme}] no topic selected; selecting the first index row`);
      await page.locator('.index-item').first().click();
      await page.waitForTimeout(600);
    }

    const rest = await page.evaluate(readBg, activeSel);

    // hover: move the real pointer over the row's shell
    const box = await page.locator(activeSel).first().boundingBox();
    if (box) await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.waitForTimeout(350);
    const hover = await page.evaluate(readBg, activeSel);

    // focus-within: focus a descendant button inside the shell
    await page.evaluate(() => {
      const row = document.querySelector('.index-item.active');
      const shell = row?.closest('.index-row-shell') ?? row?.parentElement;
      const btn = shell?.querySelector('button, .row-action, [tabindex]');
      if (btn) btn.focus();
    });
    await page.waitForTimeout(350);
    const focus = await page.evaluate(readBg, activeSel);

    const values = { rest: rest?.own, hover: hover?.own, focus: focus?.own };
    const distinct = new Set(Object.values(values));
    const ok = distinct.size === 1;
    console.log(`  [${theme}] brain .index-item.active  rest=${values.rest}  hover=${values.hover}  focus=${values.focus}  ${ok ? 'STABLE' : 'DIVERGED'}`);
    if (!ok) failures.push({ theme, surface: 'brain .index-item.active', values });

    // ---- Library sidebar: .nav-item.active::before ----
    await page.goto(BASE + '/library', { waitUntil: 'networkidle' });
    await page.waitForTimeout(1200);
    const navSel = '.nav-item.active';
    if (await page.locator(navSel).count()) {
      const pBox = await page.locator(navSel).first().boundingBox();
      const navRest = await page.evaluate(readBg, navSel);
      if (pBox) await page.mouse.move(pBox.x + pBox.width / 2, pBox.y + pBox.height / 2);
      await page.waitForTimeout(350);
      const navHover = await page.evaluate(readBg, navSel);
      const v = { rest: navRest?.before, hover: navHover?.before, color: navRest?.color };
      console.log(`  [${theme}] sidebar .nav-item.active::before  rest=${v.rest}  hover=${v.hover}  ink=${v.color}`);
      if (v.rest !== v.hover) failures.push({ theme, surface: 'sidebar .nav-item.active::before', values: v });
    }
    await ctx.close();
  }
} finally {
  await browser.close();
}

if (failures.length) {
  console.error('\n✖ selection state is not stable across states:');
  for (const f of failures) console.error(`   ${f.theme} / ${f.surface}: ${JSON.stringify(f.values)}`);
  console.error('');
  process.exit(1);
}
console.log('\n  ✔ selection fill stable across rest / hover / focus, both themes\n');
