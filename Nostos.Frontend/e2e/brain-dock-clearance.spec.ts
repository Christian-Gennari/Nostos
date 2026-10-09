import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';

import { loadFixture } from './support/fixture';
import { cleanupBrain, seedBrain } from './support/brain-fixture';

const viewports = [
  { width: 1440, height: 900 },
  { width: 1280, height: 720 },
  { width: 1024, height: 768 },
  { width: 820, height: 1180 },
  { width: 390, height: 844 },
  { width: 844, height: 390 },
];

async function undersizedVisibleControls(page: Page, rootSelector: string): Promise<string[]> {
  return page.evaluate((selector) => {
    const root = document.querySelector(selector);
    if (!root) return ['missing root ' + selector];
    const candidates = Array.from(root.querySelectorAll(
      'button, select, textarea, input:not([type="hidden"]):not([type="checkbox"]), summary, a[appButton], a.source-badge, a.brain-source-link, a.empty-link',
    ));
    return candidates.flatMap((element) => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      if (!rect.width || !rect.height || style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0' || element.closest('[aria-hidden="true"]')) return [];
      if (rect.width >= 44 && rect.height >= 44) return [];
      const label = element.getAttribute('aria-label') || (element.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 50);
      return [`${label || element.tagName}: ${rect.width.toFixed(1)}x${rect.height.toFixed(1)}`];
    });
  }, rootSelector);
}

async function scrollVisibleRowToEnd(page: Page, selector: string): Promise<void> {
  await page.evaluate((rowSelector) => {
    const rows = Array.from(document.querySelectorAll(rowSelector)).filter(
      (row) => row.getClientRects().length > 0,
    );
    const row = rows.at(-1);
    if (!row) throw new Error('No visible row found for ' + rowSelector);
    for (let element = row.parentElement; element; element = element.parentElement) {
      const overflowY = getComputedStyle(element).overflowY;
      if (
        element.scrollHeight > element.clientHeight &&
        (overflowY === 'auto' || overflowY === 'scroll')
      ) {
        element.scrollTop = element.scrollHeight;
      }
    }
    const pageScroller = document.scrollingElement;
    if (pageScroller) pageScroller.scrollTop = pageScroller.scrollHeight;
  }, selector);
}

test('Brain topic index clears the dock and exposes 44px actions on coarse pointers', async ({
  browser,
}) => {
  const fixture = loadFixture();
  const stamp = Date.now().toString(36);
  const topicNames = Array.from(
    { length: 40 },
    (_, index) => 'Dock clearance ' + stamp + ' ' + String(index + 1).padStart(2, '0'),
  );
  topicNames.push('Shared dock evidence ' + stamp);
  const topicNotes = Array.from(
    { length: 40 },
    (_, index) =>
      'Topic note ' +
      (index + 1) +
      ' for [[' +
      topicNames[index] +
      ']] and [[' +
      topicNames[40] +
      ']].',
  );
  const unlinkedNotes = Array.from(
    { length: 40 },
    (_, index) => index === 0
      ? 'Long dock search note ' + stamp + '\n\n' + Array.from(
          { length: 24 },
          (_, paragraph) => 'Search panel paragraph ' + (paragraph + 1) + '. ' +
            'This long synthetic note ensures the search result panel has enough content to scroll.',
        ).join('\n\n')
      : 'Unlinked dock review note ' + stamp + ' ' + String(index + 1),
  );
  const seed = await seedBrain(
    fixture.baseUrl,
    'Dock clearance source ' + stamp,
    [...topicNotes, ...unlinkedNotes],
    topicNames,
  );
  const context = await browser.newContext({
    viewport: { width: 820, height: 1180 },
    isMobile: true,
    hasTouch: true,
    deviceScaleFactor: 1,
  });
  const page = await context.newPage();

  try {
    const dockFailures: string[] = [];
    const touchTargetFailures: string[] = [];
    await page.addInitScript(() => localStorage.setItem('nostos.theme', 'light'));
    await page.goto(fixture.baseUrl + '/second-brain', {
      waitUntil: 'domcontentloaded',
    });
    await page
      .locator('.brain-areas')
      .getByRole('button', { name: 'Topics', exact: true })
      .click();

    const rows = page.locator('.index-row-shell:visible');
    const rowCount = await rows.count();
    expect(rowCount).toBeGreaterThanOrEqual(40);
    await expect
      .poll(() => page.evaluate(() => matchMedia('(pointer: coarse)').matches))
      .toBe(true);
    const list = page.locator('.index-list:visible').last();
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      await expect(list).toBeVisible();
      await scrollVisibleRowToEnd(page, '.index-row-shell');
      await list.evaluate((element) => {
        element.scrollTop = element.scrollHeight;
      });
      const rowBox = await rows.last().boundingBox();
      const dockBox = await page.locator('app-app-dock').boundingBox();
      const layoutMetrics = await page.evaluate(() => {
        const read = (selector: string) => {
          const element = document.querySelector<HTMLElement>(selector);
          if (!element) return null;
          const style = getComputedStyle(element);
          const rect = element.getBoundingClientRect();
          return {
            y: rect.y,
            height: rect.height,
            flex: style.flex,
            minHeight: style.minHeight,
            overflowY: style.overflowY,
            scrollHeight: element.scrollHeight,
            clientHeight: element.clientHeight,
            scrollTop: element.scrollTop,
          };
        };
        return {
          layout: read('.brain-layout'),
          header: read('.brain-header'),
          rail: read('.index-col'),
          list: read('.index-list'),
        };
      });
      expect(rowBox).not.toBeNull();
      expect(dockBox).not.toBeNull();
        if (rowBox!.y + rowBox!.height > dockBox!.y) {
          dockFailures.push(
            `Topics final row at ${viewport.width}x${viewport.height}: ${rowBox!.y + rowBox!.height}px > dock ${dockBox!.y}px; ${JSON.stringify(layoutMetrics)}`,
          );
        }
      if (viewport.width === 820) {
        await page.screenshot({
          path: '/tmp/804-brain-topics-index-scroll-end-after-820x1180-light.png',
          animations: 'disabled',
        });
      }
      if (viewport.width === 820 || viewport.width === 390) {
        const smallControls = await undersizedVisibleControls(page, '.brain-layout');
        if (smallControls.length) {
          touchTargetFailures.push(`Brain Topics index ${viewport.width}x${viewport.height}: ${JSON.stringify(smallControls)}`);
        }
      }
    }
    const lastRow = rows.last();
    for (const viewport of [
      { width: 820, height: 1180 },
      { width: 390, height: 844 },
    ]) {
      await page.setViewportSize(viewport);
      const backToTopics = page.getByRole('button', { name: 'Back to Index', exact: true });
      if (await backToTopics.isVisible().catch(() => false)) await backToTopics.click();
      await lastRow.locator('.index-item').focus();
      const actions = lastRow.locator('.row-actions .row-action');
      await expect(actions).toHaveCount(2);
      const hitAreas = await actions.evaluateAll((buttons) =>
        buttons.map((button) => {
          const rect = button.getBoundingClientRect();
          return { width: rect.width, height: rect.height };
        }),
      );
      if (!hitAreas.every((area) => area.width >= 44 && area.height >= 44)) {
        touchTargetFailures.push(`Topic row action hit areas ${viewport.width}x${viewport.height}: ${JSON.stringify(hitAreas)}`);
      }
      const smallControls = await undersizedVisibleControls(page, '.brain-layout');
      if (smallControls.length) touchTargetFailures.push(`Brain Topics actions ${viewport.width}x${viewport.height}: ${JSON.stringify(smallControls)}`);
      if (viewport.width === 820) {
        await page.screenshot({
          path: '/tmp/804-brain-topics-index-coarse-actions-after-820x1180-light.png',
          animations: 'disabled',
        });
      } else {
        await page.screenshot({
          path: '/tmp/804-brain-topics-index-coarse-actions-after-390x844-light.png',
          animations: 'disabled',
        });
      }
    }
    await page.evaluate(() => {
      localStorage.setItem('nostos.theme', 'dark');
      document.documentElement.setAttribute('data-theme', 'dark');
    });
    await page.screenshot({
      path: '/tmp/804-brain-topics-index-coarse-actions-after-820x1180-dark.png',
      animations: 'disabled',
    });

    await page.evaluate(() => {
      localStorage.setItem('nostos.theme', 'light');
      document.documentElement.setAttribute('data-theme', 'light');
    });
    await page
      .locator('.brain-areas')
      .getByRole('button', { name: 'Notes', exact: true })
      .click();
    const reviewFilter = page
      .locator('.brain-note-filter-row')
      .getByRole('button', { name: 'Without topics' });
    if ((await reviewFilter.getAttribute('aria-pressed')) !== 'true') {
      await reviewFilter.click();
    }
    await expect(reviewFilter).toHaveAttribute('aria-pressed', 'true');
    const noteRows = page.locator('.index-list:visible .note-row-item');
    await noteRows.first().waitFor({ timeout: 30_000 });
    expect(await noteRows.count()).toBeGreaterThan(20);
    const loadMore = page.getByRole('button', { name: 'Load more', exact: true });
    while (await loadMore.count()) {
      const previousCount = await noteRows.count();
      await loadMore.click();
      await expect.poll(() => noteRows.count()).toBeGreaterThan(previousCount);
    }
    // The browser fixture is shared across specs in one Playwright invocation.
    // Other Brain specs can leave their own unlinked rows in it, so assert the
    // synthetic set is present without assuming it is the only set.
    expect(await noteRows.count()).toBeGreaterThanOrEqual(40);
    const notesList = page.locator('.index-list:visible').last();
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      await expect(notesList).toBeVisible();
      await scrollVisibleRowToEnd(page, '.index-list .note-row-item');
      const rowBox = await noteRows.last().boundingBox();
      const dockBox = await page.locator('app-app-dock').boundingBox();
      expect(rowBox).not.toBeNull();
      expect(dockBox).not.toBeNull();
        if (rowBox!.y + rowBox!.height > dockBox!.y) {
          dockFailures.push(
            `Without topics final row at ${viewport.width}x${viewport.height}: ${rowBox!.y + rowBox!.height}px > dock ${dockBox!.y}px`,
          );
        }
      if (viewport.width === 390 || viewport.width === 820) {
        const smallControls = await undersizedVisibleControls(page, '.brain-layout');
        if (smallControls.length) touchTargetFailures.push(`Brain Without topics ${viewport.width}x${viewport.height}: ${JSON.stringify(smallControls)}`);
      }
      if (viewport.width === 820) {
        await page.screenshot({
          path: '/tmp/804-brain-notes-without-topics-scroll-end-after-820x1180-light.png',
          animations: 'disabled',
        });
      }
    }

    await page.setViewportSize({ width: 1440, height: 900 });
    await page
      .locator('.brain-areas')
      .getByRole('button', { name: 'Topics', exact: true })
      .click();
    const noteSearch = page.getByRole('textbox', { name: 'Search topics and notes' });
    await noteSearch.fill('Long dock search note ' + stamp);
    const searchResult = page
      .locator('.brain-notes-list .note-row-item')
      .filter({ hasText: 'Long dock search note ' + stamp });
    await expect(searchResult).toHaveCount(1);
    await searchResult.click();
    const searchPanel = page.locator('[data-testid="brain-note-panel"]');
    await expect(searchPanel).toBeVisible();
    const searchPanelFailures: string[] = [];
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      const panelEnd = await page.evaluate(() => {
        const panel = document.querySelector<HTMLElement>('.brain-note-panel')!;
        const documentScroller = document.scrollingElement!;
        panel.scrollTop = panel.scrollHeight;
        documentScroller.scrollTop = documentScroller.scrollHeight;
        const actionRow = document
          .querySelector('.brain-note-panel-actions')!
          .getBoundingClientRect();
        const dock = document.querySelector('app-app-dock')!.getBoundingClientRect();
        return { actionBottom: actionRow.bottom, dockTop: dock.top };
      });
      if (panelEnd.actionBottom > panelEnd.dockTop) {
        searchPanelFailures.push(
          `${viewport.width}x${viewport.height}: action bottom ${panelEnd.actionBottom}px > dock top ${panelEnd.dockTop}px`,
        );
      }
      if (viewport.width === 390 || viewport.width === 820) {
        const smallControls = await undersizedVisibleControls(page, '.brain-layout');
        if (smallControls.length) touchTargetFailures.push(`Brain Search panel ${viewport.width}x${viewport.height}: ${JSON.stringify(smallControls)}`);
      }
      if (viewport.width === 820 || viewport.width === 844) {
        await page.screenshot({
          path: '/tmp/804-brain-search-note-panel-scroll-end-after-' +
            viewport.width + 'x' + viewport.height + '-light.png',
          animations: 'disabled',
        });
      }
    }
    dockFailures.push(...searchPanelFailures.map((failure) => `Search note panel ${failure}`));

    await page.locator('.brain-note-panel-close').click();
    await page.getByRole('button', { name: 'Clear search' }).click();
    await page.setViewportSize({ width: 820, height: 1180 });
    const sharedTopic = page
      .locator('.index-item')
      .filter({ hasText: topicNames[40] })
      .first();
    await sharedTopic.click();
    const evidence = page.locator('[data-testid="topic-evidence"] .note-card');
    await expect(evidence).toHaveCount(40);
    const topicDetail = page.locator('.content-col:visible').last();
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      await expect(topicDetail).toBeVisible();
      await scrollVisibleRowToEnd(
        page,
        '[data-testid="topic-evidence"] .note-card',
      );
      const cardBox = await evidence.last().boundingBox();
      const dockBox = await page.locator('app-app-dock').boundingBox();
      expect(cardBox).not.toBeNull();
      expect(dockBox).not.toBeNull();
        if (cardBox!.y + cardBox!.height > dockBox!.y) {
          dockFailures.push(
            `Topic evidence final card at ${viewport.width}x${viewport.height}: ${cardBox!.y + cardBox!.height}px > dock ${dockBox!.y}px`,
          );
        }
      if (viewport.width === 820) {
        await page.screenshot({
          path: '/tmp/804-brain-topic-detail-scroll-end-after-820x1180-light.png',
          animations: 'disabled',
        });
      }
      if (viewport.width === 390 || viewport.width === 820) {
        const smallControls = await undersizedVisibleControls(page, '.brain-layout');
        if (smallControls.length) {
          touchTargetFailures.push(`Brain Topic detail ${viewport.width}x${viewport.height}: ${JSON.stringify(smallControls)}`);
        }
      }
    }

    await page
      .locator('.brain-areas')
      .getByRole('button', { name: 'Notes', exact: true })
      .click();
    const withoutTopics = page
      .locator('.brain-note-filter-row')
      .getByRole('button', { name: 'Without topics' });
    if ((await withoutTopics.getAttribute('aria-pressed')) !== 'true') {
      await withoutTopics.click();
    }
    await expect(withoutTopics).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Review one by one' })).toBeVisible();
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.getByRole('button', { name: 'Review one by one' }).click();
    const reviewList = page.locator('.index-list[aria-label="Notes with no topic"]');
    const reviewRows = reviewList.locator('.note-row-item');
    await expect(reviewRows.first()).toBeVisible();
    const reviewLoadMore = reviewList.locator('.review-load-more');
    while (await reviewLoadMore.count()) {
      const previousCount = await reviewRows.count();
      await reviewLoadMore.click();
      await expect.poll(() => reviewRows.count()).toBeGreaterThan(previousCount);
    }
    expect(await reviewRows.count()).toBeGreaterThanOrEqual(40);
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      if (await reviewList.isVisible()) {
        await reviewList.evaluate((element) => {
          element.scrollTop = element.scrollHeight;
        });
        const reviewEnd = await page.evaluate(() => {
          const row = document
            .querySelector('[aria-label="Notes with no topic"] .note-row-item:last-child')!
            .getBoundingClientRect();
          const dock = document.querySelector('app-app-dock')!.getBoundingClientRect();
          return { rowBottom: row.bottom, dockTop: dock.top };
        });
        if (reviewEnd.rowBottom > reviewEnd.dockTop) {
          dockFailures.push(
            `Review queue final row at ${viewport.width}x${viewport.height}: ${reviewEnd.rowBottom}px > dock ${reviewEnd.dockTop}px`,
          );
        }
      }
      if ((viewport.width === 390 || viewport.width === 820) && await reviewList.isVisible()) {
        const smallControls = await undersizedVisibleControls(page, '.brain-layout');
        if (smallControls.length) {
          touchTargetFailures.push(`Brain Review list ${viewport.width}x${viewport.height}: ${JSON.stringify(smallControls)}`);
        }
      }
      if (viewport.width === 820) {
        await page.screenshot({
          path: '/tmp/804-brain-review-list-scroll-end-after-820x1180-light.png',
          animations: 'disabled',
        });
      }
    }
    expect(dockFailures, 'All visible Brain scroll regions clear the dock').toEqual([]);
    expect(touchTargetFailures, 'Coarse-pointer Brain row actions meet 44px').toEqual([]);
  } finally {
    await context.close();
    await cleanupBrain(fixture.baseUrl, seed);
  }
});
