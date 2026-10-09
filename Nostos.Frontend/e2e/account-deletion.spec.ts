import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

import { loadFixture } from './support/fixture';

test.use({ serviceWorkers: 'block' });

const pendingStatus = {
  state: 'GracePeriod',
  gracePeriodDays: 14,
  requestedAtUtc: '2026-10-01T12:00:00Z',
  eligibleAtUtc: '2026-10-15T12:00:00Z',
  completedAtUtc: null,
  canCancel: true,
  portableExportUrl: '/api/portability/export',
};

async function mockHostedAccount(page: Page): Promise<{
  calls: string[];
  setAccountState: (state: 'Active' | 'DeletionRequested') => void;
}> {
  const calls: string[] = [];
  let accountState: 'Active' | 'DeletionRequested' = 'Active';

  await page.route('**/api/runtime/capabilities', (route: Route) =>
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
        supportsAccountDeletion: true,
      },
    }),
  );
  await page.route('**/api/auth/session', (route: Route) =>
    route.fulfill({
      json: {
        authenticated: true,
        accountState,
        account: { id: 'deletion-e2e', displayName: 'Reader', email: 'reader@example.test' },
      },
    }),
  );
  await page.route('**/api/cloud/onboarding**', (route: Route) =>
    route.fulfill({
      json: {
        state: 'ready',
        subscriptionStatus: 'Active',
        ready: true,
        canCheckout: false,
        canCheckSubscription: false,
        canManageSubscription: false,
        canRetry: false,
        selectedOffer: null,
      },
    }),
  );
  await page.route(/\/api\/cloud\/account\/deletion(?:\/.*)?$/, async (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    calls.push(`${request.method()} ${path}`);

    if (request.method() === 'GET') {
      return route.fulfill({ json: accountState === 'DeletionRequested' ? pendingStatus : {
        ...pendingStatus,
        state: 'Active',
        canCancel: false,
        portableExportUrl: null,
      } });
    }

    if (path.endsWith('/cancel')) {
      accountState = 'Active';
      return route.fulfill({
        json: { ...pendingStatus, state: 'Cancelled', canCancel: false, portableExportUrl: null },
      });
    }

    expect(request.postDataJSON()).toEqual({ confirm: true });
    accountState = 'DeletionRequested';
    return route.fulfill({ json: pendingStatus });
  });

  return {
    calls,
    setAccountState: (state) => { accountState = state; },
  };
}

async function waitForConfirmationDialog(page: Page): Promise<void> {
  await expect(page.locator('.modal-card[role="alertdialog"]')).toHaveCSS('opacity', '1');
  await expect(page.locator('.modal-backdrop')).toHaveCSS('opacity', '1');
}

test('SelfHosted settings never renders or calls the Cloud deletion route', async ({ page }) => {
  const calls: string[] = [];
  await page.route(/\/api\/cloud\/account\/deletion(?:\/.*)?$/, (route) => {
    calls.push(`${route.request().method()} ${new URL(route.request().url()).pathname}`);
    return route.fulfill({ status: 500, body: 'SelfHosted must not call this route' });
  });

  await page.goto(`${loadFixture().baseUrl}/settings`);
  await expect(page.getByTestId('cloud-account-deletion-row')).toHaveCount(0);
  expect(calls).toEqual([]);
});

test('Cloud settings confirms, enters pending state, and cancellation returns to the app', async ({ page }) => {
  const hosted = await mockHostedAccount(page);
  await page.goto(`${loadFixture().baseUrl}/settings`);

  await page.getByRole('tab', { name: 'Account' }).click();
  await expect(page.getByTestId('cloud-account-deletion-row')).toBeVisible();
  await page.getByTestId('cloud-account-deletion-open').click();

  const dialog = page.getByRole('alertdialog', { name: 'Delete your Nostos Cloud account?' });
  await expect(dialog).toBeVisible();
  await waitForConfirmationDialog(page);
  const confirmation = page.getByTestId('cloud-account-deletion-confirmation');
  const confirm = dialog.getByRole('button', { name: 'Request deletion' });
  const exportLink = dialog.getByRole('link', { name: 'Manage library' });
  await expect(exportLink).toBeFocused();
  await expect(confirm).toBeDisabled();
  await expect(dialog).toContainText(
    'Your subscription will not renew while this deletion request is pending',
  );
  await expect(dialog).toContainText('not refunded automatically');
  await expect(dialog.getByRole('link', { name: 'Account & billing' })).toHaveCount(0);

  await page.keyboard.press('Tab');
  await expect(confirmation).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('button', { name: 'Keep account' })).toBeFocused();

  await confirmation.fill('DELETE');
  await expect(confirm).toBeEnabled();
  await confirm.click();

  await expect(page.getByRole('heading', { name: 'Deletion pending' })).toBeVisible();
  await expect(page.getByText(/October 15, 2026/u)).toBeVisible();
  await expect(page.getByRole('link', { name: 'Export my library' })).toHaveAttribute(
    'href',
    '/api/portability/export',
  );
  await expect(page.getByRole('button', { name: 'Cancel deletion' })).toBeVisible();
  await expect(
    page.getByText(
      'Your subscription will not renew while deletion is pending. Cancel deletion before your paid period ends to keep your subscription; after it ends, cancelling deletion still restores your account and library, but you will need to subscribe again.',
    ),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: 'Account & billing' })).toBeVisible();

  await page.getByRole('button', { name: 'Cancel deletion' }).click();
  await expect(page.getByRole('tablist', { name: 'Settings sections' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Deletion pending' })).toHaveCount(0);
  await page.getByRole('tab', { name: 'Account' }).click();
  await expect(page.getByTestId('cloud-account-deletion-row')).toBeVisible();

  expect(hosted.calls.filter((call) => call.startsWith('POST'))).toEqual([
    'POST /api/cloud/account/deletion/',
    'POST /api/cloud/account/deletion/cancel',
  ]);
});

test('a DeletionRequested session reopens the pending screen after refresh', async ({ page }) => {
  const hosted = await mockHostedAccount(page);
  hosted.setAccountState('DeletionRequested');
  await page.goto(`${loadFixture().baseUrl}/settings`);

  await expect(page.getByRole('heading', { name: 'Deletion pending' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Export my library' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Cancel deletion' })).toBeVisible();
  expect(hosted.calls).toContain('GET /api/cloud/account/deletion/');
});

test('captures the hosted deletion controls in both themes and target viewports', async ({ browser }) => {
  const output = '/tmp/nostos-account-deletion';
  mkdirSync(output, { recursive: true });

  for (const theme of ['light', 'dark'] as const) {
    for (const viewport of [
      { name: 'desktop', width: 1440, height: 900 },
      { name: 'mobile', width: 390, height: 844 },
    ]) {
      const context = await browser.newContext({
        viewport: { width: viewport.width, height: viewport.height },
        deviceScaleFactor: 1,
        serviceWorkers: 'block',
      });
      const page = await context.newPage();
      await page.addInitScript((value) => localStorage.setItem('nostos.theme', value), theme);
      const hosted = await mockHostedAccount(page);
      await page.goto(`${loadFixture().baseUrl}/settings`);
      await page.getByRole('tab', { name: 'Account' }).click();
      await expect(page.getByTestId('cloud-account-deletion-row')).toBeVisible();
      await page.screenshot({
        path: path.join(output, `settings-row-${theme}-${viewport.name}.png`),
      });

      await page.getByTestId('cloud-account-deletion-open').click();
      const dialog = page.getByRole('alertdialog', { name: 'Delete your Nostos Cloud account?' });
      await expect(dialog).toBeVisible();
      await waitForConfirmationDialog(page);
      await page.screenshot({
        path: path.join(output, `dialog-empty-${theme}-${viewport.name}.png`),
      });

      if (viewport.width === 390) {
        const cardBounds = await dialog.boundingBox();
        const keepBounds = await dialog.getByRole('button', { name: 'Keep account' }).boundingBox();
        const requestBounds = await dialog
          .getByRole('button', { name: 'Request deletion' })
          .boundingBox();
        expect(cardBounds).not.toBeNull();
        expect(cardBounds!.y + cardBounds!.height).toBeLessThanOrEqual(viewport.height);
        expect(keepBounds).not.toBeNull();
        expect(requestBounds).not.toBeNull();
        expect(keepBounds!.x + keepBounds!.width).toBeLessThan(requestBounds!.x);
      }

      await page.getByTestId('cloud-account-deletion-confirmation').fill('DELETE');
      await expect(dialog.getByRole('button', { name: 'Request deletion' })).toBeEnabled();
      if (viewport.width === 390) {
        await page.keyboard.press('Tab');
        await expect(dialog.getByRole('button', { name: 'Keep account' })).toBeFocused();
        await page.keyboard.press('Tab');
        await expect(dialog.getByRole('button', { name: 'Request deletion' })).toBeFocused();
        await page.getByTestId('cloud-account-deletion-confirmation').focus();
      }
      await waitForConfirmationDialog(page);
      await page.screenshot({
        path: path.join(output, `dialog-ready-${theme}-${viewport.name}.png`),
      });

      await dialog.getByRole('button', { name: 'Request deletion' }).click();
      await expect(page.getByRole('heading', { name: 'Deletion pending' })).toBeVisible();
      await page.screenshot({
        path: path.join(output, `pending-${theme}-${viewport.name}.png`),
      });
      await context.close();
      hosted.setAccountState('Active');
    }
  }
});
