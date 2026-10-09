import { expect, test } from '@playwright/test';
import { loadFixture } from './support/fixture';
import { cleanupBrain, seedBrain } from './support/brain-fixture';
import { DESKTOP_VIEWPORT, newCapturePage } from './support/visual-capture';

test('Notes, review and topic evidence share one calm action hierarchy', async ({ browser }) => {
  const fixture = loadFixture();
  const run = Date.now().toString(36);
  const topicName = 'Action hierarchy ' + run;
  const bookTitle = 'Action hierarchy source ' + run;
  const unlinkedText = 'Unlinked action hierarchy review note ' + run;
  const seed = await seedBrain(
    fixture.baseUrl,
    bookTitle,
    [
      'First linked evidence for [[' + topicName + ']].',
      'Second linked evidence for [[' + topicName + ']].',
      'Third linked evidence for [[' + topicName + ']].',
      'Fourth linked evidence for [[' + topicName + ']].',
      unlinkedText,
    ],
    [topicName]
  );
  const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);

  try {
    await page.goto(fixture.baseUrl + '/second-brain', { waitUntil: 'domcontentloaded' });
    await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
    await expect(page.locator('.review-entry')).toHaveCount(0);
    await expect(page.locator('.rail-foot-action')).toHaveCount(0);

    await page.locator('.brain-areas').getByRole('button', { name: 'Notes', exact: true }).click();
    await page.locator('.index-list .note-row-item').first().waitFor({ timeout: 30_000 });
    await expect(page.locator('.review-entry')).toHaveCount(0);

    await page.locator('.brain-note-filter-row').getByRole('button', { name: 'Without topics' }).click();
    await page.locator('#brain-all-notes-search').fill(unlinkedText);
    const noteRow = page.locator('.index-list .note-row-item');
    await expect(noteRow).toHaveCount(1);
    await expect(page.locator('.review-entry')).toContainText('Review one by one');
    await expect(page.locator('.review-entry p')).toHaveText(
      'Step through these notes in order. Optional: nothing changes unless you link one.'
    );

    await noteRow.click();
    await expect(page.locator('.brain-browse-detail .note-text')).toContainText(unlinkedText);
    const sourceBadge = page.locator('.brain-browse-detail .source-badge');
    await expect(sourceBadge).toContainText('Open book');
    // A short book link must not paint a full-width hover hit area.
    const provenance = await sourceBadge.evaluate((element) => {
      const metadata = element.closest('.note-metadata');
      return {
        badgeWidth: element.getBoundingClientRect().width,
        metadataWidth: metadata?.getBoundingClientRect().width ?? 0,
        flexGrow: getComputedStyle(element).flexGrow,
      };
    });
    expect(provenance.flexGrow).toBe('0');
    expect(provenance.metadataWidth - provenance.badgeWidth).toBeGreaterThan(48);
    await expect(page.locator('.brain-browse-detail .brain-note-action-row button')).toHaveText([
      'Link to topic',
      'Keep with writing…',
      'Suggest topics',
      'More',
    ]);

    const more = page.locator('.brain-browse-detail .brain-note-more-trigger');
    await more.focus();
    await page.keyboard.press('Enter');
    await expect(more).toHaveAttribute('aria-expanded', 'true');
    const desktopTrigger = await more.boundingBox();
    const desktopMenu = await page.locator('.brain-browse-detail .brain-note-more-disclosure').boundingBox();
    expect(desktopTrigger).not.toBeNull();
    expect(desktopMenu).not.toBeNull();
    if (desktopTrigger && desktopMenu) {
      expect(Math.abs(desktopMenu.x + desktopMenu.width - desktopTrigger.x - desktopTrigger.width)).toBeLessThan(4);
      expect(desktopMenu.y - desktopTrigger.y - desktopTrigger.height).toBeGreaterThanOrEqual(0);
      expect(desktopMenu.y - desktopTrigger.y - desktopTrigger.height).toBeLessThan(12);
    }
    await page.keyboard.press('Tab');
    const editItem = page.locator('.brain-browse-detail [data-note-more-item]').first();
    await expect(editItem).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(page.locator('.brain-note-more-disclosure')).toHaveCount(0);
    await expect(more).toBeFocused();

    // The same popup stays inside a phone viewport after actions wrap.
    await page.setViewportSize({ width: 390, height: 844 });
    await more.click();
    const mobileMenu = await page.locator('.brain-browse-detail .brain-note-more-disclosure').boundingBox();
    expect(mobileMenu).not.toBeNull();
    if (mobileMenu) {
      expect(mobileMenu.x).toBeGreaterThanOrEqual(-1);
      expect(mobileMenu.x + mobileMenu.width).toBeLessThanOrEqual(391);
    }
    await more.click();
    await page.setViewportSize(DESKTOP_VIEWPORT);

    await page.locator('.review-entry').getByRole('button', { name: 'Review one by one' }).click();
    await expect(page.locator('[data-testid="brain-review-inspector"]')).toBeVisible();
    await expect(page.locator('.review-section-header .brain-section-title')).toHaveText('Reviewing one by one');
    await expect(page.locator('.review-section-header .review-position')).toHaveText('1 of 1');
    await expect(page.locator('.note-inspector-actions .brain-note-action-row button')).toHaveText([
      'Link to topic',
      'Keep with writing…',
      'Suggest topics',
      'More',
    ]);

    await page.getByRole('button', { name: 'Back to notes without topics' }).click();
    await expect(page.locator('.brain-browse-detail .note-text')).toContainText(unlinkedText);
    await expect(page.locator('#brain-all-notes-search')).toHaveValue(unlinkedText);
    await expect(page.locator('.brain-note-filter-row button[aria-pressed="true"]')).toHaveText('Without topics');
    await expect(page.locator('.index-list .note-row-item')).toHaveCount(1);

    await page.locator('.brain-areas').getByRole('button', { name: 'Topics', exact: true }).click();
    const topicRow = page.locator('.index-item').filter({ hasText: topicName }).first();
    await topicRow.waitFor({ timeout: 30_000 });
    await topicRow.click();
    const evidence = page.locator('[data-testid="topic-evidence"] .note-card');
    await expect(evidence).toHaveCount(4);
    const evidenceMore = evidence.first().locator('.brain-note-more-trigger');
    await expect(evidenceMore).toBeVisible();
    await expect(page.getByTestId('topic-select-sources')).toBeVisible();
    await expect(page.locator('.topic-meta [data-testid="topic-select-sources"]')).toHaveCount(0);
    await expect(page.locator('.detail-tools [data-testid="topic-select-sources"]')).toHaveCount(1);
    const evidenceActions = evidence.first().locator('.brain-note-action-row button');
    await expect(evidenceActions).toHaveText(['Keep with writing…', 'More']);
    await expect(evidenceActions.first().locator('nostos-icon')).toHaveAttribute('name', 'bookmark-simple');
    const keepBox = await evidenceActions.nth(0).boundingBox();
    const moreBox = await evidenceActions.nth(1).boundingBox();
    expect(keepBox).not.toBeNull();
    expect(moreBox).not.toBeNull();
    if (keepBox && moreBox) expect(Math.abs(keepBox.y - moreBox.y)).toBeLessThan(2);
    const sourceLink = evidence.first().locator('.source-badge');
    await expect(sourceLink).toContainText('Open book');
    await expect(sourceLink).toHaveAttribute('title', bookTitle);
    await evidenceMore.click();
    const moreItems = evidence.first().locator('[data-note-more-item]');
    await expect(moreItems).toHaveText(['Edit note', 'Delete note']);
    await expect(moreItems.last()).toHaveClass(/danger/);
    await moreItems.last().click();
    const confirm = page.getByRole('alertdialog');
    await expect(confirm).toBeVisible();
    await expect(confirm).toContainText('Delete Note');
    await confirm.getByRole('button', { name: 'Cancel' }).click();
    await expect(confirm).toHaveCount(0);
  } finally {
    await context.close();
    await cleanupBrain(fixture.baseUrl, seed);
  }
});
