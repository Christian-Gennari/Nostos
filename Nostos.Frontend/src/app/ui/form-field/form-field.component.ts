/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * Shared label/help/validation frame for ordinary form controls.
 *
 * The actual control remains projected and native. FormField owns the repeated
 * label, required marker, help text and validation-message presentation only.
 */
@Component({
  selector: 'app-form-field',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './form-field.component.html',
  styleUrl: './form-field.component.css',
  host: {
    class: 'nostos-form-field',
    '[class.nostos-form-field--invalid]': '!!error()',
  },
})
export class FormFieldComponent {
  readonly label = input.required<string>();
  readonly forId = input.required<string>();
  readonly required = input(false);
  readonly labelNote = input<string | null>(null);
  readonly hint = input<string | null>(null);
  readonly error = input<string | null>(null);

  readonly hintId = computed(() => `${this.forId()}-hint`);
  readonly errorId = computed(() => `${this.forId()}-error`);
  readonly describedBy = computed(() => {
    const ids: string[] = [];
    if (this.hint()) ids.push(this.hintId());
    if (this.error()) ids.push(this.errorId());
    return ids.length ? ids.join(' ') : null;
  });
}
