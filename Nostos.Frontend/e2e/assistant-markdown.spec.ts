/**
 * Ask Nostos Markdown regression (issue #316).
 *
 * The component test owns the complete syntax matrix. This browser spec proves
 * that the generated DOM survives Angular sanitization and that wide Markdown
 * content (table/code/URL) stays inside the real assistant panel.
 */
import { expect, test, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';
import { enableAssistant } from './support/assistant';

test.use({ serviceWorkers: 'block' });

let fixture: ReturnType<typeof loadFixture>;

const REPLY = [
  '## Collections',
  '',
  'You have **53 unsorted books**. A useful split is:',
  '',
  '1. **Classics & Fiction**',
  '   - *War and Peace*',
  '   - *The Magic Mountain*',
  '2. **Moral & Political Philosophy**',
  '',
  '| Collection | Books |',
  '| --- | ---: |',
  '| Classics & Fiction | 18 |',
  '| Moral & Political Philosophy | 9 |',
  '',
  '```text',
  'a-very-long-code-line-that-must-scroll-locally-without-widening-the-assistant-panel-1234567890',
  '```',
  '',
  '![remote cover](https://tracker.invalid/cover.png)',
  '<script>document.body.dataset.markdownXss = "true"</script>',
].join('\n');

test.beforeAll(() => {
  fixture = loadFixture();
});

async function mockAssistant(page: Page): Promise<void> {
  await page.route('**/api/assistant/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ available: true }),
    }),
  );

  await page.route('**/api/assistant/turn/stream', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        reply: REPLY,
        acknowledgement: null,
        anchorPrompt: null,
        suggestions: [],
        changeset: null,
        capturedNoteId: null,
      }),
    }),
  );
}

test('renders structured replies without executable/media HTML or panel overflow', async ({ page }) => {
  await mockAssistant(page);
  await enableAssistant(page);
  await page.goto(`${fixture.baseUrl}/library`);

  await page.locator('[data-testid="assistant-trigger"]').click();
  const composer = page.locator('[data-testid="assistant-composer"]');
  await composer.fill('**This is user-authored Markdown-looking text**');
  await composer.press('Enter');

  const assistantEntry = page.locator('[data-testid="assistant-entry-markdown"]').last();
  await expect(assistantEntry.locator('strong').first()).toContainText('53 unsorted books');
  await expect(assistantEntry.locator('ol')).toBeVisible();
  await expect(assistantEntry.locator('table')).toBeVisible();
  await expect(assistantEntry.locator('pre code')).toContainText('a-very-long-code-line');

  await expect(assistantEntry.locator('img')).toHaveCount(0);
  await expect(assistantEntry.locator('script')).toHaveCount(0);
  await expect(assistantEntry).toContainText('[Image omitted: remote cover]');
  await expect(page.locator('body')).not.toHaveAttribute('data-markdown-xss', 'true');

  const userEntry = page.locator('.entry-user .entry-text').last();
  await expect(userEntry).toContainText('**This is user-authored Markdown-looking text**');
  await expect(userEntry.locator('strong')).toHaveCount(0);

  const fits = await assistantEntry.evaluate((element) => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
  }));
  expect(fits.scrollWidth).toBeLessThanOrEqual(fits.clientWidth + 1);
});
