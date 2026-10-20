/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import {
  booleanAttribute,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  input,
  computed,
  output,
  viewChildren,
} from '@angular/core';

export type SegmentedValue = string | number;

/** One choice. The label is visible text, so it is also the accessible name. */
export interface SegmentedOption<T extends SegmentedValue = SegmentedValue> {
  readonly value: T;
  readonly label: string;
}

/**
 * A single-choice setting drawn as a segmented track: the reader's typeface,
 * line height and margins, the PDF reading mode, the add-book source filter.
 *
 * WHY A RADIO GROUP, AND WHY THIS IS NOT nostos-view-toggle
 * ---------------------------------------------------------
 * The view toggle is icon-only and switches how a page PRESENTS data, so it is a
 * group of pressed buttons. These controls pick ONE VALUE of a setting from a
 * small labelled set, which is what a radio group is: role="radiogroup",
 * role="radio" + aria-checked, one tab stop with roving focus, and arrow keys
 * that move and select. One semantics, fixed — the component deliberately has
 * no role input (that leaky abstraction is what styles.css warns about).
 * Tabs that change a page's structure keep role="tablist" and the .toggle-opt
 * recipe; this component paints the same track and thumb so the two read as one
 * family.
 *
 * Before this existed the reader drew these as bordered buttons (.typo-opt) with
 * a grey fill and a dark outline for the selected one, and the add-book modal
 * drew its filter as pills with a third selected style — three looks for one
 * kind of choice.
 *
 * The host is the track and keeps whatever classes and aria-labelledby the call
 * site writes, so a surface can label it with its own visible heading.
 */
@Component({
  selector: 'nostos-segmented',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (option of options(); track option.value; let i = $index) {
      <button
        #opt
        type="button"
        role="radio"
        class="seg-opt"
        [attr.aria-checked]="option.value === value() ? 'true' : 'false'"
        [attr.tabindex]="i === selectedIndex() ? 0 : -1"
        [disabled]="disabled()"
        (click)="select(option.value)"
        (keydown)="onKeydown($event, i)"
      >{{ option.label }}</button>
    }
  `,
  host: {
    role: 'radiogroup',
    '[class.seg--fill]': 'fill()',
    '[attr.aria-disabled]': "disabled() ? 'true' : null",
  },
  styles: [
    `
      /* The track: the same ground, radius, padding and hairline ring as the
         view toggle and the .toggle-opt tabs, so every segmented choice in the
         app sits on one recipe. */
      :host {
        box-sizing: border-box;
        display: inline-flex;
        align-items: stretch;
        gap: 2px;
        padding: 3px;
        max-width: 100%;
        background: var(--bg-hover);
        border-radius: var(--radius-segment);
        box-shadow: inset 0 0 0 1px var(--border-color);
      }

      /* fill: the track spans its container and the options share the spare
         room. Grown from their content (flex-basis auto), not split equally: in
         the reader's 260px panel an equal split clipped "Publisher" while
         "Sans" had room to spare. */
      :host(.seg--fill) {
        display: flex;
        width: 100%;
      }
      :host(.seg--fill) .seg-opt {
        flex: 1 1 auto;
      }

      .seg-opt {
        min-width: 0;
        min-height: 26px;
        padding: 0 8px;
        border: none;
        border-radius: var(--radius-segment-option);
        background: none;
        color: var(--color-text-light);
        font-family: inherit;
        font-size: 0.8rem;
        font-weight: var(--fw-medium);
        line-height: 1.2;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
        cursor: pointer;
        touch-action: manipulation;
        transition:
          color var(--motion-fast) var(--ease-plain),
          background-color var(--motion-fast) var(--ease-plain),
          box-shadow var(--motion-fast) var(--ease-plain);
      }

      .seg-opt:hover:not([aria-checked='true']):not(:disabled) {
        background: color-mix(in srgb, var(--color-text-main) 8%, transparent);
        color: var(--color-text-main);
      }

      /* The raised thumb — identical to the view toggle's. */
      .seg-opt[aria-checked='true'] {
        background: var(--control-active-fill);
        color: var(--control-active-ink);
        outline: 1px solid var(--border-color);
        outline-offset: -1px;
        box-shadow: var(--shadow-sm), var(--control-thumb-highlight);
      }

      .seg-opt:focus-visible {
        outline: var(--focus-ring-width) solid var(--focus-ring);
        outline-offset: -2px;
      }

      .seg-opt:disabled {
        cursor: not-allowed;
        opacity: 0.5;
      }

      /* On touch the TRACK is the 44px rung: each option fills it less the
         3px inset. Growing the options themselves to 44px made every row
         6px taller and pushed the reader's phone panel past its budget. */
      @media (pointer: coarse), (hover: none) {
        .seg-opt {
          min-height: calc(var(--control-h-touch) - 6px);
        }
      }
    `,
  ],
})
export class SegmentedComponent<T extends SegmentedValue = SegmentedValue> {
  readonly options = input.required<readonly SegmentedOption<T>[]>();

  /**
   * The selected value. Controlled: the component only emits, and the surface
   * re-binds, so the persisted setting stays the single source of truth.
   * `null`/no match leaves every option unchecked (a PDF zoom that is not one of
   * the presets is a real state), and the first option keeps the tab stop.
   */
  readonly value = input.required<T | null | undefined>();

  /** Stretch the track to its container and share it equally. */
  readonly fill = input(false, { transform: booleanAttribute });

  readonly disabled = input(false, { transform: booleanAttribute });

  readonly valueChange = output<T>();

  private readonly buttons = viewChildren<ElementRef<HTMLButtonElement>>('opt');

  protected readonly selectedIndex = computed(() => {
    const index = this.options().findIndex((option) => option.value === this.value());
    return index < 0 ? 0 : index;
  });

  protected select(value: T): void {
    if (value === this.value()) return;
    this.valueChange.emit(value);
  }

  /** Radio-group keyboard contract: arrows move AND select; Home/End jump. */
  protected onKeydown(event: KeyboardEvent, index: number): void {
    const options = this.options();
    if (options.length === 0) return;

    let next: number;
    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        next = (index + 1) % options.length;
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        next = (index - 1 + options.length) % options.length;
        break;
      case 'Home':
        next = 0;
        break;
      case 'End':
        next = options.length - 1;
        break;
      default:
        return;
    }

    event.preventDefault();
    this.select(options[next].value);
    this.buttons()[next]?.nativeElement.focus();
  }
}
