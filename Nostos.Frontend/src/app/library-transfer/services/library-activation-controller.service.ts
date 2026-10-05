/**
 * Real activation controller (issue #680, slice B8; review-748).
 *
 * Root-scoped so the activation survives leaving Settings: the flow component
 * emits non-destructive requests and reports state, while this service owns
 * the transport protocol. It implements the #681 client contract:
 *
 * - `POST /activate` is admitted with the destination revision the server
 *   returned (preflight, activation status or a 409 conflict body) — never a
 *   revision invented client-side, and never a revision the user did not just
 *   see for a destructive confirmation;
 * - after a 202 the status route is polled with backoff, tolerating the 503
 *   maintenance window and transient network failures;
 * - `Accepted`/`Running` keep polling, `Completed` is final and triggers a
 *   full application reload, `Failed` allows a retry only when `canActivate`,
 *   `RecoveryFailed` is fail-closed with no retry, and `Idle` + `canActivate`
 *   after an accepted request means the run was lost: it is re-issued once per
 *   attached controller session, then surfaced;
 * - a destination/confirmation conflict raised in the server's background
 *   phases is probed for fresh 409 facts and routed into the replacement
 *   review, never retried with the rejected revision;
 * - only an observed 202 is persisted in the resume record, so a reload
 *   re-attaches to polling and an undelivered confirmed request is never
 *   replayed without the user seeing the current library again.
 *
 * The controller respects the cross-tab lease and, on completion, broadcasts a
 * library-generation marker so other tabs reload too.
 */

import { DOCUMENT } from '@angular/common';
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

/** How long the completed state is shown before the hard reload. */
export const ACTIVATION_RELOAD_DELAY_MS = 1_500;

/** Storage marker broadcast to other tabs when the library generation changed. */
export const LIBRARY_REPLACED_STORAGE_KEY = 'nostos.library-transfer.library-replaced.v1';

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
  private readonly document = inject(DOCUMENT);
  private readonly window: Window | null = this.document.defaultView;

  private readonly viewSignal = signal<ActivationView>(IDLE_VIEW);

  /** Injectable for fake-timer tests; the controller owns the scheduling. */
  pollIntervalMs = ACTIVATION_POLL_MS;
  maxPollIntervalMs = ACTIVATION_POLL_MAX_MS;
  completionReloadDelayMs = ACTIVATION_RELOAD_DELAY_MS;

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
  private emptyConflictRetries = 0;
  /** The user has explicitly confirmed replacement for the current job. */
  private userConfirmed = false;
  private pollDelayMs = ACTIVATION_POLL_MS;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private reloadTimer: ReturnType<typeof setTimeout> | null = null;
  private abort = new AbortController();

  private readonly onStorage = (event: StorageEvent): void => {
    if (event.key === LIBRARY_REPLACED_STORAGE_KEY && event.newValue) {
      // Another tab finished replacing the library; this tab's in-memory
      // entities belong to the old generation.
      this.scheduleReload(0);
    }
  };

  constructor() {
    this.window?.addEventListener('storage', this.onStorage);
  }

  // ---------------------------------------------------------------- intents --

  /** Empty-destination handoff: no user confirmation, `confirmReplacement` false. */
  async requestActivation(jobId: string): Promise<void> {
    const view = this.viewSignal();
    if (view.jobId === jobId && view.state === 'in-progress') return;
    await this.start(jobId, false);
  }

  /**
   * Fetches the current destination facts before the user confirms, so the
   * replacement dialog shows the server's counts rather than a stale preflight
   * estimate (review-748: reload must land back on a confirmation with fresh
   * counts). Non-destructive: the probe never confirms.
   */
  async reviewReplacement(jobId: string): Promise<void> {
    const view = this.viewSignal();
    if (view.conflict !== null || view.state !== 'idle') return;
    await this.start(jobId, false);
  }

  /** The user confirmed replacement in the dialog; the revision is the server's. */
  async confirmReplacement(jobId: string): Promise<void> {
    const view = this.viewSignal();
    if (view.jobId === jobId && view.state === 'in-progress' && view.conflict === null) return;
    await this.start(jobId, true);
  }

  /**
   * Re-attaches to a persisted activation after a reload. Only an observed 202
   * is persisted, so this resumes status polling; a request whose delivery was
   * ambiguous is never replayed — the flow takes the user back to the
   * confirmation step and `reviewReplacement` fetches fresh counts. Does
   * nothing while another tab owns the transfer lease.
   */
  reattach(): void {
    if (this.viewSignal().state !== 'idle') return;
    if (this.tabLease.otherTabActive()) return;

    const record = this.resumeStore.load();
    if (!record?.jobId || record.activation?.accepted !== true) return;

    this.beginRun(record.jobId);
    this.accepted = true;
    this.postAttempted = true;
    this.setView({ state: 'in-progress', phase: null });
    this.schedulePoll(0);
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
    this.emptyConflictRetries = 0;
    this.userConfirmed = false;
    this.viewSignal.set(IDLE_VIEW);
  }

  ngOnDestroy(): void {
    this.window?.removeEventListener('storage', this.onStorage);
    if (this.reloadTimer !== null) clearTimeout(this.reloadTimer);
    this.reloadTimer = null;
    this.reset();
  }

  // --------------------------------------------------------------- protocol --

  private async start(jobId: string, confirmReplacement: boolean): Promise<void> {
    // A 409 already returned the revision to use; capture it before the run
    // reset clears the view.
    const conflictRevision = this.viewSignal().conflict?.destinationRevision ?? null;
    this.beginRun(jobId);
    if (confirmReplacement) this.userConfirmed = true;
    const generation = this.generation;

    const status = await this.peekStatus(jobId);
    if (this.isStale(generation)) return;
    if (status && this.handleStatusForStart(status, generation)) return;

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
    this.setView({ state: 'in-progress', phase: null, conflict: null });

    const generation = this.generation;
    try {
      const status = await this.transport.activateJob(jobId, request, this.abort.signal);
      if (this.isStale(generation)) return;
      this.accepted = true;
      this.persistAccepted();
      await this.applyStatus(status, generation);
    } catch (error) {
      if (this.isStale(generation)) return;
      if (error instanceof MigrationActivationConflictError) {
        await this.handleConflict(error, generation);
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
        await this.settleFromStatus(failure.code, generation);
        return;
      }

      this.failWith(failure.code, failure.retryable);
    }
  }

  /**
   * Routes a synchronous 409 into the replacement review.
   *
   * A confirmation that conflicts is never terminal: the dialog shows the
   * server's updated counts and the user may confirm again with the fresh
   * revision. A conflict on the unconfirmed empty path with an empty
   * destination has nothing destructive to confirm, so it repeats once with
   * the server's new revision; a populated destination always opens the
   * dialog.
   */
  private async handleConflict(
    error: MigrationActivationConflictError,
    generation: number,
  ): Promise<void> {
    if (this.isStale(generation)) return;
    const conflict = error.conflict;

    if (error.code === 'migration_replacement_confirmation_required') {
      this.showConflict(conflict);
      return;
    }

    if (this.userConfirmed || conflict.destinationStatus === 'Populated') {
      this.showConflict(conflict);
      return;
    }

    // Empty destination, no destructive decision: repeat once with the
    // server's new revision (never the rejected one).
    if (this.emptyConflictRetries < 1 && conflict.destinationRevision) {
      this.emptyConflictRetries += 1;
      await this.attempt(this.jobId!, {
        destinationRevision: conflict.destinationRevision,
        confirmReplacement: false,
      });
      return;
    }
    this.failWith('migration_destination_conflict', true);
  }

  private showConflict(conflict: LibraryActivationConflictFacts): void {
    // The run is over and the user must review the fresh facts: stop the poll
    // loop so the still-failed durable status cannot re-trigger the recovery
    // probe and flicker a stale/no conflict over the dialog.
    this.stopPolling();
    if (this.userConfirmed) {
      // The user already confirmed: unseal the dialog with the fresh facts and
      // let them confirm the current library again.
      this.setView({
        state: 'failed',
        phase: null,
        errorCode: 'migration_destination_conflict',
        canRetry: true,
        conflict,
      });
      return;
    }
    this.setView({
      state: 'idle',
      phase: null,
      errorCode: null,
      canRetry: false,
      conflict,
    });
  }

  /** Applies a durable/status answer after a POST that had no canActivate field. */
  private async settleFromStatus(
    fallbackCode: MigrationErrorCode,
    generation: number,
  ): Promise<void> {
    const status = await this.peekStatus(this.jobId ?? '');
    if (this.isStale(generation)) return;
    if (status && !(status.outcome === 'Idle' && !status.canActivate)) {
      await this.applyStatus(status, generation);
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
  private handleStatusForStart(
    status: MigrationActivationStatusDto,
    generation: number,
  ): boolean {
    switch (status.outcome) {
      case 'Accepted':
      case 'Running':
        this.accepted = status.accepted;
        this.postAttempted = true;
        void this.applyStatus(status, generation);
        return true;
      case 'Completed':
      case 'RecoveryFailed':
        void this.applyStatus(status, generation);
        return true;
      default:
        return false;
    }
  }

  private async applyStatus(
    status: MigrationActivationStatusDto,
    generation: number,
  ): Promise<void> {
    if (this.isStale(generation)) return;
    switch (status.outcome) {
      case 'Accepted':
      case 'Running':
        this.setView({ state: 'in-progress', phase: status.phase ?? null });
        this.pollDelayMs = this.pollIntervalMs;
        this.schedulePoll();
        return;

      case 'Completed': {
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
        // Cached entities, open readers and in-memory stores still hold ids
        // from the replaced generation; a hard reload is the safety boundary.
        this.broadcastLibraryReplaced();
        this.scheduleReload(this.completionReloadDelayMs);
        return;
      }

      case 'Failed': {
        this.stopPolling();
        this.accepted = false;
        this.postAttempted = false;
        const code = this.errorCodeOf(status);
        // A background confirmation/destination conflict is recoverable: probe
        // for the fresh 409 facts and open the review instead of offering a
        // retry that would re-send the rejected revision.
        if (
          status.canActivate &&
          (code === 'migration_destination_conflict' ||
            code === 'migration_replacement_confirmation_required')
        ) {
          // A conflict already on screen is the user's to act on: never re-probe
          // over it (an in-flight poll can deliver the failed status again).
          if (this.viewSignal().conflict !== null) {
            this.stopPolling();
            return;
          }
          await this.recoverBackgroundConflict(code);
          return;
        }
        this.failWith(code, status.canActivate);
        return;
      }

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
          // transition): re-issue once per attached controller session with
          // the same server-bound request.
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

  /**
   * A conflict raised after the 202: re-issue the previously rejected request
   * WITHOUT confirmation. The server revalidates it and answers the
   * synchronous 409 with the current revision/status/counts, which then goes
   * through `handleConflict` (the dialog path). It can never activate a
   * changed destination because the server checks the revision again.
   */
  private async recoverBackgroundConflict(code: MigrationErrorCode): Promise<void> {
    const previous = this.request;
    if (!previous) {
      this.failWith(code, true);
      return;
    }
    await this.attempt(this.jobId!, {
      destinationRevision: previous.destinationRevision,
      confirmReplacement: false,
    });
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
      await this.applyStatus(status, generation);
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
        this.schedulePoll(
          Math.min(failure.retryAfterMs ?? this.pollDelayMs, this.maxPollIntervalMs),
        );
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
    // same job keeps the empty-conflict budget and the confirmed intent.
    if (this.jobId !== jobId) {
      this.emptyConflictRetries = 0;
      this.userConfirmed = false;
    }
    this.jobId = jobId;
    this.request = null;
    this.accepted = false;
    this.postAttempted = false;
    this.lostRunReissues = 0;
    this.pollDelayMs = this.pollIntervalMs;
    this.setView({ ...IDLE_VIEW, jobId, state: 'in-progress' });
  }

  private isStale(generation: number): boolean {
    return generation !== this.generation;
  }

  private reviewedRevision(jobId: string): string | null {
    const record = this.resumeStore.load();
    if (record?.jobId !== jobId) return null;
    return record.destinationRevision ?? null;
  }

  private persistAccepted(): void {
    const record = this.resumeStore.load();
    if (!record || record.jobId !== this.jobId) return;
    this.resumeStore.update({ activation: { accepted: true } });
  }

  private broadcastLibraryReplaced(): void {
    try {
      this.window?.localStorage?.setItem(LIBRARY_REPLACED_STORAGE_KEY, `${Date.now()}`);
    } catch {
      // Optional cross-tab safety only; the completing tab still reloads.
    }
  }

  private scheduleReload(delayMs: number): void {
    if (this.reloadTimer !== null) return;
    this.reloadTimer = setTimeout(() => {
      this.reloadTimer = null;
      const location = this.window?.location;
      if (location && typeof location.reload === 'function') location.reload();
    }, delayMs);
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
