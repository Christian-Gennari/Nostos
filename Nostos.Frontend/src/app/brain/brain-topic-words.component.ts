import { ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, computed, inject, input, signal, viewChild } from '@angular/core';

import { TopicsService, type TopicDto } from '../core/services/topics.service';
import { ToastService } from '../core/services/toast.service';
import { ButtonComponent } from '../ui/button/button.component';
import { TextareaDirective } from '../ui/form-control/form-control.directive';
import { BrainStateService } from './brain-state.service';

/** The longest description the server keeps. */
export const TOPIC_WORDS_MAX = 4000;

/**
 * A topic in the reader's own words, under its title: shown as paragraphs,
 * or a quiet "Add your own words…" line; a click edits it in place. Save and
 * Cancel, or Cmd/Ctrl+Enter to save and Escape to cancel. An empty text
 * clears it.
 */
@Component({
  selector: 'app-brain-topic-words',
  standalone: true,
  imports: [ButtonComponent, TextareaDirective],
  templateUrl: './brain-topic-words.component.html',
  styleUrls: ['./brain-topic-words.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrainTopicWordsComponent {
  private readonly topics = inject(TopicsService);
  private readonly toast = inject(ToastService);
  private readonly state = inject(BrainStateService);
  private readonly injector = inject(Injector);

  readonly topic = input.required<TopicDto>();

  readonly editing = signal(false);
  readonly draft = signal('');
  readonly saving = signal(false);
  readonly max = TOPIC_WORDS_MAX;

  readonly paragraphs = computed(() =>
    (this.topic().description ?? '').split(/\n\s*\n/).map((part) => part.trim()).filter(Boolean));

  private readonly field = viewChild<ElementRef<HTMLTextAreaElement>>('field');

  edit(): void {
    this.draft.set(this.topic().description ?? '');
    this.editing.set(true);
    afterNextRender(() => {
      const field = this.field()?.nativeElement;
      if (!field) return;
      field.focus();
      field.setSelectionRange(field.value.length, field.value.length);
    }, { injector: this.injector });
  }

  cancel(): void {
    if (!this.saving()) this.editing.set(false);
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
      event.preventDefault();
      this.save();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.cancel();
    }
  }

  save(): void {
    if (this.saving()) return;
    const topic = this.topic();
    const words = this.draft().trim();
    if (words === (topic.description ?? '').trim()) {
      this.editing.set(false);
      return;
    }
    this.saving.set(true);
    this.topics.setDescription(topic.id, words || null).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.editing.set(false);
        this.state.replaceTopic({ ...topic, description: saved.description ?? null });
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Could not save your words. Try again.');
      },
    });
  }
}
