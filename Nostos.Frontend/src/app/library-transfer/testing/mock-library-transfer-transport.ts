/**
 * In-memory implementation of `LibraryTransferTransport` that enforces the
 * same rules the #679 server will (plan §63 slice B1):
 *
 * - chunk sizes inside the 4-64 MiB contract range and `totalChunks` matching
 *   `ceil(totalBytes / chunkSize)`;
 * - file identity bound to the first accepted session (mismatched files are
 *   rejected with `migration_file_identity_mismatch`);
 * - idempotent job/session creation and chunk re-sends (`alreadyPresent`);
 * - typed errors with the exact #679 codes and statuses;
 * - destination-truthful preflight, cancellation and retry state rules;
 * - test-controllable latency and queued transient/permanent failures.
 *
 * It is deliberately a plain class: the DI provider decides where a real
 * adapter replaces it (slice B7).
 */

import {
  BrowserMigrationChunk,
  MigrationChunkUploadResultDto,
  MigrationCreateJobRequestDto,
  MigrationDestinationStatus,
  MigrationErrorCode,
  MigrationExistingCountsDto,
  MigrationFileIdentityDto,
  MigrationJobDto,
  MigrationJobState,
  MigrationJobStatusResponseDto,
  MigrationPreflightRequestDto,
  MigrationPreflightResponseDto,
  MigrationProgressDto,
  MigrationSessionRequestDto,
  MigrationSessionState,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
  MIGRATION_LIMITS,
  chunkCount,
  chunkLength,
  isRetryableJobState,
  isValidChunkBytes,
  isTerminalJobState,
  toChunkRanges,
} from '../models/migration-http.dtos';
import { calculateHostPeakReservationBytes, evaluatePreflight } from '../services/migration-preflight';
import {
  LibraryTransferTransport,
  MigrationTransportError,
} from '../services/library-transfer-transport';
import { sha256ChunkHex } from '../services/hash/chunk-digest';
import { readBlobBytes } from '../services/hash/blob-bytes';
import { Sha256 } from '../services/hash/sha256';

export type MockTransportOperation =
  | 'preflight'
  | 'createJob'
  | 'getJob'
  | 'cancelJob'
  | 'retryJob'
  | 'createUploadSession'
  | 'getUploadSession'
  | 'uploadChunk'
  | 'completeUpload'
  | 'getExportDownloadUrl';

export interface MockTransportFailure {
  operation: MockTransportOperation;
  code?: MigrationErrorCode;
  /** 0 means a network failure with no HTTP status (retryable). */
  status?: number;
  message?: string;
  /** How many matching calls fail before the plan is exhausted (default 1). */
  times?: number;
  /** Only fail this chunk index for `uploadChunk`. */
  chunkIndex?: number;
  retryAfterMs?: number;
  /** Extra latency before the failure is thrown. */
  latencyMs?: number;
}

export interface MockLibraryTransferTransportOptions {
  chunkSizeBytes?: number;
  destinationStatus?: MigrationDestinationStatus;
  existingCounts?: Partial<MigrationExistingCountsDto>;
  availableStorageBytes?: number;
  operationalBackup?: boolean;
  destinationRevision?: string;
  latencyMs?: number;
  /** Number of chunks served per real upload progress callback (default 1). */
  progressSteps?: number;
  /** getJob calls served as Validating before the job reaches ReadyToActivate. */
  validationPolls?: number;
  createId?: () => string;
  now?: () => number;
}

interface MockSession {
  sessionId: string;
  purpose: 'Import';
  state: MigrationSessionState;
  totalBytes: number;
  chunkSize: number;
  totalChunks: number;
  fileIdentity: MigrationFileIdentityDto;
  idempotencyKey: string;
  /** Canonical creation payload the key is bound to (server `CreationPayloadHash`). */
  creationPayload: string;
  createdAt: number;
  expiresAt: number;
  receipts: Map<number, { lengthBytes: number; sha256: string }>;
  /** Received bytes, retained so completion can re-hash the whole file. */
  chunkBlobs: Map<number, Blob>;
}

interface MockJob {
  id: string;
  direction: 'Import' | 'Export';
  state: MigrationJobState;
  createdAt: number;
  updatedAt: number;
  failureCode?: string | null;
  failureMessage?: string | null;
  reservationId?: string;
  session?: MockSession;
  downloadAvailable: boolean;
  /** Mirrors the artifact expiry state the download endpoint reports as 410. */
  artifactExpired: boolean;
  validationPollsRemaining: number;
  /** Mirrors the server's attempt counter used by the session-reactivation rule. */
  attempt: number;
}

interface MockReservation {
  id: string;
  expiresAt: number;
  claimedJobId?: string;
}

const DEFAULT_EXISTING_COUNTS: MigrationExistingCountsDto = {
  works: 0,
  books: 0,
  notes: 0,
  topics: 0,
  noteTopics: 0,
  writings: 0,
  writingNotes: 0,
  collections: 0,
  bookCollections: 0,
  acquisitions: 0,
  noteImportBookLinks: 0,
  assistantSettings: 0,
  totalRows: 0,
};

const ERROR_MESSAGES: Partial<Record<MigrationErrorCode, string>> = {
  migration_not_found: 'The migration job was not found.',
  migration_idempotency_conflict: 'The idempotency key is already bound to a different request.',
  migration_invalid_state: 'The migration job is not in a state that allows this operation.',
  migration_reservation_required: 'A valid preflight reservation is required for an import job.',
  migration_file_identity_mismatch: 'The supplied file identity does not match the session.',
  migration_chunk_conflict: 'A different chunk was already accepted for this index.',
  migration_chunk_hash_mismatch: 'The chunk checksum did not match the received bytes.',
  migration_chunk_range_invalid: 'The chunk range does not match the session contract.',
  migration_session_expired: 'The upload session has expired.',
  migration_storage_exhausted: 'The host ran out of storage during the transfer.',
  migration_cannot_cancel: 'The job can no longer be cancelled.',
  migration_not_retryable: 'Only failed, cancelled or expired jobs can be retried.',
  migration_storage_contended: 'Transfer capacity is busy. Retry shortly.',
  migration_activation_busy: 'The library is in maintenance. Try again later.',
  migration_import_preparation_unavailable:
    'Import preparation is not available on this deployment yet.',
  migration_export_artifact_unavailable:
    'Export preparation is not available on this deployment yet.',
  migration_export_not_available: 'This export job has no downloadable artifact.',
  migration_export_expired: 'The export artifact has expired.',
  migration_invalid_request: 'The migration request is malformed.',
  network_error: 'The network request failed.',
  request_aborted: 'The request was aborted.',
};

const EMPTY_STORAGE = 1024 * 1024 * 1024 * 1024;

let fallbackId = 0;

function randomId(): string {
  const uuid = globalThis.crypto?.randomUUID;
  if (typeof uuid === 'function') return uuid.call(globalThis.crypto);
  fallbackId += 1;
  return `mock-${fallbackId}-${Math.random().toString(16).slice(2, 10)}`;
}

export class MockLibraryTransferTransport implements LibraryTransferTransport {
  private readonly jobs = new Map<string, MockJob>();
  private readonly jobIdByIdempotencyKey = new Map<string, string>();
  private readonly jobPayloadByIdempotencyKey = new Map<string, string>();
  private readonly reservations = new Map<string, MockReservation>();
  private readonly failures: MockTransportFailure[] = [];
  private readonly createId: () => string;
  private readonly now: () => number;

  readonly calls: Record<MockTransportOperation, number> = {
    preflight: 0,
    createJob: 0,
    getJob: 0,
    cancelJob: 0,
    retryJob: 0,
    createUploadSession: 0,
    getUploadSession: 0,
    uploadChunk: 0,
    completeUpload: 0,
    getExportDownloadUrl: 0,
  };

  readonly uploadedChunks: number[] = [];
  readonly attemptedChunks: number[] = [];
  readonly abortedRequests: MockTransportOperation[] = [];
  activeUploads = 0;
  maxConcurrentUploads = 0;
  jobCreationCount = 0;

  constructor(private readonly options: MockLibraryTransferTransportOptions = {}) {
    const chunkSize = options.chunkSizeBytes ?? MIGRATION_LIMITS.defaultChunkBytes;
    if (
      !Number.isInteger(chunkSize) ||
      chunkSize < MIGRATION_LIMITS.minChunkBytes ||
      chunkSize > MIGRATION_LIMITS.maxChunkBytes
    ) {
      throw new Error(`Mock chunk size ${chunkSize} is outside the contract bounds.`);
    }
    this.createId = options.createId ?? randomId;
    this.now = options.now ?? (() => Date.now());
  }

  get chunkSizeBytes(): number {
    return this.options.chunkSizeBytes ?? MIGRATION_LIMITS.defaultChunkBytes;
  }

  get destinationRevision(): string {
    return this.options.destinationRevision ?? 'rev-1';
  }

  setLatency(latencyMs: number): void {
    this.options.latencyMs = latencyMs;
  }

  bumpDestinationRevision(revision = `rev-${Math.floor(this.now())}`): void {
    this.options.destinationRevision = revision;
  }

  queueFailure(failure: MockTransportFailure): void {
    this.failures.push({ times: 1, ...failure });
  }

  /**
   * Marks chunks as already received by the server, bypassing upload. When the
   * file is supplied the exact slices are retained so a later `completeUpload`
   * can re-hash the whole file, exactly as the server does.
   */
  async seedReceivedChunks(
    jobId: string,
    chunkIndexes: readonly number[],
    file?: Blob,
  ): Promise<void> {
    const session = this.requireSession(jobId);
    for (const index of chunkIndexes) {
      const lengthBytes = chunkLength(index, session.totalBytes, session.chunkSize);
      const blob = file
        ? file.slice(index * session.chunkSize, index * session.chunkSize + lengthBytes)
        : null;
      if (blob) session.chunkBlobs.set(index, blob);
      session.receipts.set(index, {
        lengthBytes,
        sha256: blob ? await sha256ChunkHex(blob) : '0'.repeat(64),
      });
    }
  }

  expireSession(jobId: string): void {
    this.requireSession(jobId).state = 'Expired';
  }

  setJobState(jobId: string, state: MigrationJobState): void {
    const job = this.requireJob(jobId);
    job.state = state;
    job.updatedAt = this.now();
  }

  markExportReady(jobId: string): void {
    const job = this.requireJob(jobId);
    job.state = 'Completed';
    job.downloadAvailable = true;
    job.artifactExpired = false;
  }

  /** Simulates the artifact retention window passing. */
  expireExportArtifact(jobId: string): void {
    const job = this.requireJob(jobId);
    if (job.direction !== 'Export') return;
    job.artifactExpired = true;
    job.downloadAvailable = false;
  }

  resolveReservationId(jobId: string): string | undefined {
    return this.jobs.get(jobId)?.reservationId;
  }

  async preflight(
    request: MigrationPreflightRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationPreflightResponseDto> {
    this.calls.preflight += 1;
    await this.before('preflight', signal);

    const existingCounts = { ...DEFAULT_EXISTING_COUNTS, ...this.options.existingCounts };
    existingCounts.totalRows = existingCountsTotal(existingCounts);
    const destinationStatus =
      this.options.destinationStatus === 'Populated' ? 'Populated' : 'Empty';
    const availableStorageBytes = this.options.availableStorageBytes ?? EMPTY_STORAGE;

    let evaluation = evaluatePreflight(request, {
      destinationStatus,
      existingCounts,
      availableStorageBytes,
      destinationRevision: this.destinationRevision,
      operationalBackup: this.options.operationalBackup ?? false,
    });

    // The server admits in two stages: the contract-bytes evaluation first,
    // then the host peak (contract bytes + one effective chunk + per-job
    // overhead) against usable capacity. A borderline import can pass the
    // first and fail the second, so the mock mirrors both.
    if (evaluation.isAllowed) {
      const hostPeakBytes = calculateHostPeakReservationBytes(
        evaluation.requiredStorageBytes,
        this.chunkSizeBytes,
      );
      if (availableStorageBytes < hostPeakBytes) {
        evaluation = {
          ...evaluation,
          decision: 'RejectedInsufficientStorage',
          isAllowed: false,
          errors: [
            ...evaluation.errors,
            `Migration requires ${hostPeakBytes} bytes of host peak storage, ` +
              `but only ${availableStorageBytes} bytes are available.`,
          ],
        };
      }
    }

    let reservationId: string | null = null;
    let reservationExpiresAtUtc: string | null = null;
    if (evaluation.isAllowed) {
      reservationId = this.createId();
      const expiresAt = this.now() + 15 * 60 * 1000;
      this.reservations.set(reservationId, { id: reservationId, expiresAt });
      reservationExpiresAtUtc = new Date(expiresAt).toISOString();
    }

    return {
      evaluation,
      reservationId,
      reservationExpiresAtUtc,
      chunkSizeBytes: this.chunkSizeBytes,
    };
  }

  async createJob(
    request: MigrationCreateJobRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    this.calls.createJob += 1;
    await this.before('createJob', signal);

    if (!request.idempotencyKey.trim()) {
      throw this.typedError('migration_invalid_request', 400);
    }

    const payload = jobCreationPayload(request);
    const existingId = this.jobIdByIdempotencyKey.get(request.idempotencyKey);
    if (existingId) {
      if (this.jobPayloadByIdempotencyKey.get(request.idempotencyKey) !== payload) {
        throw this.typedError('migration_idempotency_conflict', 409);
      }
      return this.status(this.requireJob(existingId));
    }

    if (request.direction === 'Import') {
      const reservation = request.reservationId
        ? this.reservations.get(request.reservationId)
        : undefined;
      if (!reservation || reservation.expiresAt <= this.now() || reservation.claimedJobId) {
        throw this.typedError('migration_reservation_required', 409);
      }
    }

    const timestamp = this.now();
    const job: MockJob = {
      id: this.createId(),
      direction: request.direction,
      state: 'Pending',
      createdAt: timestamp,
      updatedAt: timestamp,
      downloadAvailable: false,
      artifactExpired: false,
      validationPollsRemaining: this.options.validationPolls ?? 0,
      reservationId: request.reservationId ?? undefined,
      attempt: 1,
    };
    if (request.reservationId) {
      const reservation = this.reservations.get(request.reservationId);
      if (reservation) reservation.claimedJobId = job.id;
    }

    this.jobs.set(job.id, job);
    this.jobIdByIdempotencyKey.set(request.idempotencyKey, job.id);
    this.jobPayloadByIdempotencyKey.set(request.idempotencyKey, payload);
    this.jobCreationCount += 1;

    return this.status(job);
  }

  async getJob(jobId: string, signal?: AbortSignal): Promise<MigrationJobStatusResponseDto> {
    this.calls.getJob += 1;
    await this.before('getJob', signal);
    const job = this.jobs.get(jobId);
    if (!job) throw this.typedError('migration_not_found', 404);

    if (
      job.state === 'Validating' &&
      job.validationPollsRemaining > 0
    ) {
      job.validationPollsRemaining -= 1;
      if (job.validationPollsRemaining === 0) {
        job.state = job.direction === 'Export' ? 'Completed' : 'ReadyToActivate';
        job.downloadAvailable = job.direction === 'Export';
        job.updatedAt = this.now();
      }
    }

    return this.status(job);
  }

  async cancelJob(
    jobId: string,
    _reason?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    this.calls.cancelJob += 1;
    await this.before('cancelJob', signal);
    const job = this.jobs.get(jobId);
    if (!job) throw this.typedError('migration_not_found', 404);

    if (job.state === 'Cancelled') return this.status(job);
    if (
      job.state === 'Activating' ||
      isTerminalJobState(job.state)
    ) {
      throw this.typedError('migration_cannot_cancel', 409);
    }

    job.state = 'Cancelled';
    job.updatedAt = this.now();
    if (job.session) job.session.state = 'Cancelled';
    return this.status(job);
  }

  async retryJob(
    jobId: string,
    _idempotencyKey?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    this.calls.retryJob += 1;
    await this.before('retryJob', signal);
    const job = this.jobs.get(jobId);
    if (!job) throw this.typedError('migration_not_found', 404);

    // The server's retry path first runs the expired-session cleanup for an
    // active import job, expiring the job so it can be retried. The session
    // row is discarded with it; the browser then recreates the session through
    // the persisted request after the job is Pending again.
    const sessionExpired =
      job.session !== undefined &&
      (job.session.state === 'Expired' ||
        job.session.state === 'Cancelled' ||
        job.session.expiresAt <= this.now());
    const activeWithExpiredSession =
      sessionExpired &&
      job.direction === 'Import' &&
      (job.state === 'Pending' ||
        job.state === 'Preparing' ||
        job.state === 'Transferring' ||
        job.state === 'Validating');

    if (!isRetryableJobState(job.state) && !activeWithExpiredSession) {
      throw this.typedError('migration_not_retryable', 409);
    }

    if (activeWithExpiredSession) job.session = undefined;
    job.attempt += 1;
    job.state = 'Pending';
    job.failureCode = null;
    job.failureMessage = null;
    job.downloadAvailable = false;
    job.updatedAt = this.now();

    return this.status(job);
  }

  async createUploadSession(
    jobId: string,
    request: MigrationSessionRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto> {
    this.calls.createUploadSession += 1;
    await this.before('createUploadSession', signal);
    const job = this.jobs.get(jobId);
    if (!job) throw this.typedError('migration_not_found', 404);
    if (job.direction !== 'Import' || request.purpose !== 'Import') {
      throw this.typedError('migration_invalid_state', 409);
    }

    this.validateSessionRequest(request);

    const creationPayload = sessionCreationPayload(request);
    const existing = job.session;
    if (existing) {
      const sameKey = existing.idempotencyKey === request.idempotencyKey;
      const samePayload = existing.creationPayload === creationPayload;

      // Server `CheckPayload` order: a reused key with a changed payload is a
      // conflict; a different file identity is an identity mismatch; then a
      // different key or payload is still an idempotency conflict.
      if (sameKey && !samePayload) {
        throw this.typedError('migration_idempotency_conflict', 409);
      }
      if (!identityEquals(existing.fileIdentity, request.fileIdentity)) {
        throw this.typedError('migration_file_identity_mismatch', 409);
      }
      if (!samePayload || !sameKey) {
        throw this.typedError('migration_idempotency_conflict', 409);
      }

      const expired =
        existing.state === 'Expired' ||
        existing.state === 'Cancelled' ||
        existing.expiresAt <= this.now();
      if (expired) {
        // Reactivation only through the job retry flow: the job must be
        // Pending again on at least its second attempt. Otherwise the durable
        // job has to be retried before the browser may recreate the session.
        if (job.state !== 'Pending' || job.attempt <= 1) {
          throw this.typedError('migration_invalid_state', 409);
        }
        existing.state = 'Created';
        existing.createdAt = this.now();
        existing.expiresAt = this.now() + 24 * 60 * 60 * 1000;
      }

      return this.sessionResponse(existing);
    }

    const timestamp = this.now();
    const session: MockSession = {
      sessionId: this.createId(),
      purpose: 'Import',
      state: 'Created',
      totalBytes: request.totalBytes,
      chunkSize: request.chunkSize,
      totalChunks: request.totalChunks,
      fileIdentity: { ...request.fileIdentity },
      idempotencyKey: request.idempotencyKey,
      creationPayload,
      createdAt: timestamp,
      expiresAt: timestamp + 24 * 60 * 60 * 1000,
      receipts: new Map(),
      chunkBlobs: new Map(),
    };

    job.session = session;

    return this.sessionResponse(session);
  }

  async getUploadSession(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto> {
    this.calls.getUploadSession += 1;
    await this.before('getUploadSession', signal);
    const session = this.requireSession(jobId);
    this.throwIfExpired(session);
    return this.sessionResponse(session);
  }

  async uploadChunk(
    jobId: string,
    sessionId: string,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    this.calls.uploadChunk += 1;
    this.attemptedChunks.push(request.index);

    const session = this.requireSession(jobId);
    if (session.sessionId !== sessionId) throw this.typedError('migration_not_found', 404);
    this.throwIfExpired(session);

    const failure = this.takeFailure('uploadChunk', request.index);
    const latency = failure?.latencyMs ?? this.options.latencyMs ?? 0;

    this.activeUploads += 1;
    this.maxConcurrentUploads = Math.max(this.maxConcurrentUploads, this.activeUploads);
    try {
      await this.wait(latency, signal, 'uploadChunk');
      if (failure) throw this.failureError(failure);

      this.validateChunkRange(session, request);

      const actual = await sha256ChunkHex(request.blob);
      if (actual.toLowerCase() !== request.sha256.toLowerCase()) {
        throw this.typedError('migration_chunk_hash_mismatch', 422);
      }
      if (signal.aborted) this.abortNow('uploadChunk');

      const existing = session.receipts.get(request.index);
      if (existing) {
        if (
          existing.lengthBytes === request.lengthBytes &&
          existing.sha256.toLowerCase() === request.sha256.toLowerCase()
        ) {
          onProgress(request.lengthBytes, request.lengthBytes);
          return { sessionId, chunkIndex: request.index, alreadyPresent: true };
        }
        throw this.typedError('migration_chunk_conflict', 409);
      }

      const steps = Math.max(1, this.options.progressSteps ?? 1);
      for (let step = 1; step <= steps; step += 1) {
        if (signal.aborted) this.abortNow('uploadChunk');
        onProgress(Math.floor((request.lengthBytes * step) / steps), request.lengthBytes);
        if (step < steps) await this.wait(latency / steps, signal, 'uploadChunk');
      }

      session.receipts.set(request.index, {
        lengthBytes: request.lengthBytes,
        sha256: request.sha256,
      });
      session.chunkBlobs.set(request.index, request.blob);
      session.state = 'Receiving';
      this.uploadedChunks.push(request.index);

      return { sessionId, chunkIndex: request.index, alreadyPresent: false };
    } finally {
      this.activeUploads -= 1;
    }
  }

  async completeUpload(jobId: string, signal?: AbortSignal): Promise<MigrationSessionStatusDto> {
    this.calls.completeUpload += 1;
    await this.before('completeUpload', signal);
    const session = this.requireSession(jobId);
    this.throwIfExpired(session);

    if (session.state === 'Complete') return this.sessionDto(session);

    const receivedBytes = [...session.receipts.values()].reduce(
      (total, receipt) => total + receipt.lengthBytes,
      0,
    );
    if (session.receipts.size !== session.totalChunks || receivedBytes !== session.totalBytes) {
      throw this.typedError('migration_invalid_state', 409);
    }

    // The server streams SHA-256 over the sealed bytes and compares them with
    // the identity bound at session creation. The mock hashes its received
    // chunk blobs in index order with the same incremental primitive, never
    // materialising the whole file.
    const wholeFile = new Sha256();
    for (let index = 0; index < session.totalChunks; index += 1) {
      const blob = session.chunkBlobs.get(index);
      if (!blob) throw this.typedError('migration_invalid_state', 409);
      wholeFile.update(await readBlobBytes(blob));
    }
    if (wholeFile.hex().toLowerCase() !== session.fileIdentity.sha256Checksum.toLowerCase()) {
      throw this.typedError('migration_file_identity_mismatch', 409);
    }

    session.state = 'Complete';
    const job = this.requireJob(jobId);
    job.state = job.validationPollsRemaining > 0 ? 'Validating' : 'ReadyToActivate';
    job.updatedAt = this.now();

    return this.sessionDto(session);
  }

  getExportDownloadUrl(jobId: string): string {
    this.calls.getExportDownloadUrl += 1;
    const job = this.jobs.get(jobId);
    if (!job || job.direction !== 'Export') throw this.typedError('migration_not_found', 404);
    if (job.artifactExpired) throw this.typedError('migration_export_expired', 410);
    if (!job.downloadAvailable) {
      // The merged download route answers 404 migration_export_not_available
      // while no artifact is sealed and 410 migration_export_expired after the
      // retention window.
      throw this.typedError('migration_export_not_available', 404);
    }
    return `/api/portability/migration/jobs/${jobId}/export-download`;
  }

  private validateSessionRequest(request: MigrationSessionRequestDto): void {
    if (!request.idempotencyKey.trim()) throw this.typedError('migration_invalid_request', 400);
    if (!Number.isInteger(request.chunkSize) || !isValidChunkSize(request.chunkSize)) {
      throw this.typedError('migration_invalid_request', 400);
    }
    if (request.totalBytes <= 0 || request.totalBytes > MIGRATION_LIMITS.maxArchiveBytes) {
      throw this.typedError('migration_invalid_request', 400);
    }
    if (request.fileIdentity.totalSizeBytes !== request.totalBytes) {
      throw this.typedError('migration_file_identity_mismatch', 409);
    }
    if (!/^[0-9a-f]{64}$/i.test(request.fileIdentity.sha256Checksum)) {
      throw this.typedError('migration_invalid_request', 400);
    }
    if (request.totalChunks !== chunkCount(request.totalBytes, request.chunkSize)) {
      throw this.typedError('migration_invalid_request', 400);
    }
  }

  private validateChunkRange(session: MockSession, request: BrowserMigrationChunk): void {
    const isLast = request.index === session.totalChunks - 1;
    const expectedOffset = request.index * session.chunkSize;
    const expectedLength = chunkLength(request.index, session.totalBytes, session.chunkSize);
    const validIndex = Number.isInteger(request.index) && request.index >= 0 && request.index < session.totalChunks;
    const validBody =
      isValidChunkBytes(request.lengthBytes, session.chunkSize, isLast) &&
      request.lengthBytes === expectedLength &&
      request.offsetBytes === expectedOffset &&
      request.blob.size === request.lengthBytes;

    if (!validIndex || !validBody) {
      throw this.typedError('migration_chunk_range_invalid', 416);
    }
  }

  private throwIfExpired(session: MockSession): void {
    if (session.state === 'Expired' || session.expiresAt <= this.now()) {
      session.state = 'Expired';
      throw this.typedError('migration_session_expired', 410);
    }
  }

  private sessionResponse(session: MockSession): MigrationUploadSessionResponseDto {
    const chunks = [...session.receipts.keys()].sort((a, b) => a - b);
    return {
      session: this.sessionDto(session),
      receivedRanges: toChunkRanges(chunks),
    };
  }

  private sessionDto(session: MockSession): MigrationSessionStatusDto {
    const chunks = [...session.receipts.keys()].sort((a, b) => a - b);
    return {
      sessionId: session.sessionId,
      purpose: session.purpose,
      state: session.state,
      totalBytes: session.totalBytes,
      chunkSize: session.chunkSize,
      totalChunks: session.totalChunks,
      fileIdentity: { ...session.fileIdentity },
      receivedChunks: chunks,
      receivedChunkCount: chunks.length,
      createdAtUtc: new Date(session.createdAt).toISOString(),
      expiresAtUtc: new Date(session.expiresAt).toISOString(),
      idempotencyKey: session.idempotencyKey,
    };
  }

  private status(job: MockJob): MigrationJobStatusResponseDto {
    const progress = this.progressFor(job);
    return {
      job: this.jobDto(job),
      progress,
      session: job.session ? this.sessionDto(job.session) : null,
      downloadAvailable: job.downloadAvailable,
      artifactExpiresAtUtc: null,
      preparedImport: null,
    };
  }

  private progressFor(job: MockJob): MigrationProgressDto {
    const session = job.session;
    const receivedBytes = session
      ? [...session.receipts.values()].reduce((total, receipt) => total + receipt.lengthBytes, 0)
      : 0;
    const totalBytes = session?.totalBytes ?? null;
    const completedChunks = session?.receipts.size ?? null;
    const totalChunks = session?.totalChunks ?? null;

    switch (job.state) {
      case 'Pending':
        return { phase: 'Pending', bytesProcessed: 0, totalBytes, completedChunks, totalChunks };
      case 'Preparing':
        return { phase: 'Preparing', bytesProcessed: 0, totalBytes, completedChunks, totalChunks };
      case 'Transferring':
        return {
          phase: 'Transferring',
          bytesProcessed: receivedBytes,
          totalBytes,
          completedChunks,
          totalChunks,
        };
      case 'Validating':
        return {
          phase: 'Validating',
          bytesProcessed: totalBytes ?? 0,
          totalBytes,
          completedChunks,
          totalChunks,
        };
      case 'ReadyToActivate':
        return {
          phase: 'Validating',
          bytesProcessed: totalBytes ?? 0,
          totalBytes,
          completedChunks,
          totalChunks,
        };
      case 'Activating':
        return { phase: 'Activating', bytesProcessed: totalBytes ?? 0, totalBytes };
      case 'Completed':
        return { phase: 'Completed', bytesProcessed: totalBytes ?? 0, totalBytes };
      default:
        return { phase: 'Pending', bytesProcessed: 0, totalBytes };
    }
  }

  private jobDto(job: MockJob): MigrationJobDto {
    return {
      id: job.id,
      direction: job.direction,
      state: job.state,
      recoveryStatus: 'NotRequired',
      createdAtUtc: new Date(job.createdAt).toISOString(),
      updatedAtUtc: new Date(job.updatedAt).toISOString(),
      leaseToken: null,
      leaseExpiresAtUtc: null,
      failureCode: job.failureCode ?? null,
      failureMessage: job.failureMessage ?? null,
    };
  }

  private requireJob(jobId: string): MockJob {
    const job = this.jobs.get(jobId);
    if (!job) throw this.typedError('migration_not_found', 404);
    return job;
  }

  private requireSession(jobId: string): MockSession {
    const job = this.requireJob(jobId);
    if (!job.session) throw this.typedError('migration_invalid_state', 409);
    return job.session;
  }

  private typedError(
    code: MigrationErrorCode,
    status: number,
    retryAfterMs?: number,
  ): MigrationTransportError {
    return new MigrationTransportError(code, status, ERROR_MESSAGES[code] ?? code, {
      retryAfterMs,
    });
  }

  private failureError(failure: MockTransportFailure): MigrationTransportError {
    const code = failure.code ?? 'network_error';
    return new MigrationTransportError(
      code,
      failure.status ?? 0,
      failure.message ?? ERROR_MESSAGES[code] ?? code,
      { retryAfterMs: failure.retryAfterMs },
    );
  }

  private takeFailure(
    operation: MockTransportOperation,
    chunkIndex?: number,
  ): MockTransportFailure | null {
    const index = this.failures.findIndex(
      (failure) =>
        failure.operation === operation &&
        (failure.chunkIndex === undefined || failure.chunkIndex === chunkIndex),
    );
    if (index < 0) return null;

    const failure = this.failures[index];
    const remaining = (failure.times ?? 1) - 1;
    if (remaining <= 0) this.failures.splice(index, 1);
    else failure.times = remaining;
    return failure;
  }

  private async before(
    operation: MockTransportOperation,
    signal?: AbortSignal,
  ): Promise<void> {
    const failure = this.takeFailure(operation);
    await this.wait(failure?.latencyMs ?? this.options.latencyMs ?? 0, signal, operation);
    if (failure) throw this.failureError(failure);
    signal?.throwIfAborted?.();
    if (signal?.aborted) this.abortNow(operation);
  }

  private async wait(
    ms: number,
    signal: AbortSignal | undefined,
    operation: MockTransportOperation,
  ): Promise<void> {
    if (signal?.aborted) this.abortNow(operation);
    if (ms <= 0) {
      await Promise.resolve();
      return;
    }

    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        signal?.removeEventListener('abort', onAbort);
        resolve();
      }, ms);
      const onAbort = () => {
        clearTimeout(timer);
        this.abortedRequests.push(operation);
        reject(
          new MigrationTransportError('request_aborted', 0, ERROR_MESSAGES.request_aborted!),
        );
      };
      signal?.addEventListener('abort', onAbort, { once: true });
    });
  }

  private abortNow(operation: MockTransportOperation): never {
    this.abortedRequests.push(operation);
    throw new MigrationTransportError('request_aborted', 0, ERROR_MESSAGES.request_aborted!);
  }
}

function existingCountsTotal(counts: MigrationExistingCountsDto): number {
  return (
    counts.works +
    counts.books +
    counts.notes +
    counts.topics +
    counts.noteTopics +
    counts.writings +
    counts.writingNotes +
    counts.collections +
    counts.bookCollections +
    counts.acquisitions +
    counts.noteImportBookLinks +
    counts.assistantSettings
  );
}

function jobCreationPayload(request: MigrationCreateJobRequestDto): string {
  return JSON.stringify({
    direction: request.direction,
    reservationId: request.reservationId ?? null,
  });
}

function sessionCreationPayload(request: MigrationSessionRequestDto): string {
  return JSON.stringify({
    purpose: request.purpose,
    totalBytes: request.totalBytes,
    chunkSize: request.chunkSize,
    totalChunks: request.totalChunks,
    fileIdentity: {
      totalSizeBytes: request.fileIdentity.totalSizeBytes,
      sha256Checksum: request.fileIdentity.sha256Checksum.toLowerCase(),
      clientFingerprint: request.fileIdentity.clientFingerprint ?? null,
    },
  });
}

function identityEquals(
  left: MigrationFileIdentityDto,
  right: MigrationFileIdentityDto,
): boolean {
  return (
    left.totalSizeBytes === right.totalSizeBytes &&
    left.sha256Checksum.toLowerCase() === right.sha256Checksum.toLowerCase() &&
    (left.clientFingerprint ?? null) === (right.clientFingerprint ?? null)
  );
}

function isValidChunkSize(chunkSize: number): boolean {
  return chunkSize >= MIGRATION_LIMITS.minChunkBytes && chunkSize <= MIGRATION_LIMITS.maxChunkBytes;
}
