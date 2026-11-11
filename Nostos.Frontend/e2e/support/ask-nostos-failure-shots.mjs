#!/usr/bin/env node
/**
 * Before/after evidence for the Ask Nostos failure and retry change
 * (#1556, #1560).
 *
 * Runs against the Playwright fixture (`launch-fixture.mjs launch`) and scripts
 * only the assistant turn; no model is configured or called. The same script is
 * run once on `origin/main` (before) and once on the PR head (after), writing
 * to `e2e/visual-evidence/assistant-failure-retry/<phase>/`.
 *
 * Usage, from Nostos.Frontend/:
 *   node e2e/support/launch-fixture.mjs launch
 *   node e2e/support/ask-nostos-failure-shots.mjs before
 *   node e2e/support/launch-fixture.mjs kill
 */
import { chromium } from 'playwright';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '../../..');
const PHASE = process.argv[2] === 'before' ? 'before' : 'after';
const OUT = path.join(REPO_ROOT, 'Nostos.Frontend', 'e2e', 'visual-evidence', 'assistant-failure-retry', PHASE);
const fixture = JSON.parse(readFileSync(path.join(HERE, 'fixture-state.json'), 'utf8'));
const BASE = fixture.baseUrl;

mkdirSync(OUT, { recursive: true });

const PREFERENCES = {
  viewMode: 'grid',
  sort: 'lastread',
  pageSize: 20,
  sidebarExpanded: true,
  groupByWork: true,
  assistantEnabled: true,
  assistantVoiceEnabled: false,
};

/**
 * A managed host contract, used only for the allowance shots. The deployed
 * bundle is the same one Cloud serves; this stubs the answers `preview --cloud`
 * stubs. Without it a SelfHosted fixture has no account link to show.
 */
const MANAGED_CAPABILITIES = {
  deploymentMode: 'SelfHosted',
  requiresAuthentication: false,
  canConfigureAiProvider: false,
  managedAi: true,
  managedVoiceTranscription: false,
  usesCloudStorage: false,
  supportsLocalBackupConfiguration: true,
  supportsPrivateNetworkAccess: true,
  supportsEreaderAccess: true,
  usageMeteringAvailable: true,
  accountManagementUrl: 'https://nostos.page/account',
};

const USAGE = {
  state: 'exhausted',
  renewsAtUtc: '2026-12-01T00:00:00Z',
  refill: { available: true, state: 'active' },
};

const FAILURES = {
  timeout: {
    message: 'Ask Nostos took too long to answer. Try again.',
    code: 'assistant_provider_timeout',
    retryable: true,
    question: 'Which book should I read next?',
    managed: false,
  },
  allowance: {
    message: 'Your AI allowance is used up for now.',
    code: 'ai_usage_limit_reached',
    retryable: false,
    question: 'What have I been reading lately?',
    managed: true,
  },
};

function ndjson(turnId, failure) {
  return JSON.stringify({ turnId, sequence: 1, kind: 'started' }) + '\n'
    + JSON.stringify({
      turnId,
      sequence: 2,
      kind: 'failed',
      failure: {
        code: failure.code,
        message: failure.message,
        retryable: failure.retryable,
      },
    }) + '\n';
}

async function context(browser, { width, height, theme, mobile, managed }) {
  const ctx = await browser.newContext({
    viewport: { width, height },
    deviceScaleFactor: 1,
    isMobile: mobile,
    hasTouch: mobile,
    colorScheme: theme,
    locale: 'en-GB',
  });
  await ctx.addInitScript(
    ([prefs, selectedTheme]) => {
      localStorage.setItem('nostos.library.preferences', prefs);
      localStorage.setItem('nostos.theme', selectedTheme);
    },
    [JSON.stringify(PREFERENCES), theme],
  );
  await ctx.route('**/api/assistant/status', (route) =>
    route.fulfill({ json: { available: true } }),
  );
  if (managed) {
    await ctx.route('**/api/runtime/capabilities', (route) =>
      route.fulfill({ json: MANAGED_CAPABILITIES }),
    );
    await ctx.route('**/api/cloud/ai/usage', (route) => route.fulfill({ json: USAGE }));
  }
  return ctx;
}

async function stable(page) {
  await page.evaluate(async () => {
    await document.fonts.ready;
  }).catch(() => {});
  await page.waitForTimeout(500);
}

async function shot(page, name) {
  const file = path.join(OUT, `${name}.png`);
  await page.screenshot({ path: file, animations: 'disabled' });
  console.log(`[shot] ${path.relative(REPO_ROOT, file)}`);
}

async function capture(browser, { name, failure, width, height, theme, mobile }) {
  const ctx = await context(browser, {
    width,
    height,
    theme,
    mobile,
    managed: failure.managed,
  });
  await ctx.route('**/api/assistant/turn/stream', async (route) => {
    const request = route.request().postDataJSON();
    await route.fulfill({
      contentType: 'application/x-ndjson',
      body: ndjson(request.turnId, failure),
    });
  });

  const page = await ctx.newPage();
  await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
  await stable(page);
  await page.locator('[data-testid="assistant-trigger"]').first().click();
  await page.locator('[data-testid="assistant-panel"]').waitFor({ state: 'visible' });
  await page.getByTestId('assistant-composer').fill(failure.question);
  await page.getByTestId('assistant-send').click();

  await page.locator('.entry-error').waitFor({ timeout: 20_000 });
  if (PHASE === 'after') {
    await page.getByTestId('assistant-failure-details').last().waitFor({ timeout: 10_000 });
    if (failure.managed) {
      // Give the allowance request its turn so the renewal line is rendered.
      await page.getByTestId('assistant-failure-allowance').waitFor({ timeout: 10_000 }).catch(() => {});
    }
  }
  await page.waitForTimeout(700);

  const suffix = `${width}-${theme}`;
  await shot(page, `${name}-${suffix}`);
  await ctx.close();
}

async function main() {
  const browser = await chromium.launch();

  for (const [name, failure] of Object.entries(FAILURES)) {
    for (const theme of ['light', 'dark']) {
      await capture(browser, { name, failure, width: 1440, height: 900, theme, mobile: false });
      await capture(browser, { name, failure, width: 390, height: 844, theme, mobile: true });
    }
  }

  await browser.close();
  writeFileSync(path.join(OUT, 'README.txt'), [
    `Phase: ${PHASE}`,
    'Stubbed assistant turn; no model was configured or called.',
    'Allowance shots stub a managed deployment capability response',
    '(the same Cloud answers `npm run preview -- --cloud` simulates) so the',
    'account link and renewal date are visible on the SelfHosted fixture.',
    '',
    `Captured against ${BASE}.`,
  ].join('\n'));
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
