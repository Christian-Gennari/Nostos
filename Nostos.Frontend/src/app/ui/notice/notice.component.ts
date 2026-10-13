/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type NoticeTone = 'neutral' | 'info' | 'caution' | 'danger';

/**
 * A message box inside a page or dialog: something happened, or something
 * needs knowing before the next step.
 *
 * It owns the box only: border, corner, fill, padding and, for `info`, the
 * ink. The call site keeps its own element (`p`, `div`), its `role`
 * (`status` or `alert`), its layout and its type size, because those differ
 * with what the notice holds: one line of text, a headline with a retry
 * button, a list of steps.
 *
 *   <p appNotice tone="danger" role="alert">{{ error }}</p>
 *
 * WHY THIS EXISTS: the same box had been written as twelve rules in six
 * stylesheets, as four colour treatments that had drifted in padding (6px to
 * 12px) and corner (3px to 6px). A transient message that floats over the page
 * is a toast, not this.
 */
@Component({
  selector: '[appNotice]',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<ng-content />',
  host: {
    class: 'nostos-notice',
    '[class.nostos-notice--info]': "tone() === 'info'",
    '[class.nostos-notice--caution]': "tone() === 'caution'",
    '[class.nostos-notice--danger]': "tone() === 'danger'",
  },
  styles: [
    `
      :host {
        box-sizing: border-box;
        padding: 0.65rem 0.8rem;
        border: 1px solid var(--border-color);
        border-radius: var(--radius-surface);
        background: transparent;
      }

      :host(.nostos-notice--info) {
        border-color: var(--color-info-border);
        background: var(--color-info-bg);
        color: var(--color-info);
      }

      :host(.nostos-notice--caution) {
        border-color: color-mix(in srgb, var(--color-accent) 20%, transparent);
        background: color-mix(in srgb, var(--color-accent) 8%, transparent);
      }

      :host(.nostos-notice--danger) {
        border-color: var(--danger-field-edge);
        background: var(--danger-field);
      }
    `,
  ],
})
export class NoticeComponent {
  readonly tone = input<NoticeTone>('neutral');
}
