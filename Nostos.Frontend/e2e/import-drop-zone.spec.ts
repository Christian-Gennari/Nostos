/**
 * One drop zone for every import file (issue #1526).
 *
 * Every source is dropped on Settings → Import exactly as a person would —
 * the real fixture files from `Nostos.Backend.Tests/Fixtures/` — with no
 * source chosen first. The drop must open the preview of the right flow, and
 * the import must finish. A `.nostos` archive must reach the whole-library
 * import, and an unrecognised file must list the supported sources.
 */
import { randomUUID } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';

import { loadFixture } from './support/fixture';

const FIXTURES = path.resolve(__dirname, '../../Nostos.Backend.Tests/Fixtures');

interface Upload {
  name: string;
  path: string;
}

const upload = (relative: string, name = path.basename(relative)): Upload => ({
  name,
  path: path.join(FIXTURES, relative),
});

const Kobo = upload('kobo/fw-4.38.23552/KoboReader.sqlite');
const KOReader = upload('koreader/2026-09-epub/metadata.lua');
const Kindle = upload('kindle/2024-en/My Clippings.txt');
const AppleAnnotations = upload('applebooks/macos-unknown-2022-09/AEAnnotation_v10312011_1727_local.sqlite');
const AppleLibrary = upload('applebooks/macos-unknown-2022-09/BKLibrary-1-091020131601.sqlite');
const Readwise = upload('readwise/2026-10/readwise-export.csv');
const Goodreads = upload('goodreads/goodreads_library_export.csv');
const StoryGraph = upload('storygraph/storygraph_export.csv');

function openSettingsImport(page: Page): Promise<void> {
  return page.goto(`${loadFixture().baseUrl}/settings?section=import`).then(() => undefined);
}

/** A DataTransfer built in the page, because a File cannot cross the bridge. */
async function drop(page: Page, files: Upload[]): Promise<void> {
  const handle = await page.evaluateHandle((uploads) => {
    const transfer = new DataTransfer();
    for (const file of uploads) {
      const binary = atob(file.base64);
      const bytes = new Uint8Array(binary.length);
      for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
      transfer.items.add(new File([bytes], file.name));
    }
    return transfer;
  }, files.map((file) => ({ name: file.name, base64: readFileSync(file.path).toString('base64') })));

  await page.dispatchEvent('[data-testid="import-drop-zone-button"]', 'drop', { dataTransfer: handle });
}

/**
 * Drops a file the browser reads from disk instead of one whose bytes are
 * handed to the page.
 *
 * `drop()` passes each file in as base64, which is right for the fixture
 * exports (a few hundred kilobytes) and wrong for the whole-library archive:
 * `book-upload-resume` puts a 200 MB book into the shared fixture first, so the
 * export is a 210 MB copy of it and the base64 string alone exhausted the
 * renderer. A detached input is only a way to get a real `File` into the
 * DataTransfer the drop needs — the app never sees it.
 */
async function dropFromDisk(page: Page, filePath: string): Promise<void> {
  const source = await page.evaluateHandle(() => {
    const input = document.createElement('input');
    input.type = 'file';
    input.id = 'e2e-drop-source';
    document.body.append(input);
    return input;
  });
  await source.asElement()!.setInputFiles(filePath);
  const transfer = await page.evaluateHandle(() => {
    const input = document.querySelector<HTMLInputElement>('#e2e-drop-source');
    const data = new DataTransfer();
    for (const file of Array.from(input?.files ?? [])) data.items.add(file);
    input?.remove();
    return data;
  });
  await page.dispatchEvent('[data-testid="import-drop-zone-button"]', 'drop', { dataTransfer: transfer });
}

/**
 * Answers every row the review asks about — a suggested match, or a book that
 * is not in the library yet — until nothing is asking.
 *
 * A decision only leaves the screen when Angular renders it, so each click is
 * followed by the rendered count dropping. A count taken straight after a click
 * can still list the button that is being removed; the click that follows it
 * then resolves against nothing and waits until the action times out.
 */
async function answerHighlightReview(page: Page): Promise<void> {
  const actions = page.locator(
    '[data-testid="highlight-confirm"], [data-testid="highlight-add"]',
  );
  for (;;) {
    const pending = await actions.count();
    if (pending === 0) return;
    await actions.first().click();
    await expect(actions).toHaveCount(pending - 1);
  }
}

/**
 * Every row the library does not know is added; a resemblance is confirmed.
 * The import then runs for real and its result is asserted.
 */
async function importHighlights(page: Page, files: Upload[]): Promise<void> {
  await drop(page, files);
  await expect(page.getByTestId('highlight-review-row').first()).toBeVisible({ timeout: 30_000 });

  // A suggested match is a question; say yes so the row has a destination, and
  // add every book that is still left out.
  await answerHighlightReview(page);

  await page.getByTestId('highlight-import-commit').click();
  await expect(page.getByTestId('highlight-import-summary')).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('highlight-import-summary')).toContainText(
    /imported|Already up to date/,
  );
  await page.getByRole('button', { name: 'Done' }).click();
  await expect(page.getByTestId('highlight-import-summary')).toBeHidden();
}

async function importHistory(page: Page, file: Upload): Promise<void> {
  await drop(page, [file]);
  await expect(page.getByTestId('reading-history-counts')).toBeVisible({ timeout: 30_000 });

  // Confirming a suggestion removes that row's button when it renders, so wait
  // for the count to drop before asking the next question. The remaining
  // books then go in together.
  const confirms = page.getByTestId('reading-history-confirm');
  for (;;) {
    const pending = await confirms.count();
    if (pending === 0) break;
    await confirms.first().click();
    await expect(confirms).toHaveCount(pending - 1);
  }
  const addAll = page.getByTestId('reading-history-add-all');
  if (await addAll.count()) {
    await addAll.click();
    await expect(addAll).toHaveCount(0);
  }

  await page.getByTestId('reading-history-commit').click();
  await expect(page.getByTestId('reading-history-summary')).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('reading-history-summary')).toContainText(
    /brought in|Already up to date/,
  );
  await page.getByRole('button', { name: 'Done' }).click();
  await expect(page.getByTestId('reading-history-summary')).toBeHidden();
}

test.describe('one import drop zone', () => {
  test.beforeEach(async ({ page }) => {
    await openSettingsImport(page);
    await expect(page.getByTestId('import-drop-zone-button')).toBeVisible();
  });

  test('is a button and shows the drag-over state', async ({ page }) => {
    const zone = page.getByTestId('import-drop-zone-button');
    await expect(zone).toHaveAttribute('type', 'button');
    await expect(zone).toContainText('Drop an export here, or choose a file');

    const handle = await page.evaluateHandle(() => {
      const transfer = new DataTransfer();
      transfer.items.add(new File(['return { annotations = {} }'], 'metadata.lua'));
      return transfer;
    });
    await page.dispatchEvent('[data-testid="import-drop-zone-button"]', 'dragenter', {
      dataTransfer: handle,
    });
    await expect(zone).toHaveClass(/is-dragging/);
    await page.dispatchEvent('[data-testid="import-drop-zone-button"]', 'dragleave', {
      dataTransfer: handle,
    });
    await expect(zone).not.toHaveClass(/is-dragging/);
  });

  test('imports every highlight source with no source chosen first', async ({ page }) => {
    await importHighlights(page, [Kobo]);
    await importHighlights(page, [KOReader]);
    await importHighlights(page, [Kindle]);
    await importHighlights(page, [AppleAnnotations, AppleLibrary]);
    await importHighlights(page, [Readwise]);
  });

  test('imports a Goodreads and a StoryGraph reading history', async ({ page }) => {
    await importHistory(page, Goodreads);
    await importHistory(page, StoryGraph);
  });

  test('says which files are supported and links to their help for an unknown file', async ({
    page,
  }) => {
    await drop(page, [upload('README.md', 'notes.md')]);
    await expect(page.getByTestId('import-drop-zone-notice')).toBeVisible({ timeout: 30_000 });
    const notice = page.getByTestId('import-drop-zone-notice');
    await expect(notice).toContainText('Nostos can import these files');
    for (const source of ['kobo', 'koreader', 'kindle', 'applebooks', 'readwise', 'goodreads', 'storygraph']) {
      await expect(notice.getByTestId(`import-supported-${source}`)).toBeVisible();
    }
    await notice.getByTestId('import-supported-goodreads').click();
    await expect(page.getByTestId('import-help-goodreads').locator('[aria-expanded]')).toHaveAttribute(
      'aria-expanded',
      'true',
    );
  });

  test('the command palette opens the same drop zone', async ({ page }) => {
    // Ctrl+Shift+K is the app's other chord: plain Ctrl+K collides with the
    // browser's own search in some setups.
    await page.keyboard.press('Control+Shift+k');
    const palette = page.getByTestId('command-palette');
    await expect(palette).toBeVisible();
    await page.getByTestId('palette-input').fill('import');
    await page.getByTestId('palette-item-action').filter({ hasText: 'Import highlights' }).click();
    // The Settings → Import page stays mounted behind the dialog and carries a
    // drop zone of its own, so this assertion belongs to the dialog: an
    // unscoped one matches both the moment the dialog has rendered.
    const dialog = page.getByRole('dialog', { name: 'Import highlights' });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('heading', { name: 'Import highlights' })).toBeVisible();
    await expect(dialog.getByTestId('import-drop-zone-button')).toBeVisible();
  });

  test('hands a dropped .nostos archive to the whole-library import', async ({ page }) => {
    const baseUrl = loadFixture().baseUrl;
    const created = await fetch(`${baseUrl}/api/portability/migration/jobs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ direction: 'Export', idempotencyKey: randomUUID() }),
    });
    const createdBody = await created.text();
    expect(created.ok, createdBody).toBe(true);
    const { job } = JSON.parse(createdBody) as { job: { id: string; state: string } };

    await expect
      .poll(
        async () => {
          const status = await fetch(`${baseUrl}/api/portability/migration/jobs/${job.id}`);
          if (!status.ok) return `HTTP ${status.status}`;
          return ((await status.json()) as { job: { state: string } }).job.state;
        },
        { timeout: 120_000, intervals: [500, 1000, 2000] },
      )
      .toBe('Completed');

    const download = await fetch(`${baseUrl}/api/portability/migration/jobs/${job.id}/export-download`);
    expect(download.ok).toBe(true);
    const archive = test.info().outputPath('import-drop-zone.nostos');
    writeFileSync(archive, Buffer.from(await download.arrayBuffer()));

    await dropFromDisk(page, archive);

    await expect(page).toHaveURL(/\/settings\/library\?action=import/, { timeout: 30_000 });
    await expect(
      page
        .locator(
          '[data-testid="import-inspecting"], [data-testid="import-preflighting"], ' +
            '[data-testid="import-uploading"], [data-testid="import-replacement"]',
        )
        .first(),
    ).toBeVisible({ timeout: 90_000 });

    const cancel = page.getByTestId('import-cancel');
    if (await cancel.count()) await cancel.click();
  });
});
