/**
 * Settings host for the shared library-transfer flows (slice B5).
 *
 * Capability gating lives in `SettingsComponent`; this host only mounts both
 * flows inside the "Move your library" card and forwards completion. It is
 * rendered only when the server advertises `supportsLibraryMigration`.
 */

import { ChangeDetectionStrategy, Component, output } from '@angular/core';

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

  /**
   * Slice B8 owns activation; until it lands these requests are no-ops and
   * the flow keeps its gated empty-destination outcome.
   */
  onActivationRequested(_jobId: string): void {}

  /** Slice B8 owns replacement activation; the flow gates the confirm until then. */
  onReplacementConfirmed(_jobId: string): void {}
}
