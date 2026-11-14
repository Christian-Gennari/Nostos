import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { LibraryTransferHostComponent } from '../../library-transfer/components/library-transfer-host.component';

/**
 * Settings → Backups & data → Move to another Nostos: the portable library
 * transfer.
 *
 * Deliberately separate from the local operational backup above: these archives
 * move a library between Nostos installations, they do not recover this one.
 */
@Component({
  selector: 'app-library-move-settings',
  standalone: true,
  imports: [LibraryTransferHostComponent],
  templateUrl: './library-move-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LibraryMoveSettingsComponent {
  /** The host can activate the imported library safely (#681). */
  readonly supportsSafeActivation = input(false);

  /** The page was opened straight on its import group (`?action=import`). */
  readonly focusImport = input(false);

  /** A completed import replaced the server library: refresh what Settings reads. */
  readonly importCompleted = output<void>();
}
