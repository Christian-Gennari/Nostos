import { Overlay, OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  TemplateRef,
  ViewContainerRef,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { DOCUMENT, NgTemplateOutlet } from '@angular/common';
import { isPhoneViewport } from '../breakpoints';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { ModalShell } from '../modal-shell/modal-shell.component';
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
 * overlay the caller positions. At phone width it is a sheet instead (the same
 * approach as the “Link a topic” dialog): a popover under the field would sit
 * behind the soft keyboard, so the field goes to the top and the list scrolls
 * in the space above the keyboard.
 */
@Component({
  selector: 'app-topic-picker-popover',
  standalone: true,
  imports: [NgTemplateOutlet, NostosIconComponent, InputDirective, TopicOptionComponent, ModalShell, IconButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-template #fieldTpl>
      <label class="visually-hidden" [attr.for]="inputId">{{ label() }}</label>
      <input
        appInput
        [controlSize]="sheet ? 'normal' : 'compact'"
        class="picker-field"
        type="text"
        autocomplete="off"
        enterkeyhint="done"
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
    </ng-template>

    <ng-template #listTpl>
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
    </ng-template>

    <!-- The sheet is attached to its own overlay (see openSheet), not rendered
         here: this host sits in the caller's connected overlay, whose pane is
         positioned with a transform, and a transformed ancestor turns the
         shell's fixed backdrop into a zero-width box inside that pane. -->
    <ng-template #sheetTpl>
      <app-modal-shell
        [isOpen]="true"
        variant="sheet"
        class="picker-sheet"
        [maxWidth]="'460px'"
        [maxHeight]="'min(560px, calc(100dvh - 2.5rem))'"
        [layer]="130"
        [trapFocus]="true"
        [ariaLabel]="label()"
        (closed)="closed.emit()"
      >
        <div shellHeader class="sheet-head">
          <div class="sheet-title-row">
            <h2 class="sheet-title">{{ label() }}</h2>
            <button
              appIconButton
              type="button"
              icon="x"
              [glyphSize]="18"
              class="sheet-close"
              title="Close"
              aria-label="Close"
              (click)="closed.emit()"
            ></button>
          </div>
          <ng-container [ngTemplateOutlet]="fieldTpl"></ng-container>
        </div>
        <ng-container [ngTemplateOutlet]="listTpl"></ng-container>
      </app-modal-shell>
    </ng-template>

    @if (!sheet) {
      <div class="picker" role="dialog" [attr.aria-label]="label()">
        <ng-container [ngTemplateOutlet]="fieldTpl"></ng-container>
        <ng-container [ngTemplateOutlet]="listTpl"></ng-container>
      </div>
    }
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

      /* Phone sheet: the shell owns the card; the list fills what is left. */
      .sheet-head {
        display: flex;
        flex-direction: column;
        gap: 0.7rem;
      }

      .sheet-title-row {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 0.75rem;
      }

      .sheet-title {
        margin: 0;
        color: var(--color-text-main);
        font-size: 1.05rem;
        font-weight: var(--fw-medium);
        line-height: 1.3;
      }

      .sheet-close {
        flex-shrink: 0;
        margin: -0.35rem -0.4rem -0.35rem 0;
      }

      .picker-sheet .picker-field {
        /* 16px: anything smaller makes iOS zoom the page when the field takes focus. */
        font-size: var(--text-md);
      }

      .picker-sheet .picker-list {
        margin: 0;
        padding: 0.375rem;
        max-height: none;
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

  /** Phone width: a sheet, not a popover. Fixed for the life of the picker. */
  readonly sheet = isPhoneViewport();

  readonly query = signal('');
  readonly active = signal(0);

  private readonly sheetTpl = viewChild.required<TemplateRef<unknown>>('sheetTpl');
  private readonly overlay = inject(Overlay);
  private readonly viewContainer = inject(ViewContainerRef);
  private sheetRef: OverlayRef | null = null;
  private destroyed = false;

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
    // After the caller's overlay has finished attaching: it registers with the
    // outside-click dispatcher last, and the overlay on top of that stack is the
    // one that must hold presses inside the sheet (see openSheet).
    queueMicrotask(() => {
      if (this.destroyed) return;
      if (this.sheet) this.openSheet();
      this.document.getElementById(this.inputId)?.focus();
    });
  }

  /** The phone sheet, on a global overlay of its own (no transformed ancestor). */
  private openSheet(): void {
    const ref = this.overlay.create({
      positionStrategy: this.overlay.position().global(),
      scrollStrategy: this.overlay.scrollStrategies.noop(),
    });
    // The caller's connected overlay closes on a press outside itself. A press
    // inside this sheet is outside that one, unless this overlay claims it:
    // the dispatcher stops at the first overlay holding the press.
    ref.outsidePointerEvents().subscribe();
    ref.attach(new TemplatePortal(this.sheetTpl(), this.viewContainer));
    this.sheetRef = ref;
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    const sheet = this.sheetRef?.overlayElement;
    this.sheetRef?.dispose();
    this.sheetRef = null;
    // Closing hands focus back to the opener, unless the user already moved it
    // somewhere else on purpose (focus is then outside the picker).
    const origin = this.origin;
    this.origin = null;
    if (!origin?.isConnected) return;
    const active = this.document.activeElement;
    if (!active || active === this.document.body || this.host.nativeElement.contains(active) || sheet?.contains(active)) {
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
