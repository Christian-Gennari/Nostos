/**
 * Bare storage-target upload primitive for direct part uploads (#680 slice B9;
 * plan §20).
 *
 * This is deliberately NOT Angular `HttpClient`: interceptors, credentials and
 * application auth headers must never be applied to a presigned storage target.
 * The production implementation uses `XMLHttpRequest` directly, which gives
 * precise upload progress and abort support while leaving the application's
 * HTTP stack completely out of the request. Cookies are disabled explicitly
 * (`withCredentials = false`); the only headers set are the ticket's own.
 */

export interface DirectUploadClientRequest {
  url: string;
  method: string;
  /** Exactly the headers the upload ticket requires, and nothing else. */
  headers: Readonly<Record<string, string>>;
  body: Blob;
  signal: AbortSignal;
  onProgress?: (loaded: number, total: number) => void;
}

export interface DirectUploadClientResponse {
  status: number;
  statusText: string;
  /** Response headers the target exposed (CORS applies cross-origin). */
  headers: Readonly<Record<string, string>>;
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

export class XhrDirectUploadClient implements DirectUploadClient {
  /**
   * The factory is injectable so tests can drive a fake XMLHttpRequest and
   * assert the exact wire request without a browser.
   */
  constructor(private readonly createXhr: () => XMLHttpRequest = () => new XMLHttpRequest()) {}

  upload(request: DirectUploadClientRequest): Promise<DirectUploadClientResponse> {
    return new Promise<DirectUploadClientResponse>((resolve, reject) => {
      if (request.signal.aborted) {
        reject(new DirectUploadAbortedError());
        return;
      }

      const xhr = this.createXhr();
      let settled = false;

      const finish = () => {
        settled = true;
        request.signal.removeEventListener('abort', onAbort);
      };

      const onAbort = () => {
        if (settled) return;
        xhr.abort();
        finish();
        reject(new DirectUploadAbortedError());
      };

      xhr.open(request.method, request.url, true);
      // Never attach the application's cookies, even if the target is
      // same-origin. Cross-origin storage targets are the supported shape.
      xhr.withCredentials = false;
      for (const [name, value] of Object.entries(request.headers)) {
        xhr.setRequestHeader(name, value);
      }

      if (xhr.upload) {
        xhr.upload.onprogress = (event) => {
          if (settled) return;
          const total = event.lengthComputable ? event.total : request.body.size;
          request.onProgress?.(event.loaded, total);
        };
      }

      xhr.onload = () => {
        if (settled) return;
        finish();
        resolve({
          status: xhr.status,
          statusText: xhr.statusText,
          headers: parseResponseHeaders(xhr.getAllResponseHeaders()),
        });
      };

      xhr.onerror = () => {
        if (settled) return;
        finish();
        reject(new DirectUploadNetworkError('The storage target could not be reached.'));
      };

      xhr.ontimeout = () => {
        if (settled) return;
        finish();
        reject(new DirectUploadNetworkError('The storage target timed out.'));
      };

      request.signal.addEventListener('abort', onAbort, { once: true });
      if (request.signal.aborted) {
        onAbort();
        return;
      }

      xhr.send(request.body);
    });
  }
}

/** Parses `XMLHttpRequest.getAllResponseHeaders()` into a name → value record. */
export function parseResponseHeaders(raw: string): Record<string, string> {
  const headers: Record<string, string> = {};
  for (const line of raw.split(/\r?\n/)) {
    const separator = line.indexOf(':');
    if (separator <= 0) continue;
    const name = line.slice(0, separator).trim();
    const value = line.slice(separator + 1).trim();
    if (name) headers[name] = value;
  }
  return headers;
}

/** Case-insensitive response-header lookup; `null` when not exposed. */
export function responseHeader(
  headers: Readonly<Record<string, string>>,
  name: string,
): string | null {
  const wanted = name.toLowerCase();
  for (const [key, value] of Object.entries(headers)) {
    if (key.toLowerCase() === wanted) return value;
  }
  return null;
}
