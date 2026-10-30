/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { READING_STATUS_LABEL } from '../../core/reading-status';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { NOSTOS_CONCEPTS, type NostosConcept } from '../icon/nostos-concepts';
import type { NostosIconName } from '../icon/nostos-icons';
import { NOSTOS_MENU } from '../menu/menu.component';

/** One edition of a work, as the switcher shows it. */
export interface EditionSwitcherOption {
  readonly id: string;
  /** Short format name: "Audiobook", "EPUB", "PDF", "Physical". */
  readonly format: string;
  /** The format's glyph. */
  readonly concept: NostosConcept;
  /** What tells this edition apart from another of the same format, or null. */
  readonly detail: string | null;
  /** The edition this page is showing. */
  readonly current: boolean;
  /** 0–100. */
  readonly progress: number;
  readonly finished: boolean;
  /** Every detail in words, for the tooltip. */
  readonly description: string;
  /** This edition's own rating, 0 when none. */
  readonly rating?: number;
  /** What that rating is about: "narration" or "this edition". */
  readonly ratingNoun?: string;
}

/** The menu's last row: rate this edition, or clear its rating. */
export interface EditionRatingAction {
  /** "Rate the narration", "Clear edition rating". */
  readonly label: string;
}

/**
 * The editions of one work: a quiet pill naming the edition on screen, which
 * opens a menu of every edition with where the reader stands in each.
 *
 * WHY A MENU, NOT A ROW OF OPTIONS. Switching opens a different book, so this
 * is navigation, not a setting, and it should not look like a segmented
 * control. A row also grows with the work: past two or three editions it
 * wrapped into a crowded block on a phone. One pill stays the size of its
 * label however many editions there are, and sits beside the reading-status
 * pill it resembles.
 *
 * The menu is the shared `nostos-menu`, so keyboard, focus return and Escape
 * are the CDK's. Each item is an edition: its format glyph, its name and, as
 * the hint, "37% read", "Finished" or "Not started". The edition on screen is
 * listed too, with a check in place of its glyph and `aria-current="page"`, so
 * the list is complete and its state never rests on colour.
 *
 * Items are drawn in the order given; the caller keeps that order stable
 * across a switch so the list holds still.
 *
 * An edition can carry a rating of its own (the narration of an audiobook), an
 * exception to the book's. It shows in the hint ("34% read · narration ★★"),
 * and `action` adds the menu's last row, after a separator, to rate or clear it.
 * The row's words come from the caller; this only draws it and says it was chosen.
 */
@Component({
  selector: 'nostos-edition-switcher',
  standalone: true,
  imports: [NostosIconComponent, ...NOSTOS_MENU],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (current(); as current) {
      <nav class="es" [attr.aria-label]="label()">
        <button
          type="button"
          class="es-trigger"
          [attr.aria-label]="'Edition: ' + name(current) + '. ' + label()"
          [title]="current.description"
          [nostosMenuTriggerFor]="editions"
        >
          <nostos-icon [concept]="current.concept" [size]="14" aria-hidden="true" />
          <span class="es-value">{{ name(current) }}</span>
          <nostos-icon class="es-chevron" name="caret-down" [size]="12" aria-hidden="true" />
        </button>
      </nav>
    }

    <ng-template #editions>
      <nostos-menu [attr.aria-label]="label()">
        @for (option of options(); track option.id) {
          <button
            nostosMenuItem
            [icon]="glyph(option)"
            [hint]="hint(option)"
            [attr.aria-label]="spoken(option)"
            [attr.aria-current]="option.current ? 'page' : null"
            [title]="option.description"
            (triggered)="open(option)"
          >{{ name(option) }}</button>
        }
        @if (action(); as act) {
          <hr nostosMenuSeparator />
          <button nostosMenuItem icon="star" (triggered)="actionTriggered.emit()">{{ act.label }}</button>
        }
      </nostos-menu>
    </ng-template>
  `,
  styles: [
    `
      :host {
        display: inline-block;
        min-width: 0;
        max-width: 100%;
      }

      .es {
        display: contents;
      }

      /* Drawn as the reading-status pill's neutral sibling: same capsule,
         padding and type, with the format glyph where that pill has its dot. */
      .es-trigger {
        display: inline-flex;
        align-items: center;
        gap: 0.4rem;
        max-width: 100%;
        min-width: 0;
        padding: 5px 10px 5px 9px;
        border: 1px solid transparent;
        border-radius: var(--radius-pill);
        background: var(--bg-hover);
        color: var(--color-text-muted);
        font-family: inherit;
        font-size: 0.8rem;
        font-weight: var(--fw-medium);
        line-height: 1.2;
        cursor: pointer;
        touch-action: manipulation;
        transition:
          background-color var(--motion-fast) var(--ease-plain),
          border-color var(--motion-fast) var(--ease-plain);
      }
      .es-trigger:hover,
      .es-trigger[aria-expanded='true'] {
        border-color: color-mix(in srgb, var(--color-primary) 20%, transparent);
        background: color-mix(in srgb, var(--color-primary) 8%, var(--bg-hover));
      }
      .es-trigger:focus-visible {
        outline: var(--focus-ring-width) solid var(--focus-ring);
        outline-offset: 2px;
      }
      .es-trigger nostos-icon {
        flex: none;
      }
      .es-value {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .es-chevron {
        opacity: 0.7;
      }
    `,
  ],
})
export class EditionSwitcherComponent {
  readonly options = input.required<readonly EditionSwitcherOption[]>();
  readonly label = input('Switch edition');
  /** The rating row after the editions, or none (the default). */
  readonly action = input<EditionRatingAction | null>(null);

  /** The id of the edition to open. Never the current one. */
  readonly switched = output<string>();
  /** The rating row was chosen. */
  readonly actionTriggered = output<void>();

  readonly current = computed(() => this.options().find((option) => option.current) ?? null);

  name(option: EditionSwitcherOption): string {
    return option.detail ? `${option.format} · ${option.detail}` : option.format;
  }

  glyph(option: EditionSwitcherOption): NostosIconName {
    return option.current ? 'check' : NOSTOS_CONCEPTS[option.concept];
  }

  status(option: EditionSwitcherOption): string {
    return option.finished
      ? READING_STATUS_LABEL.finished
      : option.progress > 0
        ? `${option.progress}% read`
        : READING_STATUS_LABEL.notstarted;
  }

  /** The menu hint: where the reader stands, then the edition's own rating in stars. */
  hint(option: EditionSwitcherOption): string {
    const rating = option.rating ?? 0;
    return rating > 0
      ? `${this.status(option)} · ${option.ratingNoun ?? 'rating'} ${'★'.repeat(rating)}`
      : this.status(option);
  }

  /**
   * The item's name for a screen reader when it has a rating: the same words
   * with the stars said as a number. Null otherwise, so the text names it.
   */
  spoken(option: EditionSwitcherOption): string | null {
    const rating = option.rating ?? 0;
    if (rating <= 0) return null;
    return `${this.name(option)}, ${this.status(option).toLowerCase()}, ${option.ratingNoun ?? 'rating'} rated ${rating} out of 5`;
  }

  open(option: EditionSwitcherOption): void {
    if (!option.current) this.switched.emit(option.id);
  }
}
