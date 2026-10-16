/**
 * The command palette on a phone: no Ctrl+K, so a visible control opens it (the
 * More sheet where the dock has one, else the Library's button), and
 * it is a full-height sheet with a Cancel button and no keyboard hints.
 */
import { expect, test, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';

test.use({ serviceWorkers: 'block', hasTouch: true });

async function mockCloud(page: Page): Promise<void> {
  // The shared E2E backend runs SelfHosted; Cloud is what gives the rail its
  // More sheet. Library requests still go to the isolated backend.
  await page.route('**/api/runtime/capabilities', (route) =>
    route.fulfill({
      json: {
        deploymentMode: 'Cloud',
        requiresAuthentication: true,
        canConfigureAiProvider: false,
        managedAi: true,
        managedVoiceTranscription: true,
        usesCloudStorage: true,
        supportsLocalBackupConfiguration: false,
        supportsPrivateNetworkAccess: false,
        supportsEreaderAccess: true,
        usageMeteringAvailable: true,
        accountManagementUrl: 'https://nostos.page/account',
        feedbackUrl: 'https://nostos.page/feedback?from=settings',
      },
    }),
  );
  await page.route('**/api/auth/session', (route) =>
    route.fulfill({
      json: { authenticated: true, accountState: 'Active', account: { id: 'palette', displayName: 'P', email: null } },
    }),
  );
  await page.route('**/api/cloud/onboarding**', (route) =>
    route.fulfill({
      json: {
        state: 'ready', subscriptionStatus: 'active', ready: true, canCheckout: false,
        canCheckSubscription: false, canManageSubscription: true, canRetry: false, selectedOffer: null,
      },
    }),
  );
}

test('the Library opens a full-height palette sheet that Cancel closes', async ({ page }) => {
  await page.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  const trigger = page.getByTestId('library-search-everything');
  await expect(trigger).toBeVisible();
  const box = (await trigger.boundingBox())!;
  expect(box.width).toBeGreaterThanOrEqual(44);
  expect(box.height).toBeGreaterThanOrEqual(44);

  await trigger.tap();
  const sheet = page.getByTestId('command-palette');
  await expect(sheet).toBeVisible();
  await expect(page.getByTestId('palette-input')).toBeFocused();

  const viewport = page.viewportSize()!;
  const sheetBox = (await sheet.boundingBox())!;
  expect(sheetBox.width).toBeGreaterThanOrEqual(viewport.width - 1);
  expect(sheetBox.height).toBeGreaterThanOrEqual(viewport.height - 1);
  await expect(page.locator('.palette-hint')).toBeHidden();
  expect(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)).toBe(false);

  // Every row is a comfortable tap target.
  const rows = await page.locator('.palette-item').evaluateAll((els) => els.map((el) => el.getBoundingClientRect().height));
  expect(rows.length).toBeGreaterThan(0);
  for (const height of rows) expect(height).toBeGreaterThanOrEqual(44);

  await page.getByTestId('palette-cancel').tap();
  await expect(sheet).toHaveCount(0);
});

test('with a More sheet, search has one way in, not two', async ({ page }) => {
  await mockCloud(page);
  await page.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('dock-more')).toBeVisible();
  // The sheet's item reaches search from every page; the Library button stands down.
  await expect(page.getByTestId('library-search-everything')).toHaveCount(0);
  await page.getByTestId('dock-more').tap();
  await page.getByTestId('utility-sheet-search').tap();

  await expect(page.getByTestId('command-palette')).toBeVisible();
  await expect(page.getByTestId('palette-input')).toBeFocused();
  // The More sheet stands down rather than sitting under the palette.
  await expect(page.getByRole('dialog', { name: 'More' })).toHaveCount(0);
});

test('the phone-only button stays off the desktop', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const desktop = await context.newPage();
  await desktop.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await expect(desktop.locator('app-app-dock')).toBeVisible();
  await expect(desktop.getByTestId('library-search-everything')).toBeHidden();
  await context.close();
});
