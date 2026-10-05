import {
  DirectUploadAbortedError,
  DirectUploadNetworkError,
  XhrDirectUploadClient,
} from './direct-upload-client';

/**
 * Minimal XMLHttpRequest surface for asserting the exact wire request the bare
 * storage primitive issues. The production class is the only place in the app
 * that opens a request outside Angular's HttpClient, so these assertions are
 * the proof that no interceptor/credential can ever reach a presigned target.
 */
class FakeXhr {
  method = '';
  url = '';
  async = false;
  withCredentials = true;
  requestHeaders: Record<string, string> = {};
  body: Document | XMLHttpRequestBodyInit | null = null;
  status = 200;
  statusText = 'OK';
  responseHeaders = '';
  aborted = false;
  upload: { onprogress: ((event: ProgressEvent) => void) | null } | null = { onprogress: null };
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  ontimeout: (() => void) | null = null;

  open(method: string, url: string, async = false): void {
    this.method = method;
    this.url = url;
    this.async = async;
  }

  setRequestHeader(name: string, value: string): void {
    this.requestHeaders[name] = value;
  }

  getAllResponseHeaders(): string {
    return this.responseHeaders;
  }

  send(body?: Document | XMLHttpRequestBodyInit | null): void {
    this.body = body ?? null;
  }

  abort(): void {
    this.aborted = true;
  }
}

function clientFor(fake: FakeXhr): XhrDirectUploadClient {
  return new XhrDirectUploadClient(() => fake as unknown as XMLHttpRequest);
}

describe('XhrDirectUploadClient', () => {
  it('sends exactly the ticket headers with credentials disabled and no app auth', async () => {
    const fake = new FakeXhr();
    fake.status = 200;
    fake.statusText = 'OK';
    fake.responseHeaders = 'ETag: "abc"\r\nx-storage-request-id: req-1\r\n';
    const client = clientFor(fake);
    const blob = new Blob(['part-bytes']);

    const promise = client.upload({
      url: 'https://storage.example/part/0?sig=xyz',
      method: 'PUT',
      headers: { 'x-storage-signature': 'sig', 'Content-Type': 'application/octet-stream' },
      body: blob,
      signal: new AbortController().signal,
    });

    expect(fake.method).toBe('PUT');
    expect(fake.url).toBe('https://storage.example/part/0?sig=xyz');
    expect(fake.withCredentials).toBe(false);
    expect(fake.requestHeaders).toEqual({
      'x-storage-signature': 'sig',
      'Content-Type': 'application/octet-stream',
    });
    expect(fake.requestHeaders['Authorization']).toBeUndefined();
    expect(fake.requestHeaders['Cookie']).toBeUndefined();
    expect(fake.body).toBe(blob);

    fake.onload?.();
    await expect(promise).resolves.toEqual({
      status: 200,
      statusText: 'OK',
      headers: { ETag: '"abc"', 'x-storage-request-id': 'req-1' },
    });
  });

  it('forwards upload progress with a total even when the event is not computable', async () => {
    const fake = new FakeXhr();
    const client = clientFor(fake);
    const blob = new Blob([new Uint8Array(32)]);
    const progress: Array<[number, number]> = [];

    const promise = client.upload({
      url: 'https://storage.example/part/0',
      method: 'PUT',
      headers: {},
      body: blob,
      signal: new AbortController().signal,
      onProgress: (loaded, total) => progress.push([loaded, total]),
    });

    fake.upload?.onprogress?.({ loaded: 16, total: 0, lengthComputable: false } as ProgressEvent);
    fake.upload?.onprogress?.({ loaded: 32, total: 32, lengthComputable: true } as ProgressEvent);
    fake.onload?.();
    await promise;

    expect(progress).toEqual([
      [16, 32],
      [32, 32],
    ]);
  });

  it('aborts the XHR and rejects when the signal aborts mid-flight', async () => {
    const fake = new FakeXhr();
    const client = clientFor(fake);
    const controller = new AbortController();

    const promise = client.upload({
      url: 'https://storage.example/part/0',
      method: 'PUT',
      headers: {},
      body: new Blob(['x']),
      signal: controller.signal,
    });
    controller.abort();

    await expect(promise).rejects.toBeInstanceOf(DirectUploadAbortedError);
    expect(fake.aborted).toBe(true);
  });

  it('rejects an aborted signal before opening a request', async () => {
    const fake = new FakeXhr();
    const client = clientFor(fake);
    const controller = new AbortController();
    controller.abort();

    await expect(
      client.upload({
        url: 'https://storage.example/part/0',
        method: 'PUT',
        headers: {},
        body: new Blob(['x']),
        signal: controller.signal,
      }),
    ).rejects.toBeInstanceOf(DirectUploadAbortedError);
    expect(fake.url).toBe('');
    expect(fake.aborted).toBe(false);
  });

  it('rejects a target that cannot be reached', async () => {
    const fake = new FakeXhr();
    const client = clientFor(fake);

    const promise = client.upload({
      url: 'https://storage.example/part/0',
      method: 'PUT',
      headers: {},
      body: new Blob(['x']),
      signal: new AbortController().signal,
    });
    fake.onerror?.();

    await expect(promise).rejects.toBeInstanceOf(DirectUploadNetworkError);
  });
});
