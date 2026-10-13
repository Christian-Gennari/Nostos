import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

import { loadFixture } from './support/fixture';

const OUT = path.join(__dirname, 'test-results', 'ui-catalogue');

const CASES = [
  { name: 'desktop-light', width: 1280, height: 800, theme: 'light' as const },
  { name: 'desktop-dark', width: 1280, height: 800, theme: 'dark' as const },
  { name: 'mobile-light', width: 390, height: 844, theme: 'light' as const },
  { name: 'mobile-dark', width: 390, height: 844, theme: 'dark' as const },
];

test.describe('Nostos UI v1 catalogue', () => {
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
        await page.goto(`${fixture.baseUrl}/ui-catalogue`, { waitUntil: 'domcontentloaded' });
        const catalogue = page.getByTestId('ui-catalogue');
        await expect(catalogue).toBeVisible();

        if (tc.theme === 'dark') {
          await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
        } else {
          await expect(page.locator('html')).not.toHaveAttribute('data-theme', 'dark');
        }

        await expect(page.locator('button.nostos-button--primary').first()).toBeVisible();
        // 7 since the action menu example added its trigger (317c71ca).
        await expect(page.locator('button.icon-btn')).toHaveCount(7);
        // 6, not 7: `feat(ui): add canonical dropdown primitive` replaced a native
        // `<select class="nostos-form-control">` with `<app-dropdown>`, so the
        // catalogue renders one fewer form control. The count was stale on main
        // before this branch (verified against the running production build).
        await expect(page.locator('.nostos-form-control')).toHaveCount(6);
        await expect(page.locator('.nostos-switch')).toHaveCount(3);
        await expect(page.locator('.nostos-chip')).toHaveCount(4);
        await expect(page.locator('.nostos-badge')).toHaveCount(4);

        await expect(page.getByRole('button', { name: 'Saving…' })).toHaveAttribute(
          'aria-busy',
          'true',
        );
        await expect(page.locator('#catalogue-author')).toHaveAttribute('aria-invalid', 'true');
        await expect(page.getByLabel('Checked switch example', { exact: true })).toBeChecked();
        await expect(page.getByRole('button', { name: 'Selected', exact: true })).toHaveAttribute(
          'aria-pressed',
          'true',
        );

        const tablist = page.getByRole('tablist', { name: 'Catalogue tab example' });
        await expect(tablist).toBeVisible();
        await tablist.getByRole('tab', { name: 'Patterns', exact: true }).click();
        await expect(tablist.getByRole('tab', { name: 'Patterns', exact: true })).toHaveAttribute(
          'aria-selected',
          'true',
        );

        // The pressed-button example is now the real component, so its options are
        // named the way the app names them (icon-only: the label is the name).
        const viewGroup = page.getByRole('group', { name: 'Catalogue view example' });
        const gridOption = viewGroup.getByRole('button', { name: 'Grid view', exact: true });
        await gridOption.click();
        await expect(gridOption).toHaveAttribute('aria-pressed', 'true');
        await expect(viewGroup.getByRole('button', { name: 'List view', exact: true })).toHaveAttribute(
          'aria-pressed',
          'false',
        );

        const secondary = page.getByTestId('secondary-button');
        const rest = await secondary.evaluate((el) => {
          const style = getComputedStyle(el);
          return { background: style.backgroundColor, color: style.color };
        });
        await secondary.hover();
        await page.waitForTimeout(220);
        const hovered = await secondary.evaluate((el) => {
          const style = getComputedStyle(el);
          return { background: style.backgroundColor, color: style.color };
        });
        expect(hovered).not.toEqual(rest);

        const title = page.locator('#catalogue-title');
        await title.focus();
        const focus = await title.evaluate((el) => {
          const style = getComputedStyle(el);
          return {
            outlineStyle: style.outlineStyle,
            outlineWidth: style.outlineWidth,
            outlineColor: style.outlineColor,
          };
        });
        expect(focus.outlineStyle).not.toBe('none');
        expect(parseFloat(focus.outlineWidth)).toBeGreaterThanOrEqual(2);

        const overflow = await page.evaluate(() => ({
          document: document.documentElement.scrollWidth - document.documentElement.clientWidth,
          body: document.body.scrollWidth - document.body.clientWidth,
        }));
        expect(overflow.document).toBeLessThanOrEqual(1);
        expect(overflow.body).toBeLessThanOrEqual(1);

        await page.getByRole('button', { name: 'Show long message', exact: true }).click();
        const longToast = page.locator('.toast-info').filter({
          hasText: 'A long notification wraps cleanly',
        });
        await expect(longToast).toBeVisible();
        await expect(longToast.locator('[role="status"]')).toHaveAttribute('aria-live', 'polite');
        expect(await longToast.locator('[role="status"]').getByRole('button').count()).toBe(0);

        const longToastGeometry = await longToast.evaluate((el) => {
          const rect = el.getBoundingClientRect();
          const message = el.querySelector<HTMLElement>('.toast-message');
          const style = message ? getComputedStyle(message) : null;
          return {
            left: rect.left,
            right: rect.right,
            viewportWidth: window.innerWidth,
            overflowWrap: style?.overflowWrap,
          };
        });
        expect(longToastGeometry.left).toBeGreaterThanOrEqual(0);
        expect(longToastGeometry.right).toBeLessThanOrEqual(longToastGeometry.viewportWidth);
        expect(longToastGeometry.overflowWrap).toBe('anywhere');

        await longToast
          .getByRole('button', { name: 'Dismiss notification', exact: true })
          .click();
        await expect(longToast).toBeHidden();

        await page.getByRole('button', { name: 'Show stacked toasts', exact: true }).click();
        await expect(page.locator('.toast')).toHaveCount(3);
        // Scoped to the toast container: the page also shows a form field in
        // its error state, which is an alert of its own.
        const toasts = page.locator('app-toast-container');
        await expect(toasts.getByRole('status')).toHaveCount(2);
        await expect(toasts.getByRole('alert')).toHaveCount(1);
        await expect(toasts.getByRole('alert')).toHaveAttribute('aria-live', 'assertive');
        expect(await toasts.getByRole('alert').getByRole('button').count()).toBe(0);
        await expect(page.locator('.toast-dismiss').first()).toHaveAttribute(
          'title',
          'Dismiss notification',
        );

        await page.screenshot({
          path: path.join(OUT, `${tc.name}.png`),
          fullPage: true,
          animations: 'disabled',
        });

        for (let remaining = 3; remaining > 0; remaining--) {
          await page.locator('.toast-dismiss').last().click();
          await expect(page.locator('.toast')).toHaveCount(remaining - 1);
        }

        await page.getByRole('button', { name: 'Open modal example', exact: true }).click();
        const dialog = page.getByRole('dialog', { name: 'Archive note?' });
        await expect(dialog).toBeVisible();
        await expect(dialog.getByRole('button', { name: 'Archive', exact: true })).toBeVisible();
        await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeVisible();

        await page.screenshot({
          path: path.join(OUT, `${tc.name}-modal.png`),
          fullPage: false,
          animations: 'disabled',
        });

        await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
        await expect(dialog).toBeHidden();
      } finally {
        await context.close();
      }
    });
  }
});
