/**
 * Provider-neutral transport seam for library migration (plan §3, §42).
 *
 * Components and the coordinator only ever talk to this interface. The
 * SelfHosted HTTP adapter (`HttpLibraryTransferTransport`, slice B7) and the
 * private Cloud adapter implement the same contract. Tests provide the
 * in-memory mock under `testing/`; no production module imports it.
 */

import { HttpClient } from '@angular/common/http';
import { InjectionToken, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import {
  BrowserMigrationChunk,
  MigrationActivateRequestDto,
  MigrationActivationStatusDto,
  MigrationCreateJobRequestDto,
  MigrationChunkUploadResultDto,
  MigrationJobStatusResponseDto,
  MigrationPreflightRequestDto,
  MigrationPreflightResponseDto,
  MigrationSessionRequestDto,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
} from '../models/migration-http.dtos';
import { MIGRATION_BASE_PATH } from './http-library-transfer-transport';
import {
  DIRECT_UPLOAD_CLIENT,
  DirectUploadLibraryTransferTransport,
} from './direct-upload-library-transfer-transport';
import type { LibraryTransferMode } from '../models/library-transfer.models';

export {
  MigrationActivationConflictError,
  MigrationTransportError,
  isMaintenanceBusy,
  isTransientStatus,
  toTransferFailure,
} from './migration-transport-error';

export interface LibraryTransferTransport {
  preflight(
    request: MigrationPreflightRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationPreflightResponseDto>;

  createJob(
    request: MigrationCreateJobRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto>;

  getJob(jobId: string, signal?: AbortSignal): Promise<MigrationJobStatusResponseDto>;

  /** Optional owner-scoped discovery when this browser has lost its resume record. */
  getActiveImport?(signal?: AbortSignal): Promise<MigrationJobStatusResponseDto | null>;

  cancelJob(
    jobId: string,
    reason?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto>;

  retryJob(
    jobId: string,
    idempotencyKey?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto>;

  createUploadSession(
    jobId: string,
    request: MigrationSessionRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto>;

  getUploadSession(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto>;

  uploadChunk(
    jobId: string,
    sessionId: string,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto>;

  completeUpload(jobId: string, signal?: AbortSignal): Promise<MigrationSessionStatusDto>;

  /**
   * Starts (or observes) activation for a prepared import. Resolves with the
   * current status envelope on 202; rejects with
   * `MigrationActivationConflictError` for the two 409 admission conflicts
   * that carry fresh destination facts.
   */
  activateJob(
    jobId: string,
    request: MigrationActivateRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationActivationStatusDto>;

  /** Activation status that stays answerable during exclusive maintenance. */
  getActivationStatus(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationActivationStatusDto>;

  /**
   * Optional (slice B9): resolves and pins the part-upload data path for a
   * session before any part is sent. Adapters with a single path omit it.
   * A rejection is a transient failure and is retried; it must never be
   * interpreted as "use the application-server path".
   */
  resolveUploadMode?(sessionId: string, signal?: AbortSignal): Promise<LibraryTransferMode>;

  /** Optional (slice B9): pins a mode persisted by an earlier session. */
  pinUploadMode?(sessionId: string, mode: LibraryTransferMode): void;

  /** Native browser download URL; never fetched into Angular memory (plan §39). */
  getExportDownloadUrl(jobId: string): string;
}

/**
 * The #676 orchestration text calls this seam `PortableTransferClient`; the
 * plan calls it `LibraryTransferTransport`. Both names refer to this interface.
 */
export type PortableTransferClient = LibraryTransferTransport;

/**
 * Production DI default: the real SelfHosted #679 HTTP adapter, extended with
 * the direct part-upload mode (slice B9). The mode is resolved once per upload
 * session from the server's `supportsDirectPartUpload` deployment capability
 * and pinned, so a session never switches paths. A capability read failure is
 * retried by the transport and never silently becomes "application server".
 * Tests override `LIBRARY_TRANSFER_TRANSPORT` with an in-memory transport;
 * there is no production branch that can construct the mock.
 */
export function createLibraryTransferTransport(): LibraryTransferTransport {
  const http = inject(HttpClient);
  const capabilitiesService = inject(DeploymentCapabilitiesService);

  // Every attempt asks the server again: a failed read must stay retryable
  // rather than being cached as "direct is off" (review-749 B5).
  const loadDirectUploadCapability = async (): Promise<boolean> => {
    const capabilities = await firstValueFrom(capabilitiesService.get(true));
    return capabilities.supportsDirectPartUpload === true;
  };

  return new DirectUploadLibraryTransferTransport(http, MIGRATION_BASE_PATH, {
    loadDirectUploadCapability,
    uploadClient: inject(DIRECT_UPLOAD_CLIENT),
  });
}

/**
 * DI seam. The real SelfHosted adapter is the default; the private Cloud
 * adapter (slice B9) and tests replace it through this token.
 */
export const LIBRARY_TRANSFER_TRANSPORT = new InjectionToken<LibraryTransferTransport>(
  'NOSTOS_LIBRARY_TRANSFER_TRANSPORT',
  {
    providedIn: 'root',
    factory: createLibraryTransferTransport,
  },
);
