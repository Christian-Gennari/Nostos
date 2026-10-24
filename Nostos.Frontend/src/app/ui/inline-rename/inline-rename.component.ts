/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  viewChild,
} from '@angular/core';

import { InputDirective } from '../form-control/form-control.directive';

let nextErrorId = 0;

/**
 * Rename something in place, where its name is shown: a collection or a
 * Studio document in a flat tree, a topic in the Brain's index or at the top of
 * its page.
 *
 *   <nostos-inline-rename
 *     [name]="topic.name"
 *     [error]="renameError()"
 *     [busy]="renaming()"
 *     (committed)="save($event)"
 *     (cancelled)="stopRenaming()"
 *   />
 *
 * ONE BEHAVIOUR, everywhere:
 *   - it opens focused, with the old name selected, so typing replaces it;
 *   - Enter saves, Escape cancels;
 *   - leaving the field saves too, if the name changed and is not empty; an
 *     unchanged or emptied field just closes, so a stray click never costs a
 *     name;
 *   - a name that cannot be used (empty on Enter, or refused by `validate`, or
 *     by the server through `error`) is said under the field, and the field
 *     stays open with what was typed;
 *   - no Save or Cancel buttons: the keys and the blur are the whole contract.
 *
 * It holds no record. The owner supplies the name, says when a save is running
 * (`busy`) or was refused (`error`), and decides what saving does. `committed`
 * carries the trimmed new name, and only fires when it differs from `name`.
 *
 * `variant="title"` edits a page heading (the open topic's title): the field
 * takes the heading's face and a rule under it instead of a box.
 *
 * WHY THIS EXISTS: the flat tree cancelled on blur and showed no error; the
 * Brain's topic rename had Save/Cancel buttons and an inline error. Two ways to
 * rename a thing in one app (issue #1209).
 */
@Component({
  standalone: true,
  selector: 'nostos-inline-rename',
  imports: [InputDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './inline-rename.component.html',
  styleUrl: './inline-rename.component.css',
  host: {
    '[class.inline-rename--title]': "variant() === 'title'",
    // The field sits inside clickable rows (a tree row selects on click).
    '(click)': '$event.stopPropagation()',
  },
})
export class InlineRenameComponent {
  /** `row` edits a name in a list; `title` edits a page heading. */
  readonly variant = input<'row' | 'title'>('row');
  /** The current name: the field's starting text and its accessible label. */
  readonly name = input.required<string>();
  /** The field's accessible label; defaults to "Rename <name>". */
  readonly label = input<string | null>(null);
  /** Said under the field when Enter is pressed on an empty field. */
  readonly requiredMessage = input('A name is required.');
  /** Refuses a name before it is saved (a duplicate); returns the message. */
  readonly validate = input<((name: string) => string | null) | null>(null);
  /** The owner's refusal (the server said no). Hidden again once the user types. */
  readonly error = input<string | null>(null);
  readonly busy = input(false);
  /** Put the caret in the field when it appears (off only for a static showcase). */
  readonly focusOnOpen = input(true);

  /** The trimmed new name, different from `name`. */
  readonly committed = output<string>();
  readonly cancelled = output<void>();
  /** The text as it is typed, for an owner that keeps it. */
  readonly valueChange = output<string>();

  protected readonly errorId = `inline-rename-error-${nextErrorId++}`;
  protected readonly draft = linkedSignal(() => this.name());
  private readonly localError = signal<string | null>(null);
  /** The owner's error stays until the text changes, then steps aside. */
  private readonly ownerErrorDismissed = linkedSignal({ source: this.error, computation: () => false });
  protected readonly shownError = computed(
    () => this.localError() ?? (this.ownerErrorDismissed() ? null : this.error()),
  );
  protected readonly ariaLabel = computed(() => this.label() ?? `Rename ${this.name()}`);

  /** Closed by Escape, or by a save already sent for this exact text. */
  private closed = false;
  private submitted: string | null = null;
  private destroyed = false;

  private readonly field = viewChild.required<ElementRef<HTMLInputElement>>('field');

  constructor() {
    inject(DestroyRef).onDestroy(() => (this.destroyed = true));
    // The field appears because the user asked to rename: put them in it with
    // the old name selected, so typing replaces it and Escape leaves it alone.
    afterNextRender(() => {
      if (!this.focusOnOpen()) return;
      const field = this.field().nativeElement;
      field.focus();
      field.select();
    });
  }

  protected onInput(value: string): void {
    this.draft.set(value);
    this.localError.set(null);
    this.ownerErrorDismissed.set(true);
    this.submitted = null;
    this.valueChange.emit(value);
  }

  protected onEnter(event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    this.commit(false);
  }

  protected onEscape(event: Event): void {
    event.stopPropagation();
    this.cancel();
  }

  protected onBlur(): void {
    if (this.destroyed || this.closed || this.busy()) return;
    this.commit(true);
  }

  private commit(fromBlur: boolean): void {
    if (this.busy()) return;
    const value = this.draft().trim();
    if (value === this.name().trim()) {
      this.cancel();
      return;
    }
    if (!value) {
      if (fromBlur) this.cancel();
      else this.localError.set(this.requiredMessage());
      return;
    }
    // Leaving a field whose text was already sent (blur after Enter, or the
    // field going away once the save landed, or after a refusal) sends nothing
    // more. Enter always sends: it is how a refused save is tried again.
    if (fromBlur && value === this.submitted) return;
    const refusal = this.validate()?.(value) ?? null;
    if (refusal) {
      this.localError.set(refusal);
      return;
    }
    this.submitted = value;
    // The owner's answer to this save is shown, even if it repeats the last one.
    this.ownerErrorDismissed.set(false);
    this.committed.emit(value);
  }

  private cancel(): void {
    if (this.closed || this.destroyed) return;
    this.closed = true;
    this.cancelled.emit();
  }
}
