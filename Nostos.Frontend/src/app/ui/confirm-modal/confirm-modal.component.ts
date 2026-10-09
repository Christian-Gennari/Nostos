import { Component, input, output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ModalShell } from '../modal-shell/modal-shell.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { ButtonComponent } from '../button/button.component';
import { DialogActionsComponent } from '../dialog-actions/dialog-actions.component';

/**
 * Tone of the confirmation. `danger` is the destructive treatment (muted wine
 * mark plus a desaturated solid action); `neutral` keeps the app's pine primary
 * for confirmations that change state without destroying anything.
 */
export type ConfirmTone = 'danger' | 'neutral';

/**
 * The single in-app confirmation surface.
 *
 * Every destructive or state-overwriting question in Nostos should be asked
 * through this component rather than `window.confirm()`, which breaks the
 * visual language and cannot carry theme tokens. It is deliberately generic:
 * the caller owns the wording (`heading`, `description`, `confirmLabel`) and
 * the pending state (`busy`), so nothing here knows what is being confirmed.
 *
 * The backdrop, the card and both dismissal paths (Escape, backdrop click)
 * come from `app-modal-shell`. It stays a `dialog` rather than a `sheet`: a
 * phone screen filled with one sentence and two buttons reads as an error
 * state, not a question.
 */
@Component({
  selector: 'app-confirm-modal',
  standalone: true,
  imports: [CommonModule, NostosIconComponent, ModalShell, ButtonComponent, DialogActionsComponent],
  templateUrl: './confirm-modal.component.html',
  styleUrl: './confirm-modal.component.css',
})
export class ConfirmModal {
  isOpen = input.required<boolean>();

  /** The question itself — the caller composes it, e.g. `Delete “Meditations”?` */
  heading = input.required<string>();

  /** One calm sentence explaining the consequence. Hidden when empty. */
  description = input<string>('');

  confirmLabel = input<string>('Delete');
  cancelLabel = input<string>('Cancel');

  /** Label shown on the action while `busy` is true. */
  busyLabel = input<string>('Working…');

  /** True while the confirmed action is in flight: locks the dialog. */
  busy = input<boolean>(false);

  /** Caller-owned requirement before the destructive action becomes available. */
  confirmDisabled = input<boolean>(false);

  /** Opt-in focus containment for callers that need strict modal focus control. */
  trapFocus = input<boolean>(false);

  tone = input<ConfirmTone>('danger');

  confirm = output<void>();
  cancel = output<void>();
  /**
   * The card belongs to the shell, so its class is handed over rather than
   * applied here — the component's tests still address the dialog by it.
   */
  readonly cardClass = 'confirm-modal-card';
}
