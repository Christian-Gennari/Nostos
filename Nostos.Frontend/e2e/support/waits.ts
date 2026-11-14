/**
 * Condition-driven waits shared by the E2E specs.
 *
 * Every helper here resolves on something the app actually did — an animation
 * the browser reports, a box that stopped moving, the EPUB rendition adopting
 * the reader's page box — instead of sleeping for a guessed number of
 * milliseconds. Where the app exposes a signal of its own (a response, an
 * `aria-*` state, a class toggled when a page is shown) a spec should wait on
 * that directly; these cover the cases where the only observable is the render
 * result itself.
 */
import { expect, type Page } from '@playwright/test';

type EvaluateArg = string | undefined;

/**
 * Resolves once every *finite* CSS animation and transition under `selector`
 * (the whole page by default) has finished. Infinite animations, such as a
 * spinner, are ignored so this cannot hang on them.
 */
export async function waitForAnimations(page: Page, selector?: string): Promise<void> {
  await page.evaluate(async (target: EvaluateArg) => {
    const root: Element | Document = target ? document.querySelector(target) ?? document : document;
    const running = root
      .getAnimations({ subtree: true })
      .filter((animation) => {
        const timing = animation.effect?.getComputedTiming();
        return typeof timing?.endTime === 'number' && Number.isFinite(timing.endTime);
      });
    await Promise.all(running.map((animation) => animation.finished.catch(() => undefined)));
  }, selector);
}

/**
 * Resolves once the box of `selector` (the document root by default) has not
 * changed for three consecutive animation frames. Use it before measuring or
 * screenshotting a surface whose entrance is a CSS transition; it is the
 * render result the fixed delay was standing in for.
 *
 * A sub-pixel wobble (the box moving by up to 1 px as a wrapping line settles)
 * counts as still: it is invisible, and only a box that keeps travelling is a
 * surface that has not arrived.
 */
export async function waitForLayoutSettled(page: Page, selector?: string): Promise<void> {
  await waitForAnimations(page, selector);
  const settled = await page.evaluate(async (target: EvaluateArg) => {
    const element = target ? document.querySelector(target) : document.documentElement;
    if (!element) return false;
    const nextFrame = () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    const boxOf = () => {
      const box = element.getBoundingClientRect();
      return [box.x, box.y, box.width, box.height];
    };
    let previous = boxOf();
    let unchanged = 0;
    for (let frame = 0; frame < 180 && unchanged < 3; frame += 1) {
      await nextFrame();
      const current = boxOf();
      const moved = current.some((value, index) => Math.abs(value - previous[index]) > 1);
      unchanged = moved ? 0 : unchanged + 1;
      previous = current;
    }
    return unchanged >= 3;
  }, selector);
  if (!settled) {
    throw new Error(`Layout of ${selector ?? 'the page'} did not settle within 180 frames.`);
  }
}

/**
 * Resolves once the EPUB rendition has been resized to the reader's current
 * page box.
 *
 * The component debounces a resize by 350 ms and then calls
 * `rendition.resize(pageWidth, pageHeight)`, after which epub.js sizes the
 * book's iframe to the page. Until that lands the iframe keeps the *previous*
 * height, so "the iframe is as tall as the page" is exactly "the re-layout for
 * the geometry I just set has been applied" — the condition a fixed delay after
 * `setViewportSize` was guessing at.
 */
export async function waitForRenditionSize(page: Page, timeout = 20_000): Promise<void> {
  await expect
    .poll(
      () =>
        page.evaluate(() => {
          const pageBox = document.querySelector('#epub-page');
          const frame = document.querySelector<HTMLIFrameElement>('#epub-viewer iframe');
          if (!pageBox || !frame) return false;
          return pageBox.clientHeight > 0 && Math.abs(frame.clientHeight - pageBox.clientHeight) <= 1;
        }),
      { timeout, message: 'the EPUB rendition adopted the reader page box' },
    )
    .toBe(true);
}

/**
 * Opens an EPUB in the reader and waits until it is ready to be touched: the
 * first page is on screen at the final page size, and the text has stopped
 * moving.
 *
 * The last part matters for anything that measures the book: the reader
 * restores the saved place and re-lays the page out after the first paint, and
 * a selection or a click measured against a page that is still being placed
 * lands on the wrong words.
 */
export async function openEpubReader(page: Page, url: string): Promise<void> {
  await page.goto(url, { waitUntil: 'domcontentloaded' });
  await page.frameLocator('#epub-viewer iframe').locator('p').first().waitFor({ timeout: 45_000 });
  // `loading` is cleared when the first page has rendered, which is also when
  // the opening re-layouts start; wait for the surface to stop moving.
  await expect(page.locator('.loading-overlay')).toHaveClass(/is-hidden/, { timeout: 45_000 });
  await waitForRenditionSize(page);
  await waitForLayoutSettled(page, '#epub-viewer');
  await waitUntilStable(
    () => page.frameLocator('#epub-viewer iframe').locator('p').first().boundingBox(),
    { quietMs: 400, description: "the first paragraph's box" },
  );
}

export interface StableOptions {
  /** How long one sample is allowed to take, in ms. */
  timeout?: number;
  /** How long the value must stay unchanged, in ms. */
  quietMs?: number;
  /** Time between samples, in ms. */
  intervalMs?: number;
  description?: string;
}

/**
 * Polls `read()` until it returns the same value for `quietMs`.
 *
 * This is for the few claims that are about something *not* happening — a
 * repaint that must not arrive twice, a network log that must not grow — where
 * there is no completion event to wait for. It resolves as soon as the value
 * has held still and fails loudly if it keeps changing.
 */
export async function waitUntilStable<T>(
  read: () => Promise<T>,
  { timeout = 15_000, quietMs = 400, intervalMs = 100, description = 'the value' }: StableOptions = {},
): Promise<T> {
  const deadline = Date.now() + timeout;
  let reference = await read();
  let stableSince = Date.now();
  while (Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
    const current = await read();
    if (JSON.stringify(current) !== JSON.stringify(reference)) {
      reference = current;
      stableSince = Date.now();
      continue;
    }
    if (Date.now() - stableSince >= quietMs) return current;
  }
  throw new Error(`${description} did not hold still for ${quietMs} ms within ${timeout} ms.`);
}
