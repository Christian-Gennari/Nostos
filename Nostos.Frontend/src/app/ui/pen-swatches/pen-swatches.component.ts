/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import {
  HIGHLIGHT_COLOURS,
  HighlightColour,
  highlightFillRef,
} from '../../reader/highlight-colours';

/**
 * The highlighter pens as a row of dots (issue #1203).
 *
 * One size and one active ring wherever a pen is picked: the reader's notes
 * panel, its selection bar and highlight card, and a note being edited. The
 * swatch is the label, so each dot carries its pen's name for assistive tech
 * and as a title, and `aria-pressed` says which one is chosen.
 *
 * The dot is painted 22px. On touch it answers a larger tap through an
 * invisible `::before`, narrower than it is tall so two neighbours on the
 * 34px pitch never overlap and send the tap to the wrong pen.
 */
@Component({
  selector: 'app-pen-swatches',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'pen-swatches',
    role: 'group',
    '[attr.aria-label]': 'label()',
  },
  template: `
    @for (pen of pens; track pen.id) {
      <button
        type="button"
        class="pen-swatch"
        [style.background-color]="fill(pen.id)"
        [class.is-active]="value() === pen.id"
        [attr.aria-pressed]="value() === pen.id"
        [attr.aria-label]="pen.label + ' highlighter'"
        [attr.title]="pen.label"
        [attr.data-testid]="testIdPrefix() ? testIdPrefix() + pen.id : null"
        [disabled]="disabled()"
        (click)="chosen.emit(pen.id)"
      ></button>
    }
  `,
  styles: [
    `
      :host {
        display: flex;
        flex: none;
        align-items: center;
        gap: 12px;
      }

      .pen-swatch {
        position: relative;
        width: 22px;
        height: 22px;
        padding: 0;
        border: 1px solid var(--border-color);
        border-radius: var(--radius-round);
        cursor: pointer;
      }

      .pen-swatch::before {
        content: '';
        position: absolute;
        inset: -11px -6px;
      }

      .pen-swatch.is-active {
        outline: 2px solid var(--color-text-main);
        outline-offset: 2px;
      }

      .pen-swatch:focus-visible {
        outline: var(--focus-ring-width) solid var(--focus-ring);
        outline-offset: 2px;
      }

      .pen-swatch:disabled {
        cursor: progress;
        opacity: 0.6;
      }
    `,
  ],
})
export class PenSwatchesComponent {
  /** The chosen pen; null when none is (no ring is drawn). */
  readonly value = input<HighlightColour | null>(null);
  readonly disabled = input(false);
  readonly label = input('Highlight colour');
  /** Gives each dot `data-testid="<prefix><pen>"` for end-to-end tests. */
  readonly testIdPrefix = input<string | null>(null);

  readonly chosen = output<HighlightColour>();

  readonly pens = HIGHLIGHT_COLOURS;
  readonly fill = highlightFillRef;
}
