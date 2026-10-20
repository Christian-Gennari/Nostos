/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * Canonical interactive Nostos chip.
 *
 * The host stays a native button. Callers keep type/disabled/aria-pressed and
 * accessible naming; this component owns the compact accent capsule recipe.
 * Ordinary actions belong to appButton instead.
 */
@Component({
  selector: 'button[appChip]',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<ng-content />',
  host: {
    class: 'nostos-chip',
  },
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 0.4rem;
        padding: 0.3rem 0.75rem;
        border: 1px solid color-mix(in srgb, var(--color-brand-accent) 35%, transparent);
        border-radius: var(--radius-pill);
        background: color-mix(in srgb, var(--color-brand-accent) 12%, transparent);
        color: var(--color-brand-accent);
        font-family: inherit;
        font-size: 0.8rem;
        font-weight: var(--fw-medium);
        line-height: normal;
        white-space: nowrap;
        cursor: pointer;
        touch-action: manipulation;
        transition:
          background-color 0.15s ease,
          border-color 0.15s ease,
          color 0.15s ease,
          transform 0.15s ease;
      }

      :host(:hover:not(:disabled)) {
        background: color-mix(in srgb, var(--color-brand-accent) 22%, transparent);
      }

      :host(:active:not(:disabled)) {
        transform: scale(0.97);
      }

      :host(:focus-visible) {
        outline: var(--focus-ring-width) solid var(--focus-ring);
        outline-offset: 2px;
      }

      :host(:disabled) {
        opacity: 0.5;
        cursor: not-allowed;
      }

      :host([aria-pressed='false']) {
        border-color: var(--border-color);
        background: var(--bg-hover);
        color: var(--color-text-light);
      }

      :host([aria-pressed='false']:hover:not(:disabled)) {
        background: var(--bg-surface-alt);
        color: var(--color-text-main);
      }

      @media (prefers-reduced-motion: reduce) {
        :host {
          transition: none;
        }
      }
    `,
  ],
})
export class ChipComponent {}
