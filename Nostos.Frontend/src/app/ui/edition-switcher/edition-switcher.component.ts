/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { NOSTOS_CONCEPTS, type NostosConcept } from '../icon/nostos-concepts';
import { MenuComponent, MenuItemComponent, MenuTriggerDirective } from '../menu/menu.component';
import { ProgressComponent } from '../progress/progress.component';

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
  /** Every detail in words, for the tooltip and the overflow menu's hint. */
  readonly description: string;
}

/**
 * The editions of one work, as navigation: each option opens another book.
 *
 * WHY NOT `nostos-segmented`. That control sets a value, and its arrow keys
 * select as they move, which here would open a different book on every key
 * press. It also looks like a setting. This is a `<nav>` of buttons: Tab moves
 * through the editions and Enter opens one. The edition being shown is not a
 * button; it carries `aria-current="page"` and a check mark, so its state never
 * rests on the border or fill alone.
 *
 * Every option, current included, says where the reader stands in it: a short
 * status line ("37%", "Finished", "Not started") over a thin progress bar.
 *
 * MANY EDITIONS. Up to `maxInline` options sit in one row. Past that the row
 * would wrap into a crowded block on a phone, so the switcher collapses to the
 * current edition plus a "N more" menu listing the rest, each with its status
 * as the item's hint. The row never wraps.
 *
 * Options are drawn in the order given; the caller keeps that order stable
 * across a switch so the options hold still.
 */
@Component({
  selector: 'nostos-edition-switcher',
  standalone: true,
  imports: [NgTemplateOutlet, NostosIconComponent, ProgressComponent, MenuComponent, MenuItemComponent, MenuTriggerDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="es" [attr.aria-label]="label()">
      @for (option of inline(); track option.id) {
        @if (option.current) {
          <span class="es-option es-option--current" aria-current="page" [title]="option.description">
            <ng-container *ngTemplateOutlet="body; context: { $implicit: option }" />
          </span>
        } @else {
          <button type="button" class="es-option" [title]="'Switch to ' + option.description" (click)="switched.emit(option.id)">
            <ng-container *ngTemplateOutlet="body; context: { $implicit: option }" />
          </button>
        }
      }

      @if (overflow().length > 0) {
        <button
          type="button"
          class="es-more"
          [attr.aria-label]="overflow().length + ' more editions'"
          [nostosMenuTriggerFor]="more"
        >
          <nostos-icon name="stack" [size]="14" aria-hidden="true" />
          <span>{{ overflow().length }} more</span>
          <nostos-icon name="caret-down" [size]="12" aria-hidden="true" />
        </button>
      }
    </nav>

    <ng-template #more>
      <nostos-menu>
        @for (option of overflow(); track option.id) {
          <button nostosMenuItem [icon]="glyph(option)" [hint]="hint(option)" (triggered)="switched.emit(option.id)">
            {{ option.format }}{{ option.detail ? ' · ' + option.detail : '' }}
          </button>
        }
      </nostos-menu>
    </ng-template>

    <ng-template #body let-option>
      <span class="es-head">
        <nostos-icon [concept]="option.concept" [size]="14" aria-hidden="true" />
        <span class="es-format">{{ option.format }}</span>
        @if (option.current) {
          <nostos-icon class="es-check" name="check" [size]="12" aria-hidden="true" />
        }
      </span>
      <span class="es-meta">
        @if (option.detail) {
          <span class="es-detail">{{ option.detail }}</span>
          <span class="es-dot" aria-hidden="true">·</span>
        }
        @if (option.finished) {
          <span class="es-status es-status--finished">Finished</span>
        } @else if (option.progress > 0) {
          <span class="es-status">{{ option.progress }}%<span class="visually-hidden"> read</span></span>
        } @else {
          <span class="es-status es-status--idle">Not started</span>
        }
      </span>
      <nostos-progress class="es-bar" decorative [value]="option.finished ? 100 : option.progress" />
    </ng-template>
  `,
  styles: [
    `
      :host {
        display: block;
        max-width: 100%;
        min-width: 0;
      }

      .es {
        display: flex;
        flex-wrap: nowrap;
        gap: 6px;
        min-width: 0;
      }

      .es-option,
      .es-more {
        box-sizing: border-box;
        display: flex;
        flex-direction: column;
        justify-content: center;
        gap: 2px;
        min-width: 0;
        min-height: var(--control-h-touch);
        padding: 6px 10px 7px;
        border: 1px solid var(--border-color);
        border-radius: var(--radius-action);
        background: var(--bg-surface);
        color: var(--color-text-main);
        font-family: inherit;
        text-align: start;
        touch-action: manipulation;
        transition:
          background-color var(--motion-fast) var(--ease-plain),
          border-color var(--motion-fast) var(--ease-plain);
      }
      .es-option {
        flex: 0 1 auto;
        max-width: 15rem;
      }

      button.es-option,
      .es-more {
        cursor: pointer;
      }
      button.es-option:hover,
      .es-more:hover {
        background: var(--bg-hover);
      }
      button.es-option:focus-visible,
      .es-more:focus-visible {
        outline: var(--focus-ring-width) solid var(--focus-ring);
        outline-offset: 2px;
      }

      /* The edition on screen: raised and outlined in ink, plus the check. */
      .es-option--current {
        border-color: var(--color-text-main);
        background: var(--control-active-fill);
        box-shadow: var(--shadow-sm), var(--control-thumb-highlight);
      }

      .es-head,
      .es-meta {
        display: flex;
        align-items: center;
        min-width: 0;
        white-space: nowrap;
      }
      .es-head {
        gap: 0.35rem;
        font-size: 0.82rem;
        font-weight: var(--fw-medium);
        line-height: 1.2;
      }
      .es-head nostos-icon {
        flex: none;
      }
      .es-format {
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .es-check {
        margin-inline-start: auto;
        padding-inline-start: 0.35rem;
      }

      /* Muted, not light: this line is information, and has to read as such. */
      .es-meta {
        gap: 0.3rem;
        color: var(--color-text-muted);
        font-size: 0.74rem;
        font-weight: var(--fw-regular);
        line-height: 1.25;
      }
      .es-detail {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .es-dot,
      .es-status {
        flex: none;
      }
      .es-status {
        font-variant-numeric: tabular-nums;
      }
      .es-status--finished {
        color: var(--color-success-dark);
        font-weight: var(--fw-medium);
      }

      .es-bar {
        margin-top: 4px;
      }

      .es-more {
        flex: none;
        flex-direction: row;
        align-items: center;
        gap: 0.35rem;
        color: var(--color-text-muted);
        font-size: 0.8rem;
        font-weight: var(--fw-medium);
        white-space: nowrap;
      }

      /* On a phone the row takes the full width and shares it evenly. */
      @media (max-width: 768px) {
        .es-option {
          flex: 1 1 0;
          max-width: none;
        }
      }
    `,
  ],
})
export class EditionSwitcherComponent {
  readonly options = input.required<readonly EditionSwitcherOption[]>();
  /** Most options shown in the row before the rest move into a menu. */
  readonly maxInline = input(3);
  readonly label = input('Editions');

  /** The id of the edition to open. Never the current one. */
  readonly switched = output<string>();

  private readonly collapsed = computed(() => this.options().length > this.maxInline());

  readonly inline = computed(() =>
    this.collapsed() ? this.options().filter((option) => option.current) : this.options(),
  );

  readonly overflow = computed(() =>
    this.collapsed() ? this.options().filter((option) => !option.current) : [],
  );

  glyph(option: EditionSwitcherOption) {
    return NOSTOS_CONCEPTS[option.concept];
  }

  hint(option: EditionSwitcherOption): string {
    return option.finished ? 'Finished' : option.progress > 0 ? `${option.progress}% read` : 'Not started';
  }
}
