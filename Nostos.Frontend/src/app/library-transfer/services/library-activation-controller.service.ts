/**
 * Real activation controller (issue #680, slice B8).
 *
 * Root-scoped so the activation survives leaving Settings: the flow component
 * emits non-destructive requests and reports state, while this service owns
 * the transport protocol. It implements the #681 client contract exactly:
 *
 * - `POST /activate` is admitted with the destination revision the server
 *   returned (preflight, activation status or a 409 conflict body) — never a
 *   revision invented client-side;
 * - after a 202 the status route is polled with backoff, tolerating the 503
 *   maintenance window and transient network failures;
 * - `Accepted`/`Running` keep polling, `Completed` is final, `Failed` allows a
 *   retry only when `canActivate`, `RecoveryFailed` is fail-closed with no
 *   retry, and `Idle` + `canActivate` after an accepted request means the run
 *   was lost: it is re-issued once automatically, then surfaced;
 * - the accepted request is persisted in the resume record so a reload
 *   re-attaches to polling instead of losing the outcome.
 *
 * The controller respects the cross-tab lease: `reattach()` never starts in a
 * tab that does not own the transfer.
 */

import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';

import {
  HostActivationState,
  LibraryActivationConflictFacts,
} from '../models/library-transfer.models';
import {
  MigrationActivateRequestDto,
  MigrationActivationPhase,
  MigrationActivationStatusDto,
  MigrationErrorCode,
  SERVER_MIGRATION_ERROR_CODES,
} from '../models/migration-http.dtos';
import {
  LIBRARY_TRANSFER_TRANSPORT,
  LibraryTransferTransport,
  MigrationActivationConflictError,
  isMaintenanceBusy,
  toTransferFailure,
} from './library-transfer-transport';
import { TransferResumeStore } from './transfer-resume-store.service';
import { TransferTabLease } from './transfer-tab-lease.service';

export const ACTIVATION_POLL_MS = 1_500;
export const ACTIVATION_POLL_MAX_MS = 15_000;
export const ACTIVATION_POST_RETRY_MS = 1_000;

/** Recovery copy facts the server reports after a completed replacement. */
export interface ActivationRecoveryInfo {
  available: boolean;
  expiresAtUtc: string | null;
  sizeBytes: number | null;
}

/** Everything the import flow and its hosts render for activation. */
export interface ActivationView {
  jobId: string | null;
  state: HostActivationState;
  phase: MigrationActivationPhase | null;
  errorCode: MigrationErrorCode | null;
  canRetry: boolean;
  maintenanceRequired: boolean;
  /** Fresh destination facts from a 409, for the replacement dialog. */
  conflict: LibraryActivationConflictFacts | null;
  recovery: ActivationRecoveryInfo | null;
}

const IDLE_VIEW: ActivationView = {
  jobId: null,
  state: 'idle',
  phase: null,
  errorCode: null,
  canRetry: false,
  maintenanceRequired: false,
  conflict: null,
  recovery: null,
};

@Injectable({ providedIn: 'root' })
export class LibraryActivationController implements OnDestroy {
  private readonly transport = inject<LibraryTransferTransport>(LIBRARY_TRANSFER_TRANSPORT);
  private readonly resumeStore = inject(TransferResumeStore);
  private readonly tabLease = inject(TransferTabLease);

  private readonly viewSignal = signal<ActivationView>(IDLE_VIEW);

  /** Injectable for fake-timer tests; the controller owns the scheduling. */
  pollIntervalMs = ACTIVATION_POLL_MS;
  maxPollIntervalMs = ACTIVATION_POLL_MAX_MS;

  /** The current activation state; hosts bind this into the import flow. */
  readonly view = this.viewSignal.asReadonly();

  readonly state = computed(() => this.viewSignal().state);
  readonly phase = computed(() => this.viewSignal().phase);
  readonly errorCode = computed(() => this.viewSignal().errorCode);
  readonly canRetry = computed(() => this.viewSignal().canRetry);
  readonly maintenanceRequired = computed(() => this.viewSignal().maintenanceRequired);
  readonly conflict = computed(() => this.viewSignal().conflict);
  readonly recovery = computed(() => this.viewSignal().recovery);

  private generation = 0;
  private jobId: string | null = null;
  private request: MigrationActivateRequestDto | null = null;
  /** A 202 was observed for the current request. */
  private accepted = false;
  /** A POST was attempted; an ambiguous failure may still have reached the host. */
  private postAttempted = false;
  private lostRunReissues = 0;
  /** Destination conflicts that already forced a fresh confirmation. */
  private conflictRounds = 0;
  private pollDelayMs = ACTIVATION_POLL_MS;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private abort = new AbortController();

  // ---------------------------------------------------------------- intents --

  /** Empty-destination handoff: no user confirmation, `confirmReplacement` false. */
  async requestActivation(jobId: string): Promise<void> {
    const view = this.viewSignal();
    if (view.jobId === jobId && view.state === 'in-progress') return;
    await this.start(jobId, false);
  }

  /** The user confirmed replacement in the dialog; the revision is the server's. */
  async confirmReplacement(jobId: string): Promise<void> {
    const view = this.viewSignal();
    if (view.jobId === jobId && view.state === 'in-progress' && view.conflict === null) return;
    await this.start(jobId, true);
  }

  /**
   * Re-attaches to a persisted activation after a reload: replays an
   * undelivered request, or resumes status polling when the 202 was already
   * observed. Does nothing while another tab owns the transfer lease.
   */
  reattach(): void {
    if (this.viewSignal().state !== 'idle') return;
    if (this.tabLease.otherTabActive()) return;

    const record = this.resumeStore.load();
    const activation = record?.activation;
    if (!record?.jobId || !activation) return;

    this.beginRun(record.jobId);
    this.request = activation.request;
    if (activation.accepted) {
      this.accepted = true;
      this.postAttempted = true;
      this.setView({ state: 'in-progress', phase: null });
      this.schedulePoll(0);
      return;
    }
    void this.attempt(record.jobId, activation.request);
  }

  /** Drops any activation state; used when a new transfer begins or is dismissed. */
  reset(): void {
    this.stopPolling();
    this.abort.abort();
    this.abort = new AbortController();
    this.generation += 1;
    this.jobId = null;
    this.request = null;
    this.accepted = false;
    this.postAttempted = false;
    this.conflictRounds = 0;
    this.viewSignal.set(IDLE_VIEW);
  }

  ngOnDestroy(): void {
    this.reset();
  }

  // --------------------------------------------------------------- protocol --

  private async start(jobId: string, confirmReplacement: boolean): Promise<void> {
    // A 409 already returned the revision to use; capture it before the run
    // reset clears the view.
    const conflictRevision = this.viewSignal().conflict?.destinationRevision ?? null;
    this.beginRun(jobId);

    const status = await this.peekStatus(jobId);
    if (this.isStale()) return;
    if (status && this.handleStatusForStart(status)) return;

    const revision =
      conflictRevision ?? this.reviewedRevision(jobId) ?? status?.destinationRevision ?? null;
    if (!revision) {
      this.failWith('migration_invalid_state', false);
      return;
    }

    await this.attempt(jobId, { destinationRevision: revision, confirmReplacement });
  }

  private async attempt(jobId: string, request: MigrationActivateRequestDto): Promise<void> {
    this.request = request;
    this.accepted = false;
    this.postAttempted = true;
    this.persistActivation(request, false);
    this.setView({ state: 'in-progress', phase: null, conflict: null });

    const generation = this.generation;
    try {
      const status = await this.transport.activateJob(jobId, request, this.abort.signal);
      if (this.isStale(generation)) return;
      this.accepted = true;
      this.persistActivation(request, true);
      await this.applyStatus(status);
    } catch (error) {
      if (this.isStale(generation)) return;
      if (error instanceof MigrationActivationConflictError) {
        await this.handleConflict(error, request);
        return;
      }

      const failure = toTransferFailure(error);
      // Ambiguous or maintenance outcomes: the request may have reached the
      // server, so the status route decides instead of a client-side guess.
      if (failure.status === 0 || failure.status === 503 || isMaintenanceBusy(failure.code)) {
        this.setView({ state: 'in-progress', phase: null });
        this.schedulePoll(failure.retryAfterMs ?? ACTIVATION_POST_RETRY_MS);
        return;
      }

      if (failure.code === 'migration_not_found') {
        this.resumeStore.clear();
        this.failWith('migration_not_found', false);
        return;
      }

      // A plain 409 failure body has no `canActivate`; ask the status route.
      if (
        failure.code === 'migration_activation_failed' ||
        failure.code === 'migration_activation_recovery_failed' ||
        failure.code === 'migration_invalid_state'
      ) {
        await this.settleFromStatus(failure.code);
        return;
      }

      this.failWith(failure.code, failure.retryable);
    }
  }

  private async handleConflict(
    error: MigrationActivationConflictError,
    request: MigrationActivateRequestDto,
  ): Promise<void> {
    const conflict = error.conflict;

    if (error.code === 'migration_replacement_confirmation_required') {
      // The destination is populated and the server refused an unconfirmed
      // request. The user has not confirmed yet: show the dialog with the
      // server's fresh counts and wait for `confirmReplacement`.
      this.setView({
        state: 'idle',
        phase: null,
        errorCode: null,
        canRetry: false,
        conflict,
      });
      return;
    }

    // Destination conflict: the revision the user reviewed no longer matches.
    this.conflictRounds += 1;
    if (this.conflictRounds > 1) {
      this.failWith('migration_destination_conflict', false);
      return;
    }

    if (!request.confirmReplacement && conflict.destinationStatus === 'Empty') {
      // Nothing destructive to confirm: repeat once with the server's revision.
      if (!conflict.destinationRevision) {
        this.failWith('migration_destination_conflict', false);
        return;
      }
      await this.attempt(this.jobId!, {
        destinationRevision: conflict.destinationRevision,
        confirmReplacement: false,
      });
      return;
    }

    // Show the updated counts and require a fresh confirmation bound to the
    // revision the server just returned.
    this.setView({
      state: 'failed',
      phase: null,
      errorCode: 'migration_destination_conflict',
      canRetry: true,
      conflict,
    });
  }

  /** Applies a durable/status answer after a POST that had no canActivate field. */
  private async settleFromStatus(fallbackCode: MigrationErrorCode): Promise<void> {
    const status = await this.peekStatus(this.jobId ?? '');
    if (this.isStale()) return;
    if (status) {
      await this.applyStatus(status);
      return;
    }
    this.failWith(fallbackCode, false, fallbackCode === 'migration_activation_recovery_failed');
  }

  // --------------------------------------------------------------- statuses --

  private async peekStatus(jobId: string): Promise<MigrationActivationStatusDto | null> {
    try {
      return await this.transport.getActivationStatus(jobId, this.abort.signal);
    } catch {
      return null;
    }
  }

  /**
   * Short-circuits `start` when the status route already knows the answer:
   * a run is live, or the job is completed / fail-closed.
   */
  private handleStatusForStart(status: MigrationActivationStatusDto): boolean {
    switch (status.outcome) {
      case 'Accepted':
      case 'Running':
        this.accepted = status.accepted;
        this.postAttempted = true;
        void this.applyStatus(status);
        return true;
      case 'Completed':
      case 'RecoveryFailed':
        void this.applyStatus(status);
        return true;
      default:
        return false;
    }
  }

  private async applyStatus(status: MigrationActivationStatusDto): Promise<void> {
    switch (status.outcome) {
      case 'Accepted':
      case 'Running':
        this.setView({ state: 'in-progress', phase: status.phase ?? null });
        this.pollDelayMs = this.pollIntervalMs;
        this.schedulePoll();
        return;

      case 'Completed':
        this.stopPolling();
        this.accepted = false;
        this.postAttempted = false;
        this.request = null;
        // The whole library changed and the job is done; the resume record is
        // no longer useful and the lease must not keep heartbeating.
        this.resumeStore.clear();
        this.tabLease.release();
        this.setView({
          state: 'completed',
          phase: null,
          recovery: {
            available: status.recoveryAvailable,
            expiresAtUtc: status.recoveryExpiresAtUtc ?? null,
            sizeBytes: status.recoverySizeBytes ?? null,
          },
        });
        return;

      case 'Failed':
        this.stopPolling();
        this.accepted = false;
        this.postAttempted = false;
        this.failWith(this.errorCodeOf(status), status.canActivate);
        return;

      case 'RecoveryFailed':
        this.stopPolling();
        this.accepted = false;
        this.postAttempted = false;
        this.failWith('migration_activation_recovery_failed', false, true);
        return;

      case 'Idle': {
        if (!this.accepted && !this.postAttempted) {
          this.stopPolling();
          this.setView({ state: 'idle', phase: null, conflict: null });
          return;
        }
        if (this.lostRunReissues < 1 && status.canActivate && this.request) {
          // The accepted run was lost (host restart before the durable
          // transition): re-issue once with the same server-bound request.
          this.lostRunReissues += 1;
          const request = this.request;
          this.accepted = false;
          this.postAttempted = false;
          await this.attempt(this.jobId!, request);
          return;
        }
        this.stopPolling();
        this.accepted = false;
        this.postAttempted = false;
        this.failWith('migration_activation_failed', status.canActivate);
        return;
      }
    }
  }

  private schedulePoll(delayMs?: number): void {
    this.stopPolling();
    const generation = this.generation;
    this.pollTimer = setTimeout(() => {
      this.pollTimer = null;
      if (this.isStale(generation)) return;
      void this.pollOnce();
    }, delayMs ?? this.pollDelayMs);
  }

  private async pollOnce(): Promise<void> {
    const jobId = this.jobId;
    if (!jobId) return;
    const generation = this.generation;

    try {
      const status = await this.transport.getActivationStatus(jobId, this.abort.signal);
      if (this.isStale(generation)) return;
      this.pollDelayMs = this.pollIntervalMs;
      await this.applyStatus(status);
    } catch (error) {
      if (this.isStale(generation)) return;
      const failure = toTransferFailure(error);

      // The server's exclusive maintenance window (and the brief period when
      // the app's other API calls fail) must not turn into a failure toast:
      // keep polling with backoff and keep showing the truthful in-progress
      // state.
      if (failure.status === 0 || failure.status === 503 || isMaintenanceBusy(failure.code)) {
        this.pollDelayMs = Math.min(
          Math.max(this.pollDelayMs + 1, Math.round(this.pollDelayMs * 1.5)),
          this.maxPollIntervalMs,
        );
        this.schedulePoll(failure.retryAfterMs ?? this.pollDelayMs);
        return;
      }

      if (failure.code === 'migration_not_found') {
        this.resumeStore.clear();
        this.failWith('migration_not_found', false);
        return;
      }

      this.failWith(failure.code, failure.retryable);
    }
  }

  // ------------------------------------------------------------- internals --

  private beginRun(jobId: string): void {
    this.stopPolling();
    this.abort.abort();
    this.abort = new AbortController();
    this.generation += 1;
    // A different job starts a fresh activation history; re-confirming the
    // same job keeps the conflict budget so a loop cannot be re-armed by the
    // user clicking through the dialog forever.
    if (this.jobId !== jobId) this.conflictRounds = 0;
    this.jobId = jobId;
    this.request = null;
    this.accepted = false;
    this.postAttempted = false;
    this.lostRunReissues = 0;
    this.pollDelayMs = this.pollIntervalMs;
    this.setView({ ...IDLE_VIEW, jobId, state: 'in-progress' });
  }

  private isStale(generation = this.generation): boolean {
    return generation !== this.generation;
  }

  private reviewedRevision(jobId: string): string | null {
    const record = this.resumeStore.load();
    if (record?.jobId !== jobId) return null;
    return record.destinationRevision ?? null;
  }

  private persistActivation(request: MigrationActivateRequestDto, accepted: boolean): void {
    const record = this.resumeStore.load();
    if (!record) return;
    this.resumeStore.update({
      jobId: this.jobId ?? record.jobId,
      activation: { request: { ...request }, accepted },
    });
  }

  private errorCodeOf(status: MigrationActivationStatusDto): MigrationErrorCode {
    const code = status.errorCode;
    if (code && (SERVER_MIGRATION_ERROR_CODES as readonly string[]).includes(code)) {
      return code as MigrationErrorCode;
    }
    return status.outcome === 'RecoveryFailed'
      ? 'migration_activation_recovery_failed'
      : 'migration_activation_failed';
  }

  private failWith(
    code: MigrationErrorCode,
    canRetry: boolean,
    maintenanceRequired = false,
  ): void {
    this.stopPolling();
    this.setView({
      state: 'failed',
      phase: null,
      errorCode: code,
      canRetry,
      maintenanceRequired,
    });
  }

  private setView(patch: Partial<ActivationView>): void {
    this.viewSignal.update((current) => ({ ...current, ...patch }));
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }
}
