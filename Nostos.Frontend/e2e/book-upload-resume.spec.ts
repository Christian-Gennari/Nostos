/**
 * Resumable book-file upload acceptance (#1266 Phase A).
 *
 * A real 200 MiB book file is sent through the production Angular build to a
 * real SelfHosted backend. The browser's upload pipe is slowed with CDP so a
 * cut can land mid-part, then taken offline twice: once during the first run
 * and once during the first resume. Each failure must leave the server's
 * receipts usable, so the final resume sends fewer parts than the whole file
 * while the stored bytes still hash to the source.
 *
 * The backend log must also carry the truncated-part warning that records how
 * many bytes actually arrived, which is the operational half of the fix.
 */
import { expect, test, type CDPSession, type Response } from '@playwright/test';
import { createHash } from 'node:crypto';
import {
  closeSync,
  createReadStream,
  existsSync,
  mkdirSync,
  openSync,
  readFileSync,
  statSync,
  writeSync,
} from 'node:fs';
import { request as httpRequest } from 'node:http';
import path from 'node:path';

import { apiGet, apiPost, loadFixture } from './support/fixture';

const SOURCE_BYTES = 200 * 1024 * 1024;
const SOURCE_PATH = '/tmp/claude-1000/oc1266/book-upload-200mb.txt';
const UPLOAD_BYTES_PER_SECOND = 3 * 1024 * 1024;

interface UploadStatus {
  sessionId: string;
  state: string;
  totalBytes: number;
  totalChunks: number;
  receivedParts: number[];
}

interface BookListItem {
  id: string;
  title: string;
  hasFile: boolean;
}

function ensureSourceFile(): void {
  if (existsSync(SOURCE_PATH) && statSync(SOURCE_PATH).size === SOURCE_BYTES) return;

  mkdirSync(path.dirname(SOURCE_PATH), { recursive: true });
  const block = Buffer.alloc(1024 * 1024);
  let state = 1266;
  for (let index = 0; index < block.length; index += 1) {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    block[index] = state >>> 24;
  }

  const handle = openSync(SOURCE_PATH, 'w');
  try {
    for (let written = 0; written < SOURCE_BYTES; written += block.length) {
      // Vary every block so a resume cannot accidentally line up identical
      // slices; the bytes still need not be compressible.
      block[0] = (block[0] + 1) & 0xff;
      const length = Math.min(block.length, SOURCE_BYTES - written);
      const count = writeSync(handle, block, 0, length);
      if (count !== length) throw new Error('Short write while generating the E2E file.');
    }
  } finally {
    closeSync(handle);
  }
}

function sha256File(filePath: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256');
    createReadStream(filePath)
      .on('data', (chunk) => hash.update(chunk))
      .on('error', reject)
      .on('end', () => resolve(hash.digest('hex')));
  });
}

async function waitForValue<T>(
  read: () => Promise<T | null>,
  timeoutMs: number,
  intervalMs: number,
  description: string,
): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  let lastError: unknown = null;
  while (Date.now() < deadline) {
    try {
      const value = await read();
      if (value !== null) return value;
    } catch (error) {
      lastError = error;
    }
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
  }
  throw new Error(
    `Timed out waiting for ${description}` +
      (lastError ? `: ${String(lastError)}` : ''),
  );
}

async function uploadStatus(baseUrl: string, bookId: string): Promise<UploadStatus | null> {
  const response = await fetch(`${baseUrl}/api/books/${bookId}/file/uploads`);
  if (response.status === 204) return null;
  if (!response.ok) {
    throw new Error(`upload status -> ${response.status}: ${await response.text()}`);
  }
  return (await response.json()) as UploadStatus;
}

async function uploadStatusById(
  baseUrl: string,
  bookId: string,
  sessionId: string,
): Promise<UploadStatus | null> {
  const response = await fetch(
    `${baseUrl}/api/books/${bookId}/file/uploads/${sessionId}`,
  );
  if (response.status === 404) return null;
  if (!response.ok) {
    throw new Error(`upload status -> ${response.status}: ${await response.text()}`);
  }
  return (await response.json()) as UploadStatus;
}

async function findBook(
  baseUrl: string,
  title: string,
): Promise<BookListItem | null> {
  const list = await apiGet<{ items: BookListItem[] }>(
    baseUrl,
    `/api/books?search=${encodeURIComponent(title)}&pageSize=10`,
  );
  return list.items.find((item) => item.title === title) ?? null;
}

async function setNetwork(cdp: CDPSession, offline: boolean): Promise<void> {
  await cdp.send('Network.emulateNetworkConditions', {
    offline,
    latency: offline ? 0 : 20,
    downloadThroughput: offline ? 0 : 20 * 1024 * 1024,
    uploadThroughput: offline ? 0 : UPLOAD_BYTES_PER_SECOND,
  });
}

function partIndex(url: string): number | null {
  const match = /\/parts\/(\d+)$/.exec(new URL(url).pathname);
  return match ? Number(match[1]) : null;
}

/**
 * CDP offline stops new requests but can leave an upload already on the wire
 * running to completion, so the two browser cuts prove resume behaviour, not
 * a truncated body. This sends a contract-valid part and destroys the socket
 * halfway through, which is the operational case the warning exists for.
 */
async function sendTruncatedPart(
  baseUrl: string,
  bookId: string,
  sessionId: string,
  body: Buffer,
): Promise<void> {
  const url = new URL(
    `${baseUrl}/api/books/${bookId}/file/uploads/${sessionId}/parts/0`,
  );
  await new Promise<void>((resolve) => {
    const request = httpRequest({
      hostname: url.hostname,
      port: Number(url.port),
      path: url.pathname,
      method: 'PUT',
      headers: {
        'Content-Type': 'application/octet-stream',
        'Content-Length': body.length,
        'Content-Range': `bytes 0-${body.length - 1}/${body.length}`,
        'X-Nostos-Chunk-SHA256': createHash('sha256').update(body).digest('hex'),
      },
    });
    request.on('error', () => resolve()); // The reset we cause is expected.
    request.write(body.subarray(0, Math.floor(body.length / 4)), () => {
      setTimeout(() => {
        request.destroy();
        resolve();
      }, 200);
    });
  });
}

async function proveTruncatedPartWarning(
  baseUrl: string,
): Promise<{ bookId: string; sessionId: string }> {
  const title = `Truncated warning ${Date.now()}`;
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
    type: 'ebook',
    title,
  });
  const body = Buffer.alloc(1024 * 1024, 7);
  const sha256 = createHash('sha256').update(body).digest('hex');
  const session = await apiPost<{ sessionId: string }>(
    baseUrl,
    `/api/books/${book.id}/file/uploads`,
    {
      totalBytes: body.length,
      fileName: 'truncated.txt',
      contentType: 'text/plain',
      sha256,
    },
  );
  await sendTruncatedPart(baseUrl, book.id, session.sessionId, body);
  return { bookId: book.id, sessionId: session.sessionId };
}

test.describe('resumable book upload (#1266)', () => {
  test.use({ serviceWorkers: 'block' });

  test('survives two dropped connections and publishes the identical file', async ({
    page,
    context,
  }) => {
    test.setTimeout(600_000);
    const { baseUrl, tempDir } = loadFixture();
    ensureSourceFile();
    const sourceSha = await sha256File(SOURCE_PATH);

    const capabilities = await apiGet<{ supportsResumableBookUploads?: boolean }>(
      baseUrl,
      '/api/runtime/capabilities',
    );
    expect(capabilities.supportsResumableBookUploads).toBe(true);

    const title = `Resumable E2E ${Date.now()}`;
    const partRequests: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'PUT' && partIndex(request.url()) !== null) {
        partRequests.push(request.url());
      }
    });

    const capabilitiesResponse = page.waitForResponse(
      (response) =>
        response.url().includes('/api/runtime/capabilities') && response.ok(),
      { timeout: 30_000 },
    );
    await page.goto(`${baseUrl}/library`, { waitUntil: 'domcontentloaded' });
    // Wait for the capability read that decides the upload path.
    await capabilitiesResponse;

    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    await setNetwork(cdp, false);

    try {
      await page.getByRole('button', { name: 'Add book' }).click();
      const dialog = page.getByRole('dialog').first();
      await expect(dialog).toBeVisible();
      // Add book opens on catalogue search; the local-file form is the manual
      // path, which still accepts a file in its format section.
      await dialog.getByRole('button', { name: 'Add manually' }).click();
      await dialog.locator('input[name="title"]').fill(title);
      await dialog
        .locator('input[type="file"][accept*=".epub"]')
        .first()
        .setInputFiles(SOURCE_PATH);

      const firstPartRequest = page.waitForRequest(
        (request) =>
          request.method() === 'PUT' && partIndex(request.url()) !== null,
        { timeout: 180_000 },
      );
      await dialog.getByRole('button', { name: 'Add to Library' }).click();
      await firstPartRequest;

      // The book row exists before its file does; hold the session id so the
      // completed session stays readable after active-session discovery ends.
      const book = await waitForValue(
        () => findBook(baseUrl, title),
        120_000,
        500,
        'the created book row',
      );
      const firstStatus = await waitForValue(
        () => uploadStatus(baseUrl, book.id),
        120_000,
        500,
        'the first upload session',
      );

      // Cut the first run while the body is still in flight. Hold it down long
      // enough for the engine's four attempts to run out and show the copy.
      await page.waitForTimeout(1_200);
      await setNetwork(cdp, true);
      await page.waitForTimeout(12_000);
      await setNetwork(cdp, false);
      await expect(dialog.getByText(/connection was interrupted/i)).toBeVisible({
        timeout: 30_000,
      });

      // Resume 1: the server should answer with the receipts it kept.
      const successfulResumedParts = new Set<number>();
      const onResponse = (response: Response) => {
        if (response.request().method() !== 'PUT') return;
        const index = partIndex(response.url());
        if (index !== null && response.status() === 200) {
          successfulResumedParts.add(index);
        }
      };
      page.on('response', onResponse);

      await dialog.getByRole('button', { name: 'Try again' }).first().click();
      await expect
        .poll(() => successfulResumedParts.size, { timeout: 120_000 })
        .toBeGreaterThanOrEqual(2);

      // Cut the resumed run too, so the final attempt is a genuine second
      // resume from durable receipts.
      await setNetwork(cdp, true);
      await page.waitForTimeout(12_000);
      await setNetwork(cdp, false);
      await expect(dialog.getByText(/connection was interrupted/i)).toBeVisible({
        timeout: 30_000,
      });
      page.off('response', onResponse);

      // Final resume: no more cuts. Count only the part requests this attempt
      // makes, then compare with the whole-file part count.
      const partCountBeforeFinal = partRequests.length;
      await dialog.getByRole('button', { name: 'Try again' }).first().click();

      const completedBook = await waitForValue(
        async () => {
          const match = await findBook(baseUrl, title);
          return match?.hasFile === true ? match : null;
        },
        300_000,
        1_000,
        'the published book file',
      );
      const completed = await waitForValue(
        async () => {
          const current = await uploadStatusById(
            baseUrl,
            completedBook.id,
            firstStatus.sessionId,
          );
          return current?.state === 'Complete' ? current : null;
        },
        120_000,
        500,
        'the completed upload session',
      );

      const finalAttemptParts = partRequests.length - partCountBeforeFinal;
      expect(completed.receivedParts).toHaveLength(completed.totalChunks);
      expect(completed.totalBytes).toBe(SOURCE_BYTES);
      expect(finalAttemptParts).toBeGreaterThan(0);
      expect(finalAttemptParts).toBeLessThan(completed.totalChunks);

      const storedPath = path.join(
        tempDir,
        'Storage',
        'books',
        completedBook.id,
        'book.txt',
      );
      expect(await sha256File(storedPath)).toBe(sourceSha);

      const truncated = await proveTruncatedPartWarning(baseUrl);
      const logPath = path.join(tempDir, 'backend.log');
      const warning = await waitForValue(
        () => {
          const log = readFileSync(logPath, 'utf8');
          const match = new RegExp(
            `Book upload part 0 for book ${truncated.bookId} was interrupted after (\\d+) of (\\d+) bytes\\.`,
          ).exec(log);
          return match
            ? { received: Number(match[1]), declared: Number(match[2]) }
            : null;
        },
        30_000,
        250,
        `the truncated-part warning for ${truncated.bookId}`,
      );
      expect(warning.received).toBeGreaterThan(0);
      expect(warning.received).toBeLessThan(warning.declared);

      await fetch(`${baseUrl}/api/books/${truncated.bookId}`, {
        method: 'DELETE',
      }).catch(() => undefined);
    } finally {
      await setNetwork(cdp, false).catch(() => undefined);
      await cdp.detach().catch(() => undefined);
    }
  });
});
