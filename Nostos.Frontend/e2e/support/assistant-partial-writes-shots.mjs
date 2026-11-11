#!/usr/bin/env node
/**
 * Before/after evidence for #1558 (a write that completed before a turn
 * failed). Runs against the existing Playwright fixture and stubs only the
 * assistant's turn stream — no model is configured or called.
 *
 * Usage, from Nostos.Frontend/:
 *   node e2e/support/launch-fixture.mjs launch
 *   node e2e/support/assistant-partial-writes-shots.mjs before
 *   node e2e/support/launch-fixture.mjs kill
 *
 * `before` is the stacked PR's base (the failure body carries nothing, exactly
 * as that backend sends it); `after` is this PR's head (the failed event also
 * carries the capture that committed). Each run writes four 1:1 shots:
 * desktop 1440 and phone 390, in light and dark.
 */
import { chromium } from 'playwright';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '../../..');
const OUT = path.join(REPO_ROOT, 'Nostos.Frontend', 'e2e', 'visual-evidence', '1558-partial-writes');

const phase = process.argv[2];
if (phase !== 'before' && phase !== 'after') {
  console.error('usage: assistant-partial-writes-shots.mjs <before|after>');
  process.exit(2);
}

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

const NOTE_ID = 'note-1558-preview';
const ACKNOWLEDGEMENT = 'Saved to The Magic Mountain. You can undo this from your notes.';
const FAILURE_MESSAGE = 'The AI service could not complete this turn. Try again.';

/**
 * The base branch's backend sends a failed event with no body. The PR's
 * backend adds the committed outcomes to that same event.
 */
function streamBody(turnId) {
  const failed = {
    turnId,
    sequence: 2,
    kind: 'failed',
    failure: {
      code: 'assistant_provider_error',
      message: FAILURE_MESSAGE,
      retryable: true,
    },
    ...(phase === 'after'
      ? {
          response: {
            reply: '',
            acknowledgement: ACKNOWLEDGEMENT,
            anchorPrompt: null,
            suggestions: [],
            changeset: null,
            capturedNoteId: NOTE_ID,
            executedCapabilities: [],
            sources: [],
            evidence: [],
            resolvedBook: null,
            error: null,
          },
        }
      : {}),
  };

  return (
    JSON.stringify({ turnId, sequence: 1, kind: 'started' }) + '\n'
    + JSON.stringify(failed) + '\n'
  );
}

async function createContext(browser, { width, height, theme, mobile }) {
  const context = await browser.newContext({
    viewport: { width, height },
    deviceScaleFactor: 1,
    isMobile: mobile,
    hasTouch: mobile,
    colorScheme: theme,
    locale: 'en-GB',
  });
  await context.addInitScript(
    ([prefs, chosenTheme]) => {
      localStorage.setItem('nostos.library.preferences', prefs);
      localStorage.setItem('nostos.theme', chosenTheme);
    },
    [JSON.stringify(PREFERENCES), theme],
  );
  await context.route('**/api/assistant/status', (route) =>
    route.fulfill({ json: { available: true } }),
  );
  await context.route('**/api/assistant/turn/stream', async (route) => {
    const request = route.request().postDataJSON();
    await route.fulfill({
      contentType: 'application/x-ndjson',
      body: streamBody(request.turnId),
    });
  });
  // The capture card reads the note's topics. A capture that committed before
  // the failure has the same card a successful one does.
  await context.route(`**/api/notes/${NOTE_ID}`, (route) =>
    route.fulfill({
      json: { id: NOTE_ID, content: 'A knot holds because the rope grips itself.', topicNames: ['Attention'] },
    }),
  );
  await context.route('**/api/topics', (route) => route.fulfill({ json: [] }));
  return context;
}

async function capture(browser, { width, height, theme, mobile }) {
  const context = await createContext(browser, { width, height, theme, mobile });
  const page = await context.newPage();
  await page.goto(`${BASE}/library`, { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => document.fonts.ready).catch(() => {});
  await page.waitForTimeout(400);

  await page.keyboard.press('Control+j');
  await page.getByTestId('assistant-panel').waitFor({ state: 'visible' });
  await page.getByTestId('assistant-composer').fill(
    'Remember this phrase: a knot holds because the rope grips itself.',
  );
  await page.getByTestId('assistant-send').click();
  await page.getByTestId('assistant-failure').waitFor({ timeout: 20_000 });
  await page.waitForTimeout(700);

  const name = `${phase}-${width}-${theme}.png`;
  await page.screenshot({ path: path.join(OUT, name), animations: 'disabled' });
  console.log(`[shot] ${path.relative(REPO_ROOT, path.join(OUT, name))}`);

  const measurements = await page.evaluate((failureMessage) => {
    const artifacts = document.querySelector('[data-testid="assistant-entry-artifacts"]');
    const failure = document.querySelector('[data-testid="assistant-failure"]');
    const artifactsBeforeFailure = !!(artifacts && failure
      && (artifacts.compareDocumentPosition(failure) & Node.DOCUMENT_POSITION_FOLLOWING));
    const text = document.querySelector('[data-testid="assistant-transcript"]')?.textContent ?? '';
    return {
      hasCommittedArtifacts: !!artifacts,
      artifactsBeforeFailure,
      savedBeforeFailure:
        text.indexOf('Saved to The Magic Mountain.') >= 0
        && text.indexOf('Saved to The Magic Mountain.') < text.indexOf(failureMessage),
      capturedNoteBeforeFailure:
        text.indexOf('Saved note') >= 0 && text.indexOf('Saved note') < text.indexOf(failureMessage),
      failureCount: document.querySelectorAll('[data-testid="assistant-failure"]').length,
      retryOffered: !!document.querySelector('[data-testid="assistant-retry"]'),
    };
  }, FAILURE_MESSAGE);
  console.log(`[check] ${name} ${JSON.stringify(measurements)}`);

  await context.close();
}

const browser = await chromium.launch();
try {
  await capture(browser, { width: 1440, height: 900, theme: 'light', mobile: false });
  await capture(browser, { width: 1440, height: 900, theme: 'dark', mobile: false });
  await capture(browser, { width: 390, height: 844, theme: 'light', mobile: true });
  await capture(browser, { width: 390, height: 844, theme: 'dark', mobile: true });
} finally {
  await browser.close();
}

writeFileSync(path.join(OUT, 'README.txt'), [
  `Phase: ${phase}`,
  'Stubbed assistant turn; no model was configured or called.',
  phase === 'before'
    ? 'before: the failed event carries no body, exactly as the base branch sends it.'
    : 'after: the failed event carries the capture that committed before the failure.',
  '',
  `Captured against ${BASE}.`,
].join('\n'));
