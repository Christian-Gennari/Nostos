/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, input } from '@angular/core';

import { NostosIconComponent } from '../icon/nostos-icon.component';

/** What a row offers: an existing topic, a new one by the typed name, or an AI suggestion. */
export type TopicOptionKind = 'topic' | 'create' | 'suggestion';

/** The one thing a topic list says when there are no topics to choose from. */
export const NO_TOPICS = 'No topics yet.';

/** The same, where typing a name can create one. */
export const NO_TOPICS_YET = `${NO_TOPICS} Type a name to create one.`;

/** The one thing a topic list says when a search matches none. */
export const NO_TOPICS_MATCH = 'No topics match.';

/**
 * One row in a list of topics (issue #1204): the topic's mark, its name and,
 * when known, how many notes use it as a plain muted number. A `create` row
 * reads Create “name” after a plus. It is the same row in the note's topic
 * picker, the `[[` suggestions and Link a topic dialog, Brain Sort and the
 * merge picker.
 *
 * It goes on the element the list needs (an `<li role="option">`, a
 * `<button>`), which keeps its own role, id, selection state and events.
 */
@Component({
  selector: '[appTopicOption]',
  standalone: true,
  imports: [NostosIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'topic-option',
    '[class.is-active]': 'active()',
    '[class.is-selected]': 'selected()',
    '[class.is-suggestion]': "kind() === 'suggestion'",
    '[class.is-create]': "kind() === 'create'",
  },
  template: `
    @if (kind() === 'create') {
      <nostos-icon name="plus" [size]="14" />
      <span class="topic-option-name">Create “{{ name() }}”</span>
    } @else {
      <nostos-icon concept="topic" [size]="14" />
      <span class="topic-option-name">{{ name() }}</span>
      @if (count() !== null && count() !== undefined) {
        <span class="topic-option-count">{{ count() }}</span>
      }
      @if (selected()) {
        <nostos-icon class="topic-option-check" name="check" [size]="14" />
      }
    }
  `,
  styles: [
    `
      :host {
        display: flex;
        align-items: center;
        gap: 0.55rem;
        box-sizing: border-box;
        width: 100%;
        min-height: 2.25rem;
        padding: 0 0.6rem;
        border: 0;
        border-radius: var(--radius-popover-item);
        background: transparent;
        color: var(--color-text-main);
        font: inherit;
        font-size: var(--text-sm);
        text-align: left;
        cursor: pointer;
        scroll-margin-block: 0.375rem;
      }

      :host(.is-active) {
        background: var(--bg-hover);
      }

      :host(:focus-visible) {
        outline: var(--focus-ring-width) solid var(--focus-ring);
        outline-offset: -2px;
      }

      /* The chosen row (the merge picker): a check and a firmer name, so it is
         not mistaken for the row the pointer is on. */
      :host(.is-selected) {
        background: var(--bg-hover);
      }

      :host(.is-selected) .topic-option-name {
        font-weight: var(--fw-medium);
      }

      .topic-option-check {
        color: var(--color-text-main);
      }

      :host(.is-suggestion) {
        color: var(--color-text-muted);
      }

      nostos-icon {
        flex: none;
        color: var(--color-text-light);
      }

      .topic-option-name {
        flex: 1 1 auto;
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .topic-option-count {
        flex: none;
        margin-left: auto;
        color: var(--color-text-light);
        font-size: var(--text-xs);
        font-variant-numeric: tabular-nums;
      }

      @media (pointer: coarse), (hover: none) {
        :host {
          min-height: var(--control-h-touch);
        }
      }
    `,
  ],
})
export class TopicOptionComponent {
  readonly name = input.required<string>();
  readonly kind = input<TopicOptionKind>('topic');
  /** Notes that use the topic; null or absent shows no number. */
  readonly count = input<number | null | undefined>(null);
  readonly active = input(false);
  /** The chosen row of a single-choice list: marked with a check, not just tinted. */
  readonly selected = input(false);
}
