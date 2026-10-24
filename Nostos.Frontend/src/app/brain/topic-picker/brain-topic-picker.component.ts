import { Component, input, model, output, ChangeDetectionStrategy } from '@angular/core';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';
import { FormsModule } from '@angular/forms';

import type { TopicDto } from '../../core/services/topics.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { InputDirective } from '../../ui/form-control/form-control.directive';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { SearchFieldComponent } from '../../ui/search-field/search-field.component';
import { NO_TOPICS_MATCH, TopicOptionComponent } from '../../ui/topic-option/topic-option.component';

/**
 * Pick one existing topic from a searchable list, then confirm.
 *
 * The Brain asks for this when merging a topic into another (notes get their
 * topics from the note's own picker, app-topic-picker-popover). It only shows
 * the list, in the shared topic option rows, and reports what was chosen. The
 * surface that opened it decides which topics are candidates and what
 * confirming does, and nothing is written until it does.
 *
 * Anything projected into it appears between the list and the actions.
 */
@Component({
  standalone: true,
  selector: 'app-brain-topic-picker',
  imports: [SearchFieldComponent, FormsModule, ButtonComponent, IconButtonComponent, InputDirective, TopicOptionComponent, EmptyStateComponent],
  templateUrl: './brain-topic-picker.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './brain-topic-picker.component.css',
})
export class BrainTopicPickerComponent {
  /** `card` stands alone under the topic tools; `inspector` sits inside a note. */
  readonly variant = input<'card' | 'inspector'>('card');
  readonly role = input<'group' | 'dialog'>('group');
  readonly label = input.required<string>();
  readonly heading = input.required<string>();
  readonly closeLabel = input.required<string>();
  readonly searchLabel = input.required<string>();
  readonly emptyText = input(NO_TOPICS_MATCH);
  readonly confirmLabel = input.required<string>();
  readonly confirmDisabled = input(false);
  /** Says what linking writes before it is written. Not shown for a merge. */
  readonly linkHint = input(false);

  readonly candidates = input.required<readonly TopicDto[]>();
  readonly selectedId = input<string | null>(null);
  readonly query = model('');

  readonly chosen = output<string>();
  readonly confirmed = output<void>();
  readonly closed = output<void>();

  /** Escape in the search field closes the picker and nothing behind it. */
  onEscape(event: Event): void {
    if (event.defaultPrevented) return;
    event.preventDefault();
    event.stopPropagation();
    this.closed.emit();
  }

  /**
   * Enter in the search field picks. With a topic already chosen (and still in
   * the list) it continues; otherwise it chooses the first match, so typing a
   * name and pressing Enter twice is the whole keyboard path.
   */
  onEnter(event: Event): void {
    event.preventDefault();
    const selectedId = this.selectedId();
    const candidates = this.candidates();
    if (selectedId !== null && !this.confirmDisabled() && candidates.some((c) => c.id === selectedId)) {
      this.confirmed.emit();
    } else if (candidates.length > 0) {
      this.chosen.emit(candidates[0].id);
    }
  }
}
