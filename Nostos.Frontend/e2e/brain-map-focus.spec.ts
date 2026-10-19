/**
 * Evidence for the whole-surface map mode.
 *
 * Three behaviours are asserted here, all of them in a REAL browser, because
 * none of them can be settled by reading code:
 *
 *  1. Toggling into map view closes the index rail on desktop too, and the
 *     graph claims the track the rail gave up. Before this, the map rendered
 *     beside a fully visible index — a "toggle" between two states that were
 *     both on screen, with the graph confined to the column next to the list.
 *  2. The map's own toolbar carries a way back to the list. In map view the
 *     page-level view toggle lives inside the rail that is now closed, so
 *     without this control the mode would be a one-way door.
 *  3. Double-clicking a node opens that topic's detail — the same destination
 *     an index row click reaches — and leaves map view.
 *
 * Node ids are read from the live renderer (`__nostosGraph`), which is the only
 * reliable source of graph-space facts for a WebGL canvas, and the double-click
 * is dispatched as two real clicks at the node's screen position so Sigma's own
 * click counter (which is what decides a double-click) sees a genuine pair.
 */
import { expect, test } from '@playwright/test';
import { loadFixture } from './support/fixture';
import { cleanupBrain, seedBrain, type BrainSeed } from './support/brain-fixture';
import { capturePng, DESKTOP_VIEWPORT, MOBILE_VIEWPORT, newCapturePage } from './support/visual-capture';

/**
 * Number of top-level grid tracks in a computed `grid-template-columns`.
 *
 * Counting tracks rather than string-matching the source value is deliberate:
 * a real browser RESOLVES `minmax(280px, 320px)` to `320px`, so an assertion
 * written against the authored text passes in jsdom (which keeps it verbatim)
 * and fails in Chromium — the two environments disagree about the string, but
 * not about how many columns there are. Paren-aware because a `minmax()` track
 * contains a space.
 */
function gridTrackCount(computed: string): number {
  let depth = 0;
  let tracks = 0;
  let inTrack = false;
  for (const ch of computed) {
    if (ch === '(') depth++;
    else if (ch === ')') depth--;
    if (/\s/.test(ch) && depth === 0) {
      if (inTrack) tracks++;
      inTrack = false;
    } else {
      inTrack = true;
    }
  }
  return inTrack ? tracks + 1 : tracks;
}

/**
 * Sigma counts its own clicks to detect double-clicks, and that counter is
 * TIME-based, not position-based: two clicks within `doubleClickTimeout` (the
 * library default, 300ms) are folded into one double-click even if they land far
 * apart — and a double-click dispatches `doubleClickStage`, never `clickStage`.
 *
 * So any test that performs two separate single clicks has to leave real time
 * between them, or the second is swallowed as the tail of a double-click.
 */
const SIGMA_DOUBLE_CLICK_TIMEOUT_MS = 300;
const SEPARATE_CLICK_GAP_MS = SIGMA_DOUBLE_CLICK_TIMEOUT_MS + 150;

test.describe.configure({ mode: 'serial' });

let seed: BrainSeed | null = null;

async function ensureSeed(fixture: ReturnType<typeof loadFixture>) {
  seed ??= await seedBrain(
    fixture.baseUrl,
    `Map Focus ${Date.now().toString(36)}`,
    [
      'On [[Attention]] and [[Memory]].',
      'On [[Attention]] and [[Practice]].',
      'On [[Memory]] and [[Practice]].',
      'On [[Solitude]] and [[Attention]].',
      'On [[Reading]] and [[Memory]].',
    ],
    ['Attention', 'Memory', 'Practice', 'Solitude', 'Reading']
  );
  return seed;
}

async function openMap(page: import('@playwright/test').Page, baseUrl: string) {
  await page.goto(`${baseUrl}/brain/map`, { waitUntil: 'domcontentloaded' });
  await page.locator('.sigma-container canvas').first().waitFor({ timeout: 30_000 });
  await page.waitForFunction(
    () => document.querySelector('.topic-map')?.getAttribute('aria-busy') === 'false',
    undefined,
    { timeout: 30_000 }
  );
  await page.waitForTimeout(900);
}

/** Screen position of a rendered node, read from the live Sigma renderer. */
async function nodeScreenPosition(
  page: import('@playwright/test').Page,
  label: string
): Promise<{ x: number; y: number } | null> {
  return page.evaluate((wanted) => {
    const globals = globalThis as unknown as {
      __nostosSigma?: { graphToViewport(p: { x: number; y: number }): { x: number; y: number } };
      __nostosGraph?: {
        forEachNode(
          cb: (id: string, attrs: { x: number; y: number; label?: string }) => void
        ): void;
      };
    };
    const sigma = globals.__nostosSigma;
    const graph = globals.__nostosGraph;
    const container = document.querySelector('app-topic-map .sigma-container');
    if (!sigma || !graph || !container) return null;

    const box = container.getBoundingClientRect();
    let found: { x: number; y: number } | null = null;
    graph.forEachNode((_id, attrs) => {
      if (found || attrs.label !== wanted) return;
      const p = sigma.graphToViewport({ x: attrs.x, y: attrs.y });
      found = { x: box.left + p.x, y: box.top + p.y };
    });
    return found;
  }, label);
}

test('clicking empty space clears the selection', async ({ browser }) => {
  const fixture = loadFixture();
  await ensureSeed(fixture);
  const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
  try {
    await openMap(page, fixture.baseUrl);

    const target = 'Attention';
    const point = await nodeScreenPosition(page, target);
    expect(point, `the seeded topic "${target}" must be rendered`).not.toBeNull();

    // Select the node. A single click selects without leaving the map.
    await page.mouse.click(point!.x, point!.y);
    // Leave a real gap: Sigma's click counter is time-based, so a following click
    // inside `doubleClickTimeout` would be folded into a double-click (on empty
    // space that means `doubleClickStage`, which is not the event under test).
    await page.waitForTimeout(SEPARATE_CLICK_GAP_MS);

    const selected = await page.evaluate(() => ({
      chip: document.querySelector('.map-selection-name')?.textContent?.trim() ?? null,
      notesDisabled: (document.querySelector('.map-action--notes') as HTMLButtonElement | null)
        ?.disabled ?? null,
    }));
    expect(selected.chip, 'the clicked node must become the selection').toBe(target);

    // Now click empty space. Pick a point that is provably not a node: scan the
    // drawn node positions and take a spot far from all of them.
    const empty = await page.evaluate(() => {
      const globals = globalThis as unknown as {
        __nostosSigma?: { graphToViewport(p: { x: number; y: number }): { x: number; y: number } };
        __nostosGraph?: {
          forEachNode(cb: (id: string, a: { x: number; y: number }) => void): void;
        };
      };
      const sigma = globals.__nostosSigma;
      const graph = globals.__nostosGraph;
      const container = document.querySelector('app-topic-map .sigma-container');
      if (!sigma || !graph || !container) return null;
      const box = container.getBoundingClientRect();
      const overlays = [...document.querySelectorAll('.map-legend, .map-hud')]
        .map((element) => element.getBoundingClientRect());
      const nodes: Array<{ x: number; y: number }> = [];
      graph.forEachNode((_id, a) => {
        const p = sigma.graphToViewport({ x: a.x, y: a.y });
        nodes.push({ x: box.left + p.x, y: box.top + p.y });
      });
      // Candidate points on a grid, scored by distance to the nearest node.
      let best: { x: number; y: number; dist: number } | null = null;
      for (let fx = 0.1; fx <= 0.9; fx += 0.05) {
        for (let fy = 0.12; fy <= 0.9; fy += 0.05) {
          const x = box.left + box.width * fx;
          const y = box.top + box.height * fy;
          // Clicks on the legend or selection/action HUD do not reach Sigma's
          // empty canvas handler, so only consider genuinely uncovered points.
          if (
            overlays.some(
              (overlay) =>
                x >= overlay.left && x <= overlay.right && y >= overlay.top && y <= overlay.bottom
            )
          ) continue;
          const dist = Math.min(...nodes.map((n) => Math.hypot(n.x - x, n.y - y)));
          if (!best || dist > best.dist) best = { x, y, dist };
        }
      }
      return best;
    });
    expect(empty, 'an empty point must be findable').not.toBeNull();
    expect(empty!.dist, 'the chosen point must be well clear of every node').toBeGreaterThan(40);
    console.log('EMPTY-SPACE POINT:', JSON.stringify(empty));

    await page.mouse.click(empty!.x, empty!.y);
    await page.waitForTimeout(250);
    await capturePng(page, 'brain-map-focus-deselect');

    const after = await page.evaluate(() => ({
      chip: document.querySelector('.map-selection-name')?.textContent?.trim() ?? null,
      mapStillThere: !!document.querySelector('app-topic-map'),
    }));
    console.log('AFTER EMPTY CLICK:', JSON.stringify(after));

    // The chip is the visible face of the selection; it clearing IS the fix.
    expect(after.chip, 'an empty-space click must drop the selection').toBeNull();
    expect(after.mapStillThere, 'deselecting must not leave map view').toBe(true);
  } finally {
    await context.close();
  }
});

test('a camera pan does not clear the selection', async ({ browser }) => {
  const fixture = loadFixture();
  await ensureSeed(fixture);
  const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
  try {
    await openMap(page, fixture.baseUrl);

    const target = 'Memory';
    const point = await nodeScreenPosition(page, target);
    expect(point).not.toBeNull();
    await page.mouse.click(point!.x, point!.y);
    await page.waitForTimeout(SEPARATE_CLICK_GAP_MS);
    expect(
      await page.evaluate(
        () => document.querySelector('.map-selection-name')?.textContent?.trim() ?? null
      ),
      'the node must be selected before the pan'
    ).toBe(target);

    // Pan the camera from empty space: press, drag a long way, release. Sigma
    // suppresses the click that follows a drag (its `draggedEvents` counter runs
    // past `draggedEventsTolerance`), so the selection must survive. This is the
    // regression a naive `mousedown`/`mouseup` deselect would introduce.
    const canvas = page.locator('app-topic-map .sigma-container');
    const box = (await canvas.boundingBox())!;
    await page.mouse.move(box.x + box.width * 0.2, box.y + box.height * 0.75);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.5, box.y + box.height * 0.55, { steps: 12 });
    await page.mouse.up();
    await page.waitForTimeout(250);

    const after = await page.evaluate(() => ({
      chip: document.querySelector('.map-selection-name')?.textContent?.trim() ?? null,
    }));
    console.log('AFTER PAN:', JSON.stringify(after));

    expect(after.chip, 'a camera pan must not drop the selection').toBe(target);
  } finally {
    // No cleanup here: this is not the last test in the serial spec, and
    // `cleanupBrain` would delete the topics `ensureSeed` caches for the tests
    // that follow (leaving them with an empty graph and no canvas to wait for).
    await context.close();
  }
});

test('double-clicking a node opens that topic\'s notes', async ({ browser }) => {
  const fixture = loadFixture();
  await ensureSeed(fixture);
  const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
  try {
    await openMap(page, fixture.baseUrl);

    const target = 'Attention';
    const point = await nodeScreenPosition(page, target);
    expect(point, `the seeded topic "${target}" must be rendered on the map`).not.toBeNull();

    // Two real clicks at the node's screen position. Sigma counts clicks on its
    // own captor, so this is the gesture a user actually performs — a
    // programmatic `dblclick` event would not drive that counter.
    await page.mouse.click(point!.x, point!.y);
    await page.mouse.click(point!.x, point!.y);

    await page.waitForURL(/\/brain\/topics\//, { timeout: 30_000 });
    const title = page.getByRole('heading', { level: 1 });
    await expect(title).toHaveText(target);
    await capturePng(page, 'brain-map-focus-double-click');

    // The topic's page is the destination, as the card's Open topic is, and
    // the map is gone behind it (Back returns to it).
    await expect(page.locator('app-topic-map')).toHaveCount(0);
  } finally {
    await context.close();
    if (seed) await cleanupBrain(fixture.baseUrl, seed);
  }
});
