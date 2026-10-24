/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { booleanAttribute, ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { ButtonComponent } from '../button/button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import type { NostosConcept } from '../icon/nostos-concepts';
import type { NostosIconName } from '../icon/nostos-icons';

/** The one label a failed load offers, everywhere (issue #1202). */
export const TRY_AGAIN = 'Try again';

/**
 * What a page or pane says when it has nothing to show, or when what it was
 * loading did not arrive (issue #1202).
 *
 * One look everywhere: centred in the space it is given, an optional quiet
 * glyph, one line in the reading ink, an optional secondary line, and the
 * actions under them. `compact` is the same thing for a narrow pane (a
 * sidebar list, the Reader's notes panel, a Studio reference pane): less air,
 * a smaller glyph and the small type step.
 *
 *   <nostos-empty-state concept="note" message="No notes yet." detail="…" />
 *
 *   <nostos-empty-state error message="Your books could not be loaded." (retry)="reload()" />
 *
 * `error` turns it into the in-page failure state: an alert, with a "Try
 * again" button that emits `retry`. Set `[retryable]="false"` when there is
 * nothing to retry (the message then says what to do instead). Other actions
 * are projected and sit beside the retry.
 *
 * It owns no box: no border, fill or card. The words are the state.
 */
@Component({
  selector: 'nostos-empty-state',
  standalone: true,
  imports: [ButtonComponent, NostosIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (icon() || concept()) {
      <nostos-icon
        class="nostos-empty-state__icon"
        [name]="icon()"
        [concept]="concept()"
        [size]="compact() ? 24 : 32"
        weight="light"
        aria-hidden="true"
      />
    }
    @if (message()) {
      <p class="nostos-empty-state__message">{{ message() }}</p>
    }
    @if (detail()) {
      <p class="nostos-empty-state__detail">{{ detail() }}</p>
    }
    <div class="nostos-empty-state__actions">
      <ng-content />
      @if (showRetry()) {
        <button appButton type="button" [variant]="compact() ? 'ghost' : 'secondary'" size="sm" (click)="retry.emit()">
          {{ retryLabel }}
        </button>
      }
    </div>
  `,
  host: {
    class: 'nostos-empty-state',
    '[class.is-compact]': 'compact()',
    '[class.is-error]': 'error()',
    '[attr.role]': "error() ? 'alert' : 'status'",
  },
  styles: [
    `
      :host {
        box-sizing: border-box;
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        gap: 0.5rem;
        width: 100%;
        padding: 3rem 1.5rem;
        color: var(--color-text-muted);
        text-align: center;
      }

      :host(.is-compact) {
        gap: 0.35rem;
        padding: 1.5rem 0.75rem;
      }

      .nostos-empty-state__icon {
        margin-bottom: 0.25rem;
        color: var(--color-text-faint);
      }

      .nostos-empty-state__message,
      .nostos-empty-state__detail {
        max-width: 36rem;
        margin: 0;
        line-height: 1.5;
        overflow-wrap: anywhere;
        text-wrap: balance;
      }

      .nostos-empty-state__message {
        color: var(--color-text-main);
        font-size: var(--text-md);
      }

      .nostos-empty-state__detail {
        color: var(--color-text-light);
        font-size: var(--text-sm);
      }

      :host(.is-compact) .nostos-empty-state__message {
        font-size: var(--text-sm);
      }

      :host(.is-compact) .nostos-empty-state__detail {
        font-size: var(--text-xs);
      }

      .nostos-empty-state__actions {
        display: flex;
        flex-wrap: wrap;
        justify-content: center;
        gap: 0.5rem;
        margin-top: 0.5rem;
      }

      :host(.is-compact) .nostos-empty-state__actions {
        margin-top: 0.25rem;
      }

      .nostos-empty-state__actions:empty {
        display: none;
      }
    `,
  ],
})
export class EmptyStateComponent {
  /** A Phosphor glyph name. Prefer `concept` when the state is about a Nostos thing. */
  readonly icon = input<NostosIconName | null>(null);
  readonly concept = input<NostosConcept | null>(null);
  readonly message = input<string | null>(null);
  /** A quieter second line: what to do, or why. */
  readonly detail = input<string | null>(null);
  /** The narrow-pane variant. */
  readonly compact = input(false, { transform: booleanAttribute });
  /** A load failed: announce it and offer "Try again". */
  readonly error = input(false, { transform: booleanAttribute });
  /** Whether a failed state offers "Try again". */
  readonly retryable = input(true, { transform: booleanAttribute });

  readonly retry = output<void>();

  protected readonly retryLabel = TRY_AGAIN;
  protected readonly showRetry = computed(() => this.error() && this.retryable());
}
