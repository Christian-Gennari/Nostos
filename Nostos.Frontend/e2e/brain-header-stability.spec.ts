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
    if (!header || !tools || !areas || !search) return null;

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
      view: view ? box(view) : null,
    };
  });
}

function expectNear(actual: number, expected: number, label: string): void {
  expect(Math.abs(actual - expected), label).toBeLessThanOrEqual(1);
}

test('desktop Brain header stays anchored while Notes search fills the Topics view space', async ({ page }) => {
  await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
  await page.locator('.brain-header').waitFor({ timeout: 30_000 });

  await page.getByRole('button', { name: 'Topics', exact: true }).click();
  const topicSearch = page.locator('.brain-header input[aria-label="Search topics and notes"]');
  await topicSearch.waitFor();

  const topics = await headerGeometry(page);
  expect(topics, 'Topics header controls must render').not.toBeNull();
  expect(topics!.view, 'Topics must render the list/map control').not.toBeNull();

  await page.getByRole('button', { name: 'Notes', exact: true }).click();
  const noteSearch = page.locator('#brain-all-notes-search');
  await noteSearch.waitFor();

  const notes = await headerGeometry(page);
  expect(notes, 'Notes header controls must render').not.toBeNull();
  expect(notes!.view, 'Notes must not reserve a blank list/map control').toBeNull();

  expectNear(notes!.tools.left, topics!.tools.left, 'the tools region must not shift');
  expectNear(notes!.tools.width, topics!.tools.width, 'the tools region must keep its width');
  expectNear(notes!.areas.left, topics!.areas.left, 'Notes/Topics must stay anchored');
  expectNear(notes!.search.left, topics!.search.left, 'search must stay anchored');
  expectNear(notes!.header.height, topics!.header.height, 'the desktop header height must stay stable');

  expect(
    notes!.search.width - topics!.search.width,
    'Notes search should consume the space released by the Topics-only list/map control',
  ).toBeGreaterThan(50);
  expectNear(notes!.search.right, notes!.tools.right, 'Notes search should reach the toolbar edge');
  await expect(noteSearch).toHaveAttribute('placeholder', 'Search notes, quotes, books…');

  await page.getByRole('button', { name: 'Topics', exact: true }).click();
  await topicSearch.waitFor();

  const restored = await headerGeometry(page);
  expect(restored, 'Topics header must restore').not.toBeNull();
  expect(restored!.view, 'Topics list/map control must return').not.toBeNull();
  expectNear(restored!.areas.left, topics!.areas.left, 'Topics must return to the same x position');
  expectNear(restored!.search.left, topics!.search.left, 'search must return to the same x position');
});
