/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { RouterLink } from '@angular/router';

import { NostosIconComponent } from '../icon/nostos-icon.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import type { NostosConcept } from '../icon/nostos-concepts';

/**
 * A topic on something (issue #1204): a quiet pill with the topic's mark and
 * name. It opens the topic when given a `link` (or reports `activated` when it
 * is `actionable`, for a surface that must save first), and offers a cross when
 * `removable`. The same pill is a note's topics, Brain Sort's chosen topics,
 * a cited source's topics in the Studio and, with no mark, a book's
 * collections.
 *
 * On touch the name and the cross keep their look and grow an invisible 44px
 * hit area; the cross reaches right, into the gap, never over the name.
 */
@Component({
  selector: 'app-topic-chip',
  standalone: true,
  imports: [NgTemplateOutlet, RouterLink, NostosIconComponent, IconButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'topic-chip', '[class.is-removable]': 'removable()' },
  template: `
    <ng-template #label>
      @if (icon(); as concept) {
        <nostos-icon [concept]="concept" [size]="12" />
      }
      <span class="topic-chip-text">{{ name() }}</span>
    </ng-template>

    @if (link(); as target) {
      <a class="topic-chip-name is-action" [routerLink]="target" [attr.title]="title()">
        <ng-container [ngTemplateOutlet]="label" />
      </a>
    } @else if (actionable()) {
      <button type="button" class="topic-chip-name is-action" [attr.title]="title()" (click)="activated.emit()">
        <ng-container [ngTemplateOutlet]="label" />
      </button>
    } @else {
      <span class="topic-chip-name"><ng-container [ngTemplateOutlet]="label" /></span>
    }

    @if (removable()) {
      <button
        appIconButton
        size="xxs"
        type="button"
        icon="x"
        class="topic-chip-remove"
        [glyphSize]="11"
        [disabled]="disabled()"
        [attr.aria-label]="removeLabel() ?? 'Remove topic ' + name()"
        [title]="removeLabel() ?? 'Remove ' + name()"
        (click)="removed.emit()"
      ></button>
    }
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        max-width: 100%;
        border: 1px solid var(--border-color);
        border-radius: var(--radius-pill);
        background: var(--bg-surface-alt);
        vertical-align: middle;
      }

      .topic-chip-name {
        display: inline-flex;
        align-items: center;
        gap: 0.3rem;
        min-width: 0;
        overflow: hidden;
        padding: 0.18rem 0.6rem;
        border: 0;
        border-radius: var(--radius-pill);
        background: none;
        color: var(--color-text-muted);
        font: inherit;
        font-size: 0.8rem;
        line-height: 1.35;
        text-decoration: none;
        white-space: nowrap;
      }

      :host(.is-removable) .topic-chip-name {
        padding-right: 0.15rem;
      }

      .topic-chip-text {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
      }

      .topic-chip-name nostos-icon {
        flex: none;
        color: var(--color-text-light);
      }

      .topic-chip-name.is-action {
        cursor: pointer;
      }

      .topic-chip-name.is-action:hover {
        color: var(--color-text-main);
      }

      .topic-chip-name.is-action:focus-visible {
        outline: var(--focus-ring-width) solid var(--focus-ring);
        outline-offset: 2px;
      }

      .topic-chip-remove {
        margin-right: 0.2rem;
        color: var(--color-text-light);
      }

      @media (pointer: coarse), (hover: none) {
        .topic-chip-name,
        .topic-chip-remove {
          position: relative;
        }

        .topic-chip-name::after,
        .topic-chip-remove::after {
          content: '';
          position: absolute;
          inset-block: calc(50% - 22px);
          inset-inline: 0;
        }

        .topic-chip-remove::after {
          inset-inline: 0 calc(100% - 44px);
        }
      }
    `,
  ],
})
export class TopicChipComponent {
  readonly name = input.required<string>();
  /** Where the chip opens the topic, as a router link. */
  readonly link = input<readonly unknown[] | string | null>(null);
  /** Without a link: the name is a button that reports `activated`. */
  readonly actionable = input(false);
  readonly removable = input(false);
  readonly disabled = input(false);
  /** The mark before the name; null for a pill with none (a collection). */
  readonly icon = input<NostosConcept | null>('topic');
  /** The cross's name, when "Remove topic <name>" is not what it does. */
  readonly removeLabel = input<string | null>(null);
  readonly title = input<string | null>(null);

  readonly removed = output<void>();
  readonly activated = output<void>();
}
