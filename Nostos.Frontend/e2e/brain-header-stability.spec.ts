import { expect, test, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';

let fixture: ReturnType<typeof loadFixture>;

test.beforeAll(() => {
  fixture = loadFixture();
});

async function headerGeometry(page: Page) {
  return page.evaluate(() => {
    const header = document.querySelector('.brain-header') as HTMLElement | null;
    const tools = document.querySelector('.brain-header-tools') as HTMLElement | null;
    const areas = document.querySelector('.brain-areas') as HTMLElement | null;
    const search = document.querySelector('.brain-header .search-box') as HTMLElement | null;
    const view = document.querySelector('.brain-header .view-mode-control') as HTMLElement | null;
    if (!header || !tools || !areas || !search || !view) return null;

    const box = (element: HTMLElement) => {
      const rect = element.getBoundingClientRect();
      return {
        left: rect.left,
        right: rect.right,
        top: rect.top,
        width: rect.width,
        height: rect.height,
      };
    };

    return {
      header: box(header),
      tools: box(tools),
      areas: box(areas),
      search: box(search),
      view: box(view),
      viewVisibility: getComputedStyle(view).visibility,
      viewDisplay: getComputedStyle(view).display,
    };
  });
}

function expectNear(actual: number, expected: number, label: string): void {
  expect(Math.abs(actual - expected), label).toBeLessThanOrEqual(1);
}

test('desktop Brain header stays anchored across Topics and Notes', async ({ page }) => {
  await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
  await page.locator('.brain-header').waitFor({ timeout: 30_000 });

  await page.getByRole('button', { name: 'Topics', exact: true }).click();
  await page.locator('.brain-header input[aria-label="Search topics and notes"]').waitFor();

  const topics = await headerGeometry(page);
  expect(topics, 'Topics header controls must render').not.toBeNull();
  expect(topics!.viewVisibility).toBe('visible');

  await page.getByRole('button', { name: 'Notes', exact: true }).click();
  await page.locator('#brain-all-notes-search').waitFor();

  const notes = await headerGeometry(page);
  expect(notes, 'Notes header controls must render').not.toBeNull();
  expect(notes!.viewVisibility).toBe('hidden');
  expect(notes!.viewDisplay).not.toBe('none');

  expectNear(notes!.tools.left, topics!.tools.left, 'the tools cluster must not shift');
  expectNear(notes!.tools.width, topics!.tools.width, 'the tools cluster must keep its width');
  expectNear(notes!.areas.left, topics!.areas.left, 'Notes/Topics must stay anchored');
  expectNear(notes!.search.left, topics!.search.left, 'search must stay anchored');
  expectNear(notes!.view.left, topics!.view.left, 'the reserved view-mode slot must stay in place');
  expectNear(notes!.header.height, topics!.header.height, 'the desktop header height must stay stable');

  await page.getByRole('button', { name: 'Topics', exact: true }).click();
  await page.locator('.brain-header input[aria-label="Search topics and notes"]').waitFor();

  const restored = await headerGeometry(page);
  expect(restored, 'Topics header must restore').not.toBeNull();
  expect(restored!.viewVisibility).toBe('visible');
  expectNear(restored!.areas.left, topics!.areas.left, 'Topics must return to the same x position');
  expectNear(restored!.search.left, topics!.search.left, 'search must return to the same x position');
});
