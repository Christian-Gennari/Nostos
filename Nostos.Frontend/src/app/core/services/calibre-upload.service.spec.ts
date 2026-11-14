import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { CalibreUploadService } from './calibre-upload.service';
import type { CalibreUploadStatus } from './calibre-import.service';
import { FileDigestService } from '../uploads/file-digest.service';
import type { FileDigest } from '../uploads/upload.models';
import { TransferCancelledError } from '../uploads/upload.models';
import { UploadFailure } from '../uploads/upload-error';

const SHA = 'f'.repeat(64);
const CHUNK_SHA = 'a'.repeat(64);

function uploadStatus(overrides: Partial<CalibreUploadStatus> = {}): CalibreUploadStatus {
  return {
    sessionId: 's1',
    state: 'receiving',
    fileName: 'library.zip',
    totalBytes: 8,
    partBytes: 4,
    totalParts: 2,
    receivedParts: [],
    receivedBytes: 0,
    createdAtUtc: '2026-08-01T08:00:00Z',
    expiresAtUtc: '2026-08-02T08:00:00Z',
    maxLibraryBytes: 1_000_000,
    maxExtractedBytes: 4_000_000,
    maxFiles: 10_000,
    books: null,
    stagedBytes: null,
    failureCode: null,
    failureMessage: null,
    ...overrides,
  };
}

describe('CalibreUploadService', () => {
  let service: CalibreUploadService;
  let http: HttpTestingController;
  let digest: FileDigest;

  beforeEach(() => {
    digest = {
      sha256: vi.fn().mockResolvedValue(SHA),
      sha256Chunk: vi.fn().mockResolvedValue(CHUNK_SHA),
      fingerprint: vi.fn().mockResolvedValue('nostos-fp-v1:test'),
    };
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: FileDigestService, useValue: digest },
      ],
    });
    service = TestBed.inject(CalibreUploadService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Lets the upload's awaited hashing and part promises reach the HTTP layer. */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  it.each([
    ['an empty file', new File([], 'library.zip')],
    ['a nameless file', new File(['x'], '')],
  ])('refuses %s before hashing or sending anything', async (_case, file) => {
    await expect(service.upload(file)).rejects.toMatchObject({
      code: 'calibre_upload_invalid_request',
      status: 400,
    });

    expect(digest.sha256).not.toHaveBeenCalled();
    http.expectNone(() => true);
  });

  it('refuses a library larger than the server allows, naming both sizes', async () => {
    const file = new File(['0123456789'], 'library.zip');

    const failure = await service.upload(file, { maxBytes: 4 }).catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(UploadFailure);
    expect(failure).toMatchObject({ code: 'calibre_upload_too_large', status: 413 });
    expect((failure as UploadFailure).message).toContain('10 B');
    expect((failure as UploadFailure).message).toContain('the limit is 4 B');
    expect(digest.sha256).not.toHaveBeenCalled();
    http.expectNone(() => true);
  });

  it('stops before hashing when the caller has already cancelled', async () => {
    const controller = new AbortController();
    controller.abort();

    await expect(
      service.upload(new File(['01234567'], 'library.zip'), { signal: controller.signal }),
    ).rejects.toBeInstanceOf(TransferCancelledError);

    expect(digest.sha256).not.toHaveBeenCalled();
    http.expectNone(() => true);
  });

  it('stops when the caller cancels while the file is hashed', async () => {
    (digest.sha256 as ReturnType<typeof vi.fn>).mockRejectedValue(
      new TransferCancelledError('The upload was cancelled.'),
    );

    await expect(service.upload(new File(['01234567'], 'library.zip'))).rejects.toBeInstanceOf(
      TransferCancelledError,
    );
    http.expectNone(() => true);
  });

  it('returns a library the server has already staged without sending parts', async () => {
    const progress: string[] = [];
    const staged = uploadStatus({ state: 'staged', books: 12, stagedBytes: 8, receivedParts: [0, 1] });
    const pending = service.upload(new File(['01234567'], 'library.zip'), {
      onProgress: (frame) => progress.push(frame.phase),
    });
    await settle();

    const create = http.expectOne('/api/calibre/upload');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({ totalBytes: 8, fileName: 'library.zip', sha256: SHA });
    create.flush(staged);

    await expect(pending).resolves.toEqual(staged);
    http.expectNone((request) => request.url.includes('/parts/'));
    expect(progress).toEqual(['preparing', 'finishing']);
  });

  it('sends the missing parts, then finalises the session', async () => {
    const progress: Array<{ phase: string; percent: number }> = [];
    const file = new File(['01234567'], 'library.zip');
    const pending = service.upload(file, {
      onProgress: (frame) => progress.push({ phase: frame.phase, percent: frame.percent }),
    });
    await settle();

    const create = http.expectOne('/api/calibre/upload');
    create.flush(uploadStatus({ receivedParts: [0], receivedBytes: 4 }));
    await settle();

    // Part 0 is receipted; only part 1 travels, with its own range and digest.
    const part = http.expectOne('/api/calibre/upload/s1/parts/1');
    expect(part.request.method).toBe('PUT');
    expect(part.request.headers.get('Content-Range')).toBe('bytes 4-7/8');
    expect(part.request.headers.get('X-Nostos-Chunk-SHA256')).toBe(CHUNK_SHA);
    expect((part.request.body as Blob).size).toBe(4);
    part.flush({ sessionId: 's1', partIndex: 1, alreadyPresent: false });
    await settle();

    const complete = http.expectOne('/api/calibre/upload/s1/complete');
    expect(complete.request.method).toBe('POST');
    const staged = uploadStatus({ state: 'staged', books: 3, stagedBytes: 8, receivedParts: [0, 1] });
    complete.flush(staged);

    await expect(pending).resolves.toEqual(staged);
    expect(digest.sha256).toHaveBeenCalledTimes(1);
    expect(vi.mocked(digest.sha256).mock.calls[0][0]).toBe(file);
    expect(progress[0]).toEqual({ phase: 'preparing', percent: 0 });
    expect(progress.at(-1)).toEqual({ phase: 'finishing', percent: 100 });
    expect(progress.some((frame) => frame.phase === 'uploading')).toBe(true);
  });

  it('reads a refused create as the typed failure', async () => {
    const pending = service.upload(new File(['01234567'], 'library.zip'));
    await settle();
    http.expectOne('/api/calibre/upload').flush(
      { code: 'calibre_upload_too_large', message: 'Too big.' },
      { status: 413, statusText: 'Payload Too Large' },
    );

    await expect(pending).rejects.toMatchObject({
      code: 'calibre_upload_too_large',
      status: 413,
    });
    http.expectNone((request) => request.url.includes('/parts/'));
  });

  it('drops a session, treating an already-gone one as dropped', async () => {
    const removed = service.cancel('s1');
    const request = http.expectOne('/api/calibre/upload/s1');
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
    await expect(removed).resolves.toBeUndefined();

    const gone = service.cancel('s2');
    http.expectOne('/api/calibre/upload/s2').flush(
      { code: 'calibre_upload_not_found' },
      { status: 404, statusText: 'Not Found' },
    );
    await expect(gone).resolves.toBeUndefined();

    const refused = service.cancel('s3');
    http.expectOne('/api/calibre/upload/s3').flush(
      { code: 'calibre_upload_storage_exhausted', message: 'No room.' },
      { status: 507, statusText: 'Insufficient Storage' },
    );
    await expect(refused).rejects.toMatchObject({ code: 'calibre_upload_storage_exhausted' });
  });

  it('reads the unfinished session, or none from a 204', async () => {
    const current = service.current();
    const request = http.expectOne('/api/calibre/upload');
    expect(request.request.method).toBe('GET');
    request.flush(uploadStatus({ receivedParts: [0] }));
    await expect(current).resolves.toMatchObject({ sessionId: 's1', receivedBytes: 0 });

    const none = service.current();
    http.expectOne('/api/calibre/upload').flush(null, { status: 204, statusText: 'No Content' });
    await expect(none).resolves.toBeNull();
  });
});
