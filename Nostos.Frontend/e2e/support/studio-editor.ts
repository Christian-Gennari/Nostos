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
 * Open the Reference rail as each Studio page loads, for specs written for
 * working with sources in view. On a wide screen Reference sits beside the
 * page; there is no pin to set any more, so this presses the header's
 * Reference button once per document, as a writer would.
 */
export async function openReferenceRailOnLoad(target: Pick<Page, 'addInitScript'>): Promise<void> {
  await target.addInitScript(() => {
    if (window.innerWidth <= 900 || window.innerHeight <= 500) return;
    const started = Date.now();
    const tryOpen = () => {
      const toggle = document.querySelector<HTMLButtonElement>('.reference-toggle');
      // The button is in the DOM a moment before its bindings are: a routed
      // Studio is created first and checked on the next change detection. Until
      // aria-expanded is bound, its state is unknown, so keep waiting rather
      // than give up (this race left the rail closed on a slow CI runner).
      const expanded = toggle?.getAttribute('aria-expanded');
      if (expanded === 'false') {
        toggle!.click();
        return;
      }
      if (expanded === 'true') return;
      if (Date.now() - started < 30000) window.setTimeout(tryOpen, 50);
    };
    window.addEventListener('DOMContentLoaded', tryOpen, { once: true });
  });
}

/** Open the file list slide-over if it is closed. */
export async function openFilesRail(page: Page): Promise<void> {
  const toggle = page.locator('.files-toggle');
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
}
