import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

import { loadFixture } from './support/fixture';

const OUT = path.join(__dirname, 'test-results', 'issue-357-visual');

const CASES = [
  { name: 'desktop-light', width: 1280, height: 800, theme: 'light' as const },
  { name: 'desktop-dark', width: 1280, height: 800, theme: 'dark' as const },
  { name: 'mobile-light', width: 390, height: 844, theme: 'light' as const },
  { name: 'mobile-dark', width: 390, height: 844, theme: 'dark' as const },
];

test.describe('issue 357 — Add Book canonical form controls', () => {
  for (const tc of CASES) {
    test(tc.name, async ({ browser }) => {
      mkdirSync(OUT, { recursive: true });
      const fixture = loadFixture();
      const context = await browser.newContext({
        viewport: { width: tc.width, height: tc.height },
        deviceScaleFactor: 1,
        isMobile: tc.width < 768,
        hasTouch: tc.width < 768,
      });

      await context.addInitScript((theme: 'light' | 'dark') => {
        localStorage.setItem('nostos.theme', theme);
      }, tc.theme);

      const page = await context.newPage();
      try {
        await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
        await page.locator('button').filter({ hasText: 'Add Book' }).first().click();
        // The dialog opens on the catalogue search; the form is behind "Add manually".
        await page.getByRole('button', { name: 'Add manually' }).click();

        const form = page.locator('#add-book-form');
        await expect(form).toBeVisible();

        // The modal enters from scale(0.95). Measuring while that animation is
        // still running shrinks an otherwise 44px mobile control below the
        // touch-target floor, so verify the settled surface rather than a
        // transient animation frame.
        await page.waitForFunction(() => {
          const card = document.querySelector('app-modal-shell .modal-card');
          if (!card) return false;
          const transform = getComputedStyle(card).transform;
          return transform === 'none' || transform === 'matrix(1, 0, 0, 1, 0, 0)';
        });

        if (tc.theme === 'dark') {
          await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
        } else {
          await expect(page.locator('html')).not.toHaveAttribute('data-theme', 'dark');
        }

        // Most fields now sit behind the "more details" disclosure; open it so
        // the whole form is measured, as it was when every field was on show.
        await form.locator('details.metadata-disclosure > summary').click();
        const visibleControls = form.locator('.nostos-form-control:visible');
        expect(await visibleControls.count()).toBeGreaterThanOrEqual(10);

        const geometry = await visibleControls.evaluateAll((controls) =>
          controls.map((control) => {
            const rect = control.getBoundingClientRect();
            const style = getComputedStyle(control);
            return {
              width: rect.width,
              height: rect.height,
              right: rect.right,
              viewport: window.innerWidth,
              borderRadius: style.borderRadius,
              backgroundColor: style.backgroundColor,
              color: style.color,
            };
          }),
        );

        expect(geometry.every((g) => g.width > 0 && g.right <= g.viewport + 1)).toBe(true);
        // Desktop: the form uses the compact field rung, 36px since #921 (it was
        // 40px when this was written). Touch still grows every field to 44px.
        const floor = tc.width < 768 ? 44 : 36;
        console.log(`[issue-357:${tc.name}] geometry`, JSON.stringify(geometry));
        expect(geometry.every((g) => g.height >= floor - 0.1)).toBe(true);
        expect(new Set(geometry.map((g) => g.borderRadius)).size).toBe(1);

        const title = page.locator('#book-title');
        await title.focus();
        const focus = await title.evaluate((el) => {
          const style = getComputedStyle(el);
          return {
            outlineStyle: style.outlineStyle,
            outlineWidth: style.outlineWidth,
            outlineColor: style.outlineColor,
            borderColor: style.borderColor,
          };
        });
        expect(focus.outlineStyle).not.toBe('none');
        expect(parseFloat(focus.outlineWidth)).toBeGreaterThanOrEqual(2);

        await page.screenshot({
          path: path.join(OUT, `${tc.name}.png`),
          fullPage: false,
          animations: 'disabled',
        });
      } finally {
        await context.close();
      }
    });
  }
});
