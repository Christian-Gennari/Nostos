/**
 * Changes made somewhere else — Ask Nostos, MCP, another tab — appear on an
 * open page without a reload. The writes here go straight to the API, as any
 * of those would, while the page is left alone; the page must catch up through
 * the library change feed (`/api/library/changes`).
 */
import { expect, test, type APIRequestContext } from '@playwright/test';
import { loadFixture } from './support/fixture';

const PREFERENCES = {
  viewMode: 'list',
  sort: 'title',
  pageSize: 20,
  sidebarExpanded: true,
  groupByWork: true,
  assistantEnabled: false,
  assistantVoiceEnabled: true,
};

async function createBook(api: APIRequestContext, baseUrl: string, title: string): Promise<string> {
  const response = await api.post(`${baseUrl}/api/books`, {
    data: { type: 'physical', title, author: 'Live Update Author' },
  });
  expect(response.ok()).toBeTruthy();
  return ((await response.json()) as { id: string }).id;
}

test.beforeEach(async ({ page }) => {
  await page.addInitScript((preferences) => {
    localStorage.setItem('nostos.library.preferences', JSON.stringify(preferences));
  }, PREFERENCES);
  await page.setViewportSize({ width: 1440, height: 900 });
});

test('the library shows a collection and a book added elsewhere, without a reload', async ({ page, request }) => {
  const fixture = loadFixture();
  const suffix = Date.now().toString(36);
  let navigations = 0;
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame()) navigations++;
  });

  await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('header.toolbar')).toBeVisible();
  const navigationsAfterLoad = navigations;

  const collectionName = `Live collection ${suffix}`;
  const created = await request.post(`${fixture.baseUrl}/api/collections`, {
    data: { name: collectionName, parentId: null },
  });
  expect(created.ok()).toBeTruthy();
  const bookTitle = `Aaa live book ${suffix}`;
  await createBook(request, fixture.baseUrl, bookTitle);

  await expect(page.locator('#library-collections-sidebar')).toContainText(collectionName, { timeout: 10_000 });
  await expect(page.getByText(bookTitle).first()).toBeVisible({ timeout: 10_000 });
  expect(navigations, 'the page must not have reloaded').toBe(navigationsAfterLoad);
});

test('book detail shows a rename made elsewhere, and waits while Edit Book is open', async ({ page, request }) => {
  const fixture = loadFixture();
  const suffix = Date.now().toString(36);
  const id = await createBook(request, fixture.baseUrl, `Before ${suffix}`);

  async function rename(title: string): Promise<void> {
    const current = await (await request.get(`${fixture.baseUrl}/api/books/${id}`)).json();
    const updated = await request.put(`${fixture.baseUrl}/api/books/${id}`, {
      data: { ...current, title },
    });
    expect(updated.ok()).toBeTruthy();
  }

  await page.goto(`${fixture.baseUrl}/library/${id}`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('h1').first()).toContainText(`Before ${suffix}`);

  await rename(`After ${suffix}`);
  await expect(page.locator('h1').first()).toContainText(`After ${suffix}`, { timeout: 10_000 });

  // With Edit Book open, a change elsewhere must not re-seed the form under the
  // user's hands.
  await page.locator('.edit-metadata-btn').click();
  await page.getByRole('menuitem', { name: /book details/i }).click();
  const titleInput = page.locator('input[name="title"]').first();
  await expect(titleInput).toBeVisible();
  await titleInput.fill(`Typing ${suffix}`);

  await rename(`Elsewhere ${suffix}`);
  await page.waitForTimeout(3_000);
  await expect(titleInput).toHaveValue(`Typing ${suffix}`);
});
