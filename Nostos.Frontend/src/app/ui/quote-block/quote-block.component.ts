/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { HighlightColour, highlightFillRef } from '../../reader/highlight-colours';

/** How large the quotation reads: a row or panel, a note, a page of its own. */
export type QuoteBlockSize = 'sm' | 'md' | 'lg';

/**
 * A quotation from a book, drawn one way everywhere (issue #1203).
 *
 * The serif words beside a square rail in the pen's colour, and no added
 * quotation marks: the rail says it is a quote. When the pen is not known the
 * rail stays the neutral hairline colour rather than guessing one.
 *
 * It goes on the element that holds the words, so a `<blockquote>` in a note
 * and a `<span>` inside a row's button (where a blockquote may not go) look
 * the same. `lines` clamps the words to that many lines.
 */
@Component({
  selector: '[appQuote]',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<ng-content />',
  host: {
    class: 'nostos-quote nostos-accent-rail',
    '[class.is-sm]': "size() === 'sm'",
    '[class.is-lg]': "size() === 'lg'",
    '[class.is-clamped]': 'lines() !== null',
    '[style.--nostos-accent-rail-color]': 'rail()',
    '[style.--nostos-quote-lines]': 'lines()',
  },
  styles: [
    `
      :host {
        display: block;
        margin: 0;
        padding: 0.1rem 0 0.1rem 1.15rem;
        --nostos-accent-rail-width: 2px;
        --nostos-quote-lines: 3;
        --nostos-accent-rail-color: var(--border-color);
        color: var(--nostos-quote-ink, var(--color-text-main));
        font-family: 'Newsreader', Georgia, serif;
        font-size: 1.16rem;
        font-style: normal;
        line-height: 1.6;
        overflow-wrap: anywhere;
        text-align: left;
        text-wrap: pretty;
        white-space: pre-line;
      }

      :host(.is-sm) {
        padding-left: 0.75rem;
        font-size: 0.98rem;
        line-height: 1.45;
      }

      :host(.is-lg) {
        padding-left: 1.4rem;
        font-size: clamp(1.3rem, 2.4vw, 1.75rem);
        line-height: 1.45;
      }

      :host(.is-clamped) {
        display: -webkit-box;
        overflow: hidden;
        -webkit-box-orient: vertical;
        -webkit-line-clamp: var(--nostos-quote-lines);
        line-clamp: var(--nostos-quote-lines);
      }
    `,
  ],
})
export class QuoteBlockComponent {
  /** The highlighter pen the words were marked with; null keeps the neutral rail. */
  readonly pen = input<HighlightColour | null | undefined>(null);
  readonly size = input<QuoteBlockSize>('md');
  /** Clamp to this many lines; null shows them all. */
  readonly lines = input<number | null>(null);

  readonly rail = computed(() => {
    const pen = this.pen();
    return pen ? highlightFillRef(pen) : null;
  });
}
