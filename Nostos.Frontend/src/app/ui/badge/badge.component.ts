/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type BadgeTone = 'neutral' | 'success' | 'danger';

/**
 * Passive metadata/state capsule.
 *
 * Badge never adds interaction semantics. Use appChip for an interactive,
 * removable or selectable capsule.
 */
@Component({
  selector: 'span[appBadge]',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<ng-content />',
  host: {
    class: 'nostos-badge',
    '[class.nostos-badge--success]': "tone() === 'success'",
    '[class.nostos-badge--danger]': "tone() === 'danger'",
    '[class.nostos-badge--dot]': 'dot()',
  },
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        padding: 2px 8px;
        border-radius: var(--radius-pill);
        background: var(--bg-hover);
        color: var(--color-text-light);
        font-size: 0.72rem;
        font-weight: var(--fw-medium);
        white-space: nowrap;
      }

      :host(.nostos-badge--success:not(.nostos-badge--dot)) {
        background: color-mix(in srgb, var(--color-success) 15%, transparent);
        color: var(--color-success-dark);
      }

      :host(.nostos-badge--danger:not(.nostos-badge--dot)) {
        background: color-mix(in srgb, var(--color-danger) 12%, transparent);
        color: var(--color-danger);
      }

      :host(.nostos-badge--dot) {
        gap: 6px;
        min-height: 24px;
        background: var(--bg-hover);
        color: var(--color-text-muted);
        font-size: 0.69rem;
      }

      :host(.nostos-badge--dot)::before {
        content: '';
        width: 6px;
        height: 6px;
        flex: 0 0 auto;
        border-radius: var(--radius-round);
        background: var(--color-text-muted);
        opacity: 0.55;
      }

      :host(.nostos-badge--dot.nostos-badge--success) {
        color: var(--color-success-dark);
      }

      :host(.nostos-badge--dot.nostos-badge--success)::before {
        background: var(--color-success);
        opacity: 1;
      }

      :host(.nostos-badge--dot.nostos-badge--danger) {
        color: var(--color-danger);
      }

      :host(.nostos-badge--dot.nostos-badge--danger)::before {
        background: var(--color-danger);
        opacity: 1;
      }
    `,
  ],
})
export class BadgeComponent {
  readonly tone = input<BadgeTone>('neutral');
  readonly dot = input(false);
}
