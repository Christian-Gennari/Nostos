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

/**
 * Pin the Reference rail beside the page (issue #1040). The page-first Studio
 * opens with Reference as a closed slide-over; specs written for working with
 * sources in view use the same preference a writer would set.
 */
export async function pinReferenceRail(target: Pick<Page, 'addInitScript'>): Promise<void> {
  await target.addInitScript(() => localStorage.setItem('nostos.studio.referencePinned', '1'));
}

/** Open the file list slide-over if it is closed. */
export async function openFilesRail(page: Page): Promise<void> {
  const toggle = page.locator('.files-toggle');
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
}
