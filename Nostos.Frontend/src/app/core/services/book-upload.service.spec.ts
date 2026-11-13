import { TestBed } from '@angular/core/testing';
import { HttpRequest, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';

import { BookUploadStatusDto } from '../dtos/book-upload.dtos';
import { DeploymentCapabilities } from '../dtos/deployment-capabilities.dtos';
import { DeploymentCapabilitiesService } from './deployment-capabilities.service';
import { FileDigestService } from '../uploads/file-digest.service';
import { TransferCancelledError } from '../uploads/upload.models';
import { describeUploadFailure } from '../uploads/upload-error-copy';
import { BookUploadService } from './book-upload.service';

const BOOK_ID = 'book-1';
const SESSION_ID = 'session-1';

function status(overrides: Partial<BookUploadStatusDto> = {}): BookUploadStatusDto {
  return {
    sessionId: SESSION_ID,
    bookId: BOOK_ID,
    state: 'Receiving',
    totalBytes: 10,
    chunkSize: 4,
    totalChunks: 3,
    fileName: 'book.txt',
    fileSha256: 'a'.repeat(64),
    receivedParts: [],
    receivedBytes: 0,
    createdAtUtc: new Date(0).toISOString(),
    expiresAtUtc: new Date(60_000).toISOString(),
    completedAtUtc: null,
    failureCode: null,
    ...overrides,
  };
}

async function waitForRequests(
  http: HttpTestingController,
  predicate: (request: HttpRequest<unknown>) => boolean,
  count = 1,
): Promise<TestRequest[]> {
  let found: TestRequest[] = [];
  await vi.waitFor(() => {
    found = http.match(predicate);
    expect(found.length).toBe(count);
  });
  return found;
}

function partPredicate(request: HttpRequest<unknown>): boolean {
  return request.url.includes('/parts/');
}

describe('BookUploadService', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('hashes incrementally, sends only missing parts and completes', async () => {
    const service = TestBed.inject(BookUploadService);
    const digest = TestBed.inject(FileDigestService);
    const file = new File(['abcdefghij'], 'book.txt', { type: 'text/plain' });
    const wholeFileSha = await digest.sha256(file);
    const phases: string[] = [];

    const upload = service.upload(BOOK_ID, file, {
      onProgress: (progress) => phases.push(progress.phase),
    });

    const create = await waitForRequests(
      http,
      (request) => request.method === 'POST' && request.url.endsWith('/file/uploads'),
    );
    expect(create[0].request.body).toMatchObject({
      totalBytes: 10,
      fileName: 'book.txt',
      contentType: 'text/plain',
      sha256: wholeFileSha,
    });
    // Part 1 is already receipted: a resumed upload must not send it again.
    create[0].flush(status({ receivedParts: [1], receivedBytes: 4 }));

    const parts = await waitForRequests(http, partPredicate, 2);
    expect(parts.map((part) => part.request.url).sort()).toEqual([
      `/api/books/${BOOK_ID}/file/uploads/${SESSION_ID}/parts/0`,
      `/api/books/${BOOK_ID}/file/uploads/${SESSION_ID}/parts/2`,
    ]);

    const part0 = parts.find((part) => part.request.url.endsWith('/parts/0'))!;
    expect(part0.request.headers.get('Content-Range')).toBe('bytes 0-3/10');
    expect(part0.request.headers.get('X-Nostos-Chunk-SHA256')).toMatch(/^[0-9a-f]{64}$/);
    expect(part0.request.body).toBeInstanceOf(Blob);

    for (const part of parts) {
      part.flush({
        sessionId: SESSION_ID,
        partIndex: Number(part.request.url.split('/').pop()),
        alreadyPresent: false,
      });
    }

    const complete = await waitForRequests(
      http,
      (request) => request.method === 'POST' && request.url.endsWith('/complete'),
    );
    complete[0].flush(
      status({ state: 'Complete', receivedParts: [0, 1, 2], receivedBytes: 10 }),
    );

    await expect(upload).resolves.toMatchObject({ state: 'Complete' });
    expect(phases).toContain('preparing');
    expect(phases).toContain('uploading');
    expect(phases).toContain('finishing');
  });

  it('stops a run, aborts its in-flight parts and pauses the session', async () => {
    const service = TestBed.inject(BookUploadService);
    const file = new File(['abcdefghij'], 'book.txt', { type: 'text/plain' });

    const upload = service.upload(BOOK_ID, file);
    // The rejection is asserted below; attach now so it is never unhandled.
    const settled = upload.catch((error: unknown) => error);

    const create = await waitForRequests(
      http,
      (request) => request.method === 'POST' && request.url.endsWith('/file/uploads'),
    );
    create[0].flush(status());

    // Two parts are on the wire when the stop lands.
    await waitForRequests(http, partPredicate, 2);

    const stopped = service.stop(BOOK_ID);

    const pause = await waitForRequests(
      http,
      (request) =>
        request.method === 'POST' &&
        request.url.endsWith(`/${SESSION_ID}/pause`),
    );
    pause[0].flush(status({ state: 'Paused' }));

    await expect(settled).resolves.toBeInstanceOf(TransferCancelledError);
    await expect(stopped).resolves.toBeUndefined();

    // The run is gone: a later stop is a no-op, not a second pause.
    await expect(service.stop(BOOK_ID)).resolves.toBeUndefined();
  });

  it('completes a session whose every part is already receipted', async () => {
    const service = TestBed.inject(BookUploadService);
    const file = new File(['abcdefghij'], 'book.txt', { type: 'text/plain' });

    const upload = service.upload(BOOK_ID, file);

    const create = await waitForRequests(
      http,
      (request) => request.method === 'POST' && request.url.endsWith('/file/uploads'),
    );
    create[0].flush(status({ receivedParts: [0, 1, 2], receivedBytes: 10 }));

    const complete = await waitForRequests(
      http,
      (request) => request.method === 'POST' && request.url.endsWith('/complete'),
    );
    complete[0].flush(
      status({ state: 'Complete', receivedParts: [0, 1, 2], receivedBytes: 10 }),
    );

    await expect(upload).resolves.toMatchObject({ state: 'Complete' });
    expect(http.match(partPredicate)).toHaveLength(0);
  });

  it('rejects an empty or oversized file before hashing it', async () => {
    const service = TestBed.inject(BookUploadService);

    await expect(
      service.upload(BOOK_ID, new File([], 'empty.txt', { type: 'text/plain' })),
    ).rejects.toMatchObject({ code: 'book_upload_invalid_request' });

    const huge = new File(['x'], 'huge.txt', { type: 'text/plain' });
    Object.defineProperty(huge, 'size', { value: 4 * 1024 * 1024 * 1024 + 1 });
    await expect(service.upload(BOOK_ID, huge)).rejects.toMatchObject({
      code: 'book_upload_invalid_request',
      status: 413,
    });
  });

  it('joins the run already in flight for the same book', async () => {
    const service = TestBed.inject(BookUploadService);
    const file = new File(['abcdefghij'], 'book.txt', { type: 'text/plain' });

    const first = service.upload(BOOK_ID, file);
    const second = service.upload(BOOK_ID, file);

    const create = await waitForRequests(
      http,
      (request) => request.method === 'POST' && request.url.endsWith('/file/uploads'),
    );
    create[0].flush(status({ receivedParts: [0, 1, 2], receivedBytes: 10 }));

    const complete = await waitForRequests(
      http,
      (request) => request.method === 'POST' && request.url.endsWith('/complete'),
    );
    complete[0].flush(
      status({ state: 'Complete', receivedParts: [0, 1, 2], receivedBytes: 10 }),
    );

    await expect(first).resolves.toMatchObject({ state: 'Complete' });
    await expect(second).resolves.toMatchObject({ state: 'Complete' });
  });

  it('reads the capability synchronously and fails closed when unknown', () => {
    const service = TestBed.inject(BookUploadService);
    const capabilities = TestBed.inject(DeploymentCapabilitiesService);

    expect(service.supportsResumable()).toBe(false);

    vi.spyOn(capabilities, 'peek').mockReturnValue({
      deploymentMode: 'SelfHosted',
      supportsResumableBookUploads: true,
    } as DeploymentCapabilities);
    expect(service.supportsResumable()).toBe(true);

    expect(describeUploadFailure(new Error('odd'))).toContain(
      'connection was interrupted',
    );
  });
});
