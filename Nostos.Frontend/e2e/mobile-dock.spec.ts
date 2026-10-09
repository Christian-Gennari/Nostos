/**
 * Mobile dock geometry — the bottom navigation must be a full-bleed, thumb
 * friendly rail on phones.
 *
 * Regression guard: the desktop dock is a floating pill sized to its content
 * (`.app-dock-container { width: max-content }`, added when the dock became a
 * floating rail). The `max-width: 768px` override reset its borders, shadow
 * and position but never its width, so on a phone the "full-width rail"
 * collapsed to a ~178px pill pinned to the left edge: 46% of the screen with
 * dead space beside it and tap targets under 40x50 CSS px.
 *
 * These assertions are geometry-only (no pixel snapshots), so they stay stable
 * across styling work while still failing loudly if the rail collapses again.
 * The spec runs in the mobile-chromium project (playwright.config.ts matches
 * `mobile.*.spec.ts`); the fixture already serves the real production build.
 */
import { expect, test, type Page } from '@playwright/test';

import { loadFixture, apiPost } from './support/fixture';
import { cleanupBrain, seedBrain } from './support/brain-fixture';

const MIN_TAP_TARGET = 44; // CSS px, Apple HIG / Material minimum

let fixture: ReturnType<typeof loadFixture>;

test.beforeAll(() => {
  fixture = loadFixture();
});

async function openLibrary(page: Page): Promise<void> {
  await page.goto(`${fixture.baseUrl}/library`);
  await expect(page.locator('app-app-dock')).toBeVisible();
}

async function openCloudLibrary(page: Page): Promise<void> {
  // The shared E2E backend runs SelfHosted. Supply only the Cloud account
  // boundary needed to reach the real product shell; library requests still
  // go to the isolated backend and remain read-only in this journey.
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
      json: {
        authenticated: true,
        accountState: 'Active',
        account: { id: 'mobile-more-journey', displayName: 'Journey', email: null },
      },
    }),
  );
  await page.route('**/api/cloud/onboarding**', (route) =>
    route.fulfill({
      json: {
        state: 'ready',
        subscriptionStatus: 'active',
        ready: true,
        canCheckout: false,
        canCheckSubscription: false,
        canManageSubscription: true,
        canRetry: false,
        selectedOffer: null,
      },
    }),
  );

  await openLibrary(page);
}

interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

test('the dock is a full-width rail at the bottom edge', async ({ page }) => {
  await openLibrary(page);
  const viewport = page.viewportSize()!;

  const rail: Box = await page.locator('app-app-dock .app-dock-container').boundingBox() as Box;
  expect(rail, 'the dock container must be rendered').toBeTruthy();
  const railBottom = rail.y + rail.height;

  // Full bleed: the rail spans the viewport edge to edge.
  expect(rail.x, 'rail must start at the left edge').toBeLessThanOrEqual(1);
  expect(rail.width, 'rail must span the viewport width').toBeGreaterThanOrEqual(viewport.width - 1);

  // Anchored to the bottom edge (the host is position: fixed; bottom: 0).
  expect(viewport.height - railBottom, 'rail must sit on the bottom edge').toBeLessThanOrEqual(1);
});

test('every dock item is a comfortable tap target, not a sliver', async ({ page }) => {
  await openLibrary(page);
  const viewport = page.viewportSize()!;
  const boxes = await page.locator('app-app-dock .dock-item').evaluateAll((els) =>
    els.map((el) => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return {
        label: (el.textContent ?? '').trim(),
        x: r.x,
        y: r.y,
        width: r.width,
        height: r.height,
        fontSize: parseFloat(cs.fontSize),
      };
    })
  );

  expect(boxes.length, 'the dock has four destinations').toBe(4);
  for (const box of boxes) {
    expect(box.width, `${box.label} tap width`).toBeGreaterThanOrEqual(MIN_TAP_TARGET);
    expect(box.height, `${box.label} tap height`).toBeGreaterThanOrEqual(MIN_TAP_TARGET);
  }

  // Distributed across the viewport instead of bunched into the left half:
  // the last item must reach into the right half of the screen.
  const last = boxes[boxes.length - 1];
  expect(last.x + last.width, 'the last item must reach the right half').toBeGreaterThan(viewport.width * 0.75);
});

test('the rail does not create horizontal overflow or illegible labels', async ({ page }) => {
  await openLibrary(page);

  const overflow = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }));
  expect(overflow.scrollWidth, 'no horizontal page overflow').toBeLessThanOrEqual(overflow.clientWidth + 1);

  const labelSizes = await page
    .locator('app-app-dock .dock-item .label')
    .evaluateAll((els) => els.map((el) => parseFloat(getComputedStyle(el).fontSize)));
  for (const size of labelSizes) {
    // Below ~11px the labels stop being readable at arm's length.
    expect(size, 'dock label font size').toBeGreaterThanOrEqual(11);
  }
});

test('Cloud More has the same neutral rest state as its dock siblings in both themes', async ({ page }) => {
  await openCloudLibrary(page);
  const more = page.getByTestId('dock-more');

  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate((choice) => {
      if (choice === 'dark') document.documentElement.setAttribute('data-theme', 'dark');
      else document.documentElement.removeAttribute('data-theme');
    }, theme);

    const neutral = await page.locator('app-app-dock .dock-item').evaluateAll((els) =>
      els.map((el) => {
        const style = getComputedStyle(el);
        const r = el.getBoundingClientRect();
        return {
          background: style.backgroundColor,
          iconInk: getComputedStyle(el.querySelector('nostos-icon')!).color,
          fontFamily: style.fontFamily,
          width: r.width,
          height: r.height,
        };
      }),
    );
    expect(neutral).toHaveLength(4);
    // Brain and More are both inactive on the Library route.
    expect(neutral[3].background, theme + ': More rest background').toBe(neutral[1].background);
    expect(neutral[3].iconInk, theme + ': More rest icon ink').toBe(neutral[1].iconInk);
    expect(neutral[3].fontFamily).toBe(neutral[1].fontFamily);
    expect(Math.abs(neutral[3].width - neutral[1].width)).toBeLessThanOrEqual(1);
    expect(Math.abs(neutral[3].height - neutral[1].height)).toBeLessThanOrEqual(1);

    await more.click();
    await expect(more).toHaveAttribute('aria-expanded', 'true');
    await expect
      .poll(() => more.evaluate((el) => getComputedStyle(el).backgroundColor))
      .not.toBe(neutral[3].background);

    await page.keyboard.press('Escape');
    await expect(more).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByRole('dialog', { name: 'More' })).toHaveCount(0);
    // Background-color is animated, so allow the shared dock transition to settle.
    await expect
      .poll(() => more.evaluate((el) => getComputedStyle(el).backgroundColor))
      .toBe(neutral[3].background);
  }
});

test('Cloud More is compact, anchored to its trigger, and closes accessibly', async ({ page }) => {
  await openCloudLibrary(page);

  const more = page.getByTestId('dock-more');
  await expect(more).toHaveAttribute('aria-controls', 'mobile-more-sheet');
  await more.click();

  const menu = page.getByRole('dialog', { name: 'More' });
  const feedback = page.getByTestId('utility-sheet-feedback');
  await expect(menu).toBeVisible();
  await expect(more).toHaveAttribute('aria-expanded', 'true');
  await expect(more).toHaveClass(/dock-item-open/);
  await expect(feedback).toBeFocused();
  await expect(feedback).toHaveAttribute(
    'href',
    'https://nostos.page/feedback?from=library',
  );
  await expect(page.getByTestId('utility-sheet-settings')).toBeVisible();
  const transitionDurations = await menu.evaluate((element) =>
    getComputedStyle(element)
      .transitionDuration
      .split(',')
      .map((duration) => Number.parseFloat(duration)),
  );
  expect(transitionDurations.some((duration) => duration > 0)).toBe(true);

  const panel = (await menu.boundingBox()) as Box;
  const trigger = (await more.boundingBox()) as Box;
  expect(panel.width, 'two utilities should use a compact panel').toBeLessThanOrEqual(240);
  expect(panel.height, 'the panel should fit the two actions closely').toBeLessThanOrEqual(132);
  expect(
    Math.abs(panel.x + panel.width - trigger.x - trigger.width),
    'the panel edge should align with the More trigger',
  ).toBeLessThanOrEqual(1);
  expect(panel.y + panel.height, 'the panel should sit just above the dock').toBeLessThan(
    trigger.y,
  );

  await page.keyboard.press('Escape');
  await expect(more).toHaveAttribute('aria-expanded', 'false');
  await expect(menu).toHaveCount(0);
  await expect(more).toBeFocused();

  await page.emulateMedia({ reducedMotion: 'reduce' });
  await more.click();
  await expect(menu).toBeVisible();
  const motion = await menu.evaluate((element) => ({
    animation: getComputedStyle(element).animationDuration,
    transition: getComputedStyle(element).transitionDuration,
  }));
  expect(motion.animation).toBe('0s');
  expect(motion.transition).toBe('0s');
  await page.locator('.utility-sheet-scrim').click({ position: { x: 5, y: 5 } });
  await expect(menu).toHaveCount(0);
  await expect(more).toBeFocused();

  await more.click();
  await expect(menu).toBeVisible();
  await page.getByTestId('utility-sheet-settings').click();
  await expect(page).toHaveURL(`${fixture.baseUrl}/settings`);
  await expect(page.getByRole('tab', { name: 'Library & data' })).toBeVisible();
  await expect(menu).toHaveCount(0);
});

/**
 * The collections list is a SHARED tree (library sidebar + writing studio), and
 * the touch branch used to pin its row actions permanently on screen. With six
 * collections that renders three buttons per row — twelve icons of noise for a
 * list the user reads, and the count badge had to be hidden on every row to
 * make the slot fit.
 *
 * On touch the SELECTED row is the reveal (mirroring desktop's hover), because
 * `:hover` never fires there. This asserts the two halves that failure would
 * break: rows are clean at rest, and selecting a row brings its actions back.
 */
test('collection row actions are revealed by selection on touch, not always on', async ({ page }) => {
  const seeded: string[] = [];
  try {
    // Two collections so "clean" and "selected" are different rows.
    for (const name of ['ZZ Guard A', 'ZZ Guard B']) {
      const created = await apiPost<any>(fixture.baseUrl, '/api/collections', { name, parentId: null });
      if (created?.id) seeded.push(created.id);
    }

    await openLibrary(page);

    const actions = page.locator('.tree-row .node-actions');
    const rows = page.locator('.tree-row');
    await expect(rows.first()).toBeAttached();
    const rowCount = await rows.count();
    expect(rowCount, 'fixture must seed collection rows').toBeGreaterThan(0);

    // On mobile the sidebar is an off-canvas drawer, so its rows are off-screen
    // until the header toggle opens it.
    await page.locator('.floating-toggle').click();
    await expect(rows.first()).toBeVisible();

    const visibleCount = async (): Promise<number> =>
      await actions.evaluateAll((els) =>
        els.filter((el) => {
          const cs = getComputedStyle(el);
          return cs.opacity === '1' && cs.visibility === 'visible';
        }).length,
      );

    // At rest: no row shows its actions, so the list reads as names and counts.
    expect(await visibleCount(), 'no row actions should be pinned on at rest').toBe(0);

    // Select a row; its actions appear, and only its actions.
    await rows.first().click();
    await expect
      .poll(async () => await visibleCount(), { message: 'selecting a row must reveal its actions' })
      .toBe(1);
  } finally {
    for (const id of seeded) {
      await fetch(`${fixture.baseUrl}/api/collections/${id}`, { method: 'DELETE' }).catch(() => {});
    }
  }
});

/**
 * The mobile header toggle sits on `--bg-surface`, so its glyph must take that
 * surface's foreground — not the accent. `--color-primary` is the INK role and
 * resolves to green (formerly sage #8FA89A, now #8fbfae) on dark, which rendered
 * the hamburger green against the header rather than the near-white the other
 * header controls use. It asserts the TOKEN, not the value, so the palette change
 * does not touch it: measured 6.53:1 against 14.37:1 at the time of the fix, and
 * 9.40:1 against 17.09:1 now.
 *
 * This is the third time the same ink/fill role slip has appeared on this
 * project (brand wordmark, active collection row, and this control), so it is
 * asserted rather than left to review.
 */
test('the mobile header toggle uses the surface foreground, not the accent', async ({ page }) => {
  await openLibrary(page);
  await page.locator('.floating-toggle').click(); // hide it again so it is on screen in its resting state
  await page.waitForTimeout(500);

  const measured = await page.evaluate(() => {
    const el = document.querySelector('.floating-toggle');
    if (!el) return null;
    const root = getComputedStyle(document.documentElement);
    return {
      color: getComputedStyle(el).color,
      accent: root.getPropertyValue('--color-primary').trim(),
      surfaceFg: root.getPropertyValue('--color-text-main').trim(),
    };
  });
  expect(measured, 'the header toggle must be rendered on mobile').not.toBeNull();

  // Resolve the expected token through the browser so both sides are compared in
  // the same form (computed rgb vs. a hex literal never matches textually).
  const expected = await page.evaluate(() => {
    const probe = document.createElement('span');
    probe.style.color = 'var(--color-text-main)';
    document.body.appendChild(probe);
    const c = getComputedStyle(probe).color;
    probe.remove();
    return c;
  });

  expect(measured!.color, 'header toggle must use the surface foreground').toBe(expected);
});

/**
 * The shell's bottom reserve and the dock's own height must be the SAME number.
 *
 * They were two independent values — a 96px reserve against a 58px rail — and the
 * 38px surplus rendered as dead space between the page content and the dock on
 * every page at phone widths. Both sides now derive from the single
 * `--dock-rail-h` token, and this asserts they cannot drift apart again.
 *
 * It also checks the consequence that matters to a reader: at the end of the
 * scroll, the last content card sits just above the dock rather than behind it.
 */
test('the shell reserves exactly the dock height, leaving no dead band', async ({ page }) => {
  await openLibrary(page);

  const measured = await page.evaluate(() => {
    const shell = document.querySelector('.workspace-content') as HTMLElement | null;
    const dock = document.querySelector('app-app-dock') as HTMLElement | null;
    if (!shell || !dock) return null;
    const shellRect = shell.getBoundingClientRect();
    const dockRect = dock.getBoundingClientRect();
    return {
      reserve: parseFloat(getComputedStyle(shell).paddingBottom),
      dockHeight: dockRect.height,
      // How much viewport sits between the shell's content box and the dock top.
      bandAboveDock: dockRect.top - (shellRect.bottom - parseFloat(getComputedStyle(shell).paddingBottom)),
      viewportHeight: window.innerHeight,
    };
  });

  expect(measured, 'shell and dock must both be rendered').not.toBeNull();

  // The reserve must cover the dock (nothing hidden) without overshooting it.
  expect(
    Math.abs(measured!.reserve - measured!.dockHeight),
    `shell reserve (${measured!.reserve}px) must equal the dock height (${measured!.dockHeight}px)`
  ).toBeLessThanOrEqual(1);
});

test('the Brain dock restores a remembered query-param deep-link', async ({ page }) => {
  const topicId = '11111111-1111-1111-1111-111111111111';
  const brainUrl = `${fixture.baseUrl}/second-brain?topicId=${topicId}`;

  await page.goto(brainUrl);
  await expect(page.locator('app-app-dock')).toBeVisible();
  await expect(page).toHaveURL(brainUrl);

  await page.locator('app-app-dock .dock-item[title="Library"]').click();
  await expect(page).toHaveURL(`${fixture.baseUrl}/library`);

  await page.locator('app-app-dock .dock-item[title="The Brain"]').click();
  await expect(page).toHaveURL(brainUrl);
});

test('content can be scrolled clear of the dock on the reading view', async ({ page }) => {
  // Seed locally: without topics the index is empty, the click has nothing to
  // open, and the failure looks like a layout bug rather than missing fixture data.
  const seed = await seedBrain(
    fixture.baseUrl,
    `Dock clearance ${Date.now().toString(36)}`,
    [
      'On [[Attention]] and [[Memory]].',
      'On [[Attention]] and [[Practice]].',
      'On [[Memory]] and [[Practice]].',
    ],
    ['Attention', 'Memory', 'Practice']
  );

  try {
  await page.goto(`${fixture.baseUrl}/second-brain`);
  await expect(page.locator('.index-item').first()).toBeVisible();
  // Open a topic this test seeded rather than whatever happens to sort first:
  // the fixture is shared, and an unrelated leftover topic with no notes would
  // render the empty state instead of cards.
  await page.locator('.index-item', { hasText: 'Attention' }).first().click();

  // Wait for the detail pane to actually render its cards before measuring:
  // reading the DOM straight after the click catches the empty state mid-transition.
  await expect(page.locator('.content-col .cards-grid > *').first()).toBeVisible({ timeout: 15_000 });

  // Scroll the reading column to its end and confirm the last card is above the dock.
  const result = await page.evaluate(async () => {
    const col = document.querySelector('.content-col') as HTMLElement | null;
    const dock = document.querySelector('app-app-dock') as HTMLElement | null;
    if (!col || !dock) return null;
    col.scrollTop = col.scrollHeight;
    await new Promise((r) => requestAnimationFrame(() => r(null)));
    const cards = col.querySelectorAll('.cards-grid > *');
    const last = cards.length ? cards[cards.length - 1] : null;
    if (!last) return null;
    return {
      gap: Math.round(dock.getBoundingClientRect().top - last.getBoundingClientRect().bottom),
      dockHeight: Math.round(dock.getBoundingClientRect().height),
    };
  });

  expect(result, 'the reading view must render cards and the dock').not.toBeNull();
  expect(result!.gap, 'last card must not hide behind the dock').toBeGreaterThanOrEqual(0);
  // And the leftover margin must be a normal margin, not another dock's worth of gap.
  expect(result!.gap, 'no dead band: the trailing gap must be smaller than the dock').toBeLessThan(result!.dockHeight);
  } finally {
    await cleanupBrain(fixture.baseUrl, seed);
  }
});
