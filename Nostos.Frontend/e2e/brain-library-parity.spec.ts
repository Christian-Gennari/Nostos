/**
 * Brain ↔ Library parity gate.
 *
 * The Brain page is not a separate design; it is the same app. This spec measures
 * the Brain's controls against the Library's, which is the source of truth, and
 * fails when they drift. It exists because several Brain controls were built as
 * bespoke versions of controls the Library already had (different radii, a
 * bordered wrapper around a bordered select, a bespoke equal-weight button row),
 * and "does it look like the same app" only holds if it is enforced.
 *
 * See docs/brain-library-parity.md for the extracted values.
 */
import { expect, test } from '@playwright/test';
import { apiPost, loadFixture } from './support/fixture';
import { apiDelete, cleanupBrain, seedBrain, type BrainSeed } from './support/brain-fixture';
import { DESKTOP_VIEWPORT, newCapturePage } from './support/visual-capture';

/** Computed geometry for one control, in CSS pixels. */
interface Box {
  height: number;
  width: number;
  padding: string;
  borderRadius: string;
  borderWidth: string;
  outlineWidth: string;
  position: string;
  opacity: string;
  visibility: string;
  fontSize: string;
}

async function measure(
  page: import('@playwright/test').Page,
  selector: string
): Promise<Box | null> {
  return page.evaluate((sel) => {
    const el = document.querySelector(sel);
    if (!el) return null;
    const c = getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return {
      height: Math.round(rect.height * 10) / 10,
      width: Math.round(rect.width * 10) / 10,
      padding: c.padding,
      borderRadius: c.borderRadius,
      borderWidth: c.borderTopWidth,
      outlineWidth: c.outlineWidth,
      position: c.position,
      opacity: c.opacity,
      visibility: c.visibility,
      fontSize: c.fontSize,
    };
  }, selector);
}

test('brain controls match the library controls', async ({ browser }) => {
  const fixture = loadFixture();

  const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
  try {
    // --- Library: the source of truth -------------------------------------
    await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
    await page.locator('header.toolbar').waitFor({ timeout: 30_000 });
    await page.waitForTimeout(400);

    const libSearch = await measure(page, '.search-input');
    const libSelect = await measure(page, '.sort-select');
    expect(libSearch && libSelect, 'library controls present').toBeTruthy();

    // --- Brain ------------------------------------------------------------
    await page.goto(`${fixture.baseUrl}/brain`, { waitUntil: 'domcontentloaded' });
    await page.locator('.toolbar .search-input').waitFor({ timeout: 30_000 });
    await page.waitForTimeout(400);

    const brainSearch = await measure(page, '.toolbar .search-input');
    const brainSelect = await measure(page, '.toolbar .order-select');

    expect(brainSearch && brainSelect, 'brain controls present').toBeTruthy();
    console.log('SEARCH  library:', JSON.stringify(libSearch), '\n        brain  :', JSON.stringify(brainSearch));
    console.log('SELECT  library:', JSON.stringify(libSelect), '\n        brain  :', JSON.stringify(brainSelect));

    // The Brain's toolbar is the Library's: the same field and dropdown, the
    // same radii and heights. (It has no view toggle: the map is a place.)
    expect(brainSearch!.borderRadius, 'search radius').toBe(libSearch!.borderRadius);
    expect(brainSelect!.borderRadius, 'select radius').toBe(libSelect!.borderRadius);
    expect(Math.abs(brainSearch!.height - libSearch!.height), 'search height').toBeLessThanOrEqual(2);
    expect(Math.abs(brainSelect!.height - libSelect!.height), 'select height').toBeLessThanOrEqual(2);
  } finally {
    await context.close();
  }
});

let seed: BrainSeed | null = null;
let createdCollectionId: string | null = null;

test('brain sidebar row matches the library sidebar row', async ({ browser }) => {
  const fixture = loadFixture();
  // The Brain index only renders rows when topics exist, and the shared fixture
  // starts empty — so seed (and clean up) rather than assuming data.
  seed = await seedBrain(
    fixture.baseUrl,
    `Parity ${Date.now().toString(36)}`,
    [
      'A note about [[Attention]] and [[Memory]].',
      'Another on [[Attention]] and [[Practice]].',
    ],
    ['Attention', 'Memory', 'Practice']
  );

  // The shared fixture ships no collections at all, so `.tree-row` never
  // renders — but that is exactly the row style this test must compare against
  // (it is the one carrying a count plus hover rename/delete). Create one and
  // remove it in the finally block.
  const created = await apiPost<{ id: string }>(fixture.baseUrl, '/api/collections', {
    name: `Parity Collection ${Date.now().toString(36)}`,
    parentId: null,
  });
  createdCollectionId = created.id;

  const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
  try {
    await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
    // `.tree-row` (a COLLECTION), not `.nav-item` (a status filter). The Library
    // sidebar has two row styles; the topic index is the collections analogue
    // because that is the row that carries a count plus hover rename/delete.
    await page.locator('.tree-row').first().waitFor({ timeout: 30_000 });
    await page.waitForTimeout(300);
    const libRow = await measure(page, '.tree-row');
    const libBadge = await measure(page, '.tree-row .count-badge');
    // The row actions are the pair the user reported as not matching. Measured
    // BEFORE the hover deliberately: the buttons and their overlay keep their
    // geometry whether or not they are visible (they are opacity/visibility
    // toggled, not display), so this compares the box, and the visibility
    // assertions below compare the reveal behaviour.
    const libAction = await measure(page, '.tree-row .action-mini');
    const libActionBox = await measure(page, '.tree-row .node-actions');
    await page.locator('.tree-row').first().hover();
    await page.waitForTimeout(250);
    const libBadgeFontSize = await page.evaluate(() => {
      const el = document.querySelector('.tree-row .count-badge') || document.querySelector('.count-badge');
      return el ? getComputedStyle(el).fontSize : null;
    });

    await page.goto(`${fixture.baseUrl}/brain`, { waitUntil: 'domcontentloaded' });
    await page.locator('.topic-row').first().waitFor({ timeout: 30_000 });
    await page.waitForTimeout(300);
    const brainRow = await measure(page, '.topic-row');
    const brainBadge = await measure(page, '.topic-row .count');
    await page.locator('.topic-row-shell').first().hover();
    await page.waitForTimeout(250);
    const brainAction = await measure(page, '.topic-row-shell .row-action');
    const brainActionBox = await measure(page, '.topic-row-shell .row-actions');

    expect(libRow && brainRow && brainBadge, 'sidebar rows present').toBeTruthy();

    console.log('ROW   library:', JSON.stringify(libRow), '\n      brain  :', JSON.stringify(brainRow));
    console.log('BADGE library:', JSON.stringify(libBadge), '\n      brain  :', JSON.stringify(brainBadge));

    // Row geometry: the selected pill, radii and rhythm must be identical. A
    // 46px Brain row next to a 34px Library row was the visible mismatch.
    expect(brainRow!.borderRadius, 'row radius').toBe(libRow!.borderRadius);
    expect(brainRow!.padding, 'row padding').toBe(libRow!.padding);
    expect(
      Math.abs(brainRow!.height - libRow!.height),
      `row height: brain ${brainRow!.height} vs library ${libRow!.height}`
    ).toBeLessThanOrEqual(2);

    // The count is a plain number on the row, not a chip, in both rails: no
    // ground behind it, the same 11.52px figure, and the same 20px line box so
    // the slot it shares with the row actions keeps its geometry.
    const brainCountGround = await page.evaluate(() => {
      const el = document.querySelector('.topic-row .count');
      return el ? getComputedStyle(el).backgroundColor : null;
    });
    expect(brainCountGround, 'count has no chip ground').toBe('rgba(0, 0, 0, 0)');
    expect(brainBadge!.height, 'count line box').toBe(20);
    expect(brainBadge!.fontSize, 'count font size').toBe(libBadgeFontSize ?? '11.52px');

    // The Library only renders a count for a NON-ZERO collection (a brand-new
    // collection has none), so compare against its live one when it has one
    // rather than assuming it always renders.
    if (libBadge) {
      expect(brainBadge!.height, 'count height vs library').toBe(libBadge.height);
      expect(brainBadge!.fontSize, 'count size vs library').toBe(libBadge.fontSize);
    }

    expect(libAction && brainAction && libActionBox && brainActionBox, 'row actions present').toBeTruthy();

    console.log('ACTION library:', JSON.stringify(libAction), '\n       brain  :', JSON.stringify(brainAction));
    console.log('ABOX   library:', JSON.stringify(libActionBox), '\n       brain  :', JSON.stringify(brainActionBox));

    // Slice A raises Brain's topic actions from the Library's 22px utility
    // buttons to a minimum 28px desktop hit target; radius stays shared.
    expect(brainAction!.height, 'desktop action hit target').toBeGreaterThanOrEqual(28);
    expect(brainAction!.width, 'desktop action hit target width').toBeGreaterThanOrEqual(28);
    expect(brainAction!.borderRadius, 'action radius').toBe(libAction!.borderRadius);
    expect(brainActionBox!.height, 'action overlay hit target').toBeGreaterThanOrEqual(28);

    // Behaviour, not just geometry: the actions must be hidden at rest and
    // revealed on hover, exactly as the tree's are. A 28px button permanently on
    // screen would satisfy the box checks above while looking nothing like the
    // Library — which is the state this replaced.
    await page.mouse.move(0, 0);
    await page.waitForTimeout(300);
    const restingReveal = await page.evaluate(() => {
      const box = document.querySelector('.topic-row-shell .row-actions');
      const badge = document.querySelector('.topic-row-shell .count');
      return {
        actions: box ? getComputedStyle(box).visibility : null,
        badge: badge ? getComputedStyle(badge).opacity : null,
      };
    });
    expect(restingReveal.actions, 'actions hidden at rest').toBe('hidden');
    expect(Number(restingReveal.badge), 'badge visible at rest').toBe(1);

    await page.locator('.topic-row-shell').first().hover();
    await page.waitForTimeout(300);
    const hoverReveal = await page.evaluate(() => {
      const box = document.querySelector('.topic-row-shell .row-actions');
      const badge = document.querySelector('.topic-row-shell .count');
      return {
        actions: box ? getComputedStyle(box).visibility : null,
        badge: badge ? getComputedStyle(badge).opacity : null,
      };
    });
    expect(hoverReveal.actions, 'actions revealed on hover').toBe('visible');
    expect(Number(hoverReveal.badge), 'badge yields on hover').toBe(0);
  } finally {
    await context.close();
    if (seed) await cleanupBrain(fixture.baseUrl, seed);
    if (createdCollectionId) await apiDelete(fixture.baseUrl, `/api/collections/${createdCollectionId}`);
  }
});
