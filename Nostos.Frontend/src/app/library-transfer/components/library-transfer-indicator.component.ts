import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Router } from '@angular/router';

import { CloudEntryService } from '../../core/services/cloud-entry.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { LibraryTransferCoordinator } from '../services/library-transfer-coordinator.service';
import { TransferResumeStore } from '../services/transfer-resume-store.service';

@Component({
  selector: 'app-library-transfer-indicator',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ButtonComponent, NostosIconComponent],
  templateUrl: './library-transfer-indicator.component.html',
  styleUrl: './library-transfer-indicator.component.css',
})
export class LibraryTransferIndicatorComponent {
  private readonly coordinator = inject(LibraryTransferCoordinator);
  private readonly resumeStore = inject(TransferResumeStore);
  private readonly entry = inject(CloudEntryService);
  private readonly router = inject(Router);

  readonly visible = computed(() => {
    const kind = this.coordinator.state().kind;
    const active =
      kind !== 'idle' && kind !== 'completed' && kind !== 'failed' && kind !== 'cancelled';
    return active || this.resumeStore.load() !== null;
  });

  readonly message = computed(() => {
    const kind = this.coordinator.state().kind;
    if (kind === 'failed') return 'A library transfer needs attention.';
    if (kind === 'ready-to-upload') return 'A library import is ready to resume.';
    if (kind === 'idle') return 'A library transfer can be resumed.';
    return 'A library transfer is in progress.';
  });

  openManageLibrary(): void {
    const firstRunImport =
      this.entry.firstRunImportPending() || this.entry.view().kind === 'first_run';
    if (firstRunImport) this.entry.openManageLibraryFromFirstRun();

    void this.router.navigate(['/settings/library'], {
      queryParams: {
        action: 'import',
        ...(firstRunImport ? { source: 'first-run' } : {}),
      },
    });
  }
}
