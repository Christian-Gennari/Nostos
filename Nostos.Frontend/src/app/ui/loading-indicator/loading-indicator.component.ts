/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { NostosIconComponent } from '../icon/nostos-icon.component';
import { NOSTOS_ICON_SIZE } from '../icon/nostos-icons';

export type LoadingIndicatorSize = keyof typeof NOSTOS_ICON_SIZE;

/**
 * Canonical Nostos indeterminate loading indicator.
 *
 * The spinner is intentionally only a glyph: callers keep ownership of the
 * region's geometry, reserved space, error state and empty state. By default
 * the host is a polite status region with one accessible label. Set
 * `decorative` when the spinner sits inside an existing live/status region so
 * the same wait is not announced twice.
 */
@Component({
  selector: 'nostos-loading-indicator',
  standalone: true,
  imports: [NostosIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nostos-icon name="circle-notch" [size]="iconSize()" aria-hidden="true"></nostos-icon>
    <span class="loading-indicator__label">{{ label() }}</span>
  `,
  host: {
    class: 'nostos-loading-indicator',
    '[attr.role]': "decorative() ? null : 'status'",
    '[attr.aria-live]': "decorative() ? null : 'polite'",
    '[attr.aria-atomic]': "decorative() ? null : 'true'",
    '[attr.aria-hidden]': "decorative() ? 'true' : null",
  },
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        color: var(--color-text-muted);
        line-height: 0;
      }

      nostos-icon {
        transform-origin: center;
        animation: nostos-loading-spin 850ms linear infinite;
      }

      .loading-indicator__label {
        position: absolute;
        width: 1px;
        height: 1px;
        padding: 0;
        margin: -1px;
        overflow: hidden;
        clip: rect(0, 0, 0, 0);
        white-space: nowrap;
        border: 0;
      }

      @keyframes nostos-loading-spin {
        to {
          transform: rotate(360deg);
        }
      }

      @media (prefers-reduced-motion: reduce) {
        nostos-icon {
          animation: none;
        }
      }
    `,
  ],
})
export class LoadingIndicatorComponent {
  readonly label = input('Loading');
  readonly size = input<LoadingIndicatorSize>('lg');
  readonly decorative = input(false);

  protected readonly iconSize = computed(() => NOSTOS_ICON_SIZE[this.size()]);
}
