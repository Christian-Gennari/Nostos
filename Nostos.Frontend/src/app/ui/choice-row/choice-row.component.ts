/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, ViewEncapsulation, input } from '@angular/core';

/**
 * A bordered row that stands for one object you can pick or act on: an edition
 * of a book, a book to link.
 *
 * The same row is a `<button>` when picking it is the action, a `<div>` or
 * `<li>` when it only shows state or carries its own button, so the component
 * is an attribute on whichever element the call site needs:
 *
 *   <button appChoiceRow type="button" (click)="open(edition)">
 *     <span appIconTile tone="quiet"><nostos-icon name="book-open" [size]="16" /></span>
 *     <span class="nostos-choice-row__text">
 *       <span class="nostos-choice-row__title">EPUB</span>
 *       <span class="nostos-choice-row__sub">Penguin, 2004</span>
 *       <span class="nostos-choice-row__meta">42% read</span>
 *     </span>
 *   </button>
 *
 * It owns the box, the three text tiers, the hover, focus and disabled states
 * of a button row, and the selected state, which uses the app's selection
 * role (the fill the Library sidebar and the Brain's index use for "this one")
 * and re-inks the text tiers and any icon tile inside it.
 *
 * WHY THIS EXISTS: an edition is one object shown on the book page, in the
 * Editions dialog and in the Switch edition dialog, and each stylesheet had
 * its own copy of this row, with comments saying it mirrored the others.
 */
@Component({
  selector: '[appChoiceRow]',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The text tiers and the icon tile are projected content.
  encapsulation: ViewEncapsulation.None,
  template: '<ng-content />',
  host: {
    class: 'nostos-choice-row',
    '[class.nostos-choice-row--selected]': 'selected()',
  },
  styleUrl: './choice-row.component.css',
})
export class ChoiceRowComponent {
  /** This row is the current one: the edition being viewed. */
  readonly selected = input(false);
}
