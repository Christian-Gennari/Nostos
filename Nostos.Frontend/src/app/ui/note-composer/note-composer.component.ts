import { ChangeDetectionStrategy, Component, computed, inject, input, model, output, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { BrainStateService } from '../../brain/brain-state.service';
import { TopicInputComponent } from '../topic-input.component/topic-input.component';
import { TopicRowComponent } from '../topic-row/topic-row.component';
import type { TopicSuggestionState } from '../topic-picker-popover/topic-picker-popover.component';
import { includesTopic, NOTE_PLACEHOLDER, removeTopic, splitTopics, topicNames } from './topic-text';

/** What a composer holds: the note's own words and its pill topics. */
export interface NoteDraft {
  /** The words, with any `[[link]]` typed inside a sentence. */
  words: string;
  /** Topics added as pills; the server writes them into the text. */
  topics: string[];
}

/**
 * A saved note as a draft to edit: its words, and its topic lines as pills.
 * `linked` are the topics the note is linked to; one its text never declared
 * (older data) shows as a pill too, so it can be seen and removed.
 */
export function draftForNote(content: string | null | undefined, linked: readonly string[] = []): NoteDraft {
  const draft = splitTopics(content);
  const declared = [...topicNames(draft.words), ...draft.topics];
  return { words: draft.words, topics: [...draft.topics, ...linked.filter((name) => !includesTopic(declared, name))] };
}

/**
 * `draft` as a save: the words, and the pill topics the words do not already
 * link inline (those are kept by the words themselves).
 */
export function draftToSave(draft: NoteDraft): NoteDraft {
  const words = draft.words.trim();
  const inline = topicNames(words);
  return { words, topics: draft.topics.filter((name) => !includesTopic(inline, name)) };
}

/** Whether saving `draft` would change the note `before` was opened from. */
export function draftChanged(draft: NoteDraft, before: NoteDraft): boolean {
  const a = draftToSave(draft);
  const b = draftToSave(before);
  const all = (d: NoteDraft) => [...topicNames(d.words), ...d.topics];
  const sameTopics = all(a).length === all(b).length && all(a).every((name) => includesTopic(all(b), name));
  return a.words !== b.words || !sameTopics;
}

/** True when the draft holds nothing: no words and no topics. */
export function draftIsEmpty(draft: NoteDraft): boolean {
  return !draft.words.trim() && draft.topics.length === 0;
}

/**
 * Where a note is written or edited, everywhere in the app (issue #1445): the
 * words, with `[[` autocomplete for a topic inside a sentence, and the topics
 * as pills below, with **+ Topic** for a topic that is a label. Every topic
 * shows as a pill, typed or added; the `[[Name]]` lines a pill becomes in the
 * stored text never show here (docs/topic-system.md, "Writing a note").
 *
 * Removing the pill of a typed topic turns its word plain, as on a card.
 *
 * A note field anywhere uses this, never a bare textarea: a guard spec
 * (note-fields.guard.spec.ts) fails otherwise.
 */
@Component({
  selector: 'app-note-composer',
  standalone: true,
  imports: [FormsModule, TopicInputComponent, TopicRowComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'note-composer', '[class.is-compact]': 'compact()' },
  template: `
    <div class="note-composer-field">
    <app-topic-input
      [ngModel]="words()"
      [ngModelOptions]="{ standalone: true }"
      (ngModelChange)="words.set($event)"
      [disabled]="disabled()"
      [placeholder]="placeholder()"
      [rows]="rows()"
      [inputId]="inputId()"
      [ariaLabel]="ariaLabel()"
      [testId]="testId()"
      [compact]="compact()"
      (submitTrigger)="submitted.emit()"
    />
    </div>
    <app-topic-row
      class="note-composer-topics"
      [names]="chips()"
      [topics]="brain.topics()"
      [linked]="false"
      [disabled]="disabled()"
      [suggestions]="suggestions()"
      [suggestionState]="suggestionState()"
      (added)="addTopic($event)"
      (removed)="removeTopic($event)"
      (pickerOpened)="pickerOpened.emit()"
      (pickerClosed)="pickerClosed.emit()"
    />
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      min-width: 0;
    }

    :host(.is-compact) {
      gap: 0.45rem;
    }

    /* The words sit in one field look everywhere; the pills sit under it,
       outside the box, as they sit under a note's words on its card. */
    .note-composer-field {
      border: 1px solid var(--border-color);
      border-radius: var(--radius-field);
      background: var(--bg-surface);
      transition:
        border-color var(--motion-fast) ease,
        box-shadow var(--motion-fast) ease;
    }

    .note-composer-field:focus-within {
      border-color: var(--border-focus);
      box-shadow: var(--shadow-sm);
    }

    @media (prefers-reduced-motion: reduce) {
      .note-composer-field {
        transition: none;
      }
    }
  `,
})
export class NoteComposerComponent {
  protected readonly brain = inject(BrainStateService);

  readonly words = model('');
  readonly topics = model<readonly string[]>([]);

  readonly placeholder = input(NOTE_PLACEHOLDER);
  readonly rows = input(3);
  /** The reader's selection bar and highlight card: tighter, same parts. */
  readonly compact = input(false);
  readonly disabled = input(false);
  readonly inputId = input<string | null>(null);
  readonly ariaLabel = input<string | null>(null);
  readonly testId = input<string | null>(null);
  /** AI suggestions for the picker, loaded by the caller when it opens. */
  readonly suggestions = input<readonly string[]>([]);
  readonly suggestionState = input<TopicSuggestionState>('off');

  /** Ctrl/Cmd+Enter in the words. */
  readonly submitted = output<void>();
  readonly pickerOpened = output<void>();
  readonly pickerClosed = output<void>();

  private readonly input = viewChild.required(TopicInputComponent);

  /** Every topic of the draft as a pill: typed ones first, then added ones. */
  readonly chips = computed(() => {
    const names = topicNames(this.words());
    for (const name of this.topics()) if (!includesTopic(names, name)) names.push(name.trim());
    return names;
  });

  /** The draft as it would be saved. */
  readonly draft = computed<NoteDraft>(() => draftToSave({ words: this.words(), topics: [...this.topics()] }));

  /** Holds `draft`, e.g. a note opened for an edit. */
  load(draft: NoteDraft): void {
    this.words.set(draft.words);
    this.topics.set(draft.topics);
  }

  focus(): void {
    this.input().focus();
  }

  addTopic(name: string): void {
    const topic = name.trim();
    if (!topic || includesTopic(this.chips(), topic)) return;
    this.topics.set([...this.topics(), topic]);
  }

  removeTopic(name: string): void {
    if (includesTopic(this.topics(), name)) {
      this.topics.set(this.topics().filter((other) => !includesTopic([other], name)));
    }
    if (includesTopic(topicNames(this.words()), name)) {
      this.words.set(removeTopic(this.words(), name));
    }
  }
}
