/**
 * Ask Nostos with nothing asked yet offers starter questions for the page, and
 * the conversation that follows reads as one: the reader's words in a bubble
 * on the right, Nostos as plain prose, speaker names kept for screen readers.
 */
import { expect, test, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';
import { enableAssistant } from './support/assistant';

test.use({ serviceWorkers: 'block' });

async function mockAssistant(page: Page): Promise<string[]> {
  const asked: string[] = [];
  await page.route('**/api/assistant/status', (route) => route.fulfill({ json: { available: true } }));
  await page.route('**/api/assistant/turn/stream', (route) => {
    asked.push(route.request().postDataJSON().message);
    return route.fulfill({
      json: { reply: 'Try *Middlemarch* next.', acknowledgement: null, anchorPrompt: null, suggestions: [], changeset: null, capturedNoteId: null },
    });
  });
  await enableAssistant(page);
  return asked;
}

test('a starter question is asked, and the conversation reads as one', async ({ page }) => {
  const asked = await mockAssistant(page);
  await page.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await page.getByTestId('assistant-trigger').click();

  const starters = page.getByTestId('assistant-starter');
  await expect(starters).toHaveCount(3);
  await expect(starters.first()).toHaveText('What should I read next from my library?');
  await starters.first().click();

  await expect(page.getByTestId('assistant-entry-markdown')).toContainText('Try Middlemarch next.');
  expect(asked).toEqual(['What should I read next from my library?']);
  await expect(page.getByTestId('assistant-empty')).toHaveCount(0);

  // The reader's message hugs the right edge; the reply spans from the left.
  const panel = (await page.getByTestId('assistant-panel').boundingBox())!;
  const mine = (await page.locator('.entry-user').boundingBox())!;
  const reply = (await page.locator('.entry-assistant').first().boundingBox())!;
  expect(panel.x + panel.width - (mine.x + mine.width)).toBeLessThan(40);
  expect(mine.x - panel.x).toBeGreaterThan(40);
  expect(reply.x - panel.x).toBeLessThan(40);

  // Names are not on screen, but are still in the accessibility tree.
  await expect(page.getByTestId('assistant-entry-user-label')).toHaveClass(/visually-hidden/);
  await expect(page.getByTestId('assistant-panel')).toContainText('You');
});

test('the composer hint shows only while the empty field has focus', async ({ page }) => {
  await mockAssistant(page);
  await page.goto(`${loadFixture().baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await page.getByTestId('assistant-trigger').click();
  const composer = page.getByTestId('assistant-composer');
  const hint = page.locator('.composer-hint');
  await expect(composer).toBeFocused();
  await expect.poll(() => hint.evaluate((el) => getComputedStyle(el).opacity)).toBe('1');

  await composer.fill('A draft');
  await expect.poll(() => hint.evaluate((el) => getComputedStyle(el).opacity)).toBe('0');
});
