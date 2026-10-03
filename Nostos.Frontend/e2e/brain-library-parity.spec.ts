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
    const libToggleGroup = await measure(page, '.control-group');
    // ACTIVE on both sides: the active option carries the outline/shadow that the
    // inactive one does not, so comparing active-to-inactive is a false failure.
    const libToggleOpt = await measure(page, '.vt-opt[aria-pressed="true"]');

    expect(
      libSearch && libSelect && libToggleGroup && libToggleOpt,
      'library controls present'
    ).toBeTruthy();

    // --- Brain ------------------------------------------------------------
    await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
    await page.locator('.search-box input').waitFor({ timeout: 30_000 });
    await page.waitForTimeout(400);

    // appInput now owns the Brain field itself; the wrapper only positions the
    // leading glyph and clear action, so parity must measure the native control.
    const brainSearch = await measure(page, '.search-box input');
    const brainSelect = await measure(page, '.sort-select');
    const brainToggleGroup = await measure(page, '.view-mode-control');
    const brainToggleOpt = await measure(page, '.view-mode-control .vt-opt[aria-pressed="true"]');

    expect(
      brainSearch && brainSelect && brainToggleGroup && brainToggleOpt,
      'brain controls present'
    ).toBeTruthy();

    console.log(
      'SEARCH  library:', JSON.stringify(libSearch), '\n        brain  :', JSON.stringify(brainSearch)
    );
    console.log(
      'SELECT  library:', JSON.stringify(libSelect), '\n        brain  :', JSON.stringify(brainSelect)
    );
    console.log(
      'TOGGLE-GROUP library:', JSON.stringify(libToggleGroup), '\n             brain  :', JSON.stringify(brainToggleGroup)
    );
    console.log(
      'TOGGLE-OPT   library:', JSON.stringify(libToggleOpt), '\n             brain  :', JSON.stringify(brainToggleOpt)
    );

    // Radii are the most visible divergence and the one explicitly called out.
    expect(brainSearch!.borderRadius, 'search radius').toBe(libSearch!.borderRadius);
    expect(brainSelect!.borderRadius, 'select radius').toBe(libSelect!.borderRadius);
    expect(brainToggleGroup!.borderRadius, 'toggle container radius').toBe(libToggleGroup!.borderRadius);
    expect(brainToggleOpt!.borderRadius, 'toggle option radius').toBe(libToggleOpt!.borderRadius);

    // ── The view toggle must agree in BOTH themes ──────────────────────────
    // Both surfaces now render the SAME component (`nostos-view-toggle`), so this
    // parity is structural rather than a coincidence of two hand-kept stylesheets.
    // The assertion is kept because the failure it was written for was invisible
    // to geometry: back when the two were separate CSS copies, only one of them
    // lost a specificity tie on dark and kept its light tokens, so the two toggles
    // measured identical and looked different. Comparing computed COLOUR per theme
    // is what catches that class of defect; comparing geometry does not.
    //
    // One glyph box for every option on both surfaces, and it is asserted, not
    // assumed: the Library's grid glyph used to run at 20px (a per-option optical
    // size, `ViewToggleOption.size`) to chase the Brain's `map-trifold` ink extent,
    // which made it the heaviest glyph in either control. The per-option size is
    // gone; at the shared 18px the grid glyph's ink already matches the map's, so
    // `glyph` below compares the boxes directly.
    // ThemeService is the only writer of `data-theme` and re-applies it from
    // localStorage on every boot, so the theme must be set AFTER each navigation
    // (setting it before would be wiped by the next page load). The stored value
    // is set first so the inline anti-flash script agrees.
    const setTheme = async (theme: 'light' | 'dark') => {
      await page.evaluate((t) => {
        localStorage.setItem('nostos.theme', t);
        if (t === 'dark') document.documentElement.setAttribute('data-theme', 'dark');
        else document.documentElement.removeAttribute('data-theme');
      }, theme);
      await page.waitForTimeout(300);
    };

    const coloursIn = async (theme: 'light' | 'dark') => {
      const readToggle = async (groupSel: string) =>
        page.evaluate((sel) => {
          const g = document.querySelector(sel);
          const opts = g ? Array.from(g.querySelectorAll('.vt-opt')) : [];
          const rd = (el: Element | undefined) => {
            if (!el) return null;
            const c = getComputedStyle(el);
            const svg = el.querySelector('svg');
            return {
              bg: c.backgroundColor,
              color: c.color,
              shadow: c.boxShadow,
              outline: c.outline,
              icon: svg ? getComputedStyle(svg).stroke : null,
              // The glyph BOX (`width`/`height` attributes the icon component
              // writes), so a per-option size cannot creep back in on one surface.
              glyph: svg ? `${svg.getAttribute('width')}x${svg.getAttribute('height')}` : null,
            };
          };
          // Selection is `aria-pressed` (the component's contract); `.active` was the
          // old recipe's styling hook and no longer exists.
          const thumb = getComputedStyle(g!, '::before');
          return {
            active: rd(opts.find((o) => o.getAttribute('aria-pressed') === 'true')),
            inactive: rd(opts.find((o) => o.getAttribute('aria-pressed') === 'false')),
            // The selected tile's own paint lives on the track's ::before, not on the
            // option: without measuring it this spec would compare two transparent
            // options and keep passing while the tiles diverged.
            thumb: {
              bg: thumb.backgroundColor,
              shadow: thumb.boxShadow,
              outline: thumb.outline,
            },
          };
        }, groupSel);

      // Library first, then Brain — same theme state on both.
      await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
      await page.locator('.control-group').waitFor({ timeout: 30_000 });
      await setTheme(theme);
      const lib = await readToggle('.control-group');

      await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
      await page.locator('.view-mode-control').waitFor({ timeout: 30_000 });
      await setTheme(theme);
      const brain = await readToggle('.view-mode-control');

      // Prove the theme actually applied, so a no-op cannot pass as a match.
      const applied = await page.evaluate(() =>
        document.documentElement.getAttribute('data-theme') ?? 'light'
      );
      expect(applied, `${theme}: theme actually applied`).toBe(theme);
      return { lib, brain };
    };

    for (const theme of ['light', 'dark'] as const) {
      const { lib, brain } = await coloursIn(theme);
      console.log(`TOGGLE ${theme}  library:`, JSON.stringify(lib), '\n              brain  :', JSON.stringify(brain));
      expect(brain.active, `${theme}: toggle active present`).toBeTruthy();
      expect(brain.inactive, `${theme}: toggle inactive present`).toBeTruthy();
      // Track and both segments must be identical — colour, shadow and outline.
      expect(brain.active!.bg, `${theme}: active segment background`).toBe(lib.active!.bg);
      expect(brain.active!.color, `${theme}: active segment text`).toBe(lib.active!.color);
      expect(brain.active!.shadow, `${theme}: active segment shadow`).toBe(lib.active!.shadow);
      expect(brain.active!.outline, `${theme}: active segment outline`).toBe(lib.active!.outline);
      expect(brain.active!.icon, `${theme}: active icon stroke`).toBe(lib.active!.icon);
      expect(brain.inactive!.color, `${theme}: inactive segment text`).toBe(lib.inactive!.color);
      expect(brain.inactive!.icon, `${theme}: inactive icon stroke`).toBe(lib.inactive!.icon);
      // Same glyph box on both surfaces, in both states — the assertion the old
      // 20px grid-glyph asymmetry used to be documented as an exception to.
      expect(brain.active!.glyph, `${theme}: active glyph box`).toBe(lib.active!.glyph);
      expect(brain.inactive!.glyph, `${theme}: inactive glyph box`).toBe(lib.inactive!.glyph);
      // The tile itself (position is deliberately NOT compared: the two surfaces
      // default to different options, so the thumb sits at index 0 on one and 1 on
      // the other).
      expect(brain.thumb.bg, `${theme}: selected tile background`).toBe(lib.thumb.bg);
      expect(brain.thumb.shadow, `${theme}: selected tile shadow`).toBe(lib.thumb.shadow);
      expect(brain.thumb.outline, `${theme}: selected tile hairline`).toBe(lib.thumb.outline);
    }
    // Reset the stored theme: the specs share one browser context, and leaving
    // 'dark' in localStorage would re-theme every later spec.
    await setTheme('light');

    // The search and select must be the same height as the Library's, so the two
    // toolbars read at the same rhythm.
    expect(Math.abs(brainSearch!.height - libSearch!.height), 'search height').toBeLessThanOrEqual(2);
    expect(Math.abs(brainSelect!.height - libSelect!.height), 'select height').toBeLessThanOrEqual(2);
    expect(Math.abs(brainToggleGroup!.height - libToggleGroup!.height), 'toggle group height').toBeLessThanOrEqual(2);
    expect(Math.abs(brainToggleOpt!.height - libToggleOpt!.height), 'toggle option height').toBeLessThanOrEqual(2);
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

    await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
    await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
    await page.waitForTimeout(300);
    const brainRow = await measure(page, '.index-item');
    const brainBadge = await measure(page, '.index-item .count');
    await page.locator('.index-row-shell').first().hover();
    await page.waitForTimeout(250);
    const brainAction = await measure(page, '.index-row-shell .row-action');
    const brainActionBox = await measure(page, '.index-row-shell .row-actions');

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

    // The count badge is a deliberate 20px pill, not plain text.
    expect(brainBadge!.height, 'badge height').toBe(20);
    expect(brainBadge!.borderRadius, 'badge radius').toBe('999px');
    expect(brainBadge!.fontSize, 'badge font size').toBe(libBadgeFontSize ?? '11.52px');

    // The Library only renders a badge for a NON-ZERO count (a brand-new
    // collection has none), so compare against its live badge when it has one
    // rather than assuming it always renders.
    if (libBadge) {
      expect(brainBadge!.height, 'badge height vs library').toBe(libBadge.height);
      expect(brainBadge!.borderRadius, 'badge radius vs library').toBe(libBadge.borderRadius);
    }

    expect(libAction && brainAction && libActionBox && brainActionBox, 'row actions present').toBeTruthy();

    console.log('ACTION library:', JSON.stringify(libAction), '\n       brain  :', JSON.stringify(brainAction));
    console.log('ABOX   library:', JSON.stringify(libActionBox), '\n       brain  :', JSON.stringify(brainActionBox));

    expect(
      brainAction!.height,
      `action height: brain ${brainAction!.height} vs library ${libAction!.height}`
    ).toBe(libAction!.height);
    expect(brainAction!.borderRadius, 'action radius').toBe(libAction!.borderRadius);
    expect(brainActionBox!.height, 'action overlay height').toBe(libActionBox!.height);

    // Behaviour, not just geometry: the actions must be hidden at rest and
    // revealed on hover, exactly as the tree's are. A 22px button permanently on
    // screen would satisfy the box checks above while looking nothing like the
    // Library — which is the state this replaced.
    await page.mouse.move(0, 0);
    await page.waitForTimeout(300);
    const restingReveal = await page.evaluate(() => {
      const box = document.querySelector('.index-row-shell .row-actions');
      const badge = document.querySelector('.index-row-shell .count');
      return {
        actions: box ? getComputedStyle(box).visibility : null,
        badge: badge ? getComputedStyle(badge).opacity : null,
      };
    });
    expect(restingReveal.actions, 'actions hidden at rest').toBe('hidden');
    expect(Number(restingReveal.badge), 'badge visible at rest').toBe(1);

    await page.locator('.index-row-shell').first().hover();
    await page.waitForTimeout(300);
    const hoverReveal = await page.evaluate(() => {
      const box = document.querySelector('.index-row-shell .row-actions');
      const badge = document.querySelector('.index-row-shell .count');
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
