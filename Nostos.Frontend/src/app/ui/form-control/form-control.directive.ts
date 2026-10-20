/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { Directive, input } from '@angular/core';

export type FormControlSize = 'normal' | 'compact';

/**
 * Canonical Nostos single-line native input.
 *
 * The directive keeps the native input host so type, name, autocomplete,
 * validation, ngModel and accessibility attributes remain caller-owned.
 */
@Directive({
  selector: 'input[appInput]',
  standalone: true,
  host: {
    class: 'nostos-form-control nostos-form-control--input',
    '[class.nostos-form-control--compact]': "controlSize() === 'compact'",
    '[attr.aria-invalid]': "invalid() ? 'true' : null",
  },
})
export class InputDirective {
  readonly controlSize = input<FormControlSize>('normal');
  readonly invalid = input(false);
}

/**
 * Canonical Nostos multi-line native textarea.
 */
@Directive({
  selector: 'textarea[appTextarea]',
  standalone: true,
  host: {
    class: 'nostos-form-control nostos-form-control--textarea',
    '[class.nostos-form-control--compact]': "controlSize() === 'compact'",
    '[attr.aria-invalid]': "invalid() ? 'true' : null",
  },
})
export class TextareaDirective {
  readonly controlSize = input<FormControlSize>('normal');
  readonly invalid = input(false);
}
