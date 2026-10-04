/**
 * Export flow coordinator (plan §37, §38; slice B4).
 *
 * Export is not a download-progress flow: the browser only learns that the
 * archive is ready after the host has built and validated it, then hands the
 * user a native download URL. This coordinator owns job creation and status
 * polling and exposes the plan's explicit export states so the export-flow
 * component renders the server's phase instead of inventing one.
 *
 * Activation is import-only; nothing here mutates the library.
 */

import { Injectable, inject, signal } from '@angular/core';

import {
  LibraryTransferFailure,
  TransferCancelledError,
} from '../models/library-transfer.models';
import { MigrationProgressDto } from '../models/migration-http.dtos';
import {
  LIBRARY_TRANSFER_TRANSPORT,
  LibraryTransferTransport,
  MigrationTransportError,
  isMaintenanceBusy,
  toTransferFailure,
} from './library-transfer-transport';
import {
  MAINTENANCE_FALLBACK_BASE_MS,
  MAINTENANCE_MAX_WAIT_MS,
} from './library-transfer-coordinator.service';

export const DEFAULT_EXPORT_POLL_MS = 1_500;

export interface ExportMaintenanceWait {
  operation: string;
  retryAfterMs: number | null;
}

export type LibraryExportState =
  | { kind: 'idle' }
  | { kind: 'preparing'; jobId: string | null; progress?: MigrationProgressDto }
  | { kind: 'checking'; jobId: string; progress?: MigrationProgressDto }
  | { kind: 'ready'; jobId: string; downloadUrl: string; expiresAt?: string | null }
  | { kind: 'failed'; jobId?: string; failure: LibraryTransferFailure }
  | { kind: 'cancelled'; jobId: string };

@Injectable({ providedIn: 'root' })
export class LibraryExportCoordinator {
  private readonly transport = inject<LibraryTransferTransport>(LIBRARY_TRANSFER_TRANSPORT);

  private readonly stateSignal = signal<LibraryExportState>({ kind: 'idle' });
  readonly state = this.stateSignal.asReadonly();

  /** Set while the coordinator waits out server maintenance and re-attempts. */
  readonly maintenanceWaiting = signal<ExportMaintenanceWait | null>(null);

  /** Injectable for fake-timer tests; the coordinator owns the scheduling. */
  pollIntervalMs = DEFAULT_EXPORT_POLL_MS;

  private operationToken = 0;
  private operationAbort: AbortController | null = null;
  private jobId: string | null = null;

  /** Creates the export job and polls until it is downloadable or terminal. */
  async startExport(): Promise<void> {
    const { token, signal } = this.beginOperation();
    this.jobId = null;
    const idempotencyKey = newIdempotencyKey();

    try {
      this.setState({ kind: 'preparing', jobId: null });
      const created = await this.withBusyRetry(
        'createJob',
        token,
        () => this.transport.createJob({ direction: 'Export', idempotencyKey }, signal),
      );
      if (!this.isCurrent(token)) return;
      this.jobId = created.job.id;

      let status = created;
      for (;;) {
        if (!this.isCurrent(token)) return;
        this.applyStatus(status);
        if (!this.polling()) return;

        await this.sleep(this.pollIntervalMs, signal);
        if (!this.isCurrent(token)) return;
        status = await this.withBusyRetry(
          'getJob',
          token,
          () => this.transport.getJob(created.job.id, signal),
        );
      }
    } catch (error) {
      if (!this.isCurrent(token)) return;
      if (isAborted(error)) return;
      this.failWith(toTransferFailure(error), this.jobId ?? undefined);
    }
  }

  /** Aborts browser work, then records durable cancellation on the server. */
  async cancelExport(): Promise<void> {
    const jobId = this.jobId;
    this.operationToken += 1;
    this.operationAbort?.abort();
    this.operationAbort = new AbortController();
    const token = this.operationToken;
    const signal = this.operationAbort.signal;

    if (!jobId) {
      this.setState({ kind: 'cancelled', jobId: '' });
      return;
    }

    try {
      await this.withBusyRetry(
        'cancelJob',
        token,
        () => this.transport.cancelJob(jobId, undefined, signal),
      );
      if (!this.isCurrent(token)) return;
      this.setState({ kind: 'cancelled', jobId });
    } catch (error) {
      if (!this.isCurrent(token)) return;
      if (isAborted(error)) return;
      this.failWith(toTransferFailure(error), jobId);
    }
  }

  /** Acknowledges a terminal export state and returns to idle. */
  dismiss(): void {
    const kind = this.stateSignal().kind;
    if (kind === 'ready' || kind === 'cancelled' || kind === 'failed') {
      this.setState({ kind: 'idle' });
    }
  }

  private applyStatus(status: {
    job: { id: string; state: string };
    progress: MigrationProgressDto;
    downloadAvailable: boolean;
    artifactExpiresAtUtc?: string | null;
  }): void {
    const jobId = status.job.id;
    switch (status.job.state) {
      case 'Completed':
        if (status.downloadAvailable) {
          const downloadUrl = this.transport.getExportDownloadUrl(jobId);
          this.setState({
            kind: 'ready',
            jobId,
            downloadUrl,
            expiresAt: status.artifactExpiresAtUtc ?? null,
          });
        } else {
          this.setState({ kind: 'checking', jobId, progress: status.progress });
        }
        return;
      case 'Validating':
        this.setState({ kind: 'checking', jobId, progress: status.progress });
        return;
      case 'Failed':
        this.failWith({ code: 'portable_export_failed', message: '', retryable: true }, jobId);
        return;
      case 'Cancelled':
        this.setState({ kind: 'cancelled', jobId });
        return;
      case 'Expired':
        this.failWith({ code: 'migration_session_expired', message: '', retryable: true }, jobId);
        return;
      default:
        this.setState({ kind: 'preparing', jobId, progress: status.progress });
    }
  }

  /** True while the flow should keep asking for status. */
  private polling(): boolean {
    const kind = this.stateSignal().kind;
    return kind === 'preparing' || kind === 'checking';
  }

  private beginOperation(): { token: number; signal: AbortSignal } {
    this.operationAbort?.abort();
    this.operationToken += 1;
    this.operationAbort = new AbortController();
    return { token: this.operationToken, signal: this.operationAbort.signal };
  }

  private isCurrent(token: number): boolean {
    return token === this.operationToken;
  }

  private setState(state: LibraryExportState): void {
    this.stateSignal.set(state);
  }

  private failWith(failure: LibraryTransferFailure, jobId: string | undefined): void {
    this.setState({ kind: 'failed', jobId, failure });
  }

  /**
   * Waits out exclusive server maintenance and re-attempts the same operation.
   * The export job-creation key is held across re-attempts, so a lost response
   * can never create a second logical export.
   */
  private async withBusyRetry<T>(
    operation: string,
    token: number,
    call: () => Promise<T>,
  ): Promise<T> {
    const startedAt = Date.now();
    let attempt = 0;
    for (;;) {
      try {
        const result = await call();
        this.maintenanceWaiting.set(null);
        return result;
      } catch (error) {
        this.maintenanceWaiting.set(null);
        if (!this.isCurrent(token)) throw new TransferCancelledError('Export was cancelled.');
        const failure = toTransferFailure(error);
        if (!isMaintenanceBusy(failure.code)) throw error;

        const delay =
          failure.retryAfterMs ?? Math.min(MAINTENANCE_FALLBACK_BASE_MS * 2 ** attempt, 8_000);
        attempt += 1;
        if (Date.now() - startedAt + delay > MAINTENANCE_MAX_WAIT_MS) {
          throw new MigrationTransportError(
            'migration_maintenance_timeout',
            0,
            'The host stayed busy finishing another library operation.',
          );
        }

        this.maintenanceWaiting.set({ operation, retryAfterMs: failure.retryAfterMs ?? delay });
        await this.maintenanceSleep(delay);
      }
    }
  }

  private maintenanceSleep(ms: number): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      const signal = this.operationAbort?.signal;
      if (signal?.aborted) {
        reject(new TransferCancelledError('Export was cancelled.'));
        return;
      }
      const timer = setTimeout(() => {
        cleanup();
        resolve();
      }, ms);
      const onAbort = () => {
        clearTimeout(timer);
        cleanup();
        reject(new TransferCancelledError('Export was cancelled.'));
      };
      const cleanup = () => signal?.removeEventListener('abort', onAbort);
      signal?.addEventListener('abort', onAbort, { once: true });
    });
  }

  private sleep(ms: number, signal: AbortSignal): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      if (signal.aborted) {
        reject(new TransferCancelledError('Export polling was cancelled.'));
        return;
      }
      const timer = setTimeout(() => {
        signal.removeEventListener('abort', onAbort);
        resolve();
      }, ms);
      const onAbort = () => {
        clearTimeout(timer);
        reject(new TransferCancelledError('Export polling was cancelled.'));
      };
      signal.addEventListener('abort', onAbort, { once: true });
    });
  }
}

function isAborted(error: unknown): boolean {
  if (error instanceof TransferCancelledError) return true;
  return error instanceof MigrationTransportError && error.code === 'request_aborted';
}

function newIdempotencyKey(): string {
  const uuid = globalThis.crypto?.randomUUID;
  if (typeof uuid === 'function') return uuid.call(globalThis.crypto);
  return `idem-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}
