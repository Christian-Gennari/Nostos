import {
  DirectUploadAbortedError,
  DirectUploadNetworkError,
  FetchDirectUploadClient,
  UnsafeUploadTargetError,
  isLoopbackHost,
  validateDirectUploadTarget,
} from './direct-upload-client';

describe('FetchDirectUploadClient', () => {
  it('PUTs with the exact credential-free init and no headers of any kind', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 200, statusText: 'OK' }));
    const client = new FetchDirectUploadClient(fetchMock as unknown as typeof fetch);
    const blob = new Blob(['part-bytes']);
    const controller = new AbortController();

    const response = await client.upload({
      url: 'https://storage.example/part?sig=xyz',
      body: blob,
      signal: controller.signal,
    });

    expect(response).toEqual({ status: 200, statusText: 'OK' });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('https://storage.example/part?sig=xyz');
    expect(init).toEqual({
      method: 'PUT',
      body: blob,
      credentials: 'omit',
      redirect: 'error',
      referrerPolicy: 'no-referrer',
      mode: 'cors',
      signal: controller.signal,
    });
    expect('headers' in init).toBe(false);
  });

  it('returns non-2xx statuses to the transport instead of throwing', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 403, statusText: 'Forbidden' }));
    const client = new FetchDirectUploadClient(fetchMock as unknown as typeof fetch);

    await expect(
      client.upload({
        url: 'https://storage.example/part',
        body: new Blob(['x']),
        signal: new AbortController().signal,
      }),
    ).resolves.toEqual({ status: 403, statusText: 'Forbidden' });
  });

  it('maps an aborted signal to DirectUploadAbortedError', async () => {
    const controller = new AbortController();
    controller.abort();
    const fetchMock = vi.fn(async () => {
      throw new DOMException('The operation was aborted.', 'AbortError');
    });
    const client = new FetchDirectUploadClient(fetchMock as unknown as typeof fetch);

    await expect(
      client.upload({
        url: 'https://storage.example/part',
        body: new Blob(['x']),
        signal: controller.signal,
      }),
    ).rejects.toBeInstanceOf(DirectUploadAbortedError);
  });

  it('refuses to replay the body to a redirect: init says redirect=error and the rejection surfaces', async () => {
    // Browsers reject a `redirect: 'error'` response with a TypeError instead
    // of following it; the target never receives the archive part twice.
    const fetchMock = vi.fn(async () => {
      throw new TypeError('Failed to fetch');
    });
    const client = new FetchDirectUploadClient(fetchMock as unknown as typeof fetch);
    const blob = new Blob(['part-bytes']);

    await expect(
      client.upload({
        url: 'https://storage.example/part',
        body: blob,
        signal: new AbortController().signal,
      }),
    ).rejects.toBeInstanceOf(DirectUploadNetworkError);

    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(init.redirect).toBe('error');
    expect(init.credentials).toBe('omit');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('maps an unreachable target to DirectUploadNetworkError', async () => {
    const fetchMock = vi.fn(async () => {
      throw new TypeError('Failed to fetch');
    });
    const client = new FetchDirectUploadClient(fetchMock as unknown as typeof fetch);

    await expect(
      client.upload({
        url: 'https://storage.example/part',
        body: new Blob(['x']),
        signal: new AbortController().signal,
      }),
    ).rejects.toBeInstanceOf(DirectUploadNetworkError);
  });
});

describe('validateDirectUploadTarget', () => {
  const appOrigin = 'https://app.nostos.example';

  it('accepts an absolute HTTPS target on another origin', () => {
    const url = validateDirectUploadTarget('https://uploads.example/part?sig=1', appOrigin);
    expect(url.origin).toBe('https://uploads.example');
  });

  it('accepts plain HTTP only for loopback hosts', () => {
    expect(validateDirectUploadTarget('http://localhost:9000/part', appOrigin).origin).toBe(
      'http://localhost:9000',
    );
    expect(validateDirectUploadTarget('http://127.0.0.1:9000/part', appOrigin).origin).toBe(
      'http://127.0.0.1:9000',
    );
    expect(isLoopbackHost('localhost')).toBe(true);
    expect(isLoopbackHost('storage.example')).toBe(false);
  });

  it('refuses plain HTTP on a public host', () => {
    expect(() => validateDirectUploadTarget('http://storage.example/part', appOrigin)).toThrow(
      UnsafeUploadTargetError,
    );
  });

  it('refuses a target on the application origin', () => {
    expect(() => validateDirectUploadTarget(`${appOrigin}/upload`, appOrigin)).toThrow(
      UnsafeUploadTargetError,
    );
  });

  it('refuses relative, non-HTTP and non-URL targets', () => {
    for (const target of [
      '/upload/part',
      'part',
      'javascript:alert(1)',
      'data:text/plain,abc',
      'file:///tmp/part',
    ]) {
      expect(() => validateDirectUploadTarget(target, appOrigin)).toThrow(UnsafeUploadTargetError);
    }
  });
});
