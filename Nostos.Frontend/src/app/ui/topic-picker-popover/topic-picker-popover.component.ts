import {
  AfterViewInit,
  OnDestroy,
  inject,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import type { TopicDto } from '../../core/services/topics.service';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { InputDirective } from '../form-control/form-control.directive';
import { NO_TOPICS_MATCH, NO_TOPICS_YET, TopicOptionComponent } from '../topic-option/topic-option.component';
import { hasTopic, newTopicName, rankTopicChoices, topicChoices } from '../note-item/note-item.helpers';

/** Where AI topic suggestions are; `off` hides the group entirely. */
export type TopicSuggestionState = 'off' | 'loading' | 'ready' | 'error';

interface PickerOption {
  kind: 'topic' | 'create' | 'suggestion';
  name: string;
  count?: number;
}

let nextPickerId = 0;

/**
 * Choose a topic for a note: type to filter, Enter to take the highlighted
 * name, a new name creates the topic. AI suggestions, when the caller has
 * them, are a quiet group under the matches.
 *
 * It is a listbox driven from its own text field (focus stays in the field,
 * the active option is announced through aria-activedescendant), shown in an
 * overlay the caller positions.
 */
@Component({
  selector: 'app-topic-picker-popover',
  standalone: true,
  imports: [NostosIconComponent, InputDirective, TopicOptionComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="picker" role="dialog" [attr.aria-label]="label()">
      <label class="visually-hidden" [attr.for]="inputId">{{ label() }}</label>
      <input
        #field
        appInput
        controlSize="compact"
        class="picker-field"
        type="text"
        autocomplete="off"
        role="combobox"
        aria-autocomplete="list"
        aria-expanded="true"
        [id]="inputId"
        [attr.aria-controls]="listId"
        [attr.aria-activedescendant]="activeId()"
        [placeholder]="placeholder()"
        [value]="query()"
        (input)="setQuery($any($event.target).value)"
        (keydown)="onKeydown($event)"
      />
      <ul class="picker-list" role="listbox" [id]="listId" [attr.aria-label]="label()">
        @for (option of options(); track option.kind + option.name; let i = $index) {
          @if (option.kind === 'suggestion' && (i === 0 || options()[i - 1].kind !== 'suggestion')) {
            <li class="picker-group" role="presentation">
              <nostos-icon concept="aiSuggestion" [size]="12" />Suggested
            </li>
          }
          <li
            appTopicOption
            role="option"
            [id]="optionId(i)"
            [name]="option.name"
            [kind]="option.kind"
            [count]="option.count"
            [active]="i === active()"
            [attr.aria-selected]="i === active()"
            (mousedown)="$event.preventDefault()"
            (mouseenter)="active.set(i)"
            (click)="choose(option)"
          ></li>
        }
        @if (suggestionState() === 'loading') {
          <li class="picker-group picker-status" role="presentation">
            <nostos-icon concept="aiSuggestion" [size]="12" />Finding suggestions…
          </li>
        } @else if (suggestionState() === 'error') {
          <li class="picker-group picker-status" role="presentation">Suggestions are unavailable right now.</li>
        }
        @if (options().length === 0 && suggestionState() !== 'loading') {
          <li class="picker-empty" role="presentation">{{ query().trim() ? noMatch : noTopics }}</li>
        }
      </ul>
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
      }

      .picker {
        width: min(20rem, calc(100vw - 2rem));
        padding: 0.4rem;
        border: 1px solid var(--border-color);
        border-radius: var(--radius-popover);
        background: var(--bg-surface);
        box-shadow: var(--shadow-md);
      }

      .picker-field {
        width: 100%;
      }

      .picker-list {
        list-style: none;
        margin: 0.35rem 0 0;
        padding: 0;
        max-height: 17rem;
        overflow-y: auto;
      }

      .picker-group {
        display: flex;
        align-items: center;
        gap: 0.35rem;
        margin-top: 0.35rem;
        padding: 0.45rem 0.6rem 0.2rem;
        border-top: 1px solid var(--border-color);
        color: var(--color-text-light);
        font-size: 0.68rem;
        font-weight: var(--fw-semibold);
        letter-spacing: 0.06em;
        text-transform: uppercase;
      }

      .picker-status {
        font-weight: var(--fw-regular);
        letter-spacing: 0;
        text-transform: none;
        font-size: var(--text-xs);
      }

      .picker-empty {
        padding: 0.6rem;
        color: var(--color-text-light);
        font-size: var(--text-sm);
      }
    `,
  ],
})
export class TopicPickerPopoverComponent implements AfterViewInit, OnDestroy {
  /** Every topic there is, to choose from. */
  readonly topics = input<readonly TopicDto[]>([]);
  /** Names the note already has; they are not offered again. */
  readonly exclude = input<readonly string[]>([]);
  /** AI suggestions for this note, already free of names it has. */
  readonly suggestions = input<readonly string[]>([]);
  readonly suggestionState = input<TopicSuggestionState>('off');
  readonly label = input('Add a topic');
  readonly placeholder = input('Find or create a topic…');

  readonly chosen = output<string>();
  readonly closed = output<void>();

  readonly noTopics = NO_TOPICS_YET;
  readonly noMatch = NO_TOPICS_MATCH;

  readonly inputId = `topic-picker-${nextPickerId++}`;
  readonly listId = `${this.inputId}-list`;

  readonly query = signal('');
  readonly active = signal(0);

  private readonly field = viewChild.required<ElementRef<HTMLInputElement>>('field');

  /** Every topic folded for matching once per list, not once per keystroke. */
  private readonly choices = computed(() => topicChoices(this.topics()));

  readonly options = computed<PickerOption[]>(() => {
    const exclude = this.exclude();
    const options: PickerOption[] = rankTopicChoices(this.choices(), this.query(), exclude)
      .map((topic) => ({ kind: 'topic', name: topic.name, count: topic.usageCount }));
    const create = newTopicName(this.topics(), this.query());
    if (create && !hasTopic(exclude, create)) options.push({ kind: 'create', name: create });
    if (this.suggestionState() === 'ready') {
      const q = this.query().trim().toLocaleLowerCase();
      for (const name of this.suggestions()) {
        if (hasTopic(exclude, name) || hasTopic(options.map((o) => o.name), name)) continue;
        if (q && !name.toLocaleLowerCase().includes(q)) continue;
        options.push({ kind: 'suggestion', name });
      }
    }
    return options;
  });

  readonly activeId = computed(() => (this.options().length ? this.optionId(this.active()) : null));

  private readonly document = inject(DOCUMENT);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** What had focus when the picker opened: the control that asked for it. */
  private origin: HTMLElement | null = null;

  ngAfterViewInit(): void {
    const active = this.document.activeElement;
    this.origin = active instanceof HTMLElement && active !== this.document.body ? active : null;
    queueMicrotask(() => this.field().nativeElement.focus());
  }

  ngOnDestroy(): void {
    // Closing hands focus back to the opener, unless the user already moved it
    // somewhere else on purpose (focus is then outside the picker).
    const origin = this.origin;
    this.origin = null;
    if (!origin?.isConnected) return;
    const active = this.document.activeElement;
    if (!active || active === this.document.body || this.host.nativeElement.contains(active)) {
      origin.focus({ preventScroll: true });
    }
  }

  optionId(index: number): string {
    return `${this.inputId}-option-${index}`;
  }

  setQuery(value: string): void {
    this.query.set(value);
    this.active.set(0);
  }

  onKeydown(event: KeyboardEvent): void {
    const count = this.options().length;
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        if (count) this.active.set((this.active() + 1) % count);
        break;
      case 'ArrowUp':
        event.preventDefault();
        if (count) this.active.set((this.active() - 1 + count) % count);
        break;
      case 'Enter': {
        event.preventDefault();
        const option = this.options()[this.active()];
        if (option) this.choose(option);
        break;
      }
      case 'Escape':
        event.preventDefault();
        event.stopPropagation();
        this.closed.emit();
        break;
    }
  }

  choose(option: PickerOption): void {
    this.chosen.emit(option.name);
  }
}
