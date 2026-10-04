/**
 * Shared import-library flow (plan §8, §9, §27, §32, §35; slice B4).
 *
 * Renders the coordinator's state union against the mock transport today and
 * the real adapter after B7. It owns no transfer logic: every state, retry,
 * cancellation and re-selection rule comes from `LibraryTransferCoordinator`.
 * The component's only additional responsibilities are the cross-tab lease
 * (review-724 B4 item) and the host-facing outputs Settings/onboarding will
 * consume in B5/B6/B8.
 *
 * Activation is intentionally absent: confirming replacement emits
 * `replacementConfirmed` for B8 to wire, and never calls a speculative
 * endpoint.
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

import { MigrationJobState } from '../models/migration-http.dtos';
import { TransferFlowState } from '../models/library-transfer.models';
import {
  TransferFailureCopy,
  TransferProgressPhase,
  libraryTransferFailureCopy,
} from '../library-transfer.copy';
import { LibraryTransferCoordinator } from '../services/library-transfer-coordinator.service';
import { TransferResumeStore } from '../services/transfer-resume-store.service';
import { TransferTabLease } from '../services/transfer-tab-lease.service';
import { LibraryTransferProgressComponent } from './library-transfer-progress.component';
import { LibraryReplacementDialogComponent } from './library-replacement-dialog.component';
import { ButtonComponent } from '../../ui/button/button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';

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

  /** Emitted once when the durable job reaches Completed. */
  readonly importCompleted = output<void>();

  /** Emitted once when the user confirms replacement; B8 performs activation. */
  readonly replacementConfirmed = output<string>();

  readonly coordinator = inject(LibraryTransferCoordinator);
  private readonly resumeStore = inject(TransferResumeStore);
  private readonly tabLease = inject(TransferTabLease);

  readonly state = this.coordinator.state;
  readonly otherTabActive = this.tabLease.otherTabActive;
  readonly otherTabFileName = this.tabLease.otherTabFileName;

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
  readonly completed = computed(() => asKind(this.state(), 'completed'));
  readonly failed = computed(() => asKind(this.state(), 'failed'));
  readonly cancelled = computed(() => asKind(this.state(), 'cancelled'));

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
        return 'Preparing your import…';
      case 'Transferring':
        return 'Finishing the upload…';
      case 'Activating':
        return 'Importing your library…';
      default:
        return 'Checking archive, library relationships, and media…';
    }
  });

  readonly checkingPhase = computed<TransferProgressPhase>(() => {
    const state = this.checking();
    return progressPhaseForJobState(state?.jobState);
  });

  /** Cancellation is unavailable once the server has begun activation. */
  readonly checkingCancellable = computed(() => {
    const state = this.checking();
    return state !== null && state.jobState !== 'Activating';
  });

  private leaseClaimed = false;
  private completedEmitted = false;

  constructor() {
    effect(() => {
      const state = this.state();
      if (
        state.kind === 'completed' ||
        state.kind === 'cancelled' ||
        state.kind === 'idle'
      ) {
        this.releaseLease();
      }
      if (state.kind === 'completed') {
        if (!this.completedEmitted) {
          this.completedEmitted = true;
          this.importCompleted.emit();
        }
      } else {
        this.completedEmitted = false;
      }
    });
  }

  async ngOnInit(): Promise<void> {
    if (!this.autoResume()) return;
    if (this.otherTabActive()) return;
    if (this.state().kind !== 'idle') return;
    if (!this.resumeStore.load()) return;
    await this.coordinator.resume();
  }

  ngOnDestroy(): void {
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
    if (!this.tabLease.claim(file.name)) return;
    this.leaseClaimed = true;

    const state = this.state();
    const reselect = state.kind === 'ready-to-upload' && state.reselectionRequired;
    if (reselect) {
      void this.coordinator.resumeWithFile(file);
    } else {
      this.resumeStore.clear();
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

  dismiss(): void {
    this.coordinator.dismiss();
  }

  retry(): void {
    void this.coordinator.retry();
  }

  /** Runs the recovery the failure copy asked for. */
  runFailureAction(): void {
    const copy = this.failureCopy();
    const state = this.failed();
    if (!copy || !state) return;

    switch (copy.action) {
      case 'retry':
        if (state.jobId) {
          this.retry();
        } else {
          this.startOver();
        }
        return;
      case 'choose-file':
        this.startOver();
        return;
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
    this.replacementConfirmed.emit(state.jobId);
  }

  onReplacementCancelled(): void {
    void this.coordinator.cancel();
  }

  private startOver(): void {
    const state = this.state();
    if (state.kind === 'failed' || state.kind === 'cancelled') {
      this.resumeStore.clear();
    }
    this.openPicker();
  }

  private releaseLease(): void {
    if (!this.leaseClaimed) return;
    this.tabLease.release();
    this.leaseClaimed = false;
  }
}

type StateKind = TransferFlowState['kind'];

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
      return 'uploading';
    case 'Validating':
    case 'ReadyToActivate':
      return 'checking';
    default:
      return 'preparing';
  }
}
