/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { LoadingIndicatorComponent } from '../loading-indicator/loading-indicator.component';

/**
 * `overlay` is the frosted control for buttons that sit ON artwork (the book
 * cover's hover actions). It shares its material with appIconButton's overlay
 * disc, so a text action and an icon action over one image read as one family.
 *
 * `link` is an inline text action with no box — "Read full synopsis", "Show
 * details", "Manage editions" — aligned flush with the text around it. Before it
 * existed each surface hand-rolled one (five copies, half of them underlined).
 */
export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'overlay' | 'link';
export type ButtonSize = 'sm' | 'md' | 'lg';

/**
 * Canonical Nostos text/action button.
 *
 * The component matches the native <button> rather than wrapping one. Native
 * disabled/type/aria attributes and click/keyboard behaviour therefore remain
 * owned by the caller, while this component owns the visual action contract.
 *
 * Product controls with a different interaction model (tabs, radio cards,
 * icon-only actions) should keep their own semantics. Icon-only actions use
 * appIconButton.
 */
@Component({
  selector: 'button[appButton], a[appButton]',
  standalone: true,
  imports: [LoadingIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (busy()) {
      <nostos-loading-indicator
        class="nostos-button__spinner"
        decorative
        [size]="size() === 'sm' ? 'sm' : 'md'"
      />
    }
    <ng-content />
  `,
  host: {
    class: 'nostos-button',
    '[class.nostos-button--primary]': "variant() === 'primary'",
    '[class.nostos-button--secondary]': "variant() === 'secondary'",
    '[class.nostos-button--ghost]': "variant() === 'ghost'",
    '[class.nostos-button--danger]': "variant() === 'danger'",
    '[class.nostos-button--overlay]': "variant() === 'overlay'",
    '[class.nostos-button--link]': "variant() === 'link'",
    '[class.nostos-button--sm]': "size() === 'sm'",
    '[class.nostos-button--lg]': "size() === 'lg'",
    '[attr.aria-busy]': "busy() ? 'true' : null",
  },
  styles: [
    `
      /* A quiet, compact control: a softly rounded rectangle whose height is a
         fixed rung, not a by-product of padding, so a row of mixed variants
         shares one baseline and one edge. Every variant carries a 1px border
         (transparent where it has none) for the same reason. */
      :host {
        box-sizing: border-box;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 6px;
        min-height: var(--control-h-md);
        padding: 0 12px;
        border: 1px solid transparent;
        border-radius: var(--radius-action);
        font-family: inherit;
        font-size: 0.84rem;
        font-weight: var(--fw-medium);
        line-height: 1.2;
        letter-spacing: -0.005em;
        text-decoration: none;
        white-space: nowrap;
        cursor: pointer;
        touch-action: manipulation;
        transition:
          background-color 0.15s ease,
          border-color 0.15s ease,
          box-shadow 0.15s ease,
          color 0.15s ease,
          opacity 0.15s ease,
          transform 0.1s ease;
      }

      :host(:focus-visible) {
        outline: var(--focus-ring-width) solid var(--focus-ring);
        outline-offset: 2px;
      }

      :host(:disabled) {
        opacity: 0.5;
        cursor: not-allowed;
        box-shadow: none;
      }

      :host(.nostos-button--primary) {
        background: var(--cta-surface);
        color: var(--cta-ink);
        box-shadow: var(--button-shadow-primary);
      }

      /* Hover changes the fill only. The button used to rise a pixel, which
         made a row of actions shift under the pointer. */
      :host(.nostos-button--primary:hover:not(:disabled)) {
        background: var(--cta-surface-hover);
      }

      :host(.nostos-button--secondary) {
        background: var(--bg-surface);
        color: var(--color-text-main);
        border-color: var(--control-border);
        box-shadow: var(--button-shadow);
      }

      :host(.nostos-button--secondary:hover:not(:disabled)) {
        background: var(--bg-hover);
      }

      :host(.nostos-button--ghost) {
        background: transparent;
        color: var(--color-text-muted);
      }

      :host(.nostos-button--ghost:hover:not(:disabled)) {
        background: var(--bg-hover);
        color: var(--color-text-main);
      }

      :host(.nostos-button--danger) {
        background: transparent;
        color: var(--color-danger);
        border-color: var(--color-danger);
      }

      :host(.nostos-button--danger:hover:not(:disabled)) {
        background: var(--color-danger-bg);
      }

      /* Same material as appIconButton's overlay disc: --cover-disc-* tokens,
         a frosted ground and a pill shape, because it floats on an image rather
         than sitting in a row of page actions. */
      :host(.nostos-button--overlay) {
        background: var(--cover-disc-bg);
        color: var(--color-text-main);
        border-color: var(--cover-disc-border);
        border-radius: var(--radius-pill);
        box-shadow: var(--cover-disc-shadow);
        backdrop-filter: blur(6px);
        -webkit-backdrop-filter: blur(6px);
      }

      :host(.nostos-button--overlay:hover:not(:disabled)) {
        background: var(--bg-surface);
      }

      /* Link: no box, no rung, flush with the text around it. The underline
         arrives on hover so a column of them does not read as body links. */
      :host(.nostos-button--link) {
        min-height: 0;
        padding: 0;
        gap: 0.35rem;
        border: none;
        border-radius: var(--radius-sm);
        background: none;
        color: var(--color-text-muted);
        font-size: 0.8rem;
        text-underline-offset: 3px;
      }

      :host(.nostos-button--link:hover:not(:disabled)) {
        color: var(--color-text-main);
        text-decoration: underline;
      }

      :host(:active:not(:disabled)) {
        transform: scale(0.98);
      }

      :host(.nostos-button--link:active:not(:disabled)) {
        transform: none;
      }

      /* The height rungs sit inside :where(), so they weigh no more than the
         base rule above. A screen's own "min-height: 44px on touch" rule is a
         single class, and it has to keep winning: written as plain
         :host(.nostos-button--sm) the rung out-weighed it, and every small
         button on a phone stayed 28px tall. */
      :host(:where(.nostos-button--sm)) {
        min-height: var(--control-h-xs);
      }

      :host(:where(.nostos-button--lg)) {
        min-height: 36px;
      }

      :host(.nostos-button--sm) {
        padding: 0 10px;
        font-size: var(--text-xs);
      }

      :host(.nostos-button--lg) {
        padding: 0 14px;
        font-size: var(--text-sm);
      }

      /* Busy: the button draws the one spinner, in its own ink, and hides its
         leading glyph so a busy "Save" never shows a spinner AND a check. Call
         sites used to place a circle-notch by hand with one of nine copies of a
         spin keyframe, at 14, 15 or 16px. */
      .nostos-button__spinner {
        color: inherit;
      }
      :host([aria-busy='true']) ::ng-deep > nostos-icon {
        display: none;
      }

      @media (prefers-reduced-motion: reduce) {
        :host {
          transition: none;
        }
      }
    `,
  ],
})
export class ButtonComponent {
  readonly variant = input<ButtonVariant>('secondary');
  readonly size = input<ButtonSize>('md');

  /**
   * Busy sets aria-busy and draws the button's spinner (replacing any leading
   * glyph). It intentionally does not override the native disabled attribute;
   * callers decide whether work in flight should block activation.
   */
  readonly busy = input(false);
}
