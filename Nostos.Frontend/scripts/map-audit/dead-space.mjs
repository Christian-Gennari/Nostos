/**
 * How much dead space sits between the content and the dock, on every view?
 *
 * The map view was fixed by removing a duplicated dock reserve, but the READING
 * view still reserves the dock twice: the shell (`main.workspace-content`) pads
 * the bottom for the dock, and the column pads again for the same dock. Measure
 * the gap between the end of the content panel and the top of the dock on each
 * view, plus who owns it, so the fix targets the right rule.
 */
import { chromium } from '@playwright/test';

const base = process.argv[2] ?? 'http://127.0.0.1:5321';
const browser = await chromium.launch({
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'],
});

const VIEWS = [
  { name: 'brain index', path: '/second-brain', open: async () => {} },
  {
    name: 'brain reading',
    path: '/second-brain',
    open: async (page) => {
      await page.locator('.index-item').first().click();
      await page.waitForTimeout(1800);
    },
  },
  {
    name: 'brain map',
    path: '/second-brain',
    open: async (page) => {
      await page.locator('.view-mode-control .toggle-opt:last-child').click();
      await page.waitForTimeout(1800);
    },
  },
  { name: 'library', path: '/library', open: async () => {} },
  { name: 'studio', path: '/studio', open: async () => {} },
  { name: 'settings', path: '/settings', open: async () => {} },
];

console.log('page shell dock reserve vs dock height (dead space = reserve - dockH)\n');
for (const dev of [{ n: 'iphone-14', w: 390, h: 844 }, { n: 'pixel-7', w: 412, h: 915 }, { n: 'landscape', w: 844, h: 390 }]) {
  for (const view of VIEWS) {
    const ctx = await browser.newContext({ viewport: { width: dev.w, height: dev.h }, isMobile: true, hasTouch: true });
    const page = await ctx.newPage();
    await page.goto(`${base}${view.path}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2200);
    await view.open(page);
    await page.waitForTimeout(600);

    const m = await page.evaluate(() => {
      const box = (s) => {
        const el = document.querySelector(s);
        if (!el) return null;
        const b = el.getBoundingClientRect();
        const cs = getComputedStyle(el);
        return { top: Math.round(b.top), bottom: Math.round(b.bottom), h: Math.round(b.height), pb: cs.paddingBottom, mb: cs.marginBottom };
      };
      return {
        dock: box('app-app-dock'),
        shell: box('main.workspace-content'),
        layoutWrapper: box('.layout-wrapper'),
        contentCol: box('.content-col'),
        // The white panel the reader actually sees inside the reading view.
        panel: box('.topic-reading, .note-card, .content-col > *'),
      };
    });

    // Gap between the visible content bottom and the dock top.
    const reserve = m.shell && m.dock ? m.shell.pb : null;
    const dead = m.shell && m.dock && reserve !== null
      ? Math.max(0, parseFloat(reserve) - m.dock.h)
      : null;

    const match = reserve !== null && m.dock && Math.abs(parseFloat(reserve) - m.dock.h) <= 1;
    console.log(
      `  ${dev.n.padEnd(10)} ${view.name.padEnd(15)} dockH=${String(m.dock ? m.dock.h : '-').padEnd(4)} ` +
      `shellReserve=${String(reserve).padEnd(8)} DEAD_SPACE=${String(dead).padEnd(4)}px dockMatchesReserve=${match}`
    );
    await ctx.close();
  }
}
await browser.close();
