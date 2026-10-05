/**
 * Configurable storage-target double for direct part-upload tests
 * (#680 slice B9).
 *
 * `HttpTestingController` plays the application server (tickets, completion
 * reports, job/session control plane); this class plays the presigned storage
 * target the browser PUTs to. It can simulate exactly the hostile cases the
 * seam must handle:
 *
 * - ticket expiry: answer 403 so the transport refreshes the ticket;
 * - storage 5xx: answer 503 (the engine owns backoff);
 * - missing completion token: answer 2xx without the `ETag` header, as a
 *   target that does not expose it through CORS would;
 * - aborts: reject the in-flight request when its `AbortSignal` fires.
 */

import {
  DirectUploadAbortedError,
  DirectUploadClient,
  DirectUploadClientRequest,
  DirectUploadClientResponse,
} from '../services/direct-upload-client';
import { DIRECT_UPLOAD_COMPLETION_HEADER } from '../models/direct-part-upload.dtos';

export interface MockDirectUploadPlan {
  status?: number;
  statusText?: string;
  /** Response headers the target exposes; `ETag` is present unless overridden. */
  headers?: Record<string, string>;
  /** How many matching requests use this plan before it is exhausted (default 1). */
  times?: number;
  /** Skip the HTTP response and fail like an unreachable target. */
  networkError?: boolean;
  /** Latency before answering or failing. */
  latencyMs?: number;
}

export interface RecordedDirectUpload {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: Blob;
}

const DEFAULT_ETAG = '"mock-etag"';

export class MockDirectUploadClient implements DirectUploadClient {
  readonly requests: RecordedDirectUpload[] = [];
  abortedCount = 0;
  latencyMs = 0;

  private readonly plans: MockDirectUploadPlan[] = [];
  private sequence = 0;

  queue(plan: MockDirectUploadPlan): void {
    this.plans.push({ times: 1, ...plan });
  }

  queueSuccess(etag = DEFAULT_ETAG, times = 1): void {
    this.queue({ status: 200, statusText: 'OK', headers: { ETag: etag }, times });
  }

  /** 2xx accepted without the completion token header (CORS not exposing it). */
  queueMissingCompletionToken(times = 1): void {
    this.queue({ status: 200, statusText: 'OK', headers: {}, times });
  }

  queueRefusal(status = 403, times = 1): void {
    this.queue({ status, statusText: 'Forbidden', times });
  }

  queueServerError(status = 503, times = 1): void {
    this.queue({ status, statusText: 'Service Unavailable', times });
  }

  queueNetworkError(times = 1): void {
    this.queue({ networkError: true, times });
  }

  async upload(request: DirectUploadClientRequest): Promise<DirectUploadClientResponse> {
    this.requests.push({
      url: request.url,
      method: request.method,
      headers: { ...request.headers },
      body: request.body,
    });

    const plan = this.takePlan();
    await this.wait(plan?.latencyMs ?? this.latencyMs, request.signal);

    if (plan?.networkError) {
      throw new Error('The storage target could not be reached.');
    }

    const status = plan?.status ?? 200;
    const defaultHeaders: Record<string, string> =
      status >= 200 && status < 300 ? { [DIRECT_UPLOAD_COMPLETION_HEADER]: DEFAULT_ETAG } : {};
    this.sequence += 1;
    return {
      status,
      statusText: plan?.statusText ?? (status < 300 ? 'OK' : 'Error'),
      headers: plan?.headers ?? defaultHeaders,
    };
  }

  private takePlan(): MockDirectUploadPlan | null {
    const plan = this.plans[0];
    if (!plan) return null;
    const remaining = (plan.times ?? 1) - 1;
    if (remaining <= 0) this.plans.shift();
    else plan.times = remaining;
    return plan;
  }

  private wait(ms: number, signal: AbortSignal): Promise<void> {
    if (signal.aborted) {
      this.abortedCount += 1;
      return Promise.reject(new DirectUploadAbortedError());
    }
    if (ms <= 0) {
      this.sequence += 1;
      return Promise.resolve();
    }

    return new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        signal.removeEventListener('abort', onAbort);
        resolve();
      }, ms);
      const onAbort = () => {
        clearTimeout(timer);
        this.abortedCount += 1;
        reject(new DirectUploadAbortedError());
      };
      signal.addEventListener('abort', onAbort, { once: true });
    });
  }
}
