/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import {
  afterEveryRender,
  ChangeDetectionStrategy,
  Component,
  contentChild,
  ElementRef,
  input,
  output,
  signal,
  ViewEncapsulation,
} from '@angular/core';
import { InputDirective } from '../form-control/form-control.directive';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';

/**
 * A search field: a leading magnifying glass, the caller's own `<input>`, and a
 * clear button once there is something to clear.
 *
 *   <nostos-search-field>
 *     <input appInput controlSize="compact" type="search"
 *            placeholder="Search books…" aria-label="Search books"
 *            [value]="query()" (input)="onSearch($event)" />
 *   </nostos-search-field>
 *
 * The input stays the caller's: type, name, value binding, ngModel, keyboard
 * handlers, ARIA (a combobox's `aria-activedescendant`) are all written on it as
 * on any `appInput`. The field only frames it. Write `type="search"` and end the
 * placeholder with "…".
 *
 * Clear empties the input the way typing would (it dispatches `input`, so
 * `ngModel` and `(input)` handlers see it), emits `cleared`, and puts the caret
 * back in the field. The browser's own clear glyph is hidden, so there is one
 * clear control in every browser. Turn it off with `[clearable]="false"` where a
 * close button already sits beside the field (the reader's search panel, a topic
 * picker's dialog), so the two never read as one action twice.
 *
 * `size="sm"` is the dense rung for panels and dialogs: a 15px glass and a
 * narrower gutter. The default fits a page toolbar.
 *
 * WHY THIS EXISTS: eleven search inputs, some with a leading glass and some
 * without, `type="text"` beside `type="search"`, placeholders ending in "..."
 * beside "…", and a clear button only where the browser happened to draw one.
 */
@Component({
  selector: 'nostos-search-field',
  standalone: true,
  imports: [NostosIconComponent, IconButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The input is projected content, so its gutter has to be styled from here.
  encapsulation: ViewEncapsulation.None,
  template: `
    <nostos-icon
      class="nostos-search-field__icon"
      name="magnifying-glass"
      [size]="size() === 'sm' ? 15 : 18"
      aria-hidden="true"
    />
    <ng-content />
    @if (clearable() && hasValue()) {
      <button
        appIconButton
        type="button"
        size="xxs"
        icon="x"
        [glyphSize]="13"
        class="nostos-search-field__clear"
        [attr.aria-label]="clearLabel()"
        [title]="clearLabel()"
        (mousedown)="$event.preventDefault()"
        (click)="clear()"
      ></button>
    }
  `,
  host: {
    class: 'nostos-search-field',
    '[class.nostos-search-field--sm]': "size() === 'sm'",
    '[class.nostos-search-field--clearable]': 'clearable()',
    '(input)': 'sync()',
  },
  styleUrl: './search-field.component.css',
})
export class SearchFieldComponent {
  readonly size = input<'md' | 'sm'>('md');
  readonly clearable = input(true);
  readonly clearLabel = input('Clear search');

  /** The field was emptied by its clear button. */
  readonly cleared = output<void>();

  protected readonly hasValue = signal(false);
  private readonly field = contentChild.required(InputDirective, { read: ElementRef });

  constructor() {
    // A value written by the owner (a binding, ngModel, a reset) does not fire
    // `input`, so the clear button re-reads the field after every render.
    afterEveryRender({ read: () => this.sync() });
  }

  protected sync(): void {
    const field = this.field().nativeElement as HTMLInputElement;
    this.hasValue.set(field.value !== '' && !field.disabled);
  }

  clear(): void {
    const field = this.field().nativeElement as HTMLInputElement;
    field.value = '';
    field.dispatchEvent(new Event('input', { bubbles: true }));
    this.hasValue.set(false);
    this.cleared.emit();
    field.focus();
  }
}
