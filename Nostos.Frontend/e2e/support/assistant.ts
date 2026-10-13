import type { Page } from '@playwright/test';

/**
 * Turn the assistant on for a page before it loads.
 *
 * "Ask Nostos" is opt-in (#327): the trigger only renders when the reader has
 * enabled it in their preferences AND the server reports it available. A spec
 * that wants the trigger has to set both; this sets the preference, the same
 * way a returning reader's browser would carry it, and the caller mocks
 * `/api/assistant/status`.
 */
export async function enableAssistant(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem(
      'nostos.library.preferences',
      JSON.stringify({
        viewMode: 'grid',
        sort: 'lastread',
        pageSize: 20,
        sidebarExpanded: true,
        groupByWork: true,
        assistantEnabled: true,
        assistantVoiceEnabled: true,
      }),
    );
  });
}
