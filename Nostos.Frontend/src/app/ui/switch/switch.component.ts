/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * Canonical Nostos switch.
 *
 * The host is a <label>; callers project one native checkbox as its first child.
 * Checked/disabled/form/ARIA semantics therefore stay on the native input while
 * this component owns only the switch track and knob presentation.
 */
@Component({
  selector: 'label[appSwitch]',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<ng-content /><span class="nostos-switch-track" aria-hidden="true"></span>',
  host: {
    class: 'nostos-switch',
  },
  styles: [
    `
      :host {
        position: relative;
        display: inline-block;
        width: 42px;
        height: 24px;
        flex: 0 0 auto;
        cursor: pointer;
      }

      :host ::ng-deep input[type='checkbox'] {
        position: absolute;
        inset: 0;
        z-index: 1;
        width: 100%;
        height: 100%;
        margin: 0;
        opacity: 0;
        cursor: inherit;
      }

      .nostos-switch-track {
        position: absolute;
        inset: 0;
        border-radius: var(--radius-pill);
        background-color: var(--border-color);
        transition: background-color 0.2s ease;
      }

      .nostos-switch-track::before {
        content: '';
        position: absolute;
        width: 18px;
        height: 18px;
        left: 3px;
        bottom: 3px;
        border-radius: var(--radius-round);
        background-color: var(--on-primary);
        box-shadow: 0 1px 3px rgba(0, 0, 0, 0.15);
        transition: transform 0.2s ease;
      }

      :host ::ng-deep input[type='checkbox']:checked + .nostos-switch-track {
        background-color: var(--color-brand-accent-fill);
      }

      :host ::ng-deep input[type='checkbox']:checked + .nostos-switch-track::before {
        transform: translateX(18px);
      }

      :host ::ng-deep input[type='checkbox']:focus-visible + .nostos-switch-track {
        outline: var(--focus-ring-width) solid var(--focus-ring);
        outline-offset: 2px;
      }

      :host ::ng-deep input[type='checkbox']:disabled {
        cursor: not-allowed;
      }

      :host ::ng-deep input[type='checkbox']:disabled + .nostos-switch-track {
        opacity: 0.45;
        cursor: not-allowed;
      }
    `,
  ],
})
export class SwitchComponent {}
