/**
 * Library transfer coordinator (plan §8, §9, §18, §35; slice B3).
 *
 * Owns the framework-neutral import flow against the `LibraryTransferTransport`
 * seam. No component, DOM or visual concern lives here; B4 renders this state
 * and B8 appends activation. The state union mirrors the plan's import flow,
 * with `paused` carried inside `uploading` and reselection notices carried
 * inside `ready-to-upload`.
 */

import { Injectable, computed, effect, inject, signal } from '@angular/core';

import {
  ArchiveInspection,
  LibraryTransferFailure,
  PersistedTransferResumeState,
  PortableArchiveSummary,
  TransferCancelledError,
  TransferFileIdentity,
  TransferFlowState,
  TransferProgress,
  canTransitionFlow,
  isUploadComplete,
} from '../models/library-transfer.models';
import {
  MigrationErrorCode,
  MigrationJobState,
  MigrationJobStatusResponseDto,
  MigrationPreflightDecision,
  MigrationPreflightRequestDto,
  MigrationPreflightResponseDto,
  MigrationSessionRequestDto,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
  chunkCount,
  chunkLength,
} from '../models/migration-http.dtos';
import { FileDigestService } from './file-digest.service';
import {
  LIBRARY_TRANSFER_TRANSPORT,
  LibraryTransferTransport,
  MigrationTransportError,
  isMaintenanceBusy,
  toTransferFailure,
} from './library-transfer-transport';
import { ChunkUploadEngine, ChunkUploadRun } from './chunk-upload-engine.service';
import {
  PortableArchiveInspector,
  preflightRequestFromSummary,
} from './portable-archive-inspector.service';
import { TransferResumeStore } from './transfer-resume-store.service';
import { TransferTabLease } from './transfer-tab-lease.service';

export const DEFAULT_STATUS_POLL_MS = 1_500;

/**
 * Total time the coordinator waits out exclusive server maintenance
 * (`migration_activation_busy` / `migration_storage_contended`) before it
 * surfaces a retryable failure. The wait never loses work: every operation is
 * re-attempted with the idempotency keys persisted before its first send.
 */
export const MAINTENANCE_MAX_WAIT_MS = 5 * 60_000;

/** The fallback delay between maintenance re-attempts without `Retry-After`. */
export const MAINTENANCE_FALLBACK_BASE_MS = 500;

/** Operations that can be interrupted by exclusive server maintenance. */
export type TransferBusyOperation =
  | 'preflight'
  | 'createJob'
  | 'getJob'
  | 'cancelJob'
  | 'retryJob'
  | 'createUploadSession'
  | 'getUploadSession'
  | 'uploadChunk'
  | 'completeUpload';

export interface TransferMaintenanceWait {
  operation: TransferBusyOperation;
  retryAfterMs: number | null;
}

const IDLE_PROGRESS: TransferProgress = {
  uploadedBytes: 0,
  totalBytes: 0,
  completedChunks: 0,
  totalChunks: 0,
  inFlightBytes: 0,
  rateBytesPerSecond: null,
  etaSeconds: null,
  paused: false,
};

@Injectable({ providedIn: 'root' })
export class LibraryTransferCoordinator {
  private readonly transport = inject<LibraryTransferTransport>(LIBRARY_TRANSFER_TRANSPORT);
  private readonly digest = inject(FileDigestService);
  private readonly inspector = inject(PortableArchiveInspector);
  private readonly resumeStore = inject(TransferResumeStore);
  private readonly tabLease = inject(TransferTabLease);

  private readonly stateSignal = signal<TransferFlowState>({ kind: 'idle' });
  private readonly fallbackProgress = signal<TransferProgress>(IDLE_PROGRESS);

  constructor() {
    // The cross-tab lease belongs to the tab's transfer, not to a particular
    // mounted flow component: a terminal job must stop the heartbeat even when
    // Settings or onboarding was left while it was still running (review-736
    // item 4). `release()` is ownership-checked, so a foreign lease is never
    // removed.
    effect(() => {
      const kind = this.stateSignal().kind;
      if (kind === 'completed' || kind === 'cancelled' || kind === 'failed') {
        this.tabLease.release();
      }
    });
  }

  /** Current import flow state; the later UI renders this union. */
  readonly state = this.stateSignal.asReadonly();

  /** Aggregated transfer progress when the flow is uploading or checking. */
  readonly progress = computed(() => {
    const state = this.stateSignal();
    return state.kind === 'uploading' || state.kind === 'checking'
      ? state.progress
      : this.fallbackProgress();
  });

  /** Set while the coordinator waits out server maintenance and re-attempts. */
  readonly maintenanceWaiting = signal<TransferMaintenanceWait | null>(null);

  /**
   * True when a maintenance timeout left an interrupted operation that
   * `retry()` can continue (the Retry action must repeat that operation, never
   * call the job-level `/retry` endpoint on an active job).
   */
  readonly hasInterruptedOperation = computed(() => this.interruptedSignal() !== null);

  /** Injectable for fake-timer tests; the coordinator owns the scheduling. */
  pollIntervalMs = DEFAULT_STATUS_POLL_MS;

  private operationToken = 0;
  private operationAbort: AbortController | null = null;
  private run: ChunkUploadRun | null = null;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly interruptedSignal = signal<(() => Promise<void>) | null>(null);

  private jobId: string | null = null;
  private preflight: MigrationPreflightResponseDto | null = null;
  private preflightDecision: MigrationPreflightDecision | null = null;
  private file: File | null = null;

  // ---------------------------------------------------------------- import --

  /** Full fresh import: inspect, hash, preflight, create, session, upload, poll. */
  async startImport(file: File): Promise<void> {
    const { token, signal } = this.beginOperation();
    this.interruptedSignal.set(null);
    this.file = file;
    this.jobId = null;
    this.preflight = null;
    this.preflightDecision = null;

    try {
      this.setState({
        kind: 'inspecting',
        fileName: file.name,
        bytesRead: 0,
        totalBytes: file.size,
      });

      const inspection = await this.inspector.inspect(file, {
        signal,
        onProgress: (bytesRead, totalBytes) => {
          if (!this.isCurrent(token)) return;
          this.setState({ kind: 'inspecting', fileName: file.name, bytesRead, totalBytes });
        },
      });
      if (!this.isCurrent(token)) return;
      const summary = this.requirePortable(inspection);
      if (!summary) return;

      // Plan §9.1: whole-file SHA-256 before the transfer session exists.
      const sha256 = await this.digest.sha256(file, {
        signal,
        onProgress: (bytesRead, totalBytes) => {
          if (!this.isCurrent(token)) return;
          this.setState({ kind: 'inspecting', fileName: file.name, bytesRead, totalBytes });
        },
      });
      if (!this.isCurrent(token)) return;

      const identity: TransferFileIdentity = {
        totalSizeBytes: file.size,
        sha256Checksum: sha256,
        clientFingerprint: await this.digest.fingerprint(file),
      };
      if (!this.isCurrent(token)) return;

      this.setState({ kind: 'preflighting', summary });
      const request = preflightRequestFromSummary(file, summary);
      const preflight = await this.withBusyRetry(
        'preflight',
        token,
        () => this.transport.preflight(request, signal),
        () => this.startImport(file),
      );
      if (!this.isCurrent(token)) return;
      this.preflight = preflight;
      if (!preflight.evaluation.isAllowed) {
        this.failWith(this.failureFromPreflight(preflight), undefined);
        return;
      }

      // Persist the creation keys BEFORE any request can create server state
      // (plan §16, §17): if the response is lost, a reload must replay the same
      // keys rather than stranding the job (and its reservation).
      this.preflightDecision = preflight.evaluation.decision;
      const chunkSizeBytes = preflight.chunkSizeBytes;
      const jobCreationIdempotencyKey = newIdempotencyKey();
      const sessionCreationIdempotencyKey = newIdempotencyKey();
      const sessionRequest = this.sessionRequest(
        file,
        identity,
        chunkSizeBytes,
        sessionCreationIdempotencyKey,
      );
      this.resumeStore.save(
        this.provisionalResumeRecord(
          file,
          identity,
          request,
          preflight,
          jobCreationIdempotencyKey,
          sessionCreationIdempotencyKey,
          sessionRequest,
        ),
      );

      const created = await this.withBusyRetry(
        'createJob',
        token,
        () =>
          this.transport.createJob(
            {
              direction: 'Import',
              idempotencyKey: jobCreationIdempotencyKey,
              reservationId: preflight.reservationId,
            },
            signal,
          ),
        () => this.resume(),
      );
      if (!this.isCurrent(token)) return;
      this.jobId = created.job.id;
      this.resumeStore.update({ jobId: created.job.id });

      const sessionResponse = await this.withBusyRetry(
        'createUploadSession',
        token,
        () => this.transport.createUploadSession(created.job.id, sessionRequest, signal),
        () => this.resume(),
      );
      if (!this.isCurrent(token)) return;
      this.resumeStore.update({ sessionId: sessionResponse.session.sessionId });

      this.setState({
        kind: 'ready-to-upload',
        jobId: created.job.id,
        sessionId: sessionResponse.session.sessionId,
        preflight,
        reselectionRequired: false,
      });

      await this.uploadSession(token, signal, sessionResponse.session);
    } catch (error) {
      if (!this.isCurrent(token)) return;
      if (error instanceof TransferCancelledError) {
        this.cancelRun();
        this.setState({ kind: 'cancelled', jobId: this.jobId ?? '' });
        return;
      }
      this.failWith(this.failureFromError(error), this.jobId ?? undefined);
    }
  }

  // --------------------------------------------------------------- reattach --

  /** Reattaches to the persisted job after a reload or navigation. */
  async resume(): Promise<void> {
    const record = this.resumeStore.load();
    if (!record) {
      this.setState({ kind: 'idle' });
      return;
    }

    const { token, signal } = this.beginOperation();
    this.interruptedSignal.set(null);
    this.jobId = record.jobId ?? null;
    this.preflightDecision = record.preflightDecision ?? null;

    try {
      if (!this.jobId) {
        const recovered = await this.replayJobCreation(record, signal, token);
        if (!this.isCurrent(token)) return;
        if (!recovered) return;
      }

      const jobId = this.jobId;
      if (!jobId) return;
      let status = await this.withBusyRetry(
        'getJob',
        token,
        () => this.transport.getJob(jobId, signal),
        () => this.resume(),
      );
      if (!this.isCurrent(token)) return;

      if (!status.session && isPreActivationJobState(status.job.state)) {
        const sessionResponse = await this.ensureUploadSession(record, signal, token);
        if (!this.isCurrent(token)) return;
        status = { ...status, session: sessionResponse.session };
      }

      // A session holding every receipt but a job still short of validation
      // means the completion call was interrupted (maintenance or a lost
      // response). Replaying it is idempotent on the server.
      if (needsCompletionReplay(status)) {
        await this.withBusyRetry(
          'completeUpload',
          token,
          () => this.transport.completeUpload(jobId, signal),
          () => this.continueCompletion(),
        );
        if (!this.isCurrent(token)) return;
        status = await this.withBusyRetry(
          'getJob',
          token,
          () => this.transport.getJob(jobId, signal),
          () => this.resume(),
        );
        if (!this.isCurrent(token)) return;
      }

      this.applyJobStatus(status);
    } catch (error) {
      if (!this.isCurrent(token)) return;
      if (error instanceof TransferCancelledError) {
        this.cancelRun();
        this.setState({ kind: 'cancelled', jobId: this.jobId ?? '' });
        return;
      }
      const failure = this.failureFromError(error);
      if (failure.code === 'migration_not_found') this.resumeStore.clear();
      this.failWith(failure, record.jobId);
    }
  }

  /**
   * Resumes an incomplete upload after the user reselects the same file.
   * A different file never attaches to the old session (plan §13).
   */
  async resumeWithFile(file: File): Promise<void> {
    const record = this.resumeStore.load();
    const state = this.stateSignal();
    if (!record || state.kind !== 'ready-to-upload' || !state.reselectionRequired) return;

    const { token, signal } = this.beginOperation();
    this.jobId = record.jobId ?? null;
    this.file = file;

    try {
      const notice = await this.verifyReselectedFile(file, record.fileIdentity, signal);
      if (!this.isCurrent(token)) return;
      if (notice) {
        this.setState({ ...state, notice });
        return;
      }

      const sessionResponse = await this.ensureUploadSession(record, signal, token);
      if (!this.isCurrent(token)) return;
      await this.uploadSession(token, signal, sessionResponse.session);
    } catch (error) {
      if (!this.isCurrent(token)) return;
      if (error instanceof TransferCancelledError) {
        this.cancelRun();
        this.setState({ kind: 'cancelled', jobId: record.jobId ?? '' });
        return;
      }
      this.failWith(this.failureFromError(error), record.jobId);
    }
  }

  // ------------------------------------------------------------- operations --

  /** Pauses scheduling; in-flight chunk requests are awaited, not aborted. */
  async pauseUpload(): Promise<void> {
    await this.run?.pause();
    const state = this.stateSignal();
    if (state.kind === 'uploading') {
      this.setState({ ...state, paused: true, progress: { ...state.progress, paused: true } });
    }
  }

  async resumeUpload(): Promise<void> {
    // The run may not exist yet (the coordinator is still re-reading the
    // authoritative session). Clearing the paused intent here makes the engine
    // start unpaused instead of stalling behind a pause nobody can resume.
    const state = this.stateSignal();
    if (state.kind === 'uploading' && state.paused) {
      this.setState({ ...state, paused: false, progress: { ...state.progress, paused: false } });
    }
    if (this.run) await this.run.resume();
  }

  /** Cancels the browser transfer first, then records durable cancellation. */
  async cancel(): Promise<void> {
    const jobId = this.jobId;
    this.operationToken += 1;
    this.operationAbort?.abort();
    this.operationAbort = new AbortController();
    const token = this.operationToken;
    const signal = this.operationAbort.signal;
    this.interruptedSignal.set(null);
    this.stopPolling();
    this.cancelRun();

    if (!jobId) {
      this.resumeStore.clear();
      this.setState({ kind: 'cancelled', jobId: '' });
      return;
    }

    try {
      // The cancellation itself waits out maintenance: cancelling is exactly
      // what the user asked for, so it is always re-attempted.
      await this.withBusyRetry(
        'cancelJob',
        token,
        () => this.transport.cancelJob(jobId, undefined, signal),
        () => this.cancel(),
      );
      this.resumeStore.clear();
      this.setState({ kind: 'cancelled', jobId });
    } catch (error) {
      if (!this.isCurrent(token)) return;
      if (error instanceof TransferCancelledError) return;
      const failure = toTransferFailure(error);
      if (failure.code === 'migration_cannot_cancel') {
        // Activation is the point of no return; server processing continues.
        this.schedulePoll();
        return;
      }
      this.failWith(failure, jobId);
    }
  }

  /**
   * Server-side retry for a Failed/Cancelled/Expired job, then reattach. When a
   * maintenance timeout interrupted an operation, this instead repeats that
   * operation: `/jobs/{id}/retry` is only ever called for a job that is
   * actually in a retryable terminal state.
   */
  async retry(): Promise<void> {
    const interrupted = this.interruptedSignal();
    if (interrupted) {
      this.interruptedSignal.set(null);
      await interrupted();
      return;
    }

    const jobId = this.jobId ?? this.resumeStore.load()?.jobId;
    if (!jobId) return;

    const { token, signal } = this.beginOperation();
    const retryKey = newIdempotencyKey();
    try {
      const status = await this.withBusyRetry(
        'retryJob',
        token,
        () => this.transport.retryJob(jobId, retryKey, signal),
        () => this.retry(),
      );
      if (!this.isCurrent(token)) return;
      this.applyJobStatus(status);
    } catch (error) {
      if (!this.isCurrent(token)) return;
      if (error instanceof TransferCancelledError) return;
      this.failWith(this.failureFromError(error), jobId);
    }
  }

  /**
   * Acknowledges a terminal state and returns to idle. For a verified but
   * un-activatable job (`ready-empty` / `replacement-confirmation`) the resume
   * record is deliberately kept, so dismissing the UI does not throw away the
   * server job (review-730 item 2); cancel clears it instead.
   */
  dismiss(): void {
    this.interruptedSignal.set(null);
    const state = this.stateSignal();
    if (state.kind === 'completed' || state.kind === 'cancelled') {
      this.resumeStore.clear();
      this.file = null;
      this.setState({ kind: 'idle' });
      return;
    }
    if (state.kind === 'ready-empty' || state.kind === 'replacement-confirmation') {
      this.setState({ kind: 'idle' });
    }
  }

  /** One guarded polling step; public so the UI can refresh on visibility. */
  async refreshStatus(): Promise<void> {
    const token = this.operationToken;
    const jobId = this.jobId ?? this.resumeStore.load()?.jobId;
    if (!jobId) return;
    try {
      const status = await this.withBusyRetry(
        'getJob',
        token,
        () => this.transport.getJob(jobId),
        () => this.resume(),
      );
      if (token !== this.operationToken) return;
      this.applyJobStatus(status);
    } catch (error) {
      if (token !== this.operationToken) return;
      if (error instanceof TransferCancelledError) return;
      const failure = this.failureFromError(error);
      if (failure.code === 'migration_not_found') this.resumeStore.clear();
      this.failWith(failure, jobId);
    }
  }

  // ----------------------------------------------------------------- upload --

  private async uploadSession(
    token: number,
    signal: AbortSignal,
    session: MigrationSessionStatusDto,
  ): Promise<void> {
    const jobId = this.jobId;
    if (!jobId || !this.file) {
      this.failWith(
        this.failure('migration_file_identity_mismatch', 'Select the same file to continue.'),
        jobId ?? undefined,
      );
      return;
    }

    const initial = this.progressFromSession(session);
    this.fallbackProgress.set(initial);
    this.setState({ kind: 'uploading', jobId, progress: initial, paused: false });

    const uploadOutcome = await this.withBusyRetry(
      'uploadChunk',
      token,
      () => this.uploadMissingChunks(token, signal),
      () => this.resume(),
    );
    if (!this.isCurrent(token)) return;
    if (uploadOutcome.kind === 'cancelled') {
      this.setState({ kind: 'cancelled', jobId });
      return;
    }

    this.setState({
      kind: 'checking',
      jobId,
      jobState: 'Validating',
      progress: uploadOutcome.progress,
      preflight: this.preflight ?? undefined,
    });

    await this.withBusyRetry(
      'completeUpload',
      token,
      () => this.completeUploadAndRefresh(token, signal, jobId),
      () => this.continueCompletion(),
    );
  }

  /**
   * Continuation for a completion interrupted by maintenance: re-sends the
   * idempotent completion for the same durable job and then refreshes status.
   * No `/retry` and no file reselection are needed.
   */
  private async continueCompletion(): Promise<void> {
    const jobId = this.jobId ?? this.resumeStore.load()?.jobId;
    if (!jobId) {
      await this.resume();
      return;
    }
    const { token, signal } = this.beginOperation();
    this.interruptedSignal.set(null);
    try {
      await this.completeUploadAndRefresh(token, signal, jobId);
    } catch (error) {
      if (!this.isCurrent(token)) return;
      if (error instanceof TransferCancelledError) return;
      this.failWith(this.failureFromError(error), jobId);
    }
  }

  /**
   * Uploads the chunks the server has not acknowledged. The authoritative
   * session is re-read on every attempt, so a maintenance retry never
   * re-uploads bytes the server already holds.
   */
  private async uploadMissingChunks(
    token: number,
    signal: AbortSignal,
  ): Promise<{ kind: 'completed'; progress: TransferProgress } | { kind: 'cancelled' }> {
    const jobId = this.jobId;
    const file = this.file;
    if (!jobId || !file) throw new TransferCancelledError('The import was cancelled.');

    const latest = await this.withBusyRetry(
      'getUploadSession',
      token,
      () => this.transport.getUploadSession(jobId, signal),
      () => this.resume(),
    );
    if (!this.isCurrent(token)) throw new TransferCancelledError('The import was cancelled.');

    const session = latest.session;
    const progress = this.progressFromSession(session);
    this.fallbackProgress.set(progress);
    const current = this.stateSignal();
    if (current.kind === 'uploading') this.setState({ ...current, progress });

    if (session.receivedChunkCount >= session.totalChunks) {
      return { kind: 'completed', progress };
    }

    const engine = new ChunkUploadEngine(this.transport, this.digest);
    const run = engine.start({
      jobId,
      session,
      file,
      signal,
      onProgress: (update) => {
        if (!this.isCurrent(token)) return;
        const state = this.stateSignal();
        if (state.kind !== 'uploading') return;
        this.fallbackProgress.set(update);
        this.setState({ ...state, progress: update, paused: state.paused });
      },
    });
    this.run = run;
    if (current.kind === 'uploading' && current.paused) await run.pause();

    const outcome = await run.done;
    this.run = null;
    if (outcome.kind === 'cancelled') return { kind: 'cancelled' };
    return { kind: 'completed', progress: run.progress };
  }

  /** Idempotent completion, then the durable status that follows it. */
  private async completeUploadAndRefresh(
    token: number,
    signal: AbortSignal,
    jobId: string,
  ): Promise<void> {
    const completed = await this.transport.completeUpload(jobId, signal);
    if (!this.isCurrent(token)) return;
    if (!isUploadComplete(completed)) {
      this.failWith(
        this.failure('migration_invalid_state', 'The server did not accept the completed upload.'),
        jobId,
      );
      return;
    }
    await this.refreshStatus();
  }

  private progressFromSession(session: MigrationSessionStatusDto): TransferProgress {
    return {
      uploadedBytes: session.receivedChunks.reduce(
        (total, index) => total + chunkLength(index, session.totalBytes, session.chunkSize),
        0,
      ),
      totalBytes: session.totalBytes,
      completedChunks: session.receivedChunkCount,
      totalChunks: session.totalChunks,
      inFlightBytes: 0,
      rateBytesPerSecond: null,
      etaSeconds: null,
      paused: false,
    };
  }

  private async verifyReselectedFile(
    file: File,
    identity: TransferFileIdentity,
    signal: AbortSignal,
  ): Promise<LibraryTransferFailure | null> {
    if (file.size !== identity.totalSizeBytes) return this.identityMismatch();

    const fingerprint = await this.digest.fingerprint(file);
    if (identity.clientFingerprint && fingerprint !== identity.clientFingerprint) {
      return this.identityMismatch();
    }

    const sha256 = await this.digest.sha256(file, { signal });
    if (sha256.toLowerCase() !== identity.sha256Checksum.toLowerCase()) {
      return this.identityMismatch();
    }

    return null;
  }

  private identityMismatch(): LibraryTransferFailure {
    return this.failure(
      'migration_file_identity_mismatch',
      'The selected file no longer matches this upload.',
    );
  }

  private applyJobStatus(status: MigrationJobStatusResponseDto): void {
    this.jobId = status.job.id;
    this.stopPolling();
    const state = this.stateSignal();
    const preflight = this.preflight ?? undefined;
    const progress =
      state.kind === 'uploading' || state.kind === 'checking'
        ? state.progress
        : this.fallbackProgress();

    switch (status.job.state) {
      case 'ReadyToActivate':
        if (this.preflightDecision === 'AllowedReplacementRequired') {
          this.setState({
            kind: 'replacement-confirmation',
            jobId: status.job.id,
            jobState: status.job.state,
            preflight,
            preparedImport: status.preparedImport ?? undefined,
          });
        } else {
          this.setState({
            kind: 'ready-empty',
            jobId: status.job.id,
            jobState: status.job.state,
            preflight,
            preparedImport: status.preparedImport ?? undefined,
          });
        }
        return;
      case 'Completed':
        this.setState({ kind: 'completed', jobId: status.job.id });
        return;
      case 'Cancelled':
        this.setState({ kind: 'cancelled', jobId: status.job.id });
        return;
      case 'Failed':
        this.failWith(
          this.failure(
            'portable_import_failed',
            status.job.failureMessage ?? 'The import failed on the server.',
          ),
          status.job.id,
        );
        return;
      case 'Expired':
        this.failWith(
          this.failure(
            'migration_session_expired',
            'The import session expired. Retry to continue.',
            true,
          ),
          status.job.id,
        );
        return;
      case 'Activating':
        this.setState({
          kind: 'checking',
          jobId: status.job.id,
          jobState: status.job.state,
          progress,
          preflight,
        });
        this.schedulePoll();
        return;
      default: {
        const session = status.session ?? null;
        if (!session || (session.state !== 'Complete' && session.state !== 'Cancelled')) {
          this.file = null;
          this.setState({
            kind: 'ready-to-upload',
            jobId: status.job.id,
            sessionId: session?.sessionId,
            preflight,
            reselectionRequired: true,
          });
          return;
        }
        this.setState({
          kind: 'checking',
          jobId: status.job.id,
          jobState: status.job.state,
          progress,
          preflight,
        });
        this.schedulePoll();
      }
    }
  }

  private isPollingState(): boolean {
    const kind = this.stateSignal().kind;
    return kind === 'checking' || kind === 'ready-to-upload';
  }

  private schedulePoll(delayMs?: number): void {
    this.stopPolling();
    this.pollTimer = setTimeout(() => {
      this.pollTimer = null;
      void this.refreshStatus().then(() => {
        // A busy-status retry may have scheduled the next poll with the
        // server's Retry-After; never overwrite that delay.
        if (this.isPollingState() && this.pollTimer === null) this.schedulePoll();
      });
    }, delayMs ?? this.pollIntervalMs);
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  // ------------------------------------------------------------- internals --

  private beginOperation(): { token: number; signal: AbortSignal } {
    this.stopPolling();
    this.operationAbort?.abort();
    this.operationToken += 1;
    this.operationAbort = new AbortController();
    return { token: this.operationToken, signal: this.operationAbort.signal };
  }

  private isCurrent(token: number): boolean {
    return token === this.operationToken;
  }

  private setState(next: TransferFlowState): void {
    const current = this.stateSignal();
    if (current.kind !== next.kind && !canTransitionFlow(current.kind, next.kind)) {
      throw new Error(`Illegal library transfer transition from ${current.kind} to ${next.kind}.`);
    }
    this.stateSignal.set(next);
  }

  private failWith(failure: LibraryTransferFailure, jobId: string | undefined): void {
    this.stopPolling();
    this.setState({ kind: 'failed', jobId, failure });
  }

  /**
   * Re-attempts one migration operation while the server answers with a
   * retryable maintenance outcome (`migration_activation_busy` /
   * `migration_storage_contended`). Each operation owns its idempotency keys,
   * so every re-attempt replays safely. Waiting is capped: past the cap the
   * flow fails non-destructively with `migration_maintenance_timeout`, and
   * `retry()` repeats `continuation` instead of the job-level `/retry`.
   */
  private async withBusyRetry<T>(
    operation: TransferBusyOperation,
    token: number,
    call: () => Promise<T>,
    continuation: () => Promise<void>,
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
        if (!this.isCurrent(token)) {
          throw new TransferCancelledError('The operation was cancelled.');
        }
        const failure = toTransferFailure(error);
        if (!isMaintenanceBusy(failure.code)) throw error;

        const delay =
          failure.retryAfterMs ?? Math.min(MAINTENANCE_FALLBACK_BASE_MS * 2 ** attempt, 8_000);
        attempt += 1;
        if (Date.now() - startedAt + delay > MAINTENANCE_MAX_WAIT_MS) {
          this.interruptedSignal.set(continuation);
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

  /** Abortable wait: cancelling the operation interrupts the maintenance hold. */
  private maintenanceSleep(ms: number): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      const signal = this.operationAbort?.signal;
      if (signal?.aborted) {
        reject(new TransferCancelledError('The operation was cancelled.'));
        return;
      }
      const timer = setTimeout(() => {
        cleanup();
        resolve();
      }, ms);
      const onAbort = () => {
        clearTimeout(timer);
        cleanup();
        reject(new TransferCancelledError('The operation was cancelled.'));
      };
      const cleanup = () => signal?.removeEventListener('abort', onAbort);
      signal?.addEventListener('abort', onAbort, { once: true });
    });
  }

  private cancelRun(): void {
    this.run?.cancel();
    this.run = null;
  }

  private requirePortable(inspection: ArchiveInspection): PortableArchiveSummary | null {
    if (inspection.kind === 'portable') return inspection.summary;
    if (inspection.kind === 'operational-backup') {
      this.failWith(
        this.failure(
          'archive_operational_backup',
          'This is a SelfHosted backup, not a portable library archive.',
        ),
        undefined,
      );
      return null;
    }
    const version = inspection.reason.startsWith('unsupported-');
    this.failWith(
      this.failure(
        version ? 'archive_unsupported_version' : 'archive_not_portable',
        version
          ? 'This archive was created by an unsupported Nostos version.'
          : 'This file is not a supported Nostos portable library archive.',
      ),
      undefined,
    );
    return null;
  }

  private failureFromPreflight(preflight: MigrationPreflightResponseDto): LibraryTransferFailure {
    const evaluation = preflight.evaluation;
    switch (evaluation.decision) {
      case 'RejectedOperationalBackupNotPortable':
        return this.failure(
          'archive_operational_backup',
          'This is a SelfHosted backup, not a portable library archive.',
        );
      case 'RejectedInsufficientStorage':
        return {
          ...this.failure(
            'migration_storage_exhausted',
            `This import needs about ${evaluation.requiredStorageBytes} bytes of available ` +
              `storage. ${evaluation.availableStorageBytes} bytes are available.`,
          ),
          requiredStorageBytes: evaluation.requiredStorageBytes,
          availableStorageBytes: evaluation.availableStorageBytes,
        };
      case 'RejectedDestinationConflict':
        return this.failure(
          'migration_destination_conflict',
          'This library changed since the import started. Review it and try again.',
        );
      default:
        return this.failure(
          'archive_unsupported_version',
          evaluation.errors[0] ?? 'The archive is not compatible with this host.',
        );
    }
  }

  private failure(
    code: MigrationErrorCode,
    message: string,
    retryable = false,
  ): LibraryTransferFailure {
    return { code, message, retryable };
  }

  /** Session expiry is terminal for the browser transfer but actionable for the user. */
  private failureFromError(error: unknown): LibraryTransferFailure {
    const failure = toTransferFailure(error);
    if (failure.code === 'migration_session_expired') {
      return { ...failure, retryable: true };
    }
    return failure;
  }

  private sessionRequest(
    file: File,
    identity: TransferFileIdentity,
    chunkSize: number,
    idempotencyKey: string,
  ): MigrationSessionRequestDto {
    return {
      purpose: 'Import',
      totalBytes: file.size,
      chunkSize,
      totalChunks: chunkCount(file.size, chunkSize),
      fileIdentity: identity,
      idempotencyKey,
    };
  }

  private provisionalResumeRecord(
    file: File,
    identity: TransferFileIdentity,
    preflightRequest: MigrationPreflightRequestDto,
    preflight: MigrationPreflightResponseDto,
    jobCreationIdempotencyKey: string,
    sessionCreationIdempotencyKey: string,
    sessionRequest: MigrationSessionRequestDto,
  ): PersistedTransferResumeState {
    return {
      schemaVersion: 1,
      jobCreationIdempotencyKey,
      reservationId: preflight.reservationId,
      sessionCreationIdempotencyKey,
      sessionRequest,
      chunkSizeBytes: preflight.chunkSizeBytes,
      direction: 'import',
      fileIdentity: identity,
      fileName: file.name,
      preflightRequest,
      preflightDecision: preflight.evaluation.decision,
      // The revision the user reviewed; activation binds its confirmation to
      // this server value (never a client-invented one).
      destinationRevision: preflight.evaluation.destinationRevision,
      createdAt: new Date().toISOString(),
    };
  }

  /** Replays a persisted job-creation request after a lost response. */
  private async replayJobCreation(
    record: PersistedTransferResumeState,
    signal: AbortSignal,
    token: number,
  ): Promise<boolean> {
    try {
      const created = await this.withBusyRetry(
        'createJob',
        token,
        () =>
          this.transport.createJob(
            {
              direction: 'Import',
              idempotencyKey: record.jobCreationIdempotencyKey,
              reservationId: record.reservationId ?? null,
            },
            signal,
          ),
        () => this.resume(),
      );
      this.jobId = created.job.id;
      this.resumeStore.update({ jobId: created.job.id });
      return true;
    } catch (error) {
      if (error instanceof TransferCancelledError) throw error;
      this.failWith(this.failureFromError(error), undefined);
      return false;
    }
  }

  /**
   * Returns the job's upload session, replaying the persisted creation request
   * with its persisted key when the server has none (or the earlier response
   * was lost). The server's idempotency then returns the original session.
   */
  private async ensureUploadSession(
    record: PersistedTransferResumeState,
    signal: AbortSignal,
    token: number,
  ): Promise<MigrationUploadSessionResponseDto> {
    const jobId = this.jobId ?? record.jobId;
    if (!jobId) throw new Error('No migration job to attach an upload session to.');

    if (record.sessionId) {
      try {
        return await this.withBusyRetry(
          'getUploadSession',
          token,
          () => this.transport.getUploadSession(jobId, signal),
          () => this.resume(),
        );
      } catch (error) {
        const failure = toTransferFailure(error);
        if (failure.code !== 'migration_invalid_state') throw error;
        // The server has no session for this job; fall through and replay.
      }
    }

    const sessionRequest = record.sessionRequest ?? this.sessionRequestFromRecord(record);
    if (!sessionRequest) {
      throw new MigrationTransportError(
        'migration_invalid_state',
        409,
        'The upload session request was not persisted; start the import again.',
      );
    }

    const response = await this.withBusyRetry(
      'createUploadSession',
      token,
      () => this.transport.createUploadSession(jobId, sessionRequest, signal),
      () => this.resume(),
    );
    this.jobId = jobId;
    this.resumeStore.update({
      jobId,
      sessionId: response.session.sessionId,
      sessionCreationIdempotencyKey: sessionRequest.idempotencyKey,
      sessionRequest,
    });
    return response;
  }

  /**
   * Rebuilds the session request from a legacy/partial record. Fails closed
   * when the persisted creation key is gone: manufacturing a new key against a
   * server session that already exists would be an idempotency conflict, so
   * the only safe recovery is starting the import again.
   */
  private sessionRequestFromRecord(
    record: PersistedTransferResumeState,
  ): MigrationSessionRequestDto | null {
    const chunkSize = record.chunkSizeBytes;
    const idempotencyKey = record.sessionCreationIdempotencyKey;
    if (!chunkSize || !idempotencyKey) return null;
    return {
      purpose: 'Import',
      totalBytes: record.fileIdentity.totalSizeBytes,
      chunkSize,
      totalChunks: chunkCount(record.fileIdentity.totalSizeBytes, chunkSize),
      fileIdentity: record.fileIdentity,
      idempotencyKey,
    };
  }
}

function isPreActivationJobState(state: MigrationJobState): boolean {
  return (
    state === 'Pending' ||
    state === 'Preparing' ||
    state === 'Transferring' ||
    state === 'Validating'
  );
}

/**
 * A session holding every receipt while the durable job has not reached
 * validation means the completion call was interrupted (maintenance or a lost
 * response); replaying the idempotent completion is the safe recovery.
 */
function needsCompletionReplay(status: MigrationJobStatusResponseDto): boolean {
  return (
    status.session?.state === 'Complete' &&
    (status.job.state === 'Pending' ||
      status.job.state === 'Preparing' ||
      status.job.state === 'Transferring')
  );
}

function newIdempotencyKey(): string {
  const uuid = globalThis.crypto?.randomUUID;
  if (typeof uuid === 'function') return uuid.call(globalThis.crypto);
  return `idem-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}
