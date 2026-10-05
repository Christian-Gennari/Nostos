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

import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  effect,
  inject,
  input,
  output,
} from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';

import { MigrationPreflightResponseDto } from '../models/migration-http.dtos';
import { LibraryActivationConflictFacts } from '../models/library-transfer.models';
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
  /**
   * Fresh destination facts from the activation 409. When present the dialog
   * shows the server's counts and explains that the library changed.
   */
  readonly conflict = input<LibraryActivationConflictFacts | null>(null);
  readonly supportsSafeActivation = input(false);
  /** Sealed while the host performs the confirmed replacement. */
  readonly busy = input(false);
  /** Label on the sealed action while `busy`. */
  readonly busyLabel = input<string>('Finishing…');
  /** Host-reported activation failure; non-null unseals and shows the error. */
  readonly errorMessage = input<string | null>(null);
  /** True when the failed activation must not be retried (fail-closed). */
  readonly retryBlocked = input(false);

  readonly confirmed = output<void>();
  readonly cancelled = output<void>();

  private confirmedOnce = false;
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private initialFocusApplied = false;

  constructor() {
    effect(() => {
      // Reopening (destination-conflict re-review) and a host-reported failure
      // both start a fresh destructive decision; only a successful submission
      // keeps the latch.
      if (!this.isOpen() || this.errorMessage() || this.conflict()) this.confirmedOnce = false;
    });

    // CDK's autoCapture runs once, and when the dialog first renders the cancel
    // action can still be disabled while the unconfirmed activation probe is in
    // flight: the trap finds no focusable initial element and focus stays on
    // the page behind the modal. Apply the documented initial focus after the
    // dialog actually renders, as soon as it is interactive, once per opening.
    afterRenderEffect(() => {
      if (!this.isOpen()) {
        this.initialFocusApplied = false;
        return;
      }
      if (this.busy() || this.initialFocusApplied) return;
      const cancel = this.host.nativeElement.querySelector<HTMLElement>('.replacement-cancel');
      if (!cancel) return;
      cancel.focus();
      this.initialFocusApplied = true;
    });
  }

  private readonly preparedFacts = computed(() => readPreparedImportFacts(this.preparedImport()));

  /** True when the incoming counts came from the server's prepared import. */
  readonly incomingCountsVerified = computed(() => this.preparedFacts()?.incomingCounts !== undefined);

  /**
   * Current-library counts. The server's fresh 409 counts win over the
   * preflight estimate whenever the activation route re-checked the library.
   */
  readonly existingSummary = computed(() => {
    const counts = this.conflict()?.existingCounts ?? this.preflight()?.evaluation.existingCounts;
    if (!counts) return '';
    return formatLibraryCounts({
      books: counts.books,
      notes: counts.notes,
      collections: counts.collections,
    });
  });

  /**
   * Explanation shown when the server re-checked the library after a 409.
   * Only a server-confirmed change since the import started uses the
   * "changed" wording; a first confirmation of a populated destination is
   * not a change, and an older host that cannot report the flag keeps a
   * neutral re-review sentence.
   */
  readonly conflictNotice = computed(() => {
    const conflict = this.conflict();
    if (!conflict) return null;
    if (conflict.changedSinceImportStarted === true) {
      return (
        'This library changed since the import started. These are the current counts; ' +
        'confirm again to replace it.'
      );
    }
    if (conflict.changedSinceImportStarted === false) return null;
    return 'These are the current counts from this library; confirm again to replace it.';
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
    if (
      !this.supportsSafeActivation() ||
      this.busy() ||
      this.retryBlocked() ||
      this.confirmedOnce
    ) {
      return;
    }
    this.confirmedOnce = true;
    this.confirmed.emit();
  }
}
