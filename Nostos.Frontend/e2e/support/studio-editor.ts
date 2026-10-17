import type { Locator, Page } from '@playwright/test';

/**
 * The Studio's writing page (issue #1037). The editor is TipTap rendered in
 * the app's own DOM, so specs reach the page with an ordinary locator; there is
 * no TinyMCE iframe to frame into any more.
 */
export const STUDIO_PAGE_SELECTOR = 'app-markdown-editor .nostos-manuscript-body';

export function studioPage(page: Page): Locator {
  return page.locator(STUDIO_PAGE_SELECTOR);
}

export async function waitForStudioEditor(page: Page, timeout = 45_000): Promise<Locator> {
  const editor = studioPage(page);
  await editor.waitFor({ timeout });
  return editor;
}
