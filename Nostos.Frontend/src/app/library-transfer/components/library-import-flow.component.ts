/**
 * Shared import-library flow (plan §8, §9, §27, §32, §35; slice B4).
 *
 * Renders the coordinator's state union against the mock transport today and
 * the real adapter after B7. It owns no transfer logic: every state, retry,
 * cancellation and re-selection rule comes from `LibraryTransferCoordinator`.
 * The component's own responsibilities are the cross-tab lease and the
 * host-facing outputs Settings/onboarding/B8 consume.
 *
 * Activation itself is B8. B4 hands it two explicit, non-destructive requests —
 * `replacementConfirmed` after the one destructive confirmation, and
 * `activationRequested` once an empty-destination job is verified — and accepts
 * `activationState` / `activationErrorCode` back so every prepared state has a
 * rendered outcome (review-730 items 2 and 3).
 */

import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';

import {
  MigrationActivationPhase,
  MigrationErrorCode,
  MigrationJobState,
} from '../models/migration-http.dtos';
import {
  HostActivationState,
  LibraryActivationConflictFacts,
  LibraryTransferFailure,
  TransferFlowState,
} from '../models/library-transfer.models';
import {
  TransferFailureCopy,
  TransferProgressPhase,
  activationPhaseMessage,
  activationRecoveryNote,
  libraryTransferFailureCopy,
  maintenanceRetryMessage,
  formatBytes,
} from '../library-transfer.copy';
import { LibraryTransferCoordinator } from '../services/library-transfer-coordinator.service';
import { TransferResumeStore } from '../services/transfer-resume-store.service';
import { TransferTabLease } from '../services/transfer-tab-lease.service';
import { LibraryTransferProgressComponent } from './library-transfer-progress.component';
import { LibraryReplacementDialogComponent } from './library-replacement-dialog.component';
import { ButtonComponent } from '../../ui/button/button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';

export type { HostActivationState } from '../models/library-transfer.models';

@Component({
  selector: 'app-library-import-flow',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ButtonComponent,
    NostosIconComponent,
    LibraryTransferProgressComponent,
    LibraryReplacementDialogComponent,
  ],
  templateUrl: './library-import-flow.component.html',
  styleUrls: ['./library-transfer.shared.css', './library-import-flow.component.css'],
})
export class LibraryImportFlowComponent implements OnInit, OnDestroy {
  /** Host capability from deployment capabilities; false until #681 ships. */
  readonly supportsSafeActivation = input(false);

  /** Reattach to a persisted import on init (Settings/onboarding both want it). */
  readonly autoResume = input(true);

  /** Host-reported activation progress for a verified job. */
  readonly activationState = input<HostActivationState>('idle');

  /** Stable failure code when `activationState` is `failed`; null is generic. */
  readonly activationErrorCode = input<MigrationErrorCode | null>(null);

  /** Server-reported coarse phase while the host activates (slice B8). */
  readonly activationPhase = input<MigrationActivationPhase | null>(null);

  /**
   * True only when the server said the failed activation may be repeated
   * (`canActivate`). `RecoveryFailed` never retries.
   */
  readonly activationCanRetry = input(false);

  /**
   * Fresh destination facts from the server's 409; the replacement dialog
   * shows these counts and the confirmation binds to the server revision.
   */
  readonly activationConflict = input<LibraryActivationConflictFacts | null>(null);

  /** Server-reported recovery-copy expiry after a completed replacement. */
  readonly activationRecoveryExpiresAtUtc = input<string | null>(null);

  /** Emitted once when the durable job or the host reports completion. */
  readonly importCompleted = output<void>();

  /** Emitted once when the user confirms replacement; B8 performs activation. */
  readonly replacementConfirmed = output<string>();

  /** Emitted once per prepared empty-destination job; B8 performs activation. */
  readonly activationRequested = output<string>();

  readonly coordinator = inject(LibraryTransferCoordinator);
  private readonly resumeStore = inject(TransferResumeStore);
  private readonly tabLease = inject(TransferTabLease);

  readonly state = this.coordinator.state;
  readonly otherTabActive = this.tabLease.otherTabActive;
  readonly otherTabFileName = this.tabLease.otherTabFileName;

  private readonly replacementSubmittedJobId = signal<string | null>(null);
  private pendingResume = false;
  private completedEmitted = false;
  private activationRequestedJobId: string | null = null;
  private lastStateKind: TransferFlowState['kind'] = 'idle';

  /**
   * True while a live lease in another tab owns the shared resume record. This
   * tab's own ownership comes from the root-scoped `TransferTabLease`, so a
   * flow that remounts mid-transfer (Settings navigation, onboarding Back)
   * adopts the lease it already holds instead of tracking a local flag.
   */
  readonly blockedByOtherTab = computed(() => this.otherTabActive());

  readonly pausing = signal(false);
  readonly resuming = signal(false);

  @ViewChild('fileInput')
  private fileInput?: ElementRef<HTMLInputElement>;

  // Discriminated helpers keep the template type-safe without weakening the
  // union: each one is the state only when it is of that kind.
  readonly inspecting = computed(() => asKind(this.state(), 'inspecting'));
  readonly preflighting = computed(() => asKind(this.state(), 'preflighting'));
  readonly reselect = computed(() => {
    const state = this.state();
    return state.kind === 'ready-to-upload' && state.reselectionRequired ? state : null;
  });
  readonly starting = computed(() => {
    const state = this.state();
    return state.kind === 'ready-to-upload' && !state.reselectionRequired ? state : null;
  });
  readonly uploading = computed(() => asKind(this.state(), 'uploading'));
  readonly checking = computed(() => asKind(this.state(), 'checking'));
  readonly readyEmpty = computed(() => asKind(this.state(), 'ready-empty'));
  readonly replacement = computed(() => asKind(this.state(), 'replacement-confirmation'));
  readonly failed = computed(() => asKind(this.state(), 'failed'));
  readonly cancelled = computed(() => asKind(this.state(), 'cancelled'));

  /** True while the coordinator waits out server maintenance and re-attempts. */
  readonly maintenanceWaiting = this.coordinator.maintenanceWaiting;
  readonly maintenanceMessage = maintenanceRetryMessage();

  /**
   * The completed surface: either the coordinator reached Completed, or the
   * host reported that the activation it owns finished.
   */
  readonly completed = computed(() => {
    const state = this.state();
    if (state.kind === 'completed') return { jobId: state.jobId };
    if (this.activationState() !== 'completed') return null;
    const prepared =
      asKind(state, 'ready-empty') ?? asKind(state, 'replacement-confirmation');
    return prepared ? { jobId: prepared.jobId } : null;
  });

  /** Truthful phase sentence for the server-owned activation. */
  readonly activationPhaseMessage = computed(() => activationPhaseMessage(this.activationPhase()));

  /** Server-reported recovery-copy note after completion; null when unknown. */
  readonly activationRecoveryNote = computed(() =>
    activationRecoveryNote(this.activationRecoveryExpiresAtUtc()),
  );

  /**
   * A populated destination the server reported through a 409 while the flow
   * was on the empty path: the dialog appears with the server's own counts.
   */
  readonly activationConflictReplacement = computed<LibraryActivationConflictFacts | null>(() => {
    const conflict = this.activationConflict();
    return conflict && conflict.destinationStatus === 'Populated' ? conflict : null;
  });

  /**
   * True when the host failed the activation and the server forbade a retry.
   * Only the activation codes can be fail-closed: a generic host failure
   * (`portable_import_failed`, storage exhaustion) stays retryable.
   */
  readonly activationRetryBlocked = computed(() => {
    if (this.activationState() !== 'failed' || this.activationCanRetry()) return false;
    const code = this.activationErrorCode();
    return (
      code === 'migration_activation_recovery_failed' ||
      code === 'migration_activation_failed'
    );
  });

  /** The Retry import action is offered only when the server allows a repeat. */
  readonly activationRetryOffered = computed(
    () => this.activationRetryBlocked() === false && this.activationFailureCopy()?.action === 'retry',
  );

  readonly failureCopy = computed<TransferFailureCopy | null>(() => {
    const state = this.failed();
    return state ? libraryTransferFailureCopy(state.failure) : null;
  });

  /** The recovery action's label; null when the copy asks for no action. */
  readonly failureActionLabel = computed(() => {
    const copy = this.failureCopy();
    const state = this.failed();
    if (!copy || !state) return null;
    switch (copy.action) {
      case 'retry':
        return state.jobId ? 'Retry import' : 'Try again';
      case 'sign-in':
        return 'Try again';
      case 'choose-file':
        return 'Choose a different file';
      case 'start-over':
        return 'Start over';
      default:
        return null;
    }
  });

  readonly reselectNotice = computed(() => {
    const state = this.reselect();
    if (!state?.notice) return null;
    return libraryTransferFailureCopy(state.notice).message;
  });

  readonly checkingHeadline = computed(() => {
    const state = this.checking();
    if (!state) return '';
    switch (state.jobState) {
      case 'Pending':
      case 'Preparing':
      case 'Transferring':
        return 'Checking your uploaded archive…';
      case 'Activating':
        return 'Importing your library…';
      default:
        return 'Preparing your library…';
    }
  });

  readonly checkingPhase = computed<TransferProgressPhase>(() => {
    const state = this.checking();
    return progressPhaseForJobState(state?.jobState);
  });

  /** Uploaded bytes never stand in for the server's verification progress. */
  readonly processingProgress = computed(() => {
    const state = this.checking();
    const progress = state?.serverProgress;
    if (!state || !progress) return null;
    // Some older hosts reuse the finished upload's 100% as validation progress.
    // Keep those unannotated completed totals indeterminate until the job advances.
    if (!progress.message && progress.totalBytes !== null && progress.bytesProcessed >= progress.totalBytes) return null;
    const expectedPhase = state.jobState === 'Activating' ? 'Activating' : 'Validating';
    return progress.phase === expectedPhase ? progress : null;
  });

  readonly uploadSummary = computed(() => {
    const progress = this.checking()?.progress;
    if (!progress || progress.totalBytes <= 0) return '';
    return formatBytes(progress.totalBytes);
  });

  readonly statusCheckedTime = computed(() => {
    const value = this.checking()?.statusCheckedAtUtc;
    return value ? new Date(value).toLocaleTimeString() : null;
  });

  /** Cancellation is unavailable once the server has begun activation. */
  readonly checkingCancellable = computed(() => {
    const state = this.checking();
    return state !== null && state.jobState !== 'Activating';
  });

  /** True while the host has taken responsibility for activating the job. */
  readonly activationInProgress = computed(() => this.activationState() === 'in-progress');

  /** True when the host cannot activate yet, so the flow offers Dismiss. */
  readonly activationGated = computed(
    () => !this.supportsSafeActivation() && this.activationState() === 'idle',
  );

  /** Failure copy for a host-reported activation failure, if any. */
  readonly activationFailureCopy = computed<TransferFailureCopy | null>(() => {
    if (this.activationState() !== 'failed') return null;
    // A destination conflict is re-reviewed through the dialog, not a panel.
    if (this.activationConflict()) return null;
    return libraryTransferFailureCopy(this.activationFailure());
  });

  /**
   * The replacement dialog is sealed from the moment destructive intent is
   * emitted, and stays sealed whenever the server owns the cutover — including
   * a probe/activation the user has not confirmed (review-748: activation is a
   * global interaction boundary and must not be dismissible).
   */
  readonly replacementSealed = computed(
    () =>
      this.activationState() === 'in-progress' ||
      (this.replacementSubmittedJobId() !== null &&
        this.activationState() !== 'failed' &&
        this.activationState() !== 'completed'),
  );

  readonly replacementBusyLabel = computed(() =>
    this.activationState() === 'in-progress' ? 'Replacing library…' : 'Starting…',
  );

  readonly replacementError = computed(() => {
    if (this.replacementSubmittedJobId() === null || this.activationState() !== 'failed') {
      return null;
    }
    // A 409 destination conflict carries fresh facts: the dialog shows the
    // updated counts and asks for a fresh confirmation instead of an error.
    if (this.activationConflict()) return null;
    return libraryTransferFailureCopy(this.activationFailure()).message;
  });

  constructor() {
    // Release the lease when an active transfer reaches a terminal state (or
    // is explicitly dismissed). A terminal state on first mount (the transfer
    // finished while the host was closed) releases too; idle on first mount
    // must not, because auto-resume may have just claimed the lease.
    effect(() => {
      const kind = this.state().kind;
      const wasActive = this.lastStateKind !== 'idle' && !isTerminalKind(this.lastStateKind);
      this.lastStateKind = kind;
      if (isTerminalKind(kind) || (wasActive && kind === 'idle')) {
        this.releaseLease();
      }
    });

    // Completion is reported once, whether it came from the server job or the
    // host's own activation call.
    effect(() => {
      if (this.completed()) {
        if (!this.completedEmitted) {
          this.completedEmitted = true;
          this.importCompleted.emit();
        }
      } else {
        this.completedEmitted = false;
      }
    });

    // A blocked auto-resume retries as soon as the foreign lease frees
    // (expiry timer, storage event, focus) without a page reload.
    effect(() => {
      if (this.otherTabActive()) return;
      if (!this.pendingResume) return;
      this.pendingResume = false;
      void this.tryAutoResume();
    });

    // Empty-destination handoff: exactly one request per prepared job, and
    // only when the host advertises safe activation (B8 self-review (d)).
    effect(() => {
      const state = this.readyEmpty();
      if (!state || !this.supportsSafeActivation()) return;
      if (this.activationState() !== 'idle') return;
      if (this.activationRequestedJobId === state.jobId) return;
      this.activationRequestedJobId = state.jobId;
      this.activationRequested.emit(state.jobId);
    });
  }

  async ngOnInit(): Promise<void> {
    await this.tryAutoResume();
  }

  ngOnDestroy(): void {
    // The coordinator is root-scoped: if a transfer is still active the host is
    // only being torn down, not the transfer, so the lease must stay (and the
    // root service keeps heartbeating it). A later remount adopts it through
    // `TransferTabLease.ownsLease`.
    if (this.transferActive() && this.tabLease.ownsLease()) return;
    this.releaseLease();
  }

  openPicker(): void {
    this.fileInput?.nativeElement.click();
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;

    // A live lease in another tab owns the shared resume record; starting here
    // would overwrite it (review-724 cross-tab item).
    if (!this.claimForAction()) return;

    const state = this.state();
    const reselect = state.kind === 'ready-to-upload' && state.reselectionRequired;
    if (reselect) {
      void this.coordinator.resumeWithFile(file);
    } else {
      void this.coordinator.startImport(file);
    }
  }

  async pause(): Promise<void> {
    if (this.pausing()) return;
    this.pausing.set(true);
    try {
      await this.coordinator.pauseUpload();
    } finally {
      this.pausing.set(false);
    }
  }

  async resume(): Promise<void> {
    if (this.resuming()) return;
    this.resuming.set(true);
    try {
      await this.coordinator.resumeUpload();
    } finally {
      this.resuming.set(false);
    }
  }

  cancel(): void {
    void this.coordinator.cancel();
  }

  /** Acknowledges a completed job: clears the record and returns to idle. */
  done(): void {
    this.resumeStore.clear();
    this.coordinator.dismiss();
  }

  /** Leaves a verified job in place and returns the UI to idle. */
  dismissPrepared(): void {
    this.coordinator.dismiss();
  }

  retry(): void {
    if (!this.claimForAction()) return;
    void this.coordinator.retry();
  }

  /** Re-asks the host to activate after a reported activation failure. */
  retryActivation(): void {
    const state = this.readyEmpty();
    if (!state) return;
    this.activationRequested.emit(state.jobId);
  }

  /** Runs the recovery the failure copy asked for. */
  runFailureAction(): void {
    const copy = this.failureCopy();
    const state = this.failed();
    if (!copy || !state) return;

    switch (copy.action) {
      case 'retry':
        if (state.jobId || this.coordinator.hasInterruptedOperation()) {
          this.retry();
        } else {
          this.startOver();
        }
        return;
      case 'sign-in':
        if (this.claimForAction()) void this.coordinator.resume();
        return;
      case 'choose-file':
      case 'start-over':
        this.startOver();
        return;
      default:
        return;
    }
  }

  onReplacementConfirmed(): void {
    const state = this.replacement();
    if (!state) return;
    // One emission per decision; a host-reported failure re-arms the dialog,
    // so the retry emission is allowed.
    if (
      this.replacementSubmittedJobId() === state.jobId &&
      this.activationState() !== 'failed'
    ) {
      return;
    }
    this.replacementSubmittedJobId.set(state.jobId);
    this.replacementConfirmed.emit(state.jobId);
  }

  onReplacementCancelled(): void {
    // Sealed while the host activates; only reachable before submission or
    // after the host reports a failure to activate.
    if (this.replacementSealed()) return;
    void this.coordinator.cancel();
  }

  private async tryAutoResume(): Promise<void> {
    if (!this.autoResume()) return;
    if (this.state().kind !== 'idle') return;
    if (!this.resumeStore.load() && !this.coordinator.supportsActiveImportDiscovery) return;
    if (!this.claimForAction()) {
      this.pendingResume = true;
      return;
    }
    await this.coordinator.resume();
    if (this.state().kind === 'idle') this.releaseLease();
  }

  /** Claims the lease before any action that continues or starts a transfer. */
  private claimForAction(): boolean {
    if (this.tabLease.ownsLease()) return true;
    const fileName = this.resumeStore.load()?.fileName;
    return this.tabLease.claim(fileName);
  }

  private startOver(): void {
    const state = this.state();
    if (state.kind === 'cancelled') {
      this.resumeStore.clear();
    }
    this.openPicker();
  }

  private transferActive(): boolean {
    const kind = this.state().kind;
    return kind !== 'idle' && kind !== 'completed' && kind !== 'cancelled' && kind !== 'failed';
  }

  private releaseLease(): void {
    // Ownership-checked by the root service, so this is safe after a remount.
    this.tabLease.release();
  }

  private activationFailure(): LibraryTransferFailure {
    return {
      code: this.activationErrorCode() ?? 'portable_import_failed',
      message: '',
      // Retry copy is allowed only when the server said the failed activation
      // can be repeated; `RecoveryFailed` copy never offers an action anyway.
      retryable: this.activationCanRetry(),
    };
  }
}

type StateKind = TransferFlowState['kind'];

function isTerminalKind(kind: StateKind): boolean {
  return kind === 'completed' || kind === 'cancelled' || kind === 'failed';
}

function asKind<K extends StateKind>(
  state: TransferFlowState,
  kind: K,
): Extract<TransferFlowState, { kind: K }> | null {
  return state.kind === kind ? (state as Extract<TransferFlowState, { kind: K }>) : null;
}

function progressPhaseForJobState(jobState: MigrationJobState | undefined): TransferProgressPhase {
  switch (jobState) {
    case 'Activating':
      return 'activating';
    case 'Transferring':
      return 'checking';
    case 'Validating':
    case 'ReadyToActivate':
      return 'preparing-library';
    default:
      return 'checking';
  }
}
