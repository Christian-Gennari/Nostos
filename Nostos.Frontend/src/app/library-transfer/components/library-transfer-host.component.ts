/**
 * Settings host for the shared library-transfer flows (slice B5).
 *
 * Capability gating and the legacy fallback live in `SettingsComponent`; this
 * host only mounts both flows inside the "Move your library" card and forwards
 * the host-facing inputs/outputs. Activation is slice B8, so the two
 * activation requests are acknowledged without calling an endpoint and the
 * import flow keeps its own gated "not available yet" outcome visible.
 */

import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

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
  /** Host capability from deployment capabilities; false until #681 ships. */
  readonly supportsSafeActivation = input(false);

  /** Forwarded once when the durable job (or the host) reports completion. */
  readonly importCompleted = output<void>();

  /**
   * B8 owns activation. Until it lands `activationRequested` only reaches the
   * flow's gated empty-destination UI, so the host deliberately does nothing.
   */
  onActivationRequested(_jobId: string): void {}

  /** B8 owns replacement activation; the flow gates the confirm until then. */
  onReplacementConfirmed(_jobId: string): void {}
}
