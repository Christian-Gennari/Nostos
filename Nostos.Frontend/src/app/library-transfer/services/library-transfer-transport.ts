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
import { catchError, firstValueFrom, of } from 'rxjs';

import type { DeploymentCapabilities } from '../../core/dtos/deployment-capabilities.dtos';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import {
  BrowserMigrationChunk,
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

export {
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
 * the direct part-upload mode (slice B9). Whether the direct mode is used is
 * decided per upload from the server's `supportsDirectPartUpload` deployment
 * capability; absent/unreadable keeps the application-server chunk path
 * exactly as before. Tests override `LIBRARY_TRANSFER_TRANSPORT` with an
 * in-memory transport; there is no production branch that can construct the
 * mock.
 */
export function createLibraryTransferTransport(): LibraryTransferTransport {
  const http = inject(HttpClient);
  const capabilitiesService = inject(DeploymentCapabilitiesService);

  // The capability is read lazily on the first part upload and cached for the
  // transport's lifetime; a failed read defaults to off (application server).
  let capabilities: Promise<DeploymentCapabilities | null> | null = null;
  const directPartUpload = async (): Promise<boolean> => {
    capabilities ??= firstValueFrom(
      capabilitiesService.get().pipe(catchError(() => of(null))),
    );
    return (await capabilities)?.supportsDirectPartUpload === true;
  };

  return new DirectUploadLibraryTransferTransport(http, MIGRATION_BASE_PATH, {
    directPartUpload,
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
