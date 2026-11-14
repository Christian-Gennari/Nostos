import { TestBed } from '@angular/core/testing';
import { HttpEventType, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { BookUploadClient } from './book-upload.client';
import type { BookUploadStatusDto } from '../dtos/book-upload.dtos';
import { TransferCancelledError } from '../uploads/upload.models';
import { UploadFailure } from '../uploads/upload-error';

const BASE = '/api/books/b1/file/uploads';

const STATUS: BookUploadStatusDto = {
  sessionId: 's1',
  bookId: 'b1',
  state: 'Receiving',
  totalBytes: 16,
  chunkSize: 4,
  totalChunks: 4,
  fileName: 'book.epub',
  fileSha256: 'a'.repeat(64),
  receivedParts: [0],
  receivedBytes: 4,
  createdAtUtc: '2026-08-01T08:00:00Z',
  expiresAtUtc: '2026-08-02T08:00:00Z',
};

describe('BookUploadClient', () => {
  let client: BookUploadClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    client = TestBed.inject(BookUploadClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the owner’s current session', async () => {
    const pending = client.getCurrent('b1');

    const request = http.expectOne(BASE);
    expect(request.request.method).toBe('GET');
    request.flush(STATUS);

    await expect(pending).resolves.toEqual(STATUS);
  });

  it('reads "no session" from a 204', async () => {
    const pending = client.getCurrent('b1');

    http.expectOne(BASE).flush(null, { status: 204, statusText: 'No Content' });

    await expect(pending).resolves.toBeNull();
  });

  it('creates a session with the file’s identity', async () => {
    const request$ = {
      totalBytes: 16,
      fileName: 'book.epub',
      contentType: 'application/epub+zip',
      sha256: 'a'.repeat(64),
    };
    const pending = client.create('b1', request$);

    const request = http.expectOne(BASE);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(request$);
    request.flush(STATUS);

    await expect(pending).resolves.toEqual(STATUS);
  });

  it('asks to replace the book’s file when told to', async () => {
    const pending = client.create(
      'b1',
      { totalBytes: 16, fileName: 'book.epub', contentType: 'application/epub+zip', sha256: 'a'.repeat(64) },
      undefined,
      true,
    );

    const request = http.expectOne(`${BASE}?replace=true`);
    expect(request.request.method).toBe('POST');
    expect(request.request.urlWithParams).toBe(`${BASE}?replace=true`);
    request.flush(STATUS);

    await expect(pending).resolves.toEqual(STATUS);
  });

  it('sends one part with its range, digest and progress', async () => {
    const progress: Array<[number, number]> = [];
    const blob = new Blob([new Uint8Array(4)]);
    const pending = client.uploadPart(
      'b1',
      's1',
      2,
      blob,
      'a'.repeat(64),
      4,
      16,
      (loaded, total) => progress.push([loaded, total]),
    );

    const request = http.expectOne(`${BASE}/s1/parts/2`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.get('Content-Range')).toBe('bytes 4-7/16');
    expect(request.request.headers.get('X-Nostos-Chunk-SHA256')).toBe('a'.repeat(64));
    expect(request.request.body).toBe(blob);

    request.event({ type: HttpEventType.UploadProgress, loaded: 2, total: 4 });
    request.flush({ sessionId: 's1', partIndex: 2, alreadyPresent: false });

    await expect(pending).resolves.toEqual({
      sessionId: 's1',
      partIndex: 2,
      alreadyPresent: false,
    });
    expect(progress).toEqual([[2, 4]]);
  });

  it('completes and pauses a session, and discards it', async () => {
    const completing = client.complete('b1', 's1');
    const complete = http.expectOne(`${BASE}/s1/complete`);
    expect(complete.request.method).toBe('POST');
    expect(complete.request.body).toBeNull();
    complete.flush(STATUS);
    await expect(completing).resolves.toEqual(STATUS);

    const pausing = client.pause('b1', 's1');
    const pause = http.expectOne(`${BASE}/s1/pause`);
    expect(pause.request.method).toBe('POST');
    expect(pause.request.body).toBeNull();
    pause.flush({ ...STATUS, state: 'Paused' });
    await expect(pausing).resolves.toMatchObject({ state: 'Paused' });

    const discarding = client.discard('b1', 's1');
    const discard = http.expectOne(`${BASE}/s1`);
    expect(discard.request.method).toBe('DELETE');
    discard.flush(null, { status: 204, statusText: 'No Content' });
    await expect(discarding).resolves.toBeNull();
  });

  it('reads a refused part into the typed failure', async () => {
    const pending = client.uploadPart(
      'b1',
      's1',
      0,
      new Blob([new Uint8Array(4)]),
      'a'.repeat(64),
      0,
      16,
      () => {},
    );

    http.expectOne(`${BASE}/s1/parts/0`).flush(
      { error: 'book_upload_part_hash_mismatch', message: 'Send it again.' },
      { status: 422, statusText: 'Unprocessable Content' },
    );

    await expect(pending).rejects.toMatchObject({
      code: 'book_upload_part_hash_mismatch',
      status: 422,
    });
  });

  it('reads a refused create into the typed failure', async () => {
    const pending = client.create('b1', {
      totalBytes: 16,
      fileName: 'book.epub',
      contentType: 'application/epub+zip',
      sha256: 'a'.repeat(64),
    });

    http.expectOne(BASE).flush(
      { error: 'book_upload_storage_exhausted', message: 'No room.' },
      { status: 507, statusText: 'Insufficient Storage' },
    );

    await expect(pending).rejects.toBeInstanceOf(UploadFailure);
    await expect(pending).rejects.toMatchObject({ code: 'book_upload_storage_exhausted', status: 507 });
  });

  it('does not start a request the caller has already stopped', async () => {
    const controller = new AbortController();
    controller.abort();

    await expect(client.getCurrent('b1', controller.signal)).rejects.toBeInstanceOf(
      TransferCancelledError,
    );
    http.expectNone(BASE);
  });
});
