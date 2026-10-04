/**
 * Base class for transport wrappers in the B4 component specs.
 *
 * Every method delegates to a real `MockLibraryTransferTransport`; a spec
 * subclasses it and overrides only the method whose behaviour it needs to
 * control (gating an upload, dropping a response, forcing a failure), so the
 * component always drives the same stateful mock the engine's own tests use.
 */

import {
  BrowserMigrationChunk,
  MigrationChunkUploadResultDto,
  MigrationCreateJobRequestDto,
  MigrationJobStatusResponseDto,
  MigrationPreflightRequestDto,
  MigrationPreflightResponseDto,
  MigrationSessionRequestDto,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
} from '../models/migration-http.dtos';
import { LibraryTransferTransport } from '../services/library-transfer-transport';
import { MockLibraryTransferTransport } from '../services/mock-library-transfer-transport.service';

export class DelegatingTransport implements LibraryTransferTransport {
  constructor(readonly inner: MockLibraryTransferTransport) {}

  preflight(
    request: MigrationPreflightRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationPreflightResponseDto> {
    return this.inner.preflight(request, signal);
  }

  createJob(
    request: MigrationCreateJobRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    return this.inner.createJob(request, signal);
  }

  getJob(jobId: string, signal?: AbortSignal): Promise<MigrationJobStatusResponseDto> {
    return this.inner.getJob(jobId, signal);
  }

  cancelJob(
    jobId: string,
    reason?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    return this.inner.cancelJob(jobId, reason, signal);
  }

  retryJob(
    jobId: string,
    idempotencyKey?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    return this.inner.retryJob(jobId, idempotencyKey, signal);
  }

  createUploadSession(
    jobId: string,
    request: MigrationSessionRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto> {
    return this.inner.createUploadSession(jobId, request, signal);
  }

  getUploadSession(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto> {
    return this.inner.getUploadSession(jobId, signal);
  }

  uploadChunk(
    jobId: string,
    sessionId: string,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    return this.inner.uploadChunk(jobId, sessionId, request, onProgress, signal);
  }

  completeUpload(jobId: string, signal?: AbortSignal): Promise<MigrationSessionStatusDto> {
    return this.inner.completeUpload(jobId, signal);
  }

  getExportDownloadUrl(jobId: string): string {
    return this.inner.getExportDownloadUrl(jobId);
  }
}
