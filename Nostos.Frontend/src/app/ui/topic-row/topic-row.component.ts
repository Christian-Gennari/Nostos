import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { CdkConnectedOverlay, CdkOverlayOrigin, type ConnectedPosition } from '@angular/cdk/overlay';

import type { TopicDto } from '../../core/services/topics.service';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { TopicChipComponent } from '../topic-chip/topic-chip.component';
import { TopicPickerPopoverComponent, type TopicSuggestionState } from '../topic-picker-popover/topic-picker-popover.component';
import { topicLookup } from '../note-item/note-item.helpers';

const PICKER_POSITIONS: ConnectedPosition[] = [
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 6 },
  { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -6 },
  { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 6 },
];

/**
 * A note's topics as pills, with **+ Topic** to add one (issue #1447). The one
 * row every place shows a note's topics in: a note card (each pill opens its
 * topic) and the note composer, where a note is written or edited (pills do not
 * navigate away from the words). The row owns no data: it reports what was
 * added or removed, and shows the names it is given back.
 *
 * The picker is a popover beside the button, and a sheet over the keyboard on
 * a phone (`app-topic-picker-popover`).
 */
@Component({
  selector: 'app-topic-row',
  standalone: true,
  imports: [CdkConnectedOverlay, CdkOverlayOrigin, NostosIconComponent, TopicChipComponent, TopicPickerPopoverComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'topic-row', role: 'group', 'aria-label': 'Topics' },
  template: `
    @for (chip of chips(); track chip.name) {
      <app-topic-chip
        class="topic-row-chip"
        [name]="chip.name"
        [link]="chip.id ? ['/brain/topics', chip.id] : null"
        [removable]="editable()"
        [disabled]="disabled()"
        (removed)="removed.emit(chip.name)"
      />
    }
    @if (editable()) {
    <button
      type="button"
      class="topic-row-add"
      [class.is-first]="names().length === 0"
      cdkOverlayOrigin
      #addOrigin="cdkOverlayOrigin"
      [disabled]="disabled()"
      [attr.aria-expanded]="pickerOpen()"
      aria-haspopup="dialog"
      data-testid="topic-row-add"
      (click)="pickerOpen() ? closePicker() : openPicker()"
    >
      <nostos-icon name="plus" [size]="12" />{{ names().length === 0 ? 'Add a topic' : 'Topic' }}
    </button>
    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="addOrigin"
      [cdkConnectedOverlayOpen]="pickerOpen()"
      [cdkConnectedOverlayPositions]="positions"
      [cdkConnectedOverlayPush]="true"
      (overlayOutsideClick)="closePicker()"
      (detach)="closePicker()"
    >
      <app-topic-picker-popover
        [topics]="topics()"
        [exclude]="names()"
        [suggestions]="suggestions()"
        [suggestionState]="suggestionState()"
        [label]="label()"
        (chosen)="choose($event)"
        (closed)="closePicker()"
      />
    </ng-template>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.4rem;
    }

    .topic-row-add {
      position: relative;
      display: inline-flex;
      align-items: center;
      gap: 0.3rem;
      padding: 0.18rem 0.65rem;
      border: 1px dashed var(--border-color);
      border-radius: var(--radius-pill);
      background: transparent;
      color: var(--color-text-light);
      font: inherit;
      font-size: 0.8rem;
      line-height: 1.35;
      cursor: pointer;
      transition: color var(--motion-fast) ease, border-color var(--motion-fast) ease;
    }

    .topic-row-add.is-first {
      border-color: var(--color-text-light);
      color: var(--color-text-main);
    }

    .topic-row-add:hover:not(:disabled),
    .topic-row-add[aria-expanded='true'] {
      border-color: var(--color-text-muted);
      color: var(--color-text-main);
    }

    .topic-row-add:focus-visible {
      outline: var(--focus-ring-width) solid var(--focus-ring);
      outline-offset: 2px;
    }

    .topic-row-add:disabled {
      cursor: progress;
    }

    /* On touch the pill keeps its look and grows an invisible 44px hit area,
       as the chips do; the row spaces out so two targets never overlap. */
    @media (pointer: coarse), (hover: none) {
      :host {
        gap: 1rem 1.25rem;
      }

      .topic-row-add::after {
        content: '';
        position: absolute;
        inset-block: calc(50% - 22px);
        inset-inline: min(0px, calc(50% - 22px));
      }
    }

    @media (prefers-reduced-motion: reduce) {
      .topic-row-add {
        transition: none;
      }
    }
  `,
})
export class TopicRowComponent {
  /** The topics shown as pills, in order. */
  readonly names = input.required<readonly string[]>();
  /** Every topic, for the picker. */
  readonly topics = input<readonly TopicDto[]>([]);
  /** The same topics by lower-cased name, when the caller already built it. */
  readonly topicMap = input<ReadonlyMap<string, TopicDto> | null>(null);
  /** Each pill opens its topic. Off while the note is being written. */
  readonly linked = input(true);
  /** False shows the pills only: no cross, no **+ Topic**. */
  readonly editable = input(true);
  readonly disabled = input(false);
  readonly suggestions = input<readonly string[]>([]);
  readonly suggestionState = input<TopicSuggestionState>('off');
  /** The picker's accessible name. */
  readonly label = input('Add a topic to this note');

  readonly added = output<string>();
  readonly removed = output<string>();
  /** The picker opened: the caller may start loading suggestions. */
  readonly pickerOpened = output<void>();
  /** The picker closed (a choice, Escape, a click elsewhere). */
  readonly pickerClosed = output<void>();

  protected readonly positions = PICKER_POSITIONS;
  readonly pickerOpen = signal(false);

  private readonly lookup = computed(() => this.topicMap() ?? topicLookup(this.topics()));

  /** Each name with its topic's id, when it links and the topic exists. */
  protected readonly chips = computed(() => {
    const lookup = this.linked() ? this.lookup() : null;
    return this.names().map((name) => ({ name, id: lookup?.get(name.trim().toLocaleLowerCase())?.id ?? null }));
  });

  openPicker(): void {
    if (this.disabled() || this.pickerOpen()) return;
    this.pickerOpen.set(true);
    this.pickerOpened.emit();
  }

  closePicker(): void {
    if (!this.pickerOpen()) return;
    this.pickerOpen.set(false);
    this.pickerClosed.emit();
  }

  protected choose(name: string): void {
    this.closePicker();
    this.added.emit(name);
  }
}
