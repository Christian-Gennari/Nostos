import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import type { TopicDto } from '../../core/services/topics.service';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { InputDirective } from '../form-control/form-control.directive';
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
  imports: [NostosIconComponent, InputDirective],
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
            class="picker-option"
            role="option"
            [id]="optionId(i)"
            [class.is-active]="i === active()"
            [class.is-suggestion]="option.kind === 'suggestion'"
            [attr.aria-selected]="i === active()"
            (mousedown)="$event.preventDefault()"
            (mouseenter)="active.set(i)"
            (click)="choose(option)"
          >
            @if (option.kind === 'create') {
              <nostos-icon name="plus" [size]="14" />
              <span class="picker-name">Create “{{ option.name }}”</span>
            } @else {
              <nostos-icon name="hash" [size]="14" />
              <span class="picker-name">{{ option.name }}</span>
              @if (option.count !== undefined) {
                <span class="picker-count">{{ option.count }}</span>
              }
            }
          </li>
        }
        @if (suggestionState() === 'loading') {
          <li class="picker-group picker-status" role="presentation">
            <nostos-icon concept="aiSuggestion" [size]="12" />Finding suggestions…
          </li>
        } @else if (suggestionState() === 'error') {
          <li class="picker-group picker-status" role="presentation">Suggestions are unavailable right now.</li>
        }
        @if (options().length === 0 && suggestionState() !== 'loading') {
          <li class="picker-empty" role="presentation">{{ query().trim() ? 'No topic by that name.' : 'No topics yet. Type a name to create one.' }}</li>
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

      .picker-option {
        display: flex;
        align-items: center;
        gap: 0.55rem;
        min-height: 2.25rem;
        padding: 0 0.6rem;
        border-radius: var(--radius-popover-item);
        color: var(--color-text-main);
        font-size: var(--text-sm);
        cursor: pointer;
      }

      .picker-option nostos-icon {
        color: var(--color-text-light);
        flex: none;
      }

      .picker-option.is-active {
        background: var(--bg-hover);
      }

      .picker-option.is-suggestion {
        color: var(--color-text-muted);
      }

      .picker-name {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .picker-count {
        margin-left: auto;
        color: var(--color-text-light);
        font-size: var(--text-xs);
        font-variant-numeric: tabular-nums;
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
export class TopicPickerPopoverComponent implements AfterViewInit {
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

  ngAfterViewInit(): void {
    queueMicrotask(() => this.field().nativeElement.focus());
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
