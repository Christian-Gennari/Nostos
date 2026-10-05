/**
 * Settings host for the shared library-transfer flows (slice B5/B8).
 *
 * Capability gating lives in `SettingsComponent`; this host mounts both flows
 * inside the "Move your library" card, forwards completion, and wires the
 * import flow's activation handoff to the root-scoped activation controller.
 * Nothing calls the activation API unless the server advertises
 * `supportsSafeActivation` (B8 self-review (d)).
 */

import { ChangeDetectionStrategy, Component, effect, inject, input, output } from '@angular/core';

import { LibraryActivationController } from '../services/library-activation-controller.service';
import { LibraryTransferCoordinator } from '../services/library-transfer-coordinator.service';
import { LibraryExportFlowComponent } from './library-export-flow.component';
import { LibraryImportFlowComponent } from './library-import-flow.component';

@Component({
  selector: 'app-library-transfer-host',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LibraryExportFlowComponent, LibraryImportFlowComponent],
  templateUrl: './library-transfer-host.component.html',
  styleUrls: ['./library-transfer-host.component.css'],
})
export class LibraryTransferHostComponent {
  /** Forwarded once when the durable job reports completion. */
  readonly importCompleted = output<void>();

  /** Host capability; false keeps the flow's gated explanation and no API call. */
  readonly supportsSafeActivation = input(false);

  readonly activation = inject(LibraryActivationController);
  private readonly coordinator = inject(LibraryTransferCoordinator);

  constructor() {
    // Re-attach a persisted activation after a reload. The effect tracks the
    // capability and the cross-tab lease, so it retries once the other tab's
    // lease expires instead of leaving the outcome lost.
    effect(() => {
      if (!this.supportsSafeActivation()) return;
      this.activation.reattach();
    });

    // Before the user confirms, fetch the server's current destination facts
    // so the dialog shows the live counts (review-748: after a reload the
    // confirmation must never be replayed against a stale preflight view).
    effect(() => {
      if (!this.supportsSafeActivation()) return;
      const state = this.coordinator.state();
      if (state.kind !== 'replacement-confirmation') return;
      if (this.activation.state() !== 'idle' || this.activation.conflict() !== null) return;
      void this.activation.reviewReplacement(state.jobId);
    });

    // A genuinely new import (file selected) must not inherit activation state
    // from the previous job.
    effect(() => {
      if (this.coordinator.state().kind === 'inspecting') this.activation.reset();
    });
  }

  onActivationRequested(jobId: string): void {
    if (!this.supportsSafeActivation()) return;
    void this.activation.requestActivation(jobId);
  }

  onReplacementConfirmed(jobId: string): void {
    if (!this.supportsSafeActivation()) return;
    void this.activation.confirmReplacement(jobId);
  }
}
