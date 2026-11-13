import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpEventType, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { TransferCancelledError } from './upload.models';
import { putUploadPart, sendUploadRequest } from './upload-part';

const URL = '/api/calibre/upload/session-1/parts/0';
const BLOB = new Blob([new Uint8Array(4)]);

describe('putUploadPart', () => {
  let http: HttpClient;
  let testing: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    testing = TestBed.inject(HttpTestingController);
  });

  afterEach(() => testing.verify());

  function part(signal?: AbortSignal, onProgress?: (loaded: number, total: number) => void) {
    return putUploadPart<{ partIndex: number }>(http, {
      url: URL,
      blob: BLOB,
      offsetBytes: 0,
      totalBytes: 8,
      sha256: 'a'.repeat(64),
      signal,
      onProgress,
    });
  }

  it('sends the shared part headers, reports progress and resolves the receipt', async () => {
    const progress: Array<[number, number]> = [];
    const pending = part(undefined, (loaded, total) => progress.push([loaded, total]));

    const request = testing.expectOne(URL);
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.get('Content-Range')).toBe('bytes 0-3/8');
    expect(request.request.headers.get('X-Nostos-Chunk-SHA256')).toBe('a'.repeat(64));

    request.event({ type: HttpEventType.UploadProgress, loaded: 2, total: 4 });
    request.flush({ partIndex: 0 });

    await expect(pending).resolves.toEqual({ partIndex: 0 });
    expect(progress).toEqual([[2, 4]]);
  });

  it('reads a part refusal into the typed failure', async () => {
    const pending = part();
    testing.expectOne(URL).flush(
      { code: 'calibre_upload_part_hash_mismatch', message: 'Send it again.' },
      { status: 422, statusText: 'Unprocessable Content' },
    );

    await expect(pending).rejects.toMatchObject({
      code: 'calibre_upload_part_hash_mismatch',
      status: 422,
    });
  });

  it('drops a part the caller stops', async () => {
    const controller = new AbortController();
    const pending = part(controller.signal);
    testing.expectOne(URL);

    controller.abort();

    await expect(pending).rejects.toBeInstanceOf(TransferCancelledError);
  });

  it('does not start a part whose signal is already aborted', async () => {
    const controller = new AbortController();
    controller.abort();

    await expect(part(controller.signal)).rejects.toBeInstanceOf(TransferCancelledError);
    testing.expectNone(URL);
  });
});

describe('sendUploadRequest', () => {
  let http: HttpClient;
  let testing: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    testing = TestBed.inject(HttpTestingController);
  });

  afterEach(() => testing.verify());

  it('maps a refused request into the typed failure', async () => {
    const pending = sendUploadRequest(
      http.post('/api/calibre/upload', {}),
    );
    testing.expectOne('/api/calibre/upload').flush(
      { code: 'calibre_upload_too_large', message: 'Too big.' },
      { status: 413, statusText: 'Payload Too Large' },
    );

    await expect(pending).rejects.toMatchObject({
      code: 'calibre_upload_too_large',
      status: 413,
    });
  });

  it('rejects a request the caller stops', async () => {
    const controller = new AbortController();
    const pending = sendUploadRequest(http.get('/api/calibre/upload'), controller.signal);
    testing.expectOne('/api/calibre/upload');

    controller.abort();

    await expect(pending).rejects.toBeInstanceOf(TransferCancelledError);
  });
});
