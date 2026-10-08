/**
 * Geometry and resize contract for the edge-to-edge Brain map canvas.
 *
 * The graph itself continues beneath the app dock. Its floating controls stay
 * above that dock, and camera fitting keeps graph nodes in the open band between
 * the legend and the selected-topic HUD.
 */
import { expect, test, type Page } from '@playwright/test';
import { loadFixture } from './support/fixture';
import { cleanupBrain, seedBrain, type BrainSeed } from './support/brain-fixture';
import { newCapturePage, type Viewport } from './support/visual-capture';

const VIEWPORTS: Array<{ name: string; viewport: Viewport; mobile: boolean }> = [
  { name: 'desktop', viewport: { width: 1440, height: 900 }, mobile: false },
  { name: 'short desktop', viewport: { width: 1280, height: 600 }, mobile: false },
  { name: 'portrait phone', viewport: { width: 390, height: 844 }, mobile: true },
  { name: 'landscape phone', viewport: { width: 844, height: 390 }, mobile: true },
];

let fixtureBaseUrl = '';
let seed: BrainSeed | null = null;

test.beforeAll(async () => {
  const fixture = loadFixture();
  fixtureBaseUrl = fixture.baseUrl;
  seed = await seedBrain(
    fixtureBaseUrl,
    'Map Canvas Geometry',
    [
      'A [[Focus]] moves with [[Thread one]] and [[Thread two]].',
      'A [[Focus]] moves with [[Thread three]] and [[Thread four]].',
    ],
    ['Focus', 'Thread one', 'Thread two', 'Thread three', 'Thread four']
  );
});

test.afterAll(async () => {
  if (seed) await cleanupBrain(fixtureBaseUrl, seed);
});

async function openMap(page: Page): Promise<void> {
  await page.goto(fixtureBaseUrl + '/second-brain', { waitUntil: 'domcontentloaded' });
  await page.locator('.view-mode-control .vt-opt:last-child').waitFor({ timeout: 30_000 });
  await page.locator('.view-mode-control .vt-opt:last-child').click();
  await page.locator('.sigma-container canvas').first().waitFor({ timeout: 30_000 });
  await page.waitForFunction(
    () => document.querySelector('.topic-map')?.getAttribute('aria-busy') === 'false',
    undefined,
    { timeout: 30_000 }
  );
  await page.waitForFunction(
    () => Boolean(globalThis.__nostosGraph && globalThis.__nostosSigma),
    undefined,
    { timeout: 30_000 }
  );
  await page.waitForTimeout(500);
}

async function selectFocusAndFit(page: Page): Promise<void> {
  const point = await page.evaluate(() => {
    const globals = globalThis as unknown as {
      __nostosSigma?: { graphToViewport(point: { x: number; y: number }): { x: number; y: number } };
      __nostosGraph?: {
        forEachNode(
          callback: (id: string, attrs: { x: number; y: number; label?: string }) => void
        ): void;
      };
    };
    const sigma = globals.__nostosSigma;
    const graph = globals.__nostosGraph;
    const canvas = document.querySelector('.sigma-container')?.getBoundingClientRect();
    if (!sigma || !graph || !canvas) return null;

    let found: { x: number; y: number } | null = null;
    graph.forEachNode((_id, attrs) => {
      if (found || attrs.label !== 'Focus') return;
      const position = sigma.graphToViewport({ x: attrs.x, y: attrs.y });
      found = { x: canvas.left + position.x, y: canvas.top + position.y };
    });
    return found;
  });
  expect(point, 'the seeded Focus topic must be rendered').not.toBeNull();
  await page.mouse.click(point!.x, point!.y);
  await expect(page.locator('.map-card-title')).toHaveText('Focus');
  await page.getByRole('button', { name: 'Fit to view' }).click();
  await page.waitForTimeout(450);
}

async function readGeometry(page: Page) {
  return page.evaluate(() => {
    type Box = { left: number; top: number; right: number; bottom: number; width: number; height: number };
    const box = (selector: string): Box | null => {
      const element = document.querySelector(selector);
      if (!element) return null;
      const rect = element.getBoundingClientRect();
      return {
        left: rect.left,
        top: rect.top,
        right: rect.right,
        bottom: rect.bottom,
        width: rect.width,
        height: rect.height,
      };
    };
    const intersects = (a: Box | null, b: Box | null): boolean =>
      !!a &&
      !!b &&
      Math.min(a.right, b.right) > Math.max(a.left, b.left) &&
      Math.min(a.bottom, b.bottom) > Math.max(a.top, b.top);
    const stageElement = document.querySelector('.map-stage') as HTMLElement | null;
    const contentElement = document.querySelector('.content-col') as HTMLElement | null;
    const stageStyle = stageElement ? getComputedStyle(stageElement) : null;
    const graph = (globalThis as unknown as {
      __nostosGraph?: {
        forEachNode(callback: (id: string, attrs: { x: number; y: number }) => void): void;
      };
      __nostosSigma?: {
        getDimensions(): { width: number; height: number };
        graphToViewport(point: { x: number; y: number }): { x: number; y: number };
      };
    });
    const canvas = box('.sigma-container');
    const hud = box('.map-hud');
    const legend = box('.map-legend');
    const card = box('.map-card');
    const actions = box('.map-actions');
    const dock = box('.app-dock-container');
    let minNodeY = Number.POSITIVE_INFINITY;
    let maxNodeY = Number.NEGATIVE_INFINITY;
    let nodeCount = 0;
    if (graph.__nostosGraph && graph.__nostosSigma) {
      graph.__nostosGraph.forEachNode((_id, attrs) => {
        const point = graph.__nostosSigma!.graphToViewport({ x: attrs.x, y: attrs.y });
        minNodeY = Math.min(minNodeY, point.y);
        maxNodeY = Math.max(maxNodeY, point.y);
        nodeCount++;
      });
    }

    const related = document.querySelector('.map-card-related') as HTMLElement | null;
    const relatedChips = related ? Array.from(related.querySelectorAll('button')) : [];
    const relatedRows = new Set(relatedChips.map((chip) => Math.round(chip.getBoundingClientRect().top)));

    const touchTargets = Array.from(
      document.querySelectorAll('.map-actions button, .map-card button')
    )
      .map((button) => {
        const rect = button.getBoundingClientRect();
        return { width: rect.width, height: rect.height };
      })
      .filter((rect) => rect.width > 0 && rect.height > 0);

    const stage = box('.map-stage');
    const content = box('.content-col');
    const header = box('.brain-header');
    const overlayBoxes = [legend, card, actions].filter((item): item is Box => !!item);
    const overlayCollisions: string[] = [];
    const overlayNames = ['legend', 'card', 'actions'];
    for (let left = 0; left < overlayBoxes.length; left++) {
      for (let right = left + 1; right < overlayBoxes.length; right++) {
        if (intersects(overlayBoxes[left], overlayBoxes[right])) {
          overlayCollisions.push(overlayNames[left] + ':' + overlayNames[right]);
        }
      }
    }

    return {
      viewport: { width: innerWidth, height: innerHeight },
      stage,
      content,
      header,
      canvas,
      dock,
      legend,
      card,
      actions,
      hud,
      edgeDeltas: {
        left: stage && content ? stage.left - content.left : null,
        right: stage && content ? content.right - stage.right : null,
        top: stage && content ? stage.top - content.top : null,
        bottom: stage && content ? content.bottom - stage.bottom : null,
        header: stage && header ? stage.top - header.bottom : null,
      },
      style: {
        border: stageStyle?.borderWidth ?? null,
        radius: stageStyle?.borderRadius ?? null,
        contentPadding: contentElement ? getComputedStyle(contentElement).padding : null,
      },
      canvasUnderDock: intersects(canvas, dock),
      overlayDockCollisions: {
        legend: intersects(legend, dock),
        card: intersects(card, dock),
        actions: intersects(actions, dock),
      },
      overlayCollisions,
      graph: {
        nodeCount,
        minNodeY,
        maxNodeY,
        legendBottom: legend && canvas ? legend.bottom - canvas.top : 0,
        hudTop: hud && canvas ? hud.top - canvas.top : null,
        renderer: graph.__nostosSigma?.getDimensions() ?? null,
      },
      related: related
        ? {
            display: getComputedStyle(related).display,
            count: relatedChips.length,
            rows: relatedRows.size,
            clientWidth: related.clientWidth,
            scrollWidth: related.scrollWidth,
            clientHeight: related.clientHeight,
            scrollHeight: related.scrollHeight,
          }
        : null,
      touchTargets,
      coarsePointer: matchMedia('(pointer: coarse)').matches,
      pageOverflow:
        document.documentElement.scrollWidth > document.documentElement.clientWidth ||
        document.documentElement.scrollHeight > document.documentElement.clientHeight,
    };
  });
}

function assertCanvasGeometry(geometry: Awaited<ReturnType<typeof readGeometry>>): void {
  expect(geometry.stage).not.toBeNull();
  expect(geometry.content).not.toBeNull();
  expect(geometry.header).not.toBeNull();
  expect(geometry.dock).not.toBeNull();

  expect(Math.abs(geometry.edgeDeltas.left!), 'stage must touch the content left edge').toBeLessThanOrEqual(1);
  expect(Math.abs(geometry.edgeDeltas.right!), 'stage must touch the content right edge').toBeLessThanOrEqual(1);
  expect(Math.abs(geometry.edgeDeltas.top!), 'stage must start below the Brain header').toBeLessThanOrEqual(1);
  expect(Math.abs(geometry.edgeDeltas.bottom!), 'stage must reach the content bottom edge').toBeLessThanOrEqual(1);
  expect(Math.abs(geometry.edgeDeltas.header!), 'header and canvas must meet without a band').toBeLessThanOrEqual(1);

  expect(geometry.style.border).toBe('0px');
  expect(geometry.style.radius).toBe('0px');
  expect(geometry.style.contentPadding).toBe('0px');
  expect(geometry.canvasUnderDock, 'the graph surface must extend behind the dock').toBe(true);

  expect(geometry.overlayDockCollisions.legend).toBe(false);
  expect(geometry.overlayDockCollisions.card).toBe(false);
  expect(geometry.overlayDockCollisions.actions).toBe(false);
  expect(geometry.overlayCollisions).toEqual([]);
  expect(geometry.pageOverflow, 'the canvas must not add page scrollbars').toBe(false);

  expect(geometry.graph.nodeCount).toBeGreaterThan(0);
  expect(geometry.graph.renderer?.width).toBeCloseTo(geometry.canvas!.width, 0);
  expect(geometry.graph.renderer?.height).toBeCloseTo(geometry.canvas!.height, 0);
  expect(geometry.graph.minNodeY, 'fit must keep nodes below the top legend').toBeGreaterThanOrEqual(
    geometry.graph.legendBottom
  );
  expect(geometry.graph.maxNodeY, 'fit must keep nodes above the HUD and dock').toBeLessThanOrEqual(
    geometry.graph.hudTop!
  );

  if (geometry.coarsePointer) {
    for (const target of geometry.touchTargets) {
      expect(target.width, 'coarse-pointer map actions need 44px width').toBeGreaterThanOrEqual(44);
      expect(target.height, 'coarse-pointer map actions need 44px height').toBeGreaterThanOrEqual(44);
    }
  }

  if (geometry.viewport.width === 390 && geometry.viewport.height === 844) {
    expect(geometry.related?.display).toBe('grid');
    expect(geometry.related?.count).toBe(4);
    expect(geometry.related?.rows).toBeLessThanOrEqual(2);
    expect(geometry.related?.scrollWidth).toBeLessThanOrEqual(geometry.related?.clientWidth ?? 0);
    expect(geometry.related?.scrollHeight).toBeLessThanOrEqual(geometry.related?.clientHeight ?? 0);
  }
}

for (const target of VIEWPORTS) {
  test('canvas reaches content edges and overlays clear the dock at ' + target.name, async ({ browser }) => {
    const { context, page } = await newCapturePage(browser, target.viewport, target.mobile);
    try {
      await openMap(page);
      await selectFocusAndFit(page);
      const geometry = await readGeometry(page);
      console.log('MAP CANVAS ' + target.name + ': ' + JSON.stringify(geometry));
      assertCanvasGeometry(geometry);
    } finally {
      await context.close();
    }
  });
}

test('canvas and Sigma remeasure after orientation and height changes', async ({ browser }) => {
  const { context, page } = await newCapturePage(browser, { width: 1440, height: 900 });
  try {
    await openMap(page);
    await selectFocusAndFit(page);

    for (const viewport of [
      { width: 844, height: 390 },
      { width: 390, height: 844 },
      { width: 1280, height: 600 },
      { width: 1440, height: 900 },
    ]) {
      await page.setViewportSize(viewport);
      await page.waitForFunction(() => {
        const sigma = (globalThis as unknown as {
          __nostosSigma?: { getDimensions(): { width: number; height: number } };
        }).__nostosSigma;
        const canvas = document.querySelector('.sigma-container')?.getBoundingClientRect();
        if (!sigma || !canvas) return false;
        const dimensions = sigma.getDimensions();
        return (
          Math.abs(dimensions.width - canvas.width) < 1 &&
          Math.abs(dimensions.height - canvas.height) < 1
        );
      });
      await page.waitForTimeout(450);
      const geometry = await readGeometry(page);
      console.log('MAP RESIZE ' + viewport.width + 'x' + viewport.height + ': ' + JSON.stringify(geometry));
      assertCanvasGeometry(geometry);
    }
  } finally {
    await context.close();
  }
});
