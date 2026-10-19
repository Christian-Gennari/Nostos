import { ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';

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

  /** The topic being edited; a draft belongs to it and to no other. */
  private readonly editingId = signal<string | null>(null);
  readonly editing = computed(() => this.editingId() !== null && this.editingId() === this.topic().id);
  readonly draft = signal('');
  readonly saving = signal(false);
  readonly max = TOPIC_WORDS_MAX;

  readonly paragraphs = computed(() =>
    (this.topic().description ?? '').split(/\n\s*\n/).map((part) => part.trim()).filter(Boolean));

  private readonly field = viewChild<ElementRef<HTMLTextAreaElement>>('field');
  private readonly opener = viewChild<ElementRef<HTMLButtonElement>>('opener');

  constructor() {
    // Moving to another topic (the same view, a new input) drops the draft.
    effect(() => {
      const id = this.topic().id;
      untracked(() => {
        if (this.editingId() !== null && this.editingId() !== id && !this.saving()) this.editingId.set(null);
      });
    });
  }

  edit(): void {
    this.draft.set(this.topic().description ?? '');
    this.editingId.set(this.topic().id);
    this.focusField(true);
  }

  /** A click on the words edits them, unless it ended a text selection. */
  onTextClick(): void {
    if (!globalThis.getSelection?.()?.toString()) this.edit();
  }

  cancel(): void {
    if (this.saving()) return;
    this.editingId.set(null);
    this.focusOpener();
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
    if (this.editingId() !== topic.id) return;
    const words = this.draft().trim();
    if (words === (topic.description ?? '').trim()) {
      this.editingId.set(null);
      this.focusOpener();
      return;
    }
    this.saving.set(true);
    this.topics.setDescription(topic.id, words || null).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.editingId.set(null);
        this.state.replaceTopic(saved);
        this.focusOpener();
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Could not save your words. Try again.');
        this.focusField(false);
      },
    });
  }

  /** Focus stays with the words: the field while editing, Edit or Add after. */
  private focusField(toEnd: boolean): void {
    afterNextRender(() => {
      const field = this.field()?.nativeElement;
      if (!field) return;
      field.focus();
      if (toEnd) field.setSelectionRange(field.value.length, field.value.length);
    }, { injector: this.injector });
  }

  private focusOpener(): void {
    afterNextRender(() => this.opener()?.nativeElement.focus(), { injector: this.injector });
  }
}
