/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import {
  ChangeDetectionStrategy,
  Component,
  ViewEncapsulation,
  input,
} from '@angular/core';

export type DialogActionsVariant = 'footer' | 'inset';

/**
 * Canonical composition for ordinary dialog actions.
 *
 * ModalShell owns the backdrop/card/scroll geometry. This component owns the
 * ordinary action row inside that shell: spacing, the optional leading action,
 * and the narrow-screen arrangement. The actions themselves stay native
 * buttons and use appButton; special working-area/inline controls should not be
 * forced through this component.
 *
 * Use `dialogActionsStart` for the uncommon leading action (for example
 * Delete in Edit Book). Ordinary cancel/confirm actions are projected into the
 * trailing group by default.
 */
@Component({
  selector: 'app-dialog-actions',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  template: `
    <div class="nostos-dialog-actions__start">
      <ng-content select="[dialogActionsStart]" />
    </div>
    <div class="nostos-dialog-actions__end">
      <ng-content />
    </div>
  `,
  styleUrl: './dialog-actions.component.css',
  host: {
    class: 'nostos-dialog-actions',
    '[class.nostos-dialog-actions--footer]': "variant() === 'footer'",
    '[class.nostos-dialog-actions--inset]': "variant() === 'inset'",
    '[class.nostos-dialog-actions--stack-narrow]': 'stackOnNarrow()',
  },
})
export class DialogActionsComponent {
  /**
   * `footer` is the pinned, divided footer used by ordinary form dialogs.
   * `inset` is the same action composition inside an already padded compact
   * dialog such as ConfirmModal.
   */
  readonly variant = input<DialogActionsVariant>('footer');

  /**
   * Compact questions can stack their trailing actions on narrow screens.
   * Ordinary form footers keep their primary action pair on one row and move a
   * leading destructive action to its own row only when space is tight.
   */
  readonly stackOnNarrow = input(false);
}
