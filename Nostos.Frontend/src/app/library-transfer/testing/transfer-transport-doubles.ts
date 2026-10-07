/**
 * Shared transport doubles for the library-transfer component and host specs.
 *
 * Each one wraps a real `MockLibraryTransferTransport` and controls one async
 * edge — gating uploads, expiring an export artifact, failing a status read —
 * so component specs can observe an in-flight transfer deterministically
 * without re-implementing the server rules the mock already models.
 */

import {
  BrowserMigrationChunk,
  MigrationChunkUploadResultDto,
  MigrationJobStatusResponseDto,
} from '../models/migration-http.dtos';
import { MigrationTransportError } from '../services/migration-transport-error';
import { DelegatingTransport } from './delegating-transport';

export interface PendingUpload {
  jobId: string;
  sessionId: string;
  request: BrowserMigrationChunk;
  onProgress: (loaded: number, total: number) => void;
  signal: AbortSignal;
  resolve: (result: MigrationChunkUploadResultDto) => void;
  reject: (error: unknown) => void;
}

/** Holds upload requests so a transfer can be observed while it is active. */
export class GatedUploadTransport extends DelegatingTransport {
  readonly pending: PendingUpload[] = [];

  override uploadChunk(
    jobId: string,
    sessionId: string,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    return new Promise<MigrationChunkUploadResultDto>((resolve, reject) => {
      signal.addEventListener(
        'abort',
        () => reject(new MigrationTransportError('request_aborted', 0, 'aborted')),
        { once: true },
      );
      this.pending.push({ jobId, sessionId, request, onProgress, signal, resolve, reject });
    });
  }

  async releaseAll(): Promise<void> {
    const items = this.pending.splice(0);
    await Promise.all(
      items.map(async (item) => {
        try {
          const result = await this.inner.uploadChunk(
            item.jobId,
            item.sessionId,
            item.request,
            item.onProgress,
            item.signal,
          );
          item.resolve(result);
        } catch (error) {
          item.reject(error);
        }
      }),
    );
  }
}

/** Rejects status reads with a 401 until the test restores authorization. */
export class UnauthorizedOnceTransport extends DelegatingTransport {
  unauthorized = true;

  override getJob(jobId: string, signal?: AbortSignal): Promise<MigrationJobStatusResponseDto> {
    if (this.unauthorized) {
      return Promise.reject(
        new MigrationTransportError('unexpected_error', 401, 'sign in required'),
      );
    }
    return this.inner.getJob(jobId, signal);
  }
}

/** Test control surface over the real mock transport for export flows. */
export class ControlledExportTransport extends DelegatingTransport {
  readyOnCreate = false;
  failCreate = false;
  onCreated: ((jobId: string) => void) | null = null;
  expiresAt: string | null = null;
  /** Replaces the transport's download URL, for URL-safety tests. */
  overrideDownloadUrl: string | null = null;
  /** Sets the job to Validating on this getJob call number (createJob counts as 1). */
  setValidatingOnGetJob: number | null = null;

  private getJobCalls = 0;

  override async createJob(
    request: Parameters<DelegatingTransport['createJob']>[0],
    signal?: AbortSignal,
  ) {
    if (this.failCreate) {
      throw new MigrationTransportError('network_error', 0, 'connection lost');
    }
    const created = await this.inner.createJob(request, signal);
    this.onCreated?.(created.job.id);
    if (this.readyOnCreate) this.inner.markExportReady(created.job.id);
    return this.getJob(created.job.id, signal);
  }

  override async getJob(jobId: string, signal?: AbortSignal) {
    this.getJobCalls += 1;
    if (this.setValidatingOnGetJob === this.getJobCalls) {
      this.inner.setJobState(jobId, 'Validating');
    }
    const status = await this.inner.getJob(jobId, signal);
    return this.expiresAt ? { ...status, artifactExpiresAtUtc: this.expiresAt } : status;
  }

  override getExportDownloadUrl(jobId: string): string {
    return this.overrideDownloadUrl ?? this.inner.getExportDownloadUrl(jobId);
  }
}
