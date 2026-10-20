/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { booleanAttribute, ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type ProgressSize = 'sm' | 'md';
export type ProgressSurface = 'plain' | 'overlay';

/**
 * Canonical Nostos progress bar (determinate, or indeterminate when `value` is
 * null).
 *
 * WHY: the app had six hand-built track/fill pairs at four heights (2, 4, 6 and
 * 8px), on five different track tokens, with three different fills (the brand
 * ink, the on-primary white and a pastel gradient). The Library list's bar was
 * a white fill on --bg-hover, which on the light theme is close to invisible.
 *
 * The host is the progressbar (role, aria-valuenow, label) unless `decorative`
 * is set, for a surface whose own wrapper is already the progressbar region with
 * its caption inside it. The reader's page-position strip is NOT a progress bar
 * (it is a position marker with a knob) and stays the reader's.
 */
@Component({
  selector: 'nostos-progress',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="nostos-progress__fill" [style.width.%]="indeterminate() ? null : clamped()"></span>`,
  host: {
    class: 'nostos-progress',
    '[class.nostos-progress--md]': "size() === 'md'",
    '[class.nostos-progress--overlay]': "surface() === 'overlay'",
    '[class.nostos-progress--paused]': 'paused()',
    '[class.nostos-progress--indeterminate]': 'indeterminate()',
    '[attr.role]': "decorative() ? null : 'progressbar'",
    '[attr.aria-valuemin]': "decorative() ? null : 0",
    '[attr.aria-valuemax]': "decorative() ? null : 100",
    '[attr.aria-valuenow]': 'decorative() || indeterminate() ? null : clamped()',
    '[attr.aria-valuetext]': 'decorative() ? null : valueText() || null',
    '[attr.aria-label]': 'decorative() ? null : label() || null',
    '[attr.aria-hidden]': "decorative() ? 'true' : null",
  },
  styles: [
    `
      :host {
        position: relative;
        display: block;
        height: 4px;
        overflow: hidden;
        border-radius: var(--radius-pill);
        background: var(--bg-hover);
        /* A hairline so the empty track still reads as a bar on both themes. */
        box-shadow: inset 0 0 0 1px var(--border-light);
      }
      :host(.nostos-progress--md) {
        height: 6px;
      }

      .nostos-progress__fill {
        display: block;
        height: 100%;
        border-radius: inherit;
        background: var(--color-primary);
        transition: width var(--motion-base) var(--ease-out);
      }

      :host(.nostos-progress--paused) .nostos-progress__fill {
        background: var(--color-text-faint);
      }

      /* On artwork (a cover's import strip): a dark scrim track, porcelain fill. */
      :host(.nostos-progress--overlay) {
        background: var(--overlay-medium);
        box-shadow: none;
      }
      :host(.nostos-progress--overlay) .nostos-progress__fill {
        background: var(--on-primary);
      }

      :host(.nostos-progress--indeterminate) .nostos-progress__fill {
        width: 35%;
        animation: nostos-progress-indeterminate 1.4s ease-in-out infinite;
      }

      @keyframes nostos-progress-indeterminate {
        from {
          transform: translateX(-110%);
        }
        to {
          transform: translateX(320%);
        }
      }

      @media (prefers-reduced-motion: reduce) {
        .nostos-progress__fill {
          transition: none;
        }
        :host(.nostos-progress--indeterminate) .nostos-progress__fill {
          animation: none;
          width: 100%;
          opacity: 0.45;
        }
      }
    `,
  ],
})
export class ProgressComponent {
  /** 0–100, or null for indeterminate. */
  readonly value = input<number | null | undefined>(null);
  readonly size = input<ProgressSize>('sm');
  readonly surface = input<ProgressSurface>('plain');
  readonly paused = input(false, { transform: booleanAttribute });
  readonly label = input<string | null | undefined>(null);
  readonly valueText = input<string | null | undefined>(null);
  readonly decorative = input(false, { transform: booleanAttribute });

  protected readonly indeterminate = computed(() => this.value() === null || this.value() === undefined);
  protected readonly clamped = computed(() => Math.max(0, Math.min(100, this.value() ?? 0)));
}
