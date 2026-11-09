/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { IconButtonComponent } from '../icon-button/icon-button.component';

/**
 * The bar that appears while a list is in multi-select mode: it reports how
 * many rows are ticked and holds the bulk actions, floating above the dock.
 *
 * WHY THIS EXISTS: Library's books and the Brain's notes grew the same bar
 * twice — same position, same count, same phone rules, same copy-pasted CSS —
 * and it was the only chrome in the app painted as a solid `--primary-fill`
 * pill with `overlay` (cover-art) buttons. That made it read like a foreign
 * snackbar: every other floating surface in Nostos is the dock's material, and
 * every other action is a normal button.
 *
 * THE SURFACE IS THE DOCK'S. `--dock-surface` + `--dock-border` +
 * `--dock-shadow` and `--radius-dock` are the floating-chrome recipe the dock,
 * the Ask panel and the transfer indicator already share. They carry a light
 * and a dark value (and the sepia themes'), so the bar follows the theme with
 * no per-theme override. It sits `--dock-float-bottom + --dock-rail-h` above
 * the viewport bottom, exactly like the existing bars did.
 *
 * THE ACTIONS ARE THE APP'S OWN BUTTONS. Callers project `appButton`s in the
 * variants the app uses elsewhere: `ghost` for the neutral bulk actions and
 * `danger` for delete. The bar deliberately does not paint them: `overlay` is
 * the frosted material for buttons that sit ON artwork, which is why the old
 * bar's grey discs read as a different control family. With `ghost`, the
 * primitive's `:disabled { opacity: 0.5 }` is legible on the dock's light/dark
 * surface, so "0 selected" now looks as inactive as it is.
 *
 * PHONE: the bar moves one dock's height down and the action labels hide, so
 * the same buttons become touch-size icon actions (their `aria-label` stays).
 * Callers mark projected actions with `class="selection-action"` and their
 * text with `class="selection-label"`; the projected buttons stay in the
 * caller's view, so those two rules are the one place this component reaches
 * into projected content (`::ng-deep`), exactly as the reader shell does for
 * its projected actions.
 *
 * The count keeps its `role="status"` + `aria-live="polite"` so a screen
 * reader hears each change; the bar keeps `role="toolbar"`.
 *
 *   <nostos-selection-bar [count]="selected().size" label="Selected notes"
 *                         (closed)="leaveSelection()">
 *     <button appButton variant="ghost" size="sm" class="selection-action">…</button>
 *   </nostos-selection-bar>
 */
@Component({
  selector: 'nostos-selection-bar',
  standalone: true,
  imports: [IconButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="selection-bar" role="toolbar" [attr.aria-label]="label()">
      <span class="selection-count" role="status" aria-live="polite">{{ countText() }}</span>
      <div class="selection-actions">
        <ng-content />
      </div>
      <button
        appIconButton
        type="button"
        icon="x"
        class="selection-close"
        [glyphSize]="16"
        aria-label="Stop selecting"
        (click)="closed.emit()"
      ></button>
    </div>
  `,
  styles: [
    `
      /* Fixed, bottom-centre, clear of the dock. The offsets are the ones the
         two hand-written bars used, now expressed through the dock's geometry
         tokens so the bar and the dock cannot drift apart. */
      :host {
        position: fixed;
        left: 50%;
        bottom: calc(var(--dock-float-bottom) + var(--dock-rail-h) + 16px);
        z-index: var(--layer-system);
        display: block;
        max-width: calc(100vw - 2rem);
        transform: translateX(-50%);
      }

      /* The dock's material, not a filled pill: a raised surface with a
         hairline and a cast, one step rounder than a card. */
      .selection-bar {
        box-sizing: border-box;
        display: flex;
        align-items: center;
        gap: 0.35rem;
        max-width: 100%;
        padding: 0.4rem 0.45rem 0.4rem 1rem;
        border: 1px solid var(--dock-border);
        border-radius: var(--radius-dock);
        background: var(--dock-surface);
        box-shadow: var(--dock-shadow);
        color: var(--color-text-main);
        font-size: var(--text-sm);
        white-space: nowrap;
      }

      /* The count is a status, not a title: muted ink and tabular figures so
         it does not jump as the number changes. */
      .selection-count {
        flex: none;
        margin-inline-end: 0.35rem;
        color: var(--color-text-muted);
        font-variant-numeric: tabular-nums;
        font-weight: var(--fw-medium);
      }

      .selection-actions {
        display: flex;
        align-items: center;
        gap: 0.25rem;
        min-width: 0;
      }

      /* A divider before the close control would be a second edge in a 48px
         bar; the gap and the icon button already separate it. */
      .selection-close {
        flex: none;
      }

      /* Phone: the bar lifts one rail's worth and the labels give way to the
         icons, which grow to the touch floor. The projected buttons belong to
         the call site's view, so these two rules must reach through the
         projection point. */
      @media (max-width: 768px) {
        :host {
          bottom: calc(var(--dock-rail-h) + env(safe-area-inset-bottom, 0px) + 12px);
        }

        .selection-bar {
          gap: 0.15rem;
          padding-inline: 0.75rem 0.5rem;
        }

        :host ::ng-deep .selection-action {
          min-width: var(--control-h-touch);
          min-height: var(--control-h-touch);
          justify-content: center;
          padding-inline: 0.5rem;
        }

        :host ::ng-deep .selection-label {
          display: none;
        }

        .selection-close {
          width: var(--control-h-touch);
          height: var(--control-h-touch);
        }
      }
    `,
  ],
})
export class SelectionBarComponent {
  /** How many rows are ticked. Drives the live count and the empty state. */
  readonly count = input.required<number>();

  /** The toolbar's accessible name — "Selected books", "Selected notes". */
  readonly label = input('Selected items');

  /** The close control was pressed. The caller owns what leaving means. */
  readonly closed = output<void>();

  /**
   * "0 selected", "1 selected", "3 selected". The old markup special-cased the
   * singular to the same string, so this is behaviour-preserving; the app has
   * no "Select items" hint precedent, so the honest count stays.
   */
  readonly countText = computed(() => `${this.count()} selected`);
}
