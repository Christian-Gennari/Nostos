#!/usr/bin/env node
/**
 * Round-4 evidence for the Ask Nostos review card fixes (#1770).
 *
 * Every changeset in `issue-1770-round4-payloads.json` is the real server's own
 * `AssistantChangesetDto` captured from the coordinator/store/library graph
 * against SQLite. Runs against the Playwright fixture
 * (`e2e/support/launch-fixture.mjs launch`), stubs only the assistant turn and
 * changeset apply routes, and writes 1:1 card screenshots.
 *
 *   node e2e/support/issue-1770-round4-shots.mjs --out e2e/visual-evidence/issue-1770/round4
 *
 * Not part of the test suite: the file name is not *.spec.ts.
 */
import { chromium } from 'playwright';
import { readFileSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(readFileSync(path.join(HERE, 'fixture-state.json'), 'utf8'));
const payloads = JSON.parse(readFileSync(path.join(HERE, 'issue-1770-round4-payloads.json'), 'utf8'));
const BASE = fixture.baseUrl;

const args = process.argv.slice(2);
const option = (name, fallback) => {
  const at = args.indexOf(`--${name}`);
  return at >= 0 ? args[at + 1] : fallback;
};
const OUT = path.resolve(option('out', 'issue-1770-round4'));
mkdirSync(OUT, { recursive: true });

const REVIEW_SENTENCE = "Here's what I'd change. Nothing changes until you apply it.";

const PREFERENCES = {
  viewMode: 'grid',
  sort: 'lastread',
  pageSize: 20,
  sidebarExpanded: true,
  groupByWork: true,
  assistantEnabled: true,
  assistantVoiceEnabled: false,
};

/** One card state: the review the turn returns, and what applying it answers. */
const SCENARIOS = {
  kept: { changeset: payloads.partialClaim, openDetails: true },
  'kept-none': { changeset: payloads.allKept, openDetails: true },
  standalone: { changeset: payloads.standaloneKind },
  applied: {
    changeset: payloads.partialClaim,
    apply: payloads.partialClaimApplied,
    openDetails: true,
  },
};

function turnResponse(changeset) {
  return {
    reply: REVIEW_SENTENCE,
    acknowledgement: null,
    anchorPrompt: null,
    suggestions: [],
    changeset,
    capturedNoteId: null,
    executedCapabilities: [],
    sources: [],
    evidence: [],
    resolvedBook: null,
    error: null,
  };
}

async function settle(page) {
  await page.waitForTimeout(250);
  await page.evaluate(() => document.fonts?.ready);
  await page.waitForTimeout(150);
}

async function runScenario(browser, name, scenario, { width, height, theme, mobile }) {
  const ctx = await browser.newContext({
    viewport: { width, height },
    isMobile: mobile,
    hasTouch: mobile,
    deviceScaleFactor: 1,
    serviceWorkers: 'block',
  });
  await ctx.addInitScript(
    ([prefs, t]) => {
      localStorage.setItem('nostos.library.preferences', prefs);
      localStorage.setItem('nostos.theme', t);
    },
    [JSON.stringify(PREFERENCES), theme],
  );
  await ctx.route('**/api/assistant/status', (r) => r.fulfill({ json: { available: true } }));
  await ctx.route('**/api/assistant/turn/stream', async (route) => {
    const request = route.request().postDataJSON();
    await route.fulfill({
      contentType: 'application/x-ndjson',
      body: JSON.stringify({
        turnId: request.turnId,
        sequence: 1,
        kind: 'completed',
        response: turnResponse(scenario.changeset),
      }) + '\n',
    });
  });
  await ctx.route('**/api/assistant/changesets/*/apply', (r) =>
    r.fulfill({
      json: {
        success: true,
        errorCode: null,
        errorMessage: null,
        conflicts: [],
        changeset: scenario.apply ?? scenario.changeset,
      },
    }));

  const page = await ctx.newPage();
  await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
  await page.locator('[data-testid="assistant-trigger"]').first().click();
  const panel = page.locator('[data-testid="assistant-panel"]');
  await panel.waitFor({ state: 'visible' });
  await page.locator('[data-testid="assistant-composer"]').fill('Give my unkinded topics a kind.');
  await page.locator('[data-testid="assistant-send"]').click();
  await page.locator('[data-testid="assistant-changeset"]').waitFor({ state: 'visible' });

  if (scenario.apply) {
    await page.locator('[data-testid="assistant-changeset-apply"]').click();
    await page.locator('[data-testid="assistant-changeset"][data-status="applied"]')
      .waitFor({ state: 'visible' });
  }
  if (scenario.openDetails) {
    await page.locator('[data-testid="assistant-changeset-details-toggle"]').click();
    await page.locator('[data-testid="assistant-changeset-details"]').waitFor({ state: 'visible' });
  }

  await page.mouse.move(5, Math.max(5, height - 20));
  await settle(page);

  const headline = (await page.locator('[data-testid="assistant-changeset-headline"]').textContent())?.trim();
  const file = path.join(OUT, `${name}-${width}-${theme}.png`);
  await page.locator('[data-testid="assistant-changeset"]').screenshot({ path: file, animations: 'disabled' });
  console.log(`[shot] ${path.basename(file)} :: ${headline}`);
  await ctx.close();
}

async function main() {
  const browser = await chromium.launch({ executablePath: process.env.PW_CHROMIUM || undefined });
  try {
    for (const [name, scenario] of Object.entries(SCENARIOS)) {
      await runScenario(browser, name, scenario, { width: 1280, height: 800, theme: 'light', mobile: false });
      await runScenario(browser, name, scenario, { width: 1280, height: 800, theme: 'dark', mobile: false });
      await runScenario(browser, name, scenario, { width: 390, height: 844, theme: 'light', mobile: true });
      await runScenario(browser, name, scenario, { width: 390, height: 844, theme: 'dark', mobile: true });
    }
  } finally {
    await browser.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
