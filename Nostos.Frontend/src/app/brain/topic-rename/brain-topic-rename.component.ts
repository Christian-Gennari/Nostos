import {
  afterNextRender,
  Component,
  ElementRef,
  input,
  model,
  output,
  viewChild,
  ChangeDetectionStrategy,
} from '@angular/core';

import { InputDirective } from '../../ui/form-control/form-control.directive';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';

let nextErrorId = 0;

/**
 * Rename a topic in place, where its name is shown.
 *
 * The Brain renames from two places: a row of the index and the title of the
 * open topic. Both edit the name where it sits, so the field takes on the
 * type of the text it replaces and the layout around it does not move.
 *
 * It holds no topic state. The owner supplies the text, says when a save is
 * running or was refused, and decides what saving does.
 */
@Component({
  standalone: true,
  selector: 'app-brain-topic-rename',
  imports: [InputDirective, IconButtonComponent],
  templateUrl: './brain-topic-rename.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './brain-topic-rename.component.css',
})
export class BrainTopicRenameComponent {
  /** `row` edits a name in the index; `title` edits the open topic's heading. */
  readonly variant = input<'row' | 'title'>('row');
  /** The current name, for the field's accessible label. */
  readonly name = input.required<string>();
  readonly value = model('');
  readonly error = input<string | null>(null);
  readonly busy = input(false);

  readonly committed = output<void>();
  readonly cancelled = output<void>();

  protected readonly errorId = `brain-topic-rename-error-${nextErrorId++}`;
  private readonly field = viewChild.required<ElementRef<HTMLInputElement>>('field');

  constructor() {
    // The field appears because the user asked to rename: put them in it with
    // the old name selected, so typing replaces it and Escape leaves it alone.
    afterNextRender(() => {
      const field = this.field().nativeElement;
      field.focus();
      field.select();
    });
  }

  protected commit(event: Event): void {
    event.stopPropagation();
    if (!this.busy()) this.committed.emit();
  }

  protected cancel(event: Event): void {
    event.stopPropagation();
    this.cancelled.emit();
  }
}
