/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import type { NostosIconName, NostosIconWeight } from '../icon/nostos-icons';

/** Button box rungs. Measured from the live app, not invented. */
export type IconButtonSize = 'xxs' | 'xs' | 'md';
/** Tone affects the hover ink only; the fill stays neutral. */
export type IconButtonTone = 'default' | 'danger';
/**
 * What the button sits on. `plain` is the app's chrome; `overlay` is a frosted
 * disc for buttons that sit ON artwork (cover corners, cover hover actions),
 * where a transparent button would vanish into the image.
 */
export type IconButtonSurface = 'plain' | 'overlay';

/**
 * A single icon button, replacing 30 hand-built copies across 5 templates.
 *
 * WHY AN ATTRIBUTE SELECTOR ON THE NATIVE BUTTON
 * ----------------------------------------------
 * `selector: 'button[appIconButton]'` means the HOST element is the real
 * `<button>`. That is not a stylistic preference, it is the only shape that
 * survives contact with this codebase:
 *
 *  - Native `disabled`, `type`, `(click)`, keyboard activation and `aria-*` keep
 *    working with no forwarding code.
 *  - One-off classes (`desktop-only`, `zen-toggle`, `overflow-toggle`) pass
 *    through untouched, so surface CSS that targets them is unaffected.
 *  - Descendant selectors keep matching. Several surfaces style the button from a
 *    scoped rule (`.reader-toolbar .icon-btn { border-radius: var(--radius-md) }`,
 *    `.doc-actions .icon-btn`, `.note-actions .icon-btn`) and specs query
 *    `.actions .icon-btn`. A custom wrapper element would break all of them, and
 *    a plain directive could not own an encapsulated stylesheet the way every
 *    other component in `src/app/ui/` does.
 *
 * The host therefore keeps the literal `icon-btn` class. That is load-bearing:
 * `styles.css` still carries `.icon-btn:hover`, `.icon-btn.delete:hover` and
 * `nostos-icon { top: 1px }` (the glyph nudge every surface relies on), and the
 * existing specs select on `.icon-btn`. The component adds the SIZING and SHAPE,
 * which is what the five copies each re-declared inconsistently.
 *
 * THE GLYPH IS A NAME, NOT AN ICON OBJECT
 * ---------------------------------------
 * `[icon]` takes a Nostos glyph name (`icon="trash"`), so this component —
 * and every one of its call sites — knows nothing about the icon library. The
 * name resolves through `nostos-icons.ts`, which is the single place a family or
 * a glyph can be changed. Passing icon data objects (the old `[icon]="Trash2Icon"`)
 * is what made a library swap a 22-file edit.
 *
 * `weight`, NOT `strokeWidth`
 * ---------------------------
 * The previous library drew with strokes, so call sites tuned `strokeWidth`
 * (1, 1.2, 1.4, 1.5, 1.6, 1.75, 1.8, 2, 2.2 all appear). Phosphor draws with
 * filled geometry, so that knob does not exist; weight is the equivalent axis.
 * Two measurements decided the mapping.
 *
 * 1. WHAT THE OLD APP ACTUALLY PAINTED (live, on main). `lucide-angular`
 *    truncates via `parseInt`, so 1.2 / 1.5 / 1.6 / 1.75 / 1.8 all painted as
 *    `stroke-width: 1` and 2.2 painted as 2 — read off 146 rendered glyphs on
 *    /library: declared 1.5 -> svg `stroke-width="1"`, declared 2.2 -> "2".
 *    The painted range was therefore only ever {1, 2}.
 * 2. WHAT EACH WEIGHT IS (bar thickness in device px of a straight stroke at a
 *    256px render, both families rasterised side by side):
 *
 *      lucide    sw 1 -> 10px   sw 1.5 -> 16px   sw 2 -> 22px   sw 2.2 -> 24px
 *      phosphor  thin -> 8px    light  -> 12px   regular -> 16px   bold -> 24px
 *
 * So the DECLARED value is the honest guide — it is what the author asked for,
 * and the truncation was the old package's bug, not a design decision — and its
 * ratio against the default lands almost exactly on a Phosphor weight:
 *
 *      declared / 2 = 0.5-0.6  ->  thin     (0.50 of regular)
 *      declared / 2 = 0.7-0.9  ->  light    (0.75 of regular)
 *      declared / 2 = 1.0-1.1  ->  regular  (1.00)   <- and the default
 *
 * Deliberately NOT mapped by painted thickness alone: that lands the painted 2
 * on `bold` (22px -> 24px) and would make the whole app bold, which is the
 * opposite of the icon language this migration is standardising on. The visible
 * consequence of choosing `regular` is that the app's icons are FINER than
 * before: Phosphor regular is 16px where lucide's default 2 was 22px, so an
 * unspecified call site now paints ~27% thinner. That is the family's normal UI
 * weight, not a nudge, and it is the one judgement call in this file that is
 * worth a second opinion on the before/after screenshots.
 *
 * Deliberately NOT here: `data-tip`, the themed tooltip, and the `.delete`
 * fill-on-hover. Those stay in the surfaces that own them so this remains a
 * pure de-duplication and every migration is pixel-identical. Moving them is a
 * follow-up, not a refactor.
 *
 * THERE IS NO `active` INPUT, ON PURPOSE.
 * Every surface's selected state is styled by its own `.icon-btn.active` rule, and
 * because the host is the button a plain `[class.active]="tocOpen()"` keeps working
 * untouched. An `active` input emitting `icon-btn--active` would be a SECOND way to
 * express one state, and the two would drift.
 *
 * THERE IS NO `ariaLabel` INPUT, ON PURPOSE.
 * A host binding like `[attr.aria-label]="ariaLabel() || null"` OVERRIDES a
 * static `aria-label` written on the call site, so `<button appIconButton
 * aria-label="Edit book">` had its label silently replaced with nothing — a
 * real accessibility regression that no unit test caught and only the computed
 * probe did. Because the host IS the `<button>`, native attributes already pass
 * through untouched, so the input only ever added a way to lose the label.
 * Write `aria-label` (or `title`) on the call site directly.
 */
@Component({
  selector: 'button[appIconButton]',
  standalone: true,
  imports: [NostosIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<nostos-icon [name]="icon()" [size]="glyphSize()" [weight]="weight()" />`,
  host: {
    class: 'icon-btn',
    '[class.icon-btn--danger]': "tone() === 'danger'",
    '[class.icon-btn--xxs]': "size() === 'xxs'",
    '[class.icon-btn--xs]': "size() === 'xs'",
    '[class.icon-btn--overlay]': "surface() === 'overlay'",
    '[attr.aria-pressed]': 'pressedAttr()',
  },
  styles: [
    `
      /* The token graph and the "icon-btn" base stay in styles.css. Measured
         per-surface rungs, re-declared here once instead of in five places:

           reader  / modal   32px  a smart-hidden  [--control-h-md]
           library rows      28px  radius 4px
           note card .xs     24px  radius 50%   <- round chip, NOT xs=28px (card since removed)

         "small" appeared in the note card's markup but had NO CSS rule anywhere, so it
         already computed at 32px and maps to md; a "small" size is deliberately
         not a rung, because inventing one would change those buttons' size. The
         dead class was dropped during that migration. */
      :host(.icon-btn--xxs) {
        width: 24px;
        height: 24px;
      }
      :host(.icon-btn--xs) {
        width: var(--control-h-xs);
        height: var(--control-h-xs);
      }

      /* OVERLAY: one material for every button that sits on cover artwork. It
         used to be three hand-built discs on the Library card alone (32px
         frosted corners, 36px solid centre discs, each with its own hover), so a
         card showed two shapes and two inks. The disc owns its shape here, so the
         radius rule above does not apply to it. Selected inks (the favourite's
         red, the finished tick's green) stay with the surface. */
      :host(.icon-btn--overlay) {
        border-radius: var(--radius-round);
        background: var(--cover-disc-bg);
        border: 1px solid var(--cover-disc-border);
        color: var(--cover-disc-ink);
        box-shadow: var(--cover-disc-shadow);
        backdrop-filter: blur(6px);
        -webkit-backdrop-filter: blur(6px);
        transition:
          background-color 0.15s ease,
          color 0.15s ease,
          opacity 0.2s ease,
          transform 0.1s ease;
      }
      :host(.icon-btn--overlay:hover:not(:disabled)) {
        background: var(--bg-surface);
        color: var(--color-text-main);
      }
      /* Pressed solidifies the ground only; its ink is the surface's (red heart,
         green tick), so this rule must not set a colour. */
      :host(.icon-btn--overlay[aria-pressed='true']) {
        background: var(--bg-surface);
      }
      :host(.icon-btn--overlay.icon-btn--danger:hover:not(:disabled)) {
        color: var(--color-danger);
      }

      /* The host is the native button, so :active arrives on pointer-down with no
         JS state or delayed click handler. Keep the response geometry-only:
         surface-owned hover/selected colours remain untouched. */
      :host(:active:not(:disabled)) {
        transform: scale(0.94);
      }
      /* Size only. Radius deliberately NOT owned: it varies per surface on
         purpose — 3px global (--radius-sm), 4px on Library rows and the reader
         toolbar, 6px on the studio zen toggle, 50% on the old note card's round chips —
         and it mostly arrives through descendant rules that keep matching because
         the host is still the button. Encoding a radius rung here would move
         pixels on four surfaces to no benefit.
         Hover, selected-state and the .delete tone likewise stay with the surfaces. */
    `,
  ],
})
export class IconButtonComponent {
  /** The Nostos glyph to render. Required — a button with no glyph is a bug. */
  readonly icon = input.required<NostosIconName>();

  /**
   * Glyph size in px. Kept separate from the button box: several call sites use a
   * 14-15px glyph inside a 28px box, which is a deliberate optical choice.
   */
  readonly glyphSize = input<number>(16);

  /**
   * Glyph weight. Was `strokeWidth` on the previous (stroke-drawn) library; the
   * migrated call sites use the measured equivalents, and a plain call site gets
   * `regular`, which is Phosphor's normal UI weight.
   */
  readonly weight = input<NostosIconWeight>('regular');

  /** `xxs` = 24px round chip, `xs` = 28px, `md` = 32px (the token default). */
  readonly size = input<IconButtonSize>('md');

  /** `danger` tints the hover ink; the fill stays neutral by design. */
  readonly tone = input<IconButtonTone>('default');

  /** `overlay` = frosted round disc for buttons that sit on cover artwork. */
  readonly surface = input<IconButtonSurface>('plain');

  /**
   * Tri-state `aria-pressed`. `null` (the default) leaves the attribute OFF
   * entirely — most icon buttons are actions, not toggles, and emitting
   * `aria-pressed="false"` on them would misreport them as toggles to AT.
   * Migrations therefore start at `null`, which is identical to today's markup.
   */
  readonly pressed = input<boolean | null>(null);

  /** Exposed for tests: which rung class the host should carry. */
  readonly sizeClass = computed(() => `icon-btn--${this.size()}`);

  /**
   * `aria-pressed` as a STRING, computed here rather than in the host binding.
   * Angular host bindings resolve against the component instance, so the global
   * `String()` is not callable there (`TS2339: Property 'String' does not exist on
   * type IconButtonComponent`). Keeping the conversion in a `computed` also keeps
   * the host binding declarative.
   */
  readonly pressedAttr = computed(() => {
    const p = this.pressed();
    return p === null ? null : p ? 'true' : 'false';
  });
}
