/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HIGHLIGHT_COLOURS,
  HighlightColour,
  highlightFillRef,
} from '../../reader/highlight-colours';
import { ButtonComponent } from '../button/button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { NOSTOS_MENU } from '../menu/menu.component';

/**
 * Compact dropdown trigger for choosing a highlighter pen on touch surfaces
 * where an inline row of swatches would consume a whole row (#1722).
 *
 * Keeps the 44px touch floor (`--control-h-touch`), displays the active pen
 * colour, announces both role and active pen ("Highlight colour: Sage"), and
 * opens the shared `nostos-menu` (rendered as a bottom sheet at phone widths)
 * with radio-checked colour choices.
 */
@Component({
  selector: 'app-pen-picker',
  standalone: true,
  imports: [ButtonComponent, NostosIconComponent, NOSTOS_MENU],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      appButton
      type="button"
      variant="secondary"
      class="pen-picker-trigger"
      [nostosMenuTriggerFor]="penMenu"
      [disabled]="disabled()"
      [attr.aria-label]="ariaLabel()"
      [attr.title]="ariaLabel()"
      [attr.data-testid]="triggerTestId()"
    >
      <span
        class="pen-swatch pen-picker-swatch"
        [style.background-color]="fill(value())"
        aria-hidden="true"
      ></span>
      <nostos-icon name="caret-down" [size]="12" class="pen-picker-caret" />
    </button>
    <ng-template #penMenu>
      <nostos-menu [attr.aria-label]="label()">
        <div nostosMenuGroup>
          @for (pen of pens; track pen.id) {
            <button
              nostosMenuItemRadio
              [checked]="value() === pen.id"
              [attr.data-testid]="testIdPrefix() ? testIdPrefix() + pen.id : null"
              (triggered)="chosen.emit(pen.id)"
            >
              <span class="pen-picker-option">
                <span
                  class="pen-swatch pen-picker-option-dot"
                  [style.background-color]="fill(pen.id)"
                  aria-hidden="true"
                ></span>
                <span>{{ pen.label }}</span>
              </span>
            </button>
          }
        </div>
      </nostos-menu>
    </ng-template>
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        flex: none;
      }

      .pen-picker-trigger {
        min-width: var(--control-h-touch);
        min-height: var(--control-h-touch);
        height: var(--control-h-touch);
        padding: 0 8px;
        gap: 5px;
      }

      .pen-picker-swatch {
        width: 20px;
        height: 20px;
        border: 1px solid var(--border-color);
        border-radius: var(--radius-round);
        flex-shrink: 0;
      }

      .pen-picker-caret {
        color: var(--color-text-muted);
      }

      .pen-picker-option {
        display: inline-flex;
        align-items: center;
        gap: 8px;
      }

      .pen-picker-option-dot {
        width: 16px;
        height: 16px;
        border: 1px solid var(--border-color);
        border-radius: var(--radius-round);
        flex-shrink: 0;
      }
    `,
  ],
})
export class PenPickerComponent {
  readonly value = input<HighlightColour>(DEFAULT_HIGHLIGHT_COLOUR);
  readonly disabled = input(false);
  readonly label = input('Highlight colour');
  /** Gives the trigger its `data-testid` when mounted in a surface. */
  readonly triggerTestId = input<string | null>(null);
  /** Gives each menu option `data-testid="<prefix><pen>"` for end-to-end tests. */
  readonly testIdPrefix = input<string | null>(null);

  readonly chosen = output<HighlightColour>();

  readonly pens = HIGHLIGHT_COLOURS;
  readonly fill = highlightFillRef;

  readonly activeOption = computed(
    () => HIGHLIGHT_COLOURS.find((option) => option.id === this.value()) ?? HIGHLIGHT_COLOURS[0],
  );
  readonly ariaLabel = computed(() => `${this.label()}: ${this.activeOption().label}`);
}
