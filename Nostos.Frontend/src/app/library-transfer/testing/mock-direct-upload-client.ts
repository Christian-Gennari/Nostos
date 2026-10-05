/**
 * Configurable storage-target double for direct part-upload tests
 * (#680 slice B9).
 *
 * `HttpTestingController` plays the application server (part tickets,
 * reconciliation, job/session control plane); this class plays the signed
 * storage target the browser PUTs to. It can simulate exactly the hostile
 * cases the seam must handle:
 *
 * - an expired ticket: answer 403 so the transport refreshes the window;
 * - a transient target failure: 5xx statuses (the engine owns backoff);
 * - an unreachable target or a refused redirect: reject with
 *   `DirectUploadNetworkError`, the same class the fetch client throws;
 * - aborts: reject the in-flight request when its `AbortSignal` fires.
 */

import {
  DirectUploadAbortedError,
  DirectUploadClient,
  DirectUploadClientRequest,
  DirectUploadClientResponse,
  DirectUploadNetworkError,
} from '../services/direct-upload-client';

export interface MockDirectUploadPlan {
  status?: number;
  statusText?: string;
  /** How many matching requests use this plan before it is exhausted (default 1). */
  times?: number;
  /** Reject like an unreachable target (serverbare fetch rejection). */
  networkError?: boolean;
  /** Latency before answering or failing. */
  latencyMs?: number;
}

export interface RecordedDirectUpload {
  url: string;
  body: Blob;
}

export class MockDirectUploadClient implements DirectUploadClient {
  readonly requests: RecordedDirectUpload[] = [];
  abortedCount = 0;
  latencyMs = 0;

  private readonly plans: MockDirectUploadPlan[] = [];

  queue(plan: MockDirectUploadPlan): void {
    this.plans.push({ times: 1, ...plan });
  }

  queueSuccess(times = 1): void {
    this.queue({ status: 200, statusText: 'OK', times });
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
    this.requests.push({ url: request.url, body: request.body });

    const plan = this.takePlan();
    await this.wait(plan?.latencyMs ?? this.latencyMs, request.signal);

    if (plan?.networkError) {
      throw new DirectUploadNetworkError('The upload target could not be reached.');
    }

    return {
      status: plan?.status ?? 200,
      statusText: plan?.statusText ?? 'OK',
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
    if (ms <= 0) return Promise.resolve();

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
