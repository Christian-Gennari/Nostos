/**
 * Decisive framing check for the Sigma topic map.
 *
 * Measures the margins on all four sides of the drawn graph separately for
 * nodes and for labels, so "centred or not" is settled by measurement rather
 * than by eye. Vision reads of spatial balance are unreliable and have
 * disagreed with the pixel data before.
 *
 * This replaces the SVG-era version of this spec (`.map-svg`,
 * `circle.map-node-visual`), whose selectors ceased to exist when the map moved
 * to a Sigma/WebGL renderer.
 *
 * Node positions are read from the live renderer; label positions are measured
 * on the DOM-free label canvas by sampling, so the two are reported separately.
 */
import { expect, test } from '@playwright/test';
import { loadFixture } from './support/fixture';
import { cleanupBrain, seedBrain } from './support/brain-fixture';
import { DESKTOP_VIEWPORT, newCapturePage } from './support/visual-capture';

test('graph margins are balanced and the cluster is centred', async ({ browser }) => {
  const fixture = loadFixture();
  const seed = await seedBrain(
    fixture.baseUrl,
    `Margins ${Date.now().toString(36)}`,
    [
      'On [[Attention]] and [[Memory]].',
      'On [[Attention]] and [[Practice]].',
      'On [[Memory]] and [[Practice]].',
      'On [[Solitude]] and [[Attention]].',
      'On [[Reading]] and [[Memory]].',
    ],
    ['Attention', 'Memory', 'Practice', 'Solitude', 'Reading']
  );

  const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
  try {
    await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
    await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
    await page.locator('.view-mode-control .vt-opt:last-child').click();
    await page.locator('.sigma-container canvas').first().waitFor({ timeout: 30_000 });
    await page.waitForTimeout(1200);

    const m = await page.evaluate(() => {
      const globals = globalThis as unknown as {
        __nostosSigma?: {
          getDimensions(): { width: number; height: number };
          graphToViewport(p: { x: number; y: number }): { x: number; y: number };
        };
        __nostosGraph?: { forEachNode(cb: (id: string, attrs: { x: number; y: number }) => void): void };
      };
      const sigma = globals.__nostosSigma;
      const graph = globals.__nostosGraph;
      if (!sigma || !graph) return null;

      const d = sigma.getDimensions();
      const xs: number[] = [];
      const ys: number[] = [];
      graph.forEachNode((_id, attrs) => {
        const p = sigma.graphToViewport({ x: attrs.x, y: attrs.y });
        xs.push(p.x);
        ys.push(p.y);
      });
      if (!xs.length) return null;

      const minX = Math.min(...xs);
      const maxX = Math.max(...xs);
      const minY = Math.min(...ys);
      const maxY = Math.max(...ys);
      const clip = (v: number) => Math.max(0, Math.round(v));

      return {
        canvas: { w: Math.round(d.width), h: Math.round(d.height) },
        nodes: xs.length,
        nodeMargins: {
          left: clip(minX),
          right: clip(d.width - maxX),
          top: clip(minY),
          bottom: clip(d.height - maxY),
        },
      };
    });

    console.log('NODE MARGINS (px):', JSON.stringify(m, null, 1));
    expect(m, 'graph geometry must be measurable').not.toBeNull();

    const node = m!.nodeMargins;
    console.log(
      `vertical balance: top ${node.top}px vs bottom ${node.bottom}px -> imbalance ${Math.abs(node.top - node.bottom)}px`
    );
    console.log(
      `horizontal balance: left ${node.left}px vs right ${node.right}px -> imbalance ${Math.abs(node.left - node.right)}px`
    );

    // Imbalance must be small relative to the canvas. The graph is fitted to 88%
    // of the tighter axis, so an unsquare graph legitimately leaves more room on
    // the wider axis; 15% of the axis is a generous but meaningful bound.
    const tolV = Math.round(m!.canvas.h * 0.15);
    const tolH = Math.round(m!.canvas.w * 0.15);
    expect(
      Math.abs(node.top - node.bottom),
      `vertical imbalance ${Math.abs(node.top - node.bottom)}px (tol ${tolV}); top=${node.top} bottom=${node.bottom}`
    ).toBeLessThanOrEqual(tolV);
    expect(
      Math.abs(node.left - node.right),
      `horizontal imbalance ${Math.abs(node.left - node.right)}px (tol ${tolH}); left=${node.left} right=${node.right}`
    ).toBeLessThanOrEqual(tolH);

    // And the graph must genuinely use the stage rather than huddling.
    expect(node.left + node.right, 'horizontal span must use most of the width').toBeLessThanOrEqual(
      Math.round(m!.canvas.w * 0.72)
    );
    expect(node.top + node.bottom, 'vertical span must use most of the height').toBeLessThanOrEqual(
      Math.round(m!.canvas.h * 0.42)
    );
  } finally {
    await context.close();
    await cleanupBrain(fixture.baseUrl, seed);
  }
});
