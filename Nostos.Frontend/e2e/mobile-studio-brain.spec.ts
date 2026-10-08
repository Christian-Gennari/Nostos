/**
 * Mobile polish geometry — Studio and Second Brain at phone width.
 *
 * Geometry-only assertions (no pixel snapshots), matching the approach in
 * `mobile-dock.spec.ts`: stable across styling work, loud when layout regresses.
 *
 * Each guard maps to a defect measured at 390x844 on the real build:
 *
 *  - The editor's formatting toolbar wrapped to FOUR rows (124px of the writing
 *    surface) because the Oxide skin's `flex-wrap: wrap` beat TinyMCE's
 *    `toolbar_mode: 'sliding'`, which only engages while the toolbar is a
 *    single row. The sliding overflow button therefore never rendered and every
 *    control stayed in the document.
 *  - The Studio's document tree is the only way to open a document, but its
 *    chevrons were 12x20px and its row actions 22x22px.
 *  - Brain's "Back to Index" is the only exit from the topic detail sheet
 *    (the index column is display:none while a topic is open) yet it was a
 *    static block at the top of the scrolling column, so it left the viewport
 *    permanently once the sheet was scrolled.
 *
 * The spec runs in the mobile-chromium project (playwright.config.ts matches
 * `mobile.*.spec.ts`).
 */
import { expect, test, type Page } from '@playwright/test';

import { apiPost, loadFixture } from './support/fixture';
import { apiPut } from './support/visual-capture';
import { cleanupBrain, seedBrain, type BrainSeed } from './support/brain-fixture';

const MIN_TAP_TARGET = 44; // CSS px, Apple HIG / Material minimum

let fixture: ReturnType<typeof loadFixture>;

/**
 * Topics seeded by this spec, cleaned up in `afterAll`.
 *
 * Every Playwright spec shares ONE fixture instance, and
 * `visual-regression.spec.ts`'s `brain-empty-desktop` asserts the *pristine*
 * empty state. A spec that leaves topics behind therefore poisons it for
 * every later spec — which is exactly what happened before `seedBrain`/
 * `cleanupBrain` were used here. See `support/brain-fixture.ts`.
 */
const seeds: BrainSeed[] = [];

test.beforeAll(() => {
  fixture = loadFixture();
});

test.afterAll(async () => {
  for (const seed of seeds) {
    await cleanupBrain(fixture.baseUrl, seed);
  }
});

/** Seed one document through the supported REST surface (as the visual matrix does). */
async function seedDocument(title: string): Promise<void> {
  const created = await apiPost<{ id: string }>(fixture.baseUrl, '/api/writings', {
    name: title,
    type: 'Document',
    parentId: null,
  });
  await apiPut(fixture.baseUrl, `/api/writings/${created.id}`, {
    name: title,
    content:
      '# Mobile Polish\n\nA paragraph of representative prose so the editor has a ' +
      'visible writing surface after the chrome is measured.',
  });
}

/** Seed a folder that has children. Returns the folder id (the row with a chevron). */
async function seedNestedFolder(): Promise<string> {
  const stamp = Date.now();
  const parent = await apiPost<{ id: string }>(fixture.baseUrl, '/api/writings', {
    name: `MobilePolishFolder-${stamp}`,
    type: 'Folder',
    parentId: null,
  });
  await apiPost(fixture.baseUrl, '/api/writings', {
    name: `MobilePolishChild-${stamp}`,
    type: 'Document',
    parentId: parent.id,
  });
  return parent.id;
}

/** Open the Studio at phone width, reveal the drawer, open the seeded document. */
async function openStudioDocument(page: Page, title: string): Promise<void> {
  await page.goto(`${fixture.baseUrl}/studio`, { waitUntil: 'domcontentloaded' });
  // Compact Studio drawers are closed at rest; open Files from the persistent editor header.
  const filesToggle = page.locator('.files-toggle');
  if ((await filesToggle.getAttribute('aria-expanded')) !== 'true') {
    await filesToggle.click();
  }
  await expect(filesToggle).toHaveAttribute('aria-expanded', 'true');
  const row = page.locator('.file-list .tree-row', { hasText: title }).first();
  await row.waitFor({ timeout: 30_000 });
  await row.click();
  await page.locator('.tox-tinymce').waitFor({ timeout: 45_000 });
  await page.waitForTimeout(600); // let TinyMCE finish laying the toolbar out
}

test('the editor formatting toolbar occupies a single row on a phone', async ({ page }) => {
  const title = `MobilePolishRow-${Date.now()}`;
  await seedDocument(title);
  await openStudioDocument(page, title);

  const tops = await page
    .locator('.tox-toolbar__primary .tox-toolbar__group')
    .evaluateAll((els) =>
      els
        .filter((el) => el.getBoundingClientRect().width > 0)
        .map((el) => Math.round(el.getBoundingClientRect().top)),
    );
  expect(tops.length, 'the toolbar must render at least one group').toBeGreaterThan(0);

  const rows = new Set(tops);
  expect(
    rows.size,
    `the formatting toolbar must be a single row, found ${rows.size} ` +
      `(the Oxide skin's flex-wrap defeated toolbar_mode: sliding)`,
  ).toBe(1);

  const height = await page
    .locator('.tox-toolbar__primary')
    .evaluate((el) => Math.round(el.getBoundingClientRect().height));
  expect(height, 'a single toolbar row must not exceed ~60px').toBeLessThanOrEqual(60);
});

test('every editor toolbar button meets the 44px touch contract', async ({ page }) => {
  const title = `MobilePolishTargets-${Date.now()}`;
  await seedDocument(title);
  await openStudioDocument(page, title);

  // Both rows: __primary is what is visible at rest, __overflow is where the
  // sliding mode parks the remaining controls. Most formatting tools live in
  // the overflow on a phone, so it must meet the same contract.
  const slivers = await page
    .locator('.tox-toolbar__primary .tox-tbtn, .tox-toolbar__overflow .tox-tbtn')
    .evaluateAll((els) =>
      els
        .filter((el) => el.getBoundingClientRect().width > 0)
        .map((el) => ({
          label: el.getAttribute('aria-label') ?? '?',
          width: Math.round(el.getBoundingClientRect().width),
          height: Math.round(el.getBoundingClientRect().height),
        }))
        .filter((b) => b.width < 44 || b.height < 44),
    );

  expect(slivers, `toolbar buttons under 44px: ${JSON.stringify(slivers)}`).toEqual([]);
});

test('the sliding overflow control reveals the collapsed formatting buttons', async ({ page }) => {
  const title = `MobilePolishOverflow-${Date.now()}`;
  await seedDocument(title);
  await openStudioDocument(page, title);

  const overflow = page.locator('.tox-toolbar__primary .tox-tbtn[aria-label*="additional"]');
  await expect(
    overflow,
    'a single toolbar row must give TinyMCE an overflow chevron to slide into',
  ).toBeVisible();
  await overflow.click();
  await page.waitForTimeout(400);

  // The collapsed controls must actually be reachable, not merely listed.
  const revealed = page.locator('.tox-toolbar__overflow--open .tox-tbtn');
  await expect(revealed.first(), 'the overflow row must open').toBeVisible();
  const count = await revealed.count();
  expect(count, 'the overflow must expose the collapsed formatting controls').toBeGreaterThan(0);

  const overflowHeight = await page
    .locator('.tox-toolbar__overflow--open')
    .evaluate((el) => Math.round(el.getBoundingClientRect().height));
  expect(overflowHeight, 'the overflow row must not be a zero-height stub').toBeGreaterThan(20);
});

test('the studio document tree exposes 44px expand chevrons', async ({ page }) => {
  // A chevron only exists on a row that HAS children, so seed a nested folder:
  // the folder row gets the expand control.
  const parentId = await seedNestedFolder();
  await page.goto(`${fixture.baseUrl}/studio`, { waitUntil: 'domcontentloaded' });
  const openSidebar = page.getByRole('button', { name: 'Open Sidebar' });
  if (await openSidebar.isVisible().catch(() => false)) {
    await openSidebar.click();
  }

  const chevron = page.locator(`.file-list .tree-row[data-node-id="${parentId}"] .toggle-btn`);
  await expect(chevron).toBeVisible({ timeout: 30_000 });

  const box = await chevron.boundingBox();
  expect(box, 'the chevron must be measurable').toBeTruthy();
  expect(Math.round(box!.width), 'chevron tap width').toBeGreaterThanOrEqual(MIN_TAP_TARGET);
  expect(Math.round(box!.height), 'chevron tap height').toBeGreaterThanOrEqual(MIN_TAP_TARGET);

  // The rows themselves stay compact: the controls are out-of-flow overlays, so
  // enlarging them must not grow the list.
  const rowHeight = await page
    .locator(`.file-list .tree-row[data-node-id="${parentId}"]`)
    .evaluate((el) => Math.round(el.getBoundingClientRect().height));
  expect(rowHeight, 'rows must stay compact despite bigger tap targets').toBeLessThanOrEqual(44);
});

test('the Brain back control stays reachable after scrolling the detail sheet', async ({ page }) => {
  // Notes are created through a book (the app has no standalone POST /api/notes),
  // and a note's [[Topic]] reference is what materialises the topic. Seeded via
  // the shared helper so `afterAll` can restore the fixture exactly.
  const topic = `MobilePolish${Date.now()}`;
  const seed = await seedBrain(
    fixture.baseUrl,
    `Mobile Polish Brain ${topic}`,
    [
      `A note referencing [[${topic}]] with enough surrounding text to push the ` +
        'detail sheet well past the fold. '.repeat(30),
    ],
    [topic],
  );
  seeds.push(seed);

  await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
  const item = page.locator('.index-item', { hasText: topic }).first();
  await item.waitFor({ timeout: 30_000 });
  await item.click();

  const back = page.locator('.mobile-nav-header');
  await expect(back).toBeVisible();

  // The band must be full-bleed and its content must stay on the sheet's line.
  // It is a sticky box inside a column that pads 24px: without the negative
  // margins the white surface stopped 24px short of each edge and showed cream
  // page ground beside the masked strip.
  //
  // The content line is derived from the COLUMN's own padding (and cross-checked
  // against the note card), not from a sibling element that may not exist for a
  // topic with no rendered header row.
  const band = await page.evaluate(() => {
    const nav = document.querySelector('.mobile-nav-header') as HTMLElement | null;
    const col = document.querySelector('.content-col') as HTMLElement | null;
    if (!nav || !col) return null;
    const n = nav.getBoundingClientRect();
    const glyph = nav.querySelector('svg') as HTMLElement | null;
    const card = document.querySelector('app-note-card') as HTMLElement | null;
    return {
      leftGap: Math.round(n.left),
      // A scrollbar occupies the column's right edge when the sheet overflows;
      // allow it, but never the 24px content padding this bug produced.
      rightGap: Math.round(window.innerWidth - n.right),
      glyphLeft: glyph ? Math.round(glyph.getBoundingClientRect().left) : null,
      columnContentLeft: Math.round(
        col.getBoundingClientRect().left + parseFloat(getComputedStyle(col).paddingLeft),
      ),
      cardLeft: card ? Math.round(card.getBoundingClientRect().left) : null,
    };
  });
  expect(band, 'the back band and the detail column must both be rendered').not.toBeNull();

  // Regression: it used to start at x=24 with cream page ground to its left.
  expect(band!.leftGap, 'the band must reach the left viewport edge').toBeLessThanOrEqual(1);
  expect(
    band!.rightGap,
    'the band must reach the right edge (allowing for a scrollbar)',
  ).toBeLessThanOrEqual(16);
  // Full-bleed must not drag the content with it: the chevron stays on the same
  // 24px line the column establishes for its content.
  expect(band!.glyphLeft, 'the chevron must stay on the content line').toBe(band!.columnContentLeft);
  if (band!.cardLeft !== null) {
    expect(band!.cardLeft, 'the note card shares the same content line').toBe(
      band!.columnContentLeft,
    );
  }

  // Scroll far enough that a static control would be gone for good.
  await page.locator('.content-col').evaluate((el) => {
    el.scrollTop = 600;
  });
  await page.waitForTimeout(300);

  const box = await back.boundingBox();
  expect(box, 'the back control must still be rendered').toBeTruthy();
  expect(box!.y, 'the back control must stay pinned in the viewport').toBeGreaterThanOrEqual(0);
  expect(box!.y, 'the back control must remain in view').toBeLessThan(page.viewportSize()!.height);

  // Visible is not enough — it must still work.
  await back.click();
  await expect(page.locator('.index-item', { hasText: topic }).first()).toBeVisible();
});

/**
 * The view-mode toggle is deliberately NOT part of this check any more. It used
 * to assert 44px here, but the toggle now matches the Library's mobile metrics
 * (34x32 in a 38px track) so the same control does not change size between the
 * two surfaces — see second-brain.component.css. Parity won over the larger
 * target; the search field below keeps the 44px contract.
 */
test('the Brain index exposes a 44px search target', async ({ page }) => {
  await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
  await page.locator('.brain-header').waitFor({ timeout: 30_000 });

  const undersized = await page
    .locator('.brain-header .search-box input')
    .evaluateAll((els) =>
      els
        .filter((el) => el.getBoundingClientRect().width > 0)
        .map((el) => {
          const r = el.getBoundingClientRect();
          return {
            what: (el.className || '').toString().slice(0, 30),
            width: Math.round(r.width),
            height: Math.round(r.height),
          };
        })
        .filter((b) => b.width < 44 || b.height < 44),
    );

  expect(
    undersized,
    `Brain index controls under ${MIN_TAP_TARGET}px: ${JSON.stringify(undersized)}`,
  ).toEqual([]);
});

test('Notes uses the full phone toolbar while Map keeps its return control', async ({ page }) => {
  await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
  await page.locator('.brain-header').waitFor({ timeout: 30_000 });

  await page.getByRole('button', { name: 'Notes', exact: true }).click();
  const noteSearch = page.locator('#brain-all-notes-search');
  await noteSearch.waitFor();

  const portrait = await page.evaluate(() => {
    const search = document.querySelector('#brain-all-notes-search') as HTMLElement | null;
    return {
      viewControlPresent: !!document.querySelector('.view-mode-control'),
      search: search
        ? {
            width: Math.round(search.getBoundingClientRect().width),
            height: Math.round(search.getBoundingClientRect().height),
          }
        : null,
      overflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    };
  });

  expect(portrait.viewControlPresent).toBe(false);
  expect(portrait.search, 'Notes search must remain rendered on a phone').not.toBeNull();
  expect(portrait.search!.width, 'Notes search should use the available tools-row width').toBeGreaterThan(250);
  expect(portrait.search!.height).toBeGreaterThanOrEqual(MIN_TAP_TARGET);
  expect(portrait.overflow, 'Notes must not create horizontal page overflow').toBeLessThanOrEqual(1);

  // The Brain has a separate short-viewport treatment for landscape phones.
  // Notes still has no meaningless list/map control there.
  await page.setViewportSize({ width: 844, height: 390 });
  await expect(page.locator('.view-mode-control')).toHaveCount(0);

  await page.getByRole('button', { name: 'Topics', exact: true }).click();
  const mapButton = page.getByRole('button', { name: 'Map view', exact: true });
  await expect(mapButton).toBeVisible();
  await mapButton.click();

  // Map's compact phone header intentionally hides Notes/Topics, but the
  // list/map control is the way back and must remain visible and operable.
  const topicView = page.getByRole('button', { name: 'Topic view', exact: true });
  await expect(topicView).toBeVisible();
  await expect(page.locator('.brain-areas')).toBeHidden();

  await topicView.click();
  await expect(page.locator('.index-col')).toBeVisible();

  const landscapeOverflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(landscapeOverflow, 'short-landscape Brain must not overflow horizontally').toBeLessThanOrEqual(1);
});

/**
 * Mobile parity for the view-mode toggle.
 *
 * The desktop parity gate (`brain-library-parity.spec.ts`) runs only in the
 * desktop project, so nothing caught the Brain's toggle diverging on a phone:
 * Library rendered a 34x32 option in a 38px track while Brain rendered 44x44 in a
 * 50px one, and the two read as different controls.
 *
 * This measures BOTH surfaces at the same phone width inside one test, because
 * each is only meaningful against the other. Sizes are asserted exactly rather
 * than within a tolerance — the point is that they are the same control.
 */
test('the mobile view-mode toggle matches the library toggle', async ({ page }) => {
  const box = async (groupSel: string) =>
    page.evaluate((sel) => {
      const g = document.querySelector(sel);
      const opt = g ? g.querySelector('.vt-opt') : null;
      if (!g || !opt) return null;
      const gr = g.getBoundingClientRect();
      const or = opt.getBoundingClientRect();
      return {
        group: { w: Math.round(gr.width), h: Math.round(gr.height) },
        opt: { w: Math.round(or.width), h: Math.round(or.height) },
      };
    }, groupSel);

  await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await page.locator('.control-group').waitFor({ timeout: 30_000 });
  await page.waitForTimeout(300);
  const lib = await box('.control-group');

  await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
  await page.locator('.view-mode-control').waitFor({ timeout: 30_000 });
  await page.waitForTimeout(300);
  const brain = await box('.view-mode-control');

  expect(lib && brain, 'both toggles present').toBeTruthy();
  expect(brain!.opt, `brain option vs library option (${JSON.stringify({ brain, lib })})`).toEqual(
    lib!.opt,
  );
  expect(
    brain!.group,
    `brain track vs library track (${JSON.stringify({ brain, lib })})`,
  ).toEqual(lib!.group);
});

/**
 * Open a topic detail sheet and return the scrolling column, failing early if
 * the fixture produced no note cards to measure.
 */
async function openTopicDetail(page: Page): Promise<void> {
  const topic = `MobileNotes${Date.now()}`;
  const seed = await seedBrain(
    fixture.baseUrl,
    `Mobile Notes Source ${topic}`,
    [`A note about [[${topic}]] with enough text to give the card a body.`],
    [topic],
  );
  seeds.push(seed);

  await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
  const item = page.locator('.index-item', { hasText: topic }).first();
  await item.waitFor({ timeout: 30_000 });
  await item.click();
  await page.locator('app-note-card .note-card-container').first().waitFor({ timeout: 30_000 });
}

test('the topic detail pane has no horizontal scrollbar from card actions', async ({ page }) => {
  await openTopicDetail(page);

  const measured = await page.evaluate(() => {
    const col = document.querySelector('.content-col') as HTMLElement | null;
    if (!col) return null;
    const box = col.getBoundingClientRect();
    // Measure against the column's BORDER box, not its padded content box: the
    // "Back to Index" band is deliberately full-bleed (it spans the border box
    // to mask content passing under it), so a content-box limit would flag that
    // intentional edge as a defect. Nothing may escape the column itself.
    const limit = box.right;
    const overflowing = Array.from(col.querySelectorAll('*'))
      .filter((el) => {
        const b = el.getBoundingClientRect();
        return b.width > 0 && b.right > limit + 1;
      })
      .map((el) => ({
        cls: (el.className || '').toString().slice(0, 40),
        overBy: Math.round(el.getBoundingClientRect().right - limit),
      }));
    return {
      horizontalOverflow: col.scrollWidth - col.clientWidth,
      documentOverflow: document.documentElement.scrollWidth - window.innerWidth,
      overflowing,
    };
  });
  expect(measured, 'the topic detail column must be present').not.toBeNull();

  // Regression: two 44px card-action buttons needed 102px of a 287px footer
  // pinned to 32px tall, so the cluster overhung the card by 49px (and the
  // topic header by 69px); `overflow-x: auto` turned that into a second
  // scrollbar beside the vertical one.
  expect(
    measured!.overflowing,
    `elements must not escape the detail column: ${JSON.stringify(measured!.overflowing)}`,
  ).toEqual([]);
  expect(measured!.horizontalOverflow, 'detail column must not scroll sideways').toBeLessThanOrEqual(
    1,
  );
  expect(measured!.documentOverflow, 'page must not scroll sideways').toBeLessThanOrEqual(1);
});

test('card actions sit on their own row instead of overhanging the card', async ({ page }) => {
  await openTopicDetail(page);

  const geometry = await page.evaluate(() => {
    const actions = document.querySelector('app-note-card .note-actions') as HTMLElement | null;
    const footer = document.querySelector('app-note-card .note-footer') as HTMLElement | null;
    const card = document.querySelector('app-note-card .note-card-container') as HTMLElement | null;
    if (!actions || !footer || !card) return null;
    const a = actions.getBoundingClientRect();
    const f = footer.getBoundingClientRect();
    const c = card.getBoundingClientRect();
    const cs = getComputedStyle(actions);
    return {
      actionsWithinCard: a.right <= c.right + 1 && a.left >= c.left - 1,
      actionsBelowMetadata: a.top >= f.top - 1,
      actionsFullWidth: Math.abs(a.width - f.width) <= 1,
      hasBoxChrome: cs.borderTopWidth !== '0px' || cs.boxShadow !== 'none',
      buttonSizes: Array.from(actions.querySelectorAll('button')).map((b) => {
        const r = b.getBoundingClientRect();
        return { w: Math.round(r.width), h: Math.round(r.height) };
      }),
    };
  });
  expect(geometry, 'the note card must expose its footer and actions').not.toBeNull();

  expect(geometry!.actionsWithinCard, 'the action cluster must not overhang the card').toBe(true);
  expect(geometry!.actionsBelowMetadata, 'the actions must be on their own row').toBe(true);
  expect(geometry!.actionsFullWidth, 'the action row must span the footer width').toBe(true);
  // On touch the cluster is always visible, so the desktop hover-pill's border
  // and shadow would be permanent chrome around two buttons.
  expect(geometry!.hasBoxChrome, 'the hover pill box must be dropped on mobile').toBe(false);

  for (const b of geometry!.buttonSizes) {
    expect(b.w).toBeGreaterThanOrEqual(MIN_TAP_TARGET);
    expect(b.h).toBeGreaterThanOrEqual(MIN_TAP_TARGET);
  }
});
