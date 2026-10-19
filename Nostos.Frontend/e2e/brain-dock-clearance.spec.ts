/**
 * The Brain's sidebar of places and topics on a touch screen: the last topic
 * of a long list can be scrolled clear of the dock at every size, and every
 * control in it (rows, Rename and Delete on the selected topic, the sort) is
 * a 44px target. On a phone the sidebar is the drawer the place switch opens.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiGet, loadFixture } from './support/fixture';
import { cleanupBrain, seedBrain, type BrainSeed } from './support/brain-fixture';

const viewports = [
  { width: 1440, height: 900 },
  { width: 1280, height: 720 },
  { width: 1024, height: 768 },
  { width: 820, height: 1180 },
  { width: 390, height: 844 },
  { width: 844, height: 390 },
];

let seed: BrainSeed;
let firstTopic: string;

test.beforeAll(async () => {
  const stamp = Date.now().toString(36);
  const names = Array.from({ length: 40 }, (_, i) => `Dock clearance ${stamp} ${String(i + 1).padStart(2, '0')}`);
  firstTopic = names[0];
  seed = await seedBrain(
    loadFixture().baseUrl,
    `Dock clearance source ${stamp}`,
    names.map((name, i) => `Topic note ${i + 1} for [[${name}]].`),
    names,
  );
});

test.afterAll(async () => {
  await cleanupBrain(loadFixture().baseUrl, seed);
});

/** Controls inside the sidebar smaller than 44px, by name and size. */
async function smallControls(page: Page): Promise<string[]> {
  return page.evaluate(() => [...document.querySelectorAll('app-brain-sidebar button, app-brain-sidebar a[href]')]
    .flatMap((element) => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      if (!rect.width || !rect.height || style.visibility === 'hidden' || style.display === 'none') return [];
      if (rect.width >= 43.5 && rect.height >= 43.5) return [];
      const name = (element.getAttribute('aria-label') || element.textContent || '').trim().replace(/\s+/g, ' ');
      return [`${name.slice(0, 40)} ${Math.round(rect.width)}x${Math.round(rect.height)}`];
    }));
}

test('the sidebar clears the dock and its controls are 44px on touch', async ({ browser }) => {
  const { baseUrl } = loadFixture();
  const topicId = (await apiGet<Array<{ id: string; name: string }>>(baseUrl, '/api/topics'))
    .find((topic) => topic.name === firstTopic)!.id;
  const context = await browser.newContext({ viewport: viewports[0], isMobile: true, hasTouch: true, serviceWorkers: 'block' });
  const page = await context.newPage();
  const failures: string[] = [];
  try {
    // A topic page, so its row is the selected one and shows Rename and Delete.
    await page.goto(`${baseUrl}/brain/topics/${topicId}`);
    await expect.poll(() => page.evaluate(() => matchMedia('(pointer: coarse)').matches)).toBe(true);
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      if (viewport.width <= 768) {
        // On a phone the sidebar is a drawer.
        await page.locator('.place-switch').click();
        await expect(page.locator('.brain-layout.drawer-open')).toHaveCount(1);
      }
      const rows = page.locator('app-brain-sidebar .topic-row');
      await expect(rows.first()).toBeVisible();
      const gap = await page.evaluate(async () => {
        const scroller = document.querySelector('app-brain-sidebar .nav-scroll-area') as HTMLElement;
        scroller.scrollTop = scroller.scrollHeight;
        await new Promise((resolve) => requestAnimationFrame(() => resolve(null)));
        const all = scroller.querySelectorAll('.topic-row');
        const last = all[all.length - 1].getBoundingClientRect();
        const dock = document.querySelector('app-app-dock')?.getBoundingClientRect();
        const dockVisible = !!dock && dock.height > 0 && getComputedStyle(document.querySelector('app-app-dock')!).opacity !== '0';
        return dockVisible ? Math.round(dock!.top - last.bottom) : null;
      });
      if (gap !== null && gap < 0) failures.push(`${viewport.width}x${viewport.height}: last topic ${-gap}px under the dock`);
      const small = await smallControls(page);
      if (small.length) failures.push(`${viewport.width}x${viewport.height}: ${small.join('; ')}`);
      if (viewport.width <= 768) {
        await page.keyboard.press('Escape');
        await expect(page.locator('.brain-layout.drawer-open')).toHaveCount(0);
      }
    }
    expect(failures).toEqual([]);
  } finally {
    await context.close();
  }
});
