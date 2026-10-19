/**
 * Mobile polish geometry — Studio and the Brain at phone width.
 *
 * Geometry-only assertions (no pixel snapshots), matching the approach in
 * `mobile-dock.spec.ts`: stable across styling work, loud when layout regresses.
 *
 * Each guard maps to a defect measured at 390x844 on the real build:
 *
 *  - The editor's formatting toolbar wrapped to FOUR rows (124px of the writing
 *    surface) because the Oxide skin's `flex-wrap: wrap` beat TinyMCE's (the
 *    editor is TipTap since #1037; the single-row contract still holds)
 *    `toolbar_mode: 'sliding'`, which only engages while the toolbar is a
 *    single row. The sliding overflow button therefore never rendered and every
 *    control stayed in the document.
 *  - The Studio's document tree is the only way to open a document, but its
 *    chevrons were 12x20px and its row actions 22x22px.
 *  - In the Brain, the place switch (the way to the other places on a phone)
 *    must stay in reach after scrolling a long topic.
 *
 * The spec runs in the mobile-chromium project (playwright.config.ts matches
 * `mobile.*.spec.ts`).
 */
import { expect, test, type Page } from '@playwright/test';

import { apiPost, loadFixture } from './support/fixture';
import { apiPut } from './support/visual-capture';
import { cleanupBrain, seedBrain, type BrainSeed } from './support/brain-fixture';
import { STUDIO_PAGE_SELECTOR, openFilesRail } from './support/studio-editor';

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
  await page.locator(STUDIO_PAGE_SELECTOR).waitFor({ timeout: 45_000 });
}

test('the page carries no toolbar; formatting appears over a selection', async ({ page }) => {
  const title = `MobilePolishRow-${Date.now()}`;
  await seedDocument(title);
  await openStudioDocument(page, title);

  await expect(page.locator('app-markdown-editor .editor-toolbar')).toHaveCount(0);
  await expect(page.getByTestId('editor-bubble')).toHaveCount(0);

  const firstParagraph = page.locator(`${STUDIO_PAGE_SELECTOR} p`).first();
  await firstParagraph.dblclick({ position: { x: 20, y: 8 } });
  const bubble = page.getByTestId('editor-bubble');
  await expect(bubble).toBeVisible();
  const box = await bubble.boundingBox();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(page.viewportSize()!.width);
});

test('every selection bubble button meets the 44px touch contract', async ({ page }) => {
  const title = `MobilePolishTargets-${Date.now()}`;
  await seedDocument(title);
  await openStudioDocument(page, title);

  await page.locator(`${STUDIO_PAGE_SELECTOR} p`).first().dblclick({ position: { x: 20, y: 8 } });
  const bubble = page.getByTestId('editor-bubble');
  await expect(bubble).toBeVisible();
  const slivers = await bubble.locator('button').evaluateAll((els) =>
    els
      .filter((el) => el.getBoundingClientRect().width > 0)
      .map((el) => ({
        label: el.getAttribute('aria-label') ?? '?',
        width: Math.round(el.getBoundingClientRect().width),
        height: Math.round(el.getBoundingClientRect().height),
      }))
      .filter((b) => b.width < 44 || b.height < 44),
  );
  expect(slivers, `bubble buttons under 44px: ${JSON.stringify(slivers)}`).toEqual([]);
});

test('the studio document tree exposes 44px expand chevrons', async ({ page }) => {
  // A chevron only exists on a row that HAS children, so seed a nested folder:
  // the folder row gets the expand control.
  const parentId = await seedNestedFolder();
  await page.goto(`${fixture.baseUrl}/studio`, { waitUntil: 'domcontentloaded' });
  await openFilesRail(page);

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

// --- The Brain at phone width -------------------------------------------
// The old Brain's detail sheet, its Notes/Map header and its view toggle are
// gone (docs/brain.md); what they guaranteed a phone reader is checked here on
// the new places: the way back stays in reach, every control is a thumb's
// size, and nothing scrolls sideways.

async function seedLongTopic(): Promise<{ topic: string; topicId: string }> {
  const topic = `MobilePolish${Date.now()}`;
  const seed = await seedBrain(
    fixture.baseUrl,
    `Mobile Polish Brain ${topic}`,
    Array.from({ length: 6 }, (_, i) =>
      `Note ${i + 1} referencing [[${topic}]] with enough text to fill the phone. ` + 'More words. '.repeat(20)),
    [topic],
  );
  seeds.push(seed);
  const topics = await (await fetch(`${fixture.baseUrl}/api/topics`)).json() as Array<{ id: string; name: string }>;
  return { topic, topicId: topics.find((t) => t.name === topic)!.id };
}

test('the Brain place switch stays reachable after scrolling a topic', async ({ page }) => {
  const { topic, topicId } = await seedLongTopic();
  await page.goto(`${fixture.baseUrl}/brain/topics/${topicId}`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('app-note-item').first()).toBeVisible();

  await page.locator('.brain-scroller').evaluate((scroller) => { scroller.scrollTop = scroller.scrollHeight; });
  const placeSwitch = page.locator('.place-switch');
  await expect(placeSwitch).toBeInViewport();
  await expect(placeSwitch).toContainText(topic);
  const box = (await placeSwitch.boundingBox())!;
  expect(box.height).toBeGreaterThanOrEqual(MIN_TAP_TARGET);

  await placeSwitch.click();
  await expect(page.locator('.brain-layout.drawer-open')).toHaveCount(1);
});

test('the Brain search is a 44px target', async ({ page }) => {
  await page.goto(`${fixture.baseUrl}/brain`, { waitUntil: 'domcontentloaded' });
  const search = page.locator('.toolbar .search-input');
  await expect(search).toBeVisible();
  expect((await search.boundingBox())!.height).toBeGreaterThanOrEqual(MIN_TAP_TARGET);
});

test('the map keeps its way back to the other places on a phone', async ({ page }) => {
  await page.goto(`${fixture.baseUrl}/brain/map`, { waitUntil: 'domcontentloaded' });
  const placeSwitch = page.locator('.map-toolbar .place-switch');
  await expect(placeSwitch).toBeVisible();
  expect((await placeSwitch.boundingBox())!.height).toBeGreaterThanOrEqual(MIN_TAP_TARGET);
  await placeSwitch.click();
  await page.getByRole('link', { name: /^All notes/ }).click();
  await expect(page).toHaveURL(/\/brain(\?|$)/);
});

test('a topic page has no horizontal scrollbar from its cards', async ({ page }) => {
  const { topicId } = await seedLongTopic();
  await page.goto(`${fixture.baseUrl}/brain/topics/${topicId}`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('app-note-item').first()).toBeVisible();
  const overflow = await page.evaluate(() => ({
    page: document.documentElement.scrollWidth - innerWidth,
    list: (() => { const el = document.querySelector('.brain-scroller') as HTMLElement; return el.scrollWidth - el.clientWidth; })(),
  }));
  expect(overflow).toEqual({ page: 0, list: 0 });
});
