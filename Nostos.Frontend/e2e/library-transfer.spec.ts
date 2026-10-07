/**
 * Real-backend browser QA for library export/import/activation (#680 slice B10).
 *
 * Every scenario runs against the real production Angular build served by a
 * real SelfHosted Nostos backend with a disposable SQLite root and synthetic
 * media. Destination instances are launched per test and torn down in
 * `finally`, so browser projects and repeat runs never share consumed state.
 *
 * Scenario map (assignment 2a-f):
 *   a  export -> native download -> backend verifier
 *   b  empty destination import -> auto-activation -> reload serves the library
 *   c1 populated replacement: server counts, one confirmation, sealed overlay
 *   c2 change after preparation: updated counts + "changed since the import
 *      started" + fresh confirmation
 *   d1 reload mid-upload -> reselect same file -> only missing chunks re-sent
 *   d2 reload during activation -> reattach -> outcome
 *   e1 non-archive file   e2 corrupted archive   e3 cancel mid-upload
 *   e4 backend restart mid-upload   e5 second tab lease
 *   f  replacement dialog focus trap / Escape / sealed overlay
 */
import { expect, test, type Page } from '@playwright/test';
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';

import {
  TRANSFER_ARTIFACTS,
  assertBackendLogClean,
  bookByTitle,
  exportArchiveViaApi,
  killTransferInstance,
  launchTransferInstance,
  librarySnapshot,
  loadTransferFixture,
  ltGet,
  ltPost,
  snapshotCounts,
  transferInstance,
  validateArchiveOnServer,
  writeTransferArtifact,
  type LibraryTransferInstance,
} from './support/library-transfer-harness';

test.describe.configure({ mode: 'serial' });

let source: LibraryTransferInstance;
let archivePath: string | null = null;

test.beforeAll(() => {
  source = transferInstance(loadTransferFixture(), 'source');
});

/** Uses the artifact produced by scenario (a); falls back to the export API. */
async function ensureArchive(): Promise<string> {
  if (archivePath) return archivePath;
  mkdirSync(TRANSFER_ARTIFACTS, { recursive: true });
  const fallback = path.join(TRANSFER_ARTIFACTS, 'library-fallback.nostos');
  await exportArchiveViaApi(source.baseUrl, fallback);
  archivePath = fallback;
  return fallback;
}

interface ErrorCollectors {
  pageErrors: string[];
  consoleErrors: string[];
  /** Resolves console argument handles recorded by the listener. */
  settle: () => Promise<void>;
}

function collectErrors(page: Page): ErrorCollectors {
  const pageErrors: string[] = [];
  const consoleErrors: string[] = [];
  const pending: Promise<void>[] = [];
  page.on('pageerror', (error) => pageErrors.push(String(error)));
  page.on('console', (message) => {
    if (message.type() !== 'error') return;
    const text = message.text();
    const url = message.location().url;
    pending.push(
      (async () => {
        const args = message.args();
        let detail = text;
        if (args.length > 0) {
          const values = await Promise.all(
            args.map(async (arg) => {
              try {
                const value = await arg.evaluate((input: unknown) => {
                  if (input instanceof Error) {
                    return {
                      name: input.name,
                      message: input.message,
                      stack: input.stack?.slice(0, 800),
                    };
                  }
                  try {
                    return JSON.parse(JSON.stringify(input));
                  } catch {
                    return String(input);
                  }
                });
                return typeof value === 'string' ? value : JSON.stringify(value);
              } catch {
                return null;
              }
            }),
          );
          const resolved = values.filter((value): value is string => value !== null);
          if (resolved.length === 0) {
            // The page navigated/reloaded before the argument handles could be
            // read, so the entry cannot be attributed; do not report the
            // placeholder as an app error.
            return;
          }
          detail = resolved.join(' ');
        }
        consoleErrors.push(`${detail} @ ${url}`);
      })(),
    );
  });
  return {
    pageErrors,
    consoleErrors,
    settle: async () => {
      await Promise.all(pending);
    },
  };
}

/**
 * Console errors that are browser/framework noise rather than app failures:
 * epub.js renders chapters inside sandboxed `about:srcdoc` iframes (the
 * engine blocks scripts there by design), browsers probe `/favicon.ico`, the
 * reader's first open of a book legitimately 404s the cached-locations route
 * before the locations POST repopulates it, and the activation protocol uses
 * a handled 409 (confirmation required / destination conflict) as its
 * re-review signal, which the browser logs as a failed fetch. The transfer
 * host also treats an `active-import` 404 as the normal no-active-import state.
 */
/**
 * Page errors that are deliberate-navigation noise: WebKit reports an
 * XMLHttpRequest cancelled by the page's own reload as an access-control
 * failure. The suite reloads mid-request on purpose (d1/d2/e4/e5), so only
 * that cancellation shape is ignored; same-origin XHR cannot really fail CORS.
 */
function unexpectedPageErrors(errors: string[]): string[] {
  return errors.filter(
    (line) =>
      !(
        /XMLHttpRequest cannot load .*\/api\//i.test(line) &&
        /access control checks/i.test(line)
      ),
  );
}

function unexpectedConsoleErrors(errors: string[]): string[] {
  return errors.filter(
    (line) =>
      !/about:srcdoc/.test(line) &&
      !/favicon/i.test(line) &&
      // Offline/CI hosts commonly cannot reach Google Fonts; the app falls
      // back to its bundled stack and the download error is environment noise.
      !/downloadable font|fonts\.gstatic\.com/i.test(line) &&
      // WebKit reports the app's own viewport meta key as a console error.
      !/interactive-widget/i.test(line) &&
      // Firefox rejects a lazily-imported chunk when the page's own automatic
      // activation reload (or the test's final page close) interrupts it;
      // Angular's global error handler logs the rejected module load. Every
      // scenario asserts the rendered surfaces after the reload, so a chunk
      // that actually failed to load still fails the run.
      !/error loading dynamically imported module/i.test(line) &&
      !/\/api\/books\/[0-9a-f-]+\/locations\b/i.test(line) &&
      !/status of 404 .*\/api\/portability\/migration\/active-import\b/i.test(line) &&
      // Only the handled 409 re-review is expected; any other activation
      // failure status stays visible to the suite.
      !/status of 409 .*\/api\/portability\/migration\/jobs\/[0-9a-f-]+\/activate\b/i.test(line),
  );
}

async function openSettings(page: Page, baseUrl: string): Promise<void> {
  await page.goto(`${baseUrl}/settings`);
  await expect(page.getByTestId('manage-library-open')).toBeVisible();
  await page.getByTestId('manage-library-open').click();
  await expect(page).toHaveURL(`${baseUrl}/settings/library`);
  await expect(page.getByTestId('manage-library-page')).toBeVisible();
  await expect(page.getByTestId('library-transfer-card')).toBeVisible();
  // Let the route's lazily-imported chunks finish before a scenario triggers
  // the activation reload; otherwise Firefox can abort a pending import and
  // report it as a module-load TypeError.
  await page.waitForLoadState('networkidle');
}

async function selectArchive(page: Page, file: string): Promise<void> {
  await page.getByTestId('library-import-file-input').setInputFiles(file);
}

/** Per-chunk request URL indexes observed on the page, in order. */
function trackChunkIndexes(page: Page): number[] {
  const indexes: number[] = [];
  page.on('request', (request) => {
    const match = /\/upload-session\/chunks\/(\d+)/.exec(request.url());
    if (match) indexes.push(Number(match[1]));
  });
  return indexes;
}

function trackJobIds(page: Page): string[] {
  const jobIds: string[] = [];
  page.on('request', (request) => {
    const match = /\/migration\/jobs\/([0-9a-fA-F-]{36})\//.exec(request.url());
    if (match && !jobIds.includes(match[1])) jobIds.push(match[1]);
  });
  return jobIds;
}

async function delayChunks(page: Page, delayMs: number): Promise<void> {
  await page.route(
    '**/api/portability/migration/jobs/*/upload-session/chunks/*',
    async (route) => {
      await new Promise((resolve) => setTimeout(resolve, delayMs));
      await route.continue();
    },
  );
}

/** Resolves even when the reload already happened (timeOrigin comparison). */
async function navigationOrigin(page: Page): Promise<number> {
  return page.evaluate(() => performance.timeOrigin);
}

async function waitForReload(page: Page, previousOrigin: number, timeout = 240_000): Promise<void> {
  await page.waitForFunction(
    (origin) => performance.timeOrigin !== origin,
    previousOrigin,
    { timeout, polling: 250 },
  );
}

async function waitForChunkIndex(indexes: number[], index: number, timeout = 120_000): Promise<void> {
  await expect
    .poll(() => indexes.includes(index), { timeout, message: `chunk ${index} was requested` })
    .toBe(true);
}

async function assertSourceContent(baseUrl: string): Promise<void> {
  const snapshot = await librarySnapshot(baseUrl);
  expect(snapshotCounts(snapshot)).toEqual({
    books: 4,
    notes: 3,
    highlights: 1,
    collections: 2,
    collectionMemberships: 2,
  });
  expect(snapshot.books.map((book) => book.title).sort()).toEqual(
    ['Field Notes on Static', 'The Lantern Keepers', 'The Unsorted Almanac', 'Tidewater Sessions'],
  );
  const lantern = bookByTitle(snapshot, 'The Lantern Keepers');
  expect(lantern.collectionIds).toHaveLength(1);
  expect(lantern.progressPercent).toBe(42);
  expect(lantern.lastLocation).toBeTruthy();
  expect(lantern.hasFile).toBe(true);
  const lanternNotes = snapshot.notesByBook[lantern.id] ?? [];
  expect(lanternNotes.map((note) => note.content)).toContain('Why the beacon matters.');
  expect(
    lanternNotes.some(
      (note) => note.selectedText?.includes('never goes dark') && note.cfiRange === 'epubcfi(/6/4!/4/2/1:0)',
    ),
  ).toBe(true);
  const field = bookByTitle(snapshot, 'Field Notes on Static');
  expect(snapshot.notesByBook[field.id]?.map((note) => note.content)).toContain(
    'Static is the signal.',
  );
  const tide = bookByTitle(snapshot, 'Tidewater Sessions');
  expect(tide.type).toBe('audiobook');
  expect(tide.hasFile).toBe(true);
  expect(snapshot.collections.map((collection) => collection.name).sort()).toEqual([
    'Essays',
    'Fiction',
  ]);
  const writing = snapshot.writings.find((item) => item.name === 'Migration diary');
  expect(writing?.content).toContain('packed the shelves');
}

async function withDestination(
  testInfo: { project: { name: string } },
  label: string,
  seed: 'a' | 'b' | 'none',
  body: (instance: LibraryTransferInstance) => Promise<void>,
): Promise<void> {
  const name = `lt-${testInfo.project.name}-${label}`;
  const instance = launchTransferInstance(name, seed);
  try {
    await body(instance);
  } finally {
    killTransferInstance(instance.name);
  }
}

test('scenario a: export downloads a server-verifiable .nostos archive', async ({ page }, testInfo) => {
  const errors = collectErrors(page);
  const idle = await librarySnapshot(source.baseUrl);
  expect(snapshotCounts(idle)).toEqual({
    books: 4,
    notes: 3,
    highlights: 1,
    collections: 2,
    collectionMemberships: 2,
  });

  await openSettings(page, source.baseUrl);
  await expect(page.getByTestId('export-idle')).toBeVisible();
  const downloadPromise = page.waitForEvent('download', { timeout: 180_000 });
  await page.getByTestId('export-start').click();
  await expect(page.getByTestId('export-ready')).toBeVisible({ timeout: 180_000 });

  const download = await downloadPromise;
  expect(download.suggestedFilename()).toMatch(/\.nostos$/);
  mkdirSync(TRANSFER_ARTIFACTS, { recursive: true });
  archivePath = path.join(TRANSFER_ARTIFACTS, `library-${testInfo.project.name}.nostos`);
  await download.saveAs(archivePath);

  const bytes = readFileSync(archivePath);
  expect(bytes.length).toBeGreaterThan(16 * 1024 * 1024);
  expect(bytes.length).toBeGreaterThan(
    snapshotCounts(idle).books * 1024,
  );

  // Native fallback stays visible and points at the export route.
  const fallback = page.getByTestId('library-export-download');
  await expect(fallback).toBeVisible();
  await expect(fallback).toHaveAttribute('href', /\/export-download$/);

  // The backend's own reader/verifier accepts the archive end to end.
  const validatorName = `lt-validator-${testInfo.project.name}`;
  const validator = launchTransferInstance(validatorName, 'none');
  try {
    const validation = await validateArchiveOnServer(validator.baseUrl, archivePath);
    expect(validation.chunkCount).toBeGreaterThanOrEqual(5);
    const counts = validation.preparedImport.counts;
    expect(counts.books).toBe(4);
    expect(counts.notes).toBe(3);
    expect(counts.collections).toBe(2);
    expect(counts.collectionMemberships).toBe(2);
    expect(counts.mediaEntries).toBe(3);
    writeTransferArtifact(`scenario-a-${testInfo.project.name}.json`, {
      archiveBytes: bytes.length,
      archiveSha256: validation.sha256,
      chunkSizeBytes: loadTransferFixture().chunkSizeBytes,
      chunkCount: validation.chunkCount,
      serverCounts: counts,
      suggestedFilename: download.suggestedFilename(),
    });
  } finally {
    killTransferInstance(validatorName);
  }

  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
  expect(unexpectedConsoleErrors(errors.consoleErrors)).toEqual([]);
});

test('scenario b: empty destination import auto-activates and serves the library', async ({ page }, testInfo) => {
  const archive = await ensureArchive();
  const errors = collectErrors(page);
  await withDestination(testInfo, 'empty', 'none', async (destination) => {
    const before = snapshotCounts(await librarySnapshot(destination.baseUrl));
    expect(before.books).toBe(0);
    expect(before.notes).toBe(0);

    await delayChunks(page, 120);
    await openSettings(page, destination.baseUrl);
    const chunkIndexes = trackChunkIndexes(page);
    const startedAt = Date.now();
    await selectArchive(page, archive);
    await expect(page.getByTestId('import-inspecting')).toBeVisible();
    await expect(page.getByTestId('import-uploading')).toBeVisible({ timeout: 180_000 });
    await waitForChunkIndex(chunkIndexes, 4);
    expect(chunkIndexes.length).toBeGreaterThanOrEqual(5);

    const beforeReload = await navigationOrigin(page);
    await expect(page.getByTestId('library-activation-overlay')).toBeVisible({
      timeout: 180_000,
    });
    await waitForReload(page, beforeReload);
    const durationMs = Date.now() - startedAt;
    expect(durationMs).toBeGreaterThan(0);

    await page.goto(`${destination.baseUrl}/library`);
    await expect(page.getByText('The Lantern Keepers').first()).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText('Tidewater Sessions').first()).toBeVisible();
    await assertSourceContent(destination.baseUrl);

    // The derived book-text pipeline of the NEW generation is rebuilt after
    // the swap: the imported book reaches a Ready index with real chunks.
    const snapshot = await librarySnapshot(destination.baseUrl);
    const lantern = bookByTitle(snapshot, 'The Lantern Keepers');
    const readTextIndex = async (): Promise<{ status: string; chunks: number }> => {
      const state = await ltGet<{ status?: string | number; chunkCount?: number }>(
        destination.baseUrl,
        `/api/books/${lantern.id}/text-index`,
      );
      // The API serialises BookTextIngestionStatus numerically.
      const names = ['Pending', 'Processing', 'Ready', 'Failed', 'Unsupported'];
      const status =
        typeof state.status === 'number'
          ? (names[state.status] ?? `Unknown(${state.status})`)
          : (state.status ?? 'unknown');
      return { status, chunks: state.chunkCount ?? 0 };
    };
    await expect
      .poll(readTextIndex, {
        timeout: 180_000,
        message: 'the imported book text is indexed from the new generation',
      })
      .toMatchObject({ status: 'Ready' });
    expect((await readTextIndex()).chunks).toBeGreaterThan(0);

    // The file-backed EPUB actually opens and renders its text.
    await page.goto(`${destination.baseUrl}/read/${lantern.id}`);
    const frame = page.frameLocator('#epub-viewer iframe');
    await expect(frame.locator('p').first()).toContainText('lantern never goes dark', {
      timeout: 45_000,
    });

    writeTransferArtifact(`scenario-b-${testInfo.project.name}.json`, {
      durationMs,
      chunks: chunkIndexes.length,
      counts: snapshotCounts(snapshot),
    });
  });
  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
  expect(unexpectedConsoleErrors(errors.consoleErrors)).toEqual([]);
});

test('scenario c1: populated replacement shows server counts, seals the cutover, serves the new library', async ({ page }, testInfo) => {
  const archive = await ensureArchive();
  const errors = collectErrors(page);
  await withDestination(testInfo, 'populated-plain', 'b', async (destination) => {
    await openSettings(page, destination.baseUrl);
    await selectArchive(page, archive);

    await expect(page.getByTestId('import-replacement')).toBeVisible({ timeout: 240_000 });
    const dialog = page.locator('.replacement-dialog-card');
    await expect(dialog).toBeVisible();
    await expect(page.getByTestId('replacement-existing')).toContainText(
      '2 books · 1 notes · 1 collections',
    );
    await expect(page.getByTestId('replacement-incoming')).toContainText(
      '4 books · 3 notes · 2 collections',
    );
    await expect(page.getByTestId('replacement-counts-source')).toContainText('Checked by Nostos');
    await expect(page.getByTestId('replacement-conflict')).toHaveCount(0);
    await expect(page.getByTestId('replacement-recovery')).toContainText('recovery copy');

    // Keep the cutover observable: delay the activation POST response so the
    // app-wide overlay and the sealed dialog are assertable.
    await page.route('**/api/portability/migration/jobs/*/activate', async (route) => {
      await new Promise((resolve) => setTimeout(resolve, 1_500));
      await route.continue();
    });

    const beforeReload = await navigationOrigin(page);
    await dialog.locator('.replacement-confirm').click();
    await expect(page.getByTestId('library-activation-overlay')).toBeVisible({
      timeout: 60_000,
    });
    const overlay = page.getByTestId('library-activation-overlay');
    await expect(overlay).toHaveAttribute('role', 'alert');
    await expect(overlay).toHaveAttribute('aria-busy', 'true');
    await expect(page.getByTestId('replacement-sealed')).toBeVisible();

    // Sealed: Escape and backdrop cannot dismiss while the server cuts over.
    await page.keyboard.press('Escape');
    await expect(dialog).toBeVisible();
    await expect(page.getByTestId('import-cancelled')).toHaveCount(0);

    await waitForReload(page, beforeReload);
    await page.goto(`${destination.baseUrl}/library`);
    await expect(page.getByText('The Lantern Keepers').first()).toBeVisible({ timeout: 30_000 });
    await assertSourceContent(destination.baseUrl);
    assertBackendLogClean(destination, [/SqliteException/, /Book-text worker cycle failed/]);
  });
  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
  expect(unexpectedConsoleErrors(errors.consoleErrors)).toEqual([]);
});

test('scenario c2: a change after preparation shows updated counts and a fresh confirmation', async ({ page }, testInfo) => {
  const archive = await ensureArchive();
  const errors = collectErrors(page);
  await withDestination(testInfo, 'populated-changed', 'b', async (destination) => {
    const before = await librarySnapshot(destination.baseUrl);
    const harbour = bookByTitle(before, 'Harbour Ledger');
    expect(snapshotCounts(before).notes).toBe(1);

    // Capture the server's fresh revision from each 409 and the exact revision
    // the next confirmation submits, so the safety coupling is asserted at the
    // network boundary, not only through the rendered outcome.
    const conflictRevisions: string[] = [];
    const activateBodies: Array<{ destinationRevision?: string; confirmReplacement?: boolean }> = [];
    page.on('response', async (response) => {
      if (/\/activate$/.test(response.url()) && response.status() === 409) {
        const body = (await response.json().catch(() => null)) as { destinationRevision?: string } | null;
        if (body?.destinationRevision) conflictRevisions.push(body.destinationRevision);
      }
    });
    page.on('request', (request) => {
      if (request.method() === 'POST' && /\/activate$/.test(request.url())) {
        try {
          activateBodies.push(request.postDataJSON());
        } catch {
          // A body that is not JSON is not an activation request we sent.
        }
      }
    });

    await openSettings(page, destination.baseUrl);
    await selectArchive(page, archive);
    await expect(page.getByTestId('import-replacement')).toBeVisible({ timeout: 240_000 });

    // A first confirmation of an unchanged populated library must not claim a
    // change, and the probe that fetches live facts must have completed before
    // the library is changed so the confirmation's revision is deterministically
    // stale afterwards. The probe seals the dialog while it runs.
    await expect(page.getByTestId('replacement-existing')).toContainText(
      '2 books · 1 notes · 1 collections',
    );
    await expect(page.getByTestId('replacement-sealed')).toHaveCount(0, { timeout: 60_000 });

    // Change the destination after preparation/before confirming.
    await ltPost(destination.baseUrl, `/api/books/${harbour.id}/notes`, {
      content: 'Written after the import started.',
    });

    const dialog = page.locator('.replacement-dialog-card');
    await dialog.locator('.replacement-confirm').click();

    // The stale revision is refused; the dialog shows the fresh counts and the
    // server's changedSinceImportStarted hint, then re-arms.
    await expect(page.getByTestId('replacement-existing')).toContainText(
      '2 books · 2 notes · 1 collections',
    );
    await expect(page.getByTestId('replacement-conflict')).toContainText(
      'changed since the import started',
    );
    await expect(dialog.locator('.replacement-confirm')).toBeEnabled();
    const freshRevision = conflictRevisions.at(-1);
    expect(freshRevision).toBeTruthy();

    const beforeReload = await navigationOrigin(page);
    await dialog.locator('.replacement-confirm').click();
    await waitForReload(page, beforeReload);

    // The confirmation that activated the library submitted exactly the same
    // revision the dialog displayed.
    expect(activateBodies.at(-1)?.destinationRevision).toBe(freshRevision);
    expect(activateBodies.at(-1)?.confirmReplacement).toBe(true);

    await page.goto(`${destination.baseUrl}/library`);
    await expect(page.getByText('The Lantern Keepers').first()).toBeVisible({ timeout: 30_000 });
    await assertSourceContent(destination.baseUrl);
    assertBackendLogClean(destination, [/SqliteException/, /Book-text worker cycle failed/]);
    const after = await librarySnapshot(destination.baseUrl);
    expect(after.notes.some((note) => note.content === 'Written after the import started.')).toBe(
      false,
    );
  });
  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
  expect(unexpectedConsoleErrors(errors.consoleErrors)).toEqual([]);
});

test('scenario d1: reload mid-upload resumes with only the missing chunks', async ({ page }, testInfo) => {
  const archive = await ensureArchive();
  const errors = collectErrors(page);
  await withDestination(testInfo, 'resume-upload', 'none', async (destination) => {
    await delayChunks(page, 600);
    await openSettings(page, destination.baseUrl);
    const chunkIndexes = trackChunkIndexes(page);
    const jobIds = trackJobIds(page);
    await selectArchive(page, archive);
    await expect(page.getByTestId('import-uploading')).toBeVisible({ timeout: 180_000 });
    await waitForChunkIndex(chunkIndexes, 3);

    const jobId = jobIds[0];
    expect(jobId).toBeTruthy();

    // Some chunks were received before the reload.
    const preReloadSession = await (
      await fetch(`${destination.baseUrl}/api/portability/migration/jobs/${jobId}/upload-session`)
    ).json();
    const preReloadReceived = new Set<number>();
    for (const range of preReloadSession.receivedRanges ?? []) {
      for (let index = range.startIndex; index <= range.endIndex; index += 1) {
        preReloadReceived.add(index);
      }
    }
    expect(preReloadReceived.size).toBeGreaterThan(0);
    expect(preReloadReceived.size).toBeLessThan(preReloadSession.session.totalChunks);

    await page.reload();
    const reselect = page.getByTestId('import-reselect');
    const failed = page.getByTestId('import-failed');
    await expect
      .poll(async () => (await reselect.isVisible()) || (await failed.isVisible()), {
        timeout: 60_000,
        message: 'the flow resumed to reselection or failed',
      })
      .toBe(true);
    if (await failed.isVisible()) {
      const status = await (
        await fetch(`${destination.baseUrl}/api/portability/migration/jobs/${jobId}`)
      ).json();
      throw new Error(
        `Import failed after reload: ${JSON.stringify({
          state: status.job.state,
          code: status.job.failureCode,
          message: status.job.failureMessage,
          progress: status.progress,
        })}`,
      );
    }
    await expect(reselect).toContainText('Select the same file again to resume');

    // Authoritative post-reload receipt state: no chunk is in flight now, so
    // the resume must request exactly the complement of these ranges.
    await page.waitForTimeout(1_000);
    const session = await (
      await fetch(`${destination.baseUrl}/api/portability/migration/jobs/${jobId}/upload-session`)
    ).json();
    const received = new Set<number>();
    for (const range of session.receivedRanges ?? []) {
      for (let index = range.startIndex; index <= range.endIndex; index += 1) received.add(index);
    }
    const expectedMissing = Array.from(
      { length: session.session.totalChunks as number },
      (_, index) => index,
    ).filter((index) => !received.has(index));
    expect(expectedMissing.length).toBeGreaterThan(0);

    chunkIndexes.length = 0;
    await selectArchive(page, archive);
    // The still-in-flight chunk can have landed server-side during the reload;
    // then nothing is missing and the flow goes straight to checking. Either
    // state is correct — what matters is that received chunks are not re-sent.
    await expect
      .poll(
        async () =>
          (await page.getByTestId('import-uploading').isVisible()) ||
          (await page.getByTestId('import-checking').isVisible()),
        { timeout: 60_000, message: 'the upload resumed or went straight to checking' },
      )
      .toBe(true);

    const beforeReload = await navigationOrigin(page);
    await expect(page.getByTestId('library-activation-overlay')).toBeVisible({
      timeout: 240_000,
    });
    await waitForReload(page, beforeReload);

    // After reselecting the same file the client requested exactly the
    // missing chunks, each once: no received chunk was re-sent and no missing
    // chunk was skipped.
    expect([...chunkIndexes].sort((a, b) => a - b)).toEqual(expectedMissing);

    await page.goto(`${destination.baseUrl}/library`);
    await assertSourceContent(destination.baseUrl);
    writeTransferArtifact(`scenario-d1-${testInfo.project.name}.json`, {
      chunksAlreadyReceived: received.size,
      chunkRequestsAfterReload: chunkIndexes.length,
      totalChunks: session.session.totalChunks,
    });
  });
  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
});

test('scenario d3: retry an exhausted part upload without restarting the job', async ({ page }, testInfo) => {
  const archive = await ensureArchive();
  await withDestination(testInfo, 'retry-part', 'none', async (destination) => {
    let rejectPart = true;
    let retryJobRequests = 0;
    page.on('request', request => {
      if (request.method() === 'POST' && /\/jobs\/[^/]+\/retry$/.test(request.url())) {
        retryJobRequests += 1;
      }
    });
    await page.route('**/api/portability/migration/jobs/*/upload-session/chunks/1', async route => {
      if (rejectPart) {
        await route.fulfill({ status: 503, contentType: 'application/json',
          body: JSON.stringify({ error: 'unexpected_error', message: 'Temporary storage failure' }) });
      } else {
        await route.continue();
      }
    });
    await openSettings(page, destination.baseUrl);
    const chunks = trackChunkIndexes(page);
    const jobs = trackJobIds(page);
    await selectArchive(page, archive);
    await expect(page.getByTestId('import-failed')).toBeVisible({ timeout: 180_000 });
    const jobId = jobs[0];
    expect(jobId).toBeTruthy();
    const status = await (await fetch(`${destination.baseUrl}/api/portability/migration/jobs/${jobId}`)).json();
    expect(status.job.state).toBe('Transferring');
    expect(status.session.receivedChunkCount).toBe(status.session.totalChunks - 1);
    expect(await page.evaluate(() => JSON.parse(localStorage.getItem('nostos.library-transfer.active.v1')!).jobId)).toBe(jobId);
    await testInfo.attach('part-failure', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });

    rejectPart = false;
    chunks.length = 0;
    const beforeReload = await navigationOrigin(page);
    await page.getByTestId('import-failure-action').click();
    await waitForReload(page, beforeReload);

    expect(chunks).toEqual([1]);
    expect(retryJobRequests).toBe(0);
    expect(jobs).toHaveLength(1);
    await page.goto(`${destination.baseUrl}/library`);
    await assertSourceContent(destination.baseUrl);
    await testInfo.attach('library-imported', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
  });
});

for (const loseBrowserMetadata of [false, true]) {
test(`scenario d4: reload after all parts resumes completion without selecting the file${loseBrowserMetadata ? ' after losing browser metadata' : ''}`, async ({ page }, testInfo) => {
  const archive = await ensureArchive();
  await withDestination(testInfo, 'resume-completion', 'none', async (destination) => {
    let completionRequests = 0;
    let reportProcessing = false;
    let verifiedFraction = 0.25;
    let finish!: () => void;
    const gate = new Promise<void>(resolve => { finish = resolve; });
    await page.route('**/api/portability/migration/jobs/*', async route => {
      const response = await route.fetch();
      const status = await response.json();
      if (reportProcessing && status.job.state === 'Transferring' && status.session) {
        status.progress = { phase: 'Validating',
          bytesProcessed: Math.floor(status.session.totalBytes * verifiedFraction),
          totalBytes: status.session.totalBytes, message: 'Verifying uploaded archive' };
      }
      await route.fulfill({ response, json: status });
    });
    await page.route('**/api/portability/migration/jobs/*/upload-session/complete', async route => {
      completionRequests += 1;
      if (completionRequests === 1) {
        await route.abort('connectionreset');
      } else {
        await gate;
        await route.continue();
      }
    });
    try {
      await openSettings(page, destination.baseUrl);
      const chunks = trackChunkIndexes(page);
      const jobs = trackJobIds(page);
      await selectArchive(page, archive);
      await expect(page.getByTestId('import-failed')).toBeVisible({ timeout: 180_000 });
      const jobId = jobs[0];
      const status = await (await fetch(`${destination.baseUrl}/api/portability/migration/jobs/${jobId}`)).json();
      expect(status.session.state).toBe('Receiving');
      expect(status.session.receivedChunkCount).toBe(status.session.totalChunks);

      chunks.length = 0;
      if (loseBrowserMetadata) {
        await page.route('**/api/portability/migration/active-import', async route => {
          const response = await route.fetch({ url: `${destination.baseUrl}/api/portability/migration/jobs/${jobId}` });
          await route.fulfill({ response });
        });
        await page.evaluate(() => localStorage.removeItem('nostos.library-transfer.active.v1'));
      }
      await page.reload();
      await expect(page.getByTestId('import-checking')).toBeVisible({ timeout: 60_000 });
      await expect(page.getByTestId('import-checking').getByRole('progressbar')).not.toHaveAttribute('aria-valuenow');
      const fill = page.getByTestId('import-checking').locator('.transfer-progress-fill');
      expect(await fill.evaluate(element => element.getBoundingClientRect().width)).toBeGreaterThan(0);
      reportProcessing = true;
      await expect(page.getByTestId('import-checking').getByRole('progressbar')).toHaveAttribute('aria-valuenow', '25');
      await expect(page.getByTestId('import-upload-complete')).toContainText('Upload complete');
      await expect(page.getByTestId('import-status-checked')).toContainText('Status checked at');
      await page.getByTestId('import-checking').scrollIntoViewIfNeeded();
      await testInfo.attach('processing-after-reload', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
      verifiedFraction = 0.5;
      await expect(page.getByTestId('import-checking').getByRole('progressbar')).toHaveAttribute('aria-valuenow', '50');
      const beforeReload = await navigationOrigin(page);
      finish();
      // The disposable SelfHosted fixture completes server-side; Cloud
      // confirmation is covered by the coordinator/component contract.
      await waitForReload(page, beforeReload);

      expect(completionRequests).toBe(2);
      expect(chunks).toEqual([]);
      expect(jobs).toHaveLength(1);
      await page.goto(`${destination.baseUrl}/library`);
      await assertSourceContent(destination.baseUrl);
    } finally {
      finish();
    }
  });
});
}

test('scenario d2: reload during activation reattaches and reports the outcome', async ({ page }, testInfo) => {
  const archive = await ensureArchive();
  const errors = collectErrors(page);
  await withDestination(testInfo, 'resume-activation', 'none', async (destination) => {
    // Hold the activation status responses so the cutover is still running
    // when the page reloads.
    await page.route(
      '**/api/portability/migration/jobs/*/activation',
      async (route) => {
        await new Promise((resolve) => setTimeout(resolve, 700));
        await route.continue();
      },
    );

    await openSettings(page, destination.baseUrl);
    const jobIds = trackJobIds(page);
    await selectArchive(page, archive);
    await expect(page.getByTestId('library-activation-overlay')).toBeVisible({
      timeout: 240_000,
    });

    // The claim under test is a reload while the SERVER cutover is live, not
    // only while the browser shows an outstanding activation.
    await expect
      .poll(
        async () => {
          const status = await ltGet<{ outcome?: string; state?: string }>(
            destination.baseUrl,
            `/api/portability/migration/jobs/${jobIds[0]}/activation`,
          );
          return status.outcome === 'Running' || status.outcome === 'Accepted';
        },
        { timeout: 60_000, message: 'the server cutover is running before the reload' },
      )
      .toBe(true);

    await page.reload();
    // The activation's own post-completion reload is still ahead; track it from
    // the manual reload's document so the wait is not satisfied by the manual
    // navigation itself.
    const beforeCompletionReload = await navigationOrigin(page);
    // The controller reattaches from the persisted activation on reload. The
    // tiny fixture cutover can also finish during the navigation itself; both
    // paths must report the completed library, and the reattach path must show
    // the non-dismissible overlay while it polls.
    const overlay = page.getByTestId('library-activation-overlay');
    const settled = await Promise.race([
      overlay
        .waitFor({ state: 'visible', timeout: 90_000 })
        .then(() => 'reattached' as const)
        .catch(() => null),
      page
        .getByText('The Lantern Keepers')
        .first()
        .waitFor({ state: 'visible', timeout: 90_000 })
        .then(() => 'completed' as const)
        .catch(() => null),
    ]);
    expect(settled, 'the reload either reattached to the run or observed its completion').not.toBeNull();
    if (settled === 'reattached') {
      await waitForReload(page, beforeCompletionReload);
    }

    await page.goto(`${destination.baseUrl}/library`);
    await expect(page.getByText('The Lantern Keepers').first()).toBeVisible({ timeout: 30_000 });
    await assertSourceContent(destination.baseUrl);
  });
  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
});

test('scenario e1: a non-archive file is rejected without touching the library', async ({ page }, testInfo) => {
  const errors = collectErrors(page);
  mkdirSync(TRANSFER_ARTIFACTS, { recursive: true });
  const notAnArchive = path.join(TRANSFER_ARTIFACTS, `wrong-${testInfo.project.name}.txt`);
  writeFileSync(notAnArchive, 'This is plainly not a portable Nostos archive.\n');

  const before = snapshotCounts(await librarySnapshot(source.baseUrl));
  await openSettings(page, source.baseUrl);
  await selectArchive(page, notAnArchive);

  await expect(page.getByTestId('import-failed')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('import-failed')).toContainText('Not a portable library archive');
  await expect(page.getByTestId('import-failed')).toContainText(
    'This file is not a supported Nostos portable library archive.',
  );
  await expect(page.getByTestId('library-activation-overlay')).toHaveCount(0);

  const after = snapshotCounts(await librarySnapshot(source.baseUrl));
  expect(after).toEqual(before);
  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
  expect(unexpectedConsoleErrors(errors.consoleErrors)).toEqual([]);
});

test('scenario e2: a corrupted archive is rejected and the existing library stays intact', async ({ page }, testInfo) => {
  const archive = await ensureArchive();
  const errors = collectErrors(page);
  mkdirSync(TRANSFER_ARTIFACTS, { recursive: true });
  const corrupted = path.join(TRANSFER_ARTIFACTS, `corrupted-${testInfo.project.name}.nostos`);
  copyFileSync(archive, corrupted);
  const bytes = readFileSync(corrupted);
  // Flip a byte inside the media payload: the ZIP structure and manifest stay
  // readable, so the archive reaches server-side validation and fails there.
  const position = Math.floor(bytes.length * 0.6);
  bytes[position] = bytes[position] ^ 0xff;
  writeFileSync(corrupted, bytes);

  const before = await librarySnapshot(source.baseUrl);
  await openSettings(page, source.baseUrl);
  await selectArchive(page, corrupted);

  await expect(page.getByTestId('import-failed')).toBeVisible({ timeout: 240_000 });
  await expect(page.getByTestId('library-activation-overlay')).toHaveCount(0);
  const text = await page.getByTestId('import-failed').innerText();
  expect(text.toLowerCase()).toContain('import');

  const after = await librarySnapshot(source.baseUrl);
  expect(snapshotCounts(after)).toEqual(snapshotCounts(before));
  expect(after.books.map((book) => book.id).sort()).toEqual(before.books.map((book) => book.id).sort());
  await assertSourceContent(source.baseUrl);
  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
});

test('scenario e3: cancel mid-upload cleans up and a new import can start', async ({ page }, testInfo) => {
  const archive = await ensureArchive();
  const errors = collectErrors(page);
  await withDestination(testInfo, 'failure-cancel', 'none', async (destination) => {
    await delayChunks(page, 500);
    await openSettings(page, destination.baseUrl);
    const chunkIndexes = trackChunkIndexes(page);
    const jobIds = trackJobIds(page);
    await selectArchive(page, archive);
    await expect(page.getByTestId('import-uploading')).toBeVisible({ timeout: 180_000 });
    await waitForChunkIndex(chunkIndexes, 1);

    await page.getByTestId('import-cancel').click();
    await expect(page.getByTestId('import-cancelled')).toBeVisible({ timeout: 60_000 });
    await expect(page.getByTestId('import-cancelled')).toContainText(
      'Your existing library was not changed.',
    );

    const firstJobId = jobIds[0];
    expect(firstJobId).toBeTruthy();
    const status = await (await fetch(
      `${destination.baseUrl}/api/portability/migration/jobs/${firstJobId}`,
    )).json();
    expect(status.job.state).toBe('Cancelled');
    expect(status.session?.state).toBe('Cancelled');
    const firstSessionId = status.session?.sessionId;
    expect(firstSessionId).toBeTruthy();

    // A brand-new import starts from scratch (new durable job).
    await page.getByTestId('import-close').click();
    await expect(page.getByTestId('import-idle')).toBeVisible();
    chunkIndexes.length = 0;
    await selectArchive(page, archive);
    await expect(page.getByTestId('import-uploading')).toBeVisible({ timeout: 180_000 });
    await expect.poll(() => jobIds.length, { timeout: 60_000 }).toBeGreaterThan(1);
    expect(jobIds[1]).not.toBe(firstJobId);
    const second = await (
      await fetch(`${destination.baseUrl}/api/portability/migration/jobs/${jobIds[1]}`)
    ).json();
    expect(second.session?.sessionId).toBeTruthy();
    expect(second.session?.sessionId).not.toBe(firstSessionId);

    await page.getByTestId('import-cancel').click();
    await expect(page.getByTestId('import-cancelled')).toBeVisible({ timeout: 60_000 });
  });
  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
});

test('scenario e4: a backend restart mid-upload recovers after it is back', async ({ page }, testInfo) => {
  const archive = await ensureArchive();
  const errors = collectErrors(page);
  await withDestination(testInfo, 'failure-restart', 'none', async (destination) => {
    await delayChunks(page, 700);
    await openSettings(page, destination.baseUrl);
    const chunkIndexes = trackChunkIndexes(page);
    const jobIds = trackJobIds(page);
    await selectArchive(page, archive);
    await expect(page.getByTestId('import-uploading')).toBeVisible({ timeout: 180_000 });
    await waitForChunkIndex(chunkIndexes, 1);

    const jobId = jobIds[0];
    expect(jobId).toBeTruthy();
    await expect
      .poll(
        async () => {
          const state = await (
            await fetch(
              `${destination.baseUrl}/api/portability/migration/jobs/${jobId}/upload-session`,
            )
          ).json();
          return (state.session?.receivedChunkCount as number) ?? 0;
        },
        { timeout: 60_000, message: 'the server confirmed at least one chunk before the restart' },
      )
      .toBeGreaterThan(0);
    const preRestart = await (
      await fetch(`${destination.baseUrl}/api/portability/migration/jobs/${jobId}/upload-session`)
    ).json();
    const receivedBeforeRestart = new Set<number>();
    for (const range of preRestart.receivedRanges ?? []) {
      for (let index = range.startIndex; index <= range.endIndex; index += 1) {
        receivedBeforeRestart.add(index);
      }
    }
    expect(receivedBeforeRestart.size).toBeGreaterThan(0);
    const requestsBeforeRestart = chunkIndexes.length;

    // Real crash + restart over the same disposable root.
    const { restartTransferInstance } = await import('./support/library-transfer-harness');
    restartTransferInstance(destination.name, true);

    const beforeReload = await navigationOrigin(page);
    // The client either retries transparently or surfaces a retryable failure;
    // either way the same page must recover without reselecting from zero.
    const deadline = Date.now() + 300_000;
    let recovered = false;
    while (Date.now() < deadline) {
      if (await page.getByTestId('library-activation-overlay').isVisible().catch(() => false)) {
        recovered = true;
        break;
      }
      if (await page.getByTestId('import-reselect').isVisible().catch(() => false)) {
        await selectArchive(page, archive);
      } else if (await page.getByTestId('import-failed').isVisible().catch(() => false)) {
        const action = page.getByTestId('import-failure-action');
        if (await action.isVisible().catch(() => false)) {
          await action.click();
        } else {
          await selectArchive(page, archive);
        }
      }
      await page.waitForTimeout(1_000);
    }
    expect(recovered, 'import reached activation after the backend restart').toBe(true);

    // Recovery reused the same durable job and session, and no chunk the
    // server had already received was requested again.
    const postRestart = await (
      await fetch(`${destination.baseUrl}/api/portability/migration/jobs/${jobId}/upload-session`)
    ).json();
    expect(postRestart.session.sessionId).toBe(preRestart.session.sessionId);
    const postRestartRequests = chunkIndexes.slice(requestsBeforeRestart);
    expect(postRestartRequests.filter((index) => receivedBeforeRestart.has(index))).toEqual([]);

    await waitForReload(page, beforeReload);

    await page.goto(`${destination.baseUrl}/library`);
    await expect(page.getByText('The Lantern Keepers').first()).toBeVisible({ timeout: 30_000 });
    await assertSourceContent(destination.baseUrl);
  });
  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
  // Induced connection failures legitimately log network console errors.
  expect(
    unexpectedConsoleErrors(errors.consoleErrors.filter((line) => !isExpectedRestartError(line))),
  ).toEqual([]);
});

function isExpectedRestartError(line: string): boolean {
  return /ERR_CONNECTION|Failed to load resource|NS_ERROR|Load failed|NetworkError|503|Service Unavailable/i.test(
    line,
  );
}

test('scenario e5: a second tab is blocked by the lease with no double upload', async ({
  page,
  context,
}, testInfo) => {
  const archive = await ensureArchive();
  const errors = collectErrors(page);
  await withDestination(testInfo, 'second-tab', 'none', async (destination) => {
    await delayChunks(page, 600);
    await openSettings(page, destination.baseUrl);
    const chunkIndexes = trackChunkIndexes(page);
    await selectArchive(page, archive);
    await expect(page.getByTestId('import-uploading')).toBeVisible({ timeout: 180_000 });
    await waitForChunkIndex(chunkIndexes, 1);

    const second = await context.newPage();
    const secondErrors = collectErrors(second);
    const secondChunks: number[] = [];
    second.on('request', (request) => {
      const match = /\/upload-session\/chunks\/(\d+)/.exec(request.url());
      if (match) secondChunks.push(Number(match[1]));
    });
    try {
      await second.goto(`${destination.baseUrl}/settings`);
      await expect(second.getByTestId('manage-library-open')).toBeVisible();
      await second.getByTestId('manage-library-open').click();
      await expect(second).toHaveURL(`${destination.baseUrl}/settings/library`);
      await expect(second.getByTestId('manage-library-page')).toBeVisible();
      await expect(second.getByTestId('library-transfer-card')).toBeVisible();
      await expect(second.getByTestId('transfer-other-tab')).toBeVisible({ timeout: 30_000 });
      await expect(second.getByTestId('transfer-other-tab')).toContainText(
        'An import is already in progress in another tab.',
      );
      await second.waitForTimeout(2_500);
      expect(secondChunks).toEqual([]);
      await secondErrors.settle();
      expect(secondErrors.pageErrors).toEqual([]);
    } finally {
      await second.close();
    }

    // The owning tab still owns the transfer and can cancel it cleanly.
    await page.getByTestId('import-cancel').click();
    await expect(page.getByTestId('import-cancelled')).toBeVisible({ timeout: 60_000 });
  });
  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
});

test('scenario f: replacement dialog traps focus and Escape cancels before activation', async ({ page }, testInfo) => {
  const archive = await ensureArchive();
  const errors = collectErrors(page);
  await withDestination(testInfo, 'a11y-dialog', 'b', async (destination) => {
    await openSettings(page, destination.baseUrl);
    await selectArchive(page, archive);
    await expect(page.getByTestId('import-replacement')).toBeVisible({ timeout: 240_000 });

    const dialog = page.locator('.replacement-dialog-card');
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAttribute('role', 'alertdialog');
    await expect(dialog).toHaveAttribute('aria-modal', 'true');
    await expect(dialog).toHaveAttribute('aria-labelledby', 'replacement-dialog-title');
    await expect(dialog).toHaveAttribute('aria-describedby', 'replacement-dialog-description');

    // Focus enters the modal and stays trapped through a full Tab cycle.
    await expect
      .poll(
        () =>
          page.evaluate(() => {
            const active = document.activeElement as HTMLElement | null;
            return {
              inside: active?.closest('[role="alertdialog"]') !== null,
              tag: active?.tagName ?? null,
              className: active?.className ?? null,
            };
          }),
        { timeout: 15_000 },
      )
      .toMatchObject({ inside: true });
    for (let press = 0; press < 6; press += 1) {
      await page.keyboard.press('Tab');
      const inside = await page.evaluate(
        () => document.activeElement?.closest('[role="alertdialog"]') !== null,
      );
      expect(inside, `focus stayed inside the dialog after Tab ${press + 1}`).toBe(true);
    }

    // Escape before activation dismisses the decision and cancels the transfer
    // without mutating the destination.
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('import-cancelled')).toBeVisible({ timeout: 60_000 });
    await expect(dialog).toHaveCount(0);

    const snapshot = await librarySnapshot(destination.baseUrl);
    expect(snapshot.books.map((book) => book.title).sort()).toEqual([
      'Harbour Ledger',
      'Second Almanac',
    ]);
    expect(snapshotCounts(snapshot).notes).toBe(1);
  });
  await errors.settle();
  expect(unexpectedPageErrors(errors.pageErrors)).toEqual([]);
});
