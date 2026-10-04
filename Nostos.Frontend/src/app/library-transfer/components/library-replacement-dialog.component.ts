/**
 * The single explicit replacement confirmation (plan §27, §28; slice B4).
 *
 * The plan forbids stacking "are you sure?" questions, a checkbox plus a
 * button, or any second confirmation: this dialog is the one deliberate
 * destructive intent. ConfirmModal cannot render the structured current vs
 * incoming counts and the recovery explanation, so this is a dedicated dialog
 * built from the same modal primitives (`app-modal-shell`, `app-dialog-actions`,
 * `button[appButton]`) with CDK focus trapping.
 *
 * Server-derived prepared-import counts are preferred whenever the host
 * reports them; manifest-derived preflight counts stay labelled as estimates
 * (review-724 counts item). Safe activation is a host capability: until #681
 * advertises it, the destructive action is disabled and the plan's explanatory
 * copy is shown instead.
 *
 * `busy` is the seal: the flow sets it the moment destructive intent is
 * emitted, and ModalShell's `busy` input makes Escape and backdrop clicks
 * inert while the host activates (review-730 item 3). A reported activation
 * failure arrives as `errorMessage`, which unseals the dialog and re-arms the
 * destructive action as a retry.
 */

import { ChangeDetectionStrategy, Component, computed, effect, input, output } from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';

import { MigrationPreflightResponseDto } from '../models/migration-http.dtos';
import { readPreparedImportFacts } from '../models/prepared-import';
import { formatLibraryCounts } from '../library-transfer.copy';
import { ModalShell } from '../../ui/modal-shell/modal-shell.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { ButtonComponent } from '../../ui/button/button.component';
import { DialogActionsComponent } from '../../ui/dialog-actions/dialog-actions.component';

@Component({
  selector: 'app-library-replacement-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [A11yModule, ModalShell, NostosIconComponent, ButtonComponent, DialogActionsComponent],
  templateUrl: './library-replacement-dialog.component.html',
  styleUrl: './library-replacement-dialog.component.css',
})
export class LibraryReplacementDialogComponent {
  readonly isOpen = input.required<boolean>();
  readonly preflight = input<MigrationPreflightResponseDto | null>(null);
  /** Raw `MigrationJobStatusResponseDto.preparedImport`; read defensively. */
  readonly preparedImport = input<unknown>(null);
  readonly supportsSafeActivation = input(false);
  /** Sealed while the host performs the confirmed replacement. */
  readonly busy = input(false);
  /** Label on the sealed action while `busy`. */
  readonly busyLabel = input<string>('Finishing…');
  /** Host-reported activation failure; non-null unseals and shows the error. */
  readonly errorMessage = input<string | null>(null);

  readonly confirmed = output<void>();
  readonly cancelled = output<void>();

  private confirmedOnce = false;

  constructor() {
    effect(() => {
      // Reopening (destination-conflict re-review) and a host-reported failure
      // both start a fresh destructive decision; only a successful submission
      // keeps the latch.
      if (!this.isOpen() || this.errorMessage()) this.confirmedOnce = false;
    });
  }

  private readonly preparedFacts = computed(() => readPreparedImportFacts(this.preparedImport()));

  /** True when the incoming counts came from the server's prepared import. */
  readonly incomingCountsVerified = computed(() => this.preparedFacts()?.incomingCounts !== undefined);

  readonly existingSummary = computed(() => {
    const counts = this.preflight()?.evaluation.existingCounts;
    if (!counts) return '';
    return formatLibraryCounts({
      books: counts.books,
      notes: counts.notes,
      collections: counts.collections,
    });
  });

  readonly incomingSummary = computed(() => {
    const verified = this.preparedFacts()?.incomingCounts;
    const fallback = this.preflight()?.evaluation.incomingCounts;
    const counts = verified ?? fallback;
    if (!counts) return '';
    return formatLibraryCounts({
      books: counts.books,
      notes: counts.notes,
      collections: counts.collections,
    });
  });

  readonly incomingSourceLabel = computed(() =>
    this.incomingCountsVerified()
      ? 'Checked by Nostos while preparing this import.'
      : 'Estimated from the archive manifest. Nostos will confirm the final counts before replacing.',
  );

  /** Emits once per decision; the destructive action cannot be re-entered. */
  confirm(): void {
    if (!this.supportsSafeActivation() || this.busy() || this.confirmedOnce) return;
    this.confirmedOnce = true;
    this.confirmed.emit();
  }
}
