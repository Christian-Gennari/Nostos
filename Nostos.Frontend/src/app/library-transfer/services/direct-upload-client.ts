/**
 * Credential-free storage-target upload primitive for direct part uploads
 * (#680 slice B9; plan §20).
 *
 * The only transport a presigned target ever sees is a bare `fetch`:
 *
 * - `credentials: 'omit'` suppresses application cookies even for a
 *   same-origin target (`withCredentials = false` on XHR did not);
 * - `redirect: 'error'` refuses to replay the archive body to a redirect
 *   destination the caller never validated;
 * - `referrerPolicy: 'no-referrer'` sends no application URL to the target;
 * - `mode: 'cors'` keeps the request a normal cross-origin CORS request;
 * - no headers are set at all. Angular `HttpClient` and its interceptors are
 *   never involved, so no application authorization can attach itself.
 *
 * The method is fixed to `PUT`; a ticket cannot change it or supply headers.
 */

export interface DirectUploadClientRequest {
  url: string;
  body: Blob;
  signal: AbortSignal;
}

export interface DirectUploadClientResponse {
  status: number;
  statusText: string;
}

export interface DirectUploadClient {
  upload(request: DirectUploadClientRequest): Promise<DirectUploadClientResponse>;
}

/** The caller aborted the in-flight storage request. */
export class DirectUploadAbortedError extends Error {
  constructor() {
    super('The storage upload was aborted.');
    this.name = 'DirectUploadAbortedError';
  }
}

/** The storage target could not be reached (no HTTP response). */
export class DirectUploadNetworkError extends Error {
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options);
    this.name = 'DirectUploadNetworkError';
  }
}

/** The host handed the browser a target the security policy forbids. */
export class UnsafeUploadTargetError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'UnsafeUploadTargetError';
  }
}

export class FetchDirectUploadClient implements DirectUploadClient {
  /** Injectable so tests can assert the exact `fetch` init. */
  constructor(private readonly fetchImpl: typeof fetch = (...args) => fetch(...args)) {}

  async upload(request: DirectUploadClientRequest): Promise<DirectUploadClientResponse> {
    try {
      const response = await this.fetchImpl(request.url, {
        method: 'PUT',
        body: request.body,
        credentials: 'omit',
        redirect: 'error',
        referrerPolicy: 'no-referrer',
        mode: 'cors',
        signal: request.signal,
      });
      return { status: response.status, statusText: response.statusText };
    } catch (error) {
      if (request.signal.aborted || isAbortError(error)) {
        throw new DirectUploadAbortedError();
      }
      // Includes the browser's rejection for a redirect when
      // `redirect: 'error'`: the body is never replayed anywhere.
      throw new DirectUploadNetworkError('The upload target could not be reached.', {
        cause: error,
      });
    }
  }
}

/**
 * Validates an upload target before any byte can reach it.
 *
 * The target must be an absolute URL using `https:` (plain `http:` is allowed
 * only for loopback hosts so local development and tests can run), and its
 * origin must differ from the application origin: a same-origin target would
 * be inside the application's own credential scope even with credentials
 * omitted, so it is refused outright.
 */
export function validateDirectUploadTarget(rawUrl: string, applicationOrigin: string): URL {
  let target: URL;
  try {
    target = new URL(rawUrl);
  } catch {
    throw new UnsafeUploadTargetError(
      'The upload target is not an absolute URL and cannot be trusted.',
    );
  }

  const loopback = isLoopbackHost(target.hostname);
  if (target.protocol !== 'https:' && !(target.protocol === 'http:' && loopback)) {
    throw new UnsafeUploadTargetError('The upload target must use HTTPS.');
  }

  if (applicationOrigin && target.origin === applicationOrigin) {
    throw new UnsafeUploadTargetError(
      'The upload target must not share this application’s origin.',
    );
  }

  return target;
}

/** Loopback hosts are the only plain-HTTP exception, for local development. */
export function isLoopbackHost(hostname: string): boolean {
  const host = hostname.toLowerCase();
  return host === 'localhost' || host === '127.0.0.1' || host === '[::1]' || host === '::1';
}

function isAbortError(error: unknown): boolean {
  return (
    (typeof DOMException !== 'undefined' &&
      error instanceof DOMException &&
      error.name === 'AbortError') ||
    (error instanceof Error && error.name === 'AbortError')
  );
}
