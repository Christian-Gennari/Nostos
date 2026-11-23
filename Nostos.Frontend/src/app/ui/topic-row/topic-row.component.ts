import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { CdkConnectedOverlay, CdkOverlayOrigin, type ConnectedPosition } from '@angular/cdk/overlay';
import { RouterLink } from '@angular/router';

import type { TopicDto } from '../../core/services/topics.service';
import { ButtonComponent } from '../button/button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { TopicChipComponent } from '../topic-chip/topic-chip.component';
import { TopicPickerPopoverComponent, type TopicSuggestionState } from '../topic-picker-popover/topic-picker-popover.component';
import { topicLookup } from '../note-item/note-item.helpers';

const PICKER_POSITIONS: ConnectedPosition[] = [
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 6 },
  { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -6 },
  { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 6 },
];

/** How many names a quiet line shows before it counts the rest (`+N`). */
const QUIET_VISIBLE = 3;

/**
 * A note's topics, with **+ Topic** to add one (issue #1447). The one row every
 * place shows a note's topics in: a note card (each topic opens its topic) and
 * the note composer, where a note is written or edited (pills do not navigate
 * away from the words). The row owns no data: it reports what was added or
 * removed, and shows the names it is given back.
 *
 * `quiet` (a card in a list, issue #1797) shows one muted line of linked names,
 * three at most and `+N` for the rest, with no pills and no crosses. Tapping the
 * line or its trailing control turns it into the editable pill row, in place,
 * so removing a topic is a tap on it and adding one is a tap on **+ Topic**.
 *
 * The picker is a popover beside the button, and a sheet over the keyboard on
 * a phone (`app-topic-picker-popover`).
 */
@Component({
  selector: 'app-topic-row',
  standalone: true,
  imports: [CdkConnectedOverlay, CdkOverlayOrigin, RouterLink, ButtonComponent, NostosIconComponent, TopicChipComponent, TopicPickerPopoverComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'topic-row', role: 'group', 'aria-label': 'Topics' },
  template: `
    @if (asQuietLine()) {
    <div class="topic-line" (click)="onLineClick($event)">
      @for (chip of lineChips(); track chip.name) {
      <span class="topic-line-part">
        @if (chip.id) {
        <a class="topic-line-name" [routerLink]="['/brain/topics', chip.id]"
          [attr.title]="'Show notes on ' + chip.name">#{{ chip.name }}</a>
        } @else {
        <span class="topic-line-name">#{{ chip.name }}</span>
        }
      </span>
      @if (!$last) { <span class="topic-line-sep" aria-hidden="true">·</span> }
      }
      @if (hiddenCount() > 0) {
      <button
        appButton
        variant="link"
        size="sm"
        type="button"
        class="topic-line-more"
        [attr.aria-label]="'Show all ' + names().length + ' topics and edit them'"
        [disabled]="disabled()"
        data-testid="topic-line-more"
        (click)="openControls()"
      >+{{ hiddenCount() }}</button>
      } @else if (editable()) {
      <button
        appButton
        variant="link"
        size="sm"
        type="button"
        class="topic-line-edit"
        aria-label="Edit topics"
        title="Edit topics"
        [disabled]="disabled()"
        data-testid="topic-line-edit"
        (click)="openControls()"
      ><nostos-icon name="caret-down" [size]="12" /></button>
      }
    </div>
    } @else {
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
        (suggestionRetry)="suggestionRetry.emit()"
      />
    </ng-template>
    }
    @if (quiet() && names().length > 0) {
    <button
      appButton
      variant="link"
      size="sm"
      type="button"
      class="topic-line-done"
      aria-label="Done editing topics"
      title="Done editing topics"
      (click)="closeControls()"
    >Done</button>
    }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.4rem;
    }

    /* One quiet line of topics in a list card (issue #1797): muted names, no
       pills, no crosses. It never wraps; a long name is what gives way. */
    .topic-line {
      display: flex;
      align-items: baseline;
      gap: 0.3rem;
      min-width: 0;
      max-width: 100%;
      color: var(--color-text-light);
      font-size: 0.8rem;
      line-height: 1.5;
      cursor: pointer;
    }

    .topic-line-part {
      min-width: 0;
    }

    .topic-line-name {
      display: inline-block;
      overflow: hidden;
      max-width: 100%;
      color: var(--color-text-muted);
      text-decoration: none;
      text-overflow: ellipsis;
      vertical-align: bottom;
      white-space: nowrap;
    }

    a.topic-line-name:hover {
      color: var(--color-text-main);
      text-decoration: underline;
      text-underline-offset: 3px;
    }

    a.topic-line-name:focus-visible {
      outline: var(--focus-ring-width) solid var(--focus-ring);
      outline-offset: 2px;
    }

    .topic-line-sep {
      flex: none;
      color: var(--color-text-light);
    }

    .topic-line-more,
    .topic-line-edit,
    .topic-line-done {
      flex: none;
      color: var(--color-text-muted);
      font-size: 0.8rem;
    }

    .topic-line-more:hover:not(:disabled),
    .topic-line-edit:hover:not(:disabled),
    .topic-line-done:hover:not(:disabled) {
      color: var(--color-text-main);
    }

    .topic-line-more {
      font-variant-numeric: tabular-nums;
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

      /* The quiet line is still one line; every name and control in it keeps
         its look and grows the 44px target under it. */
      .topic-line {
        gap: 0.5rem;
      }

      .topic-line-name,
      .topic-line-more,
      .topic-line-edit,
      .topic-line-done {
        position: relative;
      }

      .topic-line-name::after,
      .topic-line-more::after,
      .topic-line-edit::after,
      .topic-line-done::after {
        content: '';
        position: absolute;
        inset-block: calc(50% - 22px);
        inset-inline: 0;
      }

      .topic-line-done::after {
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
  /**
   * In a card's list view (issue #1797): one quiet line of linked names with
   * the add/remove controls behind a tap, instead of a row of pills.
   */
  readonly quiet = input(false);

  readonly added = output<string>();
  readonly removed = output<string>();
  /** The picker opened: the caller may start loading suggestions. */
  readonly pickerOpened = output<void>();
  /** The picker closed (a choice, Escape, a click elsewhere). */
  readonly pickerClosed = output<void>();
  /** Try again in the picker's failure state: ask the same note again. */
  readonly suggestionRetry = output<void>();

  protected readonly positions = PICKER_POSITIONS;
  readonly pickerOpen = signal(false);
  /** The quiet line was tapped: show its editable pill row, in place. */
  readonly controlsOpen = signal(false);

  private readonly lookup = computed(() => this.topicMap() ?? topicLookup(this.topics()));

  /** Each name with its topic's id, when it links and the topic exists. */
  protected readonly chips = computed(() => {
    const lookup = this.linked() ? this.lookup() : null;
    return this.names().map((name) => ({ name, id: lookup?.get(name.trim().toLocaleLowerCase())?.id ?? null }));
  });

  /** A quiet line is only worth it for topics that exist; with none, the add
   *  pill is already the smallest thing a card can show. */
  protected readonly asQuietLine = computed(() =>
    this.quiet() && !this.controlsOpen() && this.names().length > 0);

  /** What the quiet line shows before `+N`: three linked names at most. */
  protected readonly lineChips = computed(() => this.chips().slice(0, QUIET_VISIBLE));

  protected readonly hiddenCount = computed(() =>
    Math.max(0, this.names().length - QUIET_VISIBLE));

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

  /** The quiet line becomes its controls: the names get crosses and + Topic. */
  protected openControls(): void {
    if (this.disabled() || this.controlsOpen()) return;
    this.controlsOpen.set(true);
  }

  protected closeControls(): void {
    this.closePicker();
    this.controlsOpen.set(false);
  }

  /**
   * A tap on the line opens its controls, except on a topic name: that is a
   * link to the topic and must filter, not unfold the card.
   */
  protected onLineClick(event: MouseEvent): void {
    const target = event.target as HTMLElement | null;
    if (target?.closest('a, button')) return;
    this.openControls();
  }

  protected choose(name: string): void {
    this.closePicker();
    this.added.emit(name);
  }
}
