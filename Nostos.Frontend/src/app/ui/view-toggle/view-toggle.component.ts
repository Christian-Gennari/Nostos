/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  computed,
  input,
  output,
  signal,
  viewChildren,
} from '@angular/core';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import type { NostosIconName } from '../icon/nostos-icons';

/** One option in the control. Icon-only by design, so the label is the only name. */
export interface ViewToggleOption {
  /** Emitted (and compared) as the selected value. */
  readonly value: string;
  /** A Nostos glyph NAME — resolved by the icon registry, never an icon package. */
  readonly icon: NostosIconName;
  /** Accessible name AND tooltip. The control has no visible text. */
  readonly label: string;
}

/**
 * Glyph size in px — ONE box for every option, on both surfaces, at every width.
 *
 * There is deliberately no per-option optical size any more. The Library's grid
 * glyph briefly ran one at 20px (PR #430) so its smaller `squares-four` drawing
 * would match the Brain's `map-trifold` ink extent, and it was reverted on
 * review as the heaviest glyph in either toggle. Measured off the running app:
 * 14x14px of ink carrying 146 dark px (DPR 1) against the shared box's 12x12
 * and 116 — while at 18px that glyph's ink ALREADY matches the map glyph's (116
 * vs 120 at DPR 1, 402 vs 390 at DPR 2), so the bump was buying weight, not
 * parity. The two surfaces are the same control, so they render the same box.
 */
const GLYPH_SIZE = 18;

/**
 * The view toggle — one segmented control for switching how a page presents the
 * same data (Library: list ⇄ grid, Brain: list ⇄ map).
 *
 * WHY THIS EXISTS AS A COMPONENT, AFTER THE RECIPE SAID NOT TO EXTRACT ONE
 * ----------------------------------------------------------------------
 * `styles.css` used to carry `.toggle-opt` as a shared *recipe* plus a long
 * comment arguing the markup should stay duplicated, because the four segmented
 * surfaces have four different interaction contracts (Studio: role="tablist";
 * Settings: radiogroup; Brain/Library: role="group" + aria-pressed). That
 * reasoning is still right about FOUR surfaces and still wrong about TWO: the
 * Library and the Brain are the same control with the same contract, shipped as
 * two byte-identical hand-copies whose CSS had already drifted apart once (one
 * copy got the focus ring, the other did not, so the same control behaved
 * differently for keyboard users depending on the page).
 *
 * So this component covers exactly the two that ARE one control, and Studio's
 * tabs and Settings' theme choice are deliberately NOT migrated: they need
 * different semantics (tab and radio roles, visible text labels, roving
 * selection that changes a page's structure), and generalising this into
 * `role`/`aria` inputs would be the leaky abstraction the old comment predicted.
 * Their chrome stays where it is.
 *
 * THE HOST IS THE TRACK, AND IT KEEPS THE LEGACY CLASS HOOKS
 * ---------------------------------------------------------
 * The host element carries whatever classes the call site writes
 * (`class="control-group"` in the Library, `class="control-group view-mode-control"`
 * in the Brain). That is load-bearing, not cosmetic:
 *
 *  - the two surface stylesheets and their specs select the track by those
 *    classes, and so does `e2e/brain-library-parity.spec.ts`, which exists to
 *    assert the two surfaces render the SAME control — with one component that
 *    parity is now structural instead of a CSS coincidence;
 *  - it lets a surface keep a LAYOUT-only declaration (the Brain pins
 *    `flex: 0 0 auto` so the toolbar cannot squeeze the track) while the control
 *    itself owns its chrome.
 *
 * WHAT IS NEW COMPARED TO THE RECIPE IT REPLACES
 * ----------------------------------------------
 *  1. A sliding thumb (220ms, --ease-out) instead of a static fill. The thumb is
 *     positioned from `--vt-index` by `calc()` — no measuring, no ResizeObserver,
 *     and it stays correct across the breakpoint that changes the option width.
 *  2. The selected glyph steps `light` → `regular`. `fill` was prototyped and
 *     rejected: at 18px on a dark track the filled glyph read as a bright blob,
 *     heavier than any other selected state in the app.
 *  3. A quiet hover wash on the option that is NOT selected. The old recipe marked
 *     hover on the ink only, so the two halves of the control felt different.
 *  4. Arrow keys. The control is a `role="group"` of pressed buttons, so it gets
 *     the segmented-control keyboard contract the web expects: one tab stop
 *     (roving tabindex) plus Arrow/Home/End, rather than two Tab presses.
 *  5. A 1px inner top edge on the thumb (`--control-thumb-highlight`) so the
 *     selected option reads as RAISED on dark, where `--shadow-sm` is a black
 *     shadow on a near-black track and does nothing at all.
 *
 * The animation is suppressed for the very first paint (`afterNextRender`): a
 * thumb that slides in from the left on page load reads as a glitch, not as polish.
 */
@Component({
  selector: 'nostos-view-toggle',
  standalone: true,
  imports: [NostosIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (option of options(); track option.value; let i = $index) {
      <button
        #opt
        type="button"
        class="vt-opt"
        [attr.aria-label]="option.label"
        [attr.title]="option.label"
        [attr.aria-pressed]="isSelected(option) ? 'true' : 'false'"
        [attr.tabindex]="i === selectedIndex() ? 0 : -1"
        (click)="select(option.value)"
        (keydown)="onKeydown($event, i)"
      >
        <nostos-icon
          [name]="option.icon"
          [size]="glyphSize"
          [weight]="isSelected(option) ? 'regular' : 'light'"
        />
      </button>
    }
  `,
  host: {
    role: 'group',
    '[style.--vt-index]': 'selectedIndex()',
    '[class.vt-animated]': '!firstPaint()',
  },
  styles: [
    `
      /* Geometry lives here, once. 26px option in a 32px track on desktop and
         32px in 38px under 768px — the two rungs the Library and the Brain had
         each hand-rolled (and had already agreed on, in two files). */
      :host {
        --vt-w: 30px;
        --vt-h: 26px;
        --vt-gap: 2px;
        --vt-pad: 3px;
        /* Declared here as the default AND written by the host binding
           ([style.--vt-index], an inline style, so it always wins). Declaring it
           is what keeps it honest: a token consumed only from TS reads as a typo to
           the design-drift scanner, and rightly so — from CSS alone there is no way
           to tell "bound at runtime" apart from "misspelled". */
        --vt-index: 0;

        position: relative;
        display: inline-flex;
        align-items: center;
        gap: var(--vt-gap);
        padding: var(--vt-pad);
        background: var(--bg-hover);
        border-radius: var(--radius-md);
        /* A hairline ring, not a border: it defines the track on dark, where
           --bg-hover sits ~1.5:1 from the page and the fill alone reads as a
           smudge. A border would change the box and shift both surfaces. */
        box-shadow: inset 0 0 0 1px var(--border-color);
      }

      /* The selected option's thumb, as a PSEUDO-ELEMENT — and that is load-bearing,
         not a style preference. As a real child it shifted the options in the DOM,
         so the first option stopped matching :first-child and the Brain's "the mode
         switch survives map view" spec went red — a structural selector that surface
         already relied on. A ::before is not an element as far as :first-child and
         :last-child are concerned, so the track still contains exactly its two
         buttons. (No backticks in this block, ever: it is a JS template literal, so
         a stray backtick inside a comment ends the string and AOT fails with
         "Failed to resolve styles at position 0 to a string".) */
      :host::before {
        content: '';
        position: absolute;
        top: var(--vt-pad);
        left: var(--vt-pad);
        width: var(--vt-w);
        height: var(--vt-h);
        border-radius: var(--radius-sm);
        background: var(--control-active-fill);
        outline: 1px solid var(--border-color);
        outline-offset: -1px;
        box-shadow: var(--shadow-sm), var(--control-thumb-highlight);
        transform: translateX(calc(var(--vt-index, 0) * (var(--vt-w) + var(--vt-gap))));
      }

      /* Declared only after the first paint, so the thumb is already in place on
         load and animates only when the value actually changes. */
      :host(.vt-animated)::before {
        transition: transform var(--motion-base) var(--ease-out);
      }

      .vt-opt {
        position: relative;
        z-index: 1;
        width: var(--vt-w);
        height: var(--vt-h);
        display: flex;
        align-items: center;
        justify-content: center;
        padding: 0;
        border: none;
        background: none;
        border-radius: var(--radius-sm);
        color: var(--color-text-light);
        cursor: pointer;
        transition:
          color var(--motion-fast) var(--ease-plain),
          background-color var(--motion-fast) var(--ease-plain);
      }

      /* The wash is for the option that is NOT selected — that is the one whose
         hover needs to say "you can switch to me". The selected option already
         has the thumb behind it, and washing it too would double the cue. */
      .vt-opt:hover:not([aria-pressed='true']) {
        background: color-mix(in srgb, var(--color-text-main) 8%, transparent);
        color: var(--color-text-main);
      }

      .vt-opt[aria-pressed='true'] {
        color: var(--control-active-ink);
      }

      .vt-opt:focus-visible {
        outline: var(--focus-ring-width) solid var(--focus-ring);
        outline-offset: -2px;
      }

      /* Press feedback on the glyph, not on the box: the box is the hit target
         and shrinking it would move the thing the pointer is aiming at. */
      .vt-opt:active nostos-icon {
        transform: scale(0.9);
      }

      @media (max-width: 768px) {
        :host {
          --vt-w: 34px;
          --vt-h: 32px;
        }
      }

      @media (prefers-reduced-motion: reduce) {
        :host(.vt-animated)::before {
          transition: none;
        }
      }
    `,
  ],
})
export class ViewToggleComponent {
  /** The options, in render order. Two is the current product shape. */
  readonly options = input.required<readonly ViewToggleOption[]>();

  /**
   * The selected value. The component is CONTROLLED: it never changes its own
   * selection, it only emits, and the surface re-binds `value`. That keeps the
   * persisted preference (LibraryPreferencesService / Brain localStorage) the
   * single source of truth and a `viewMode` changed elsewhere — the Brain's
   * "read this topic's notes" rail action switches back to list — animating
   * the thumb like any other change.
   */
  readonly value = input.required<string>();

  /** Emits the newly selected value. Narrowed by the call site, which owns the union. */
  readonly valueChange = output<string>();

  /** The one glyph box, exposed as a class member so the template can bind it. */
  protected readonly glyphSize = GLYPH_SIZE;

  private readonly buttons = viewChildren<ElementRef<HTMLButtonElement>>('opt');

  /**
   * Protected, not private: a host binding is type-checked against the class and
   * cannot read a private member. Drives the one-shot animation suppression above.
   */
  protected readonly firstPaint = signal(true);

  constructor() {
    afterNextRender(() => this.firstPaint.set(false));
  }

  /**
   * Index of the selected option, falling back to the first. The fallback matters
   * beyond the thumb's position: it is what keeps exactly one option tabbable
   * when `value` matches nothing (a stale persisted value, a surface that has not
   * hydrated yet), so the control can never be unreachable by keyboard.
   */
  protected readonly selectedIndex = computed(() => {
    const index = this.options().findIndex((option) => option.value === this.value());
    return index < 0 ? 0 : index;
  });

  protected isSelected(option: ViewToggleOption): boolean {
    return option.value === this.value();
  }

  protected select(value: string): void {
    if (value === this.value()) return;
    this.valueChange.emit(value);
  }

  /**
   * Roving tabindex keyboard contract: arrows move AND select (there is no
   * "focus without choosing" state in a two-option view switch), Home/End jump
   * to the ends. Up/Down are accepted as well as Left/Right because the control
   * is a horizontal group and both conventions are in the wild.
   */
  protected onKeydown(event: KeyboardEvent, index: number): void {
    const options = this.options();
    if (options.length === 0) return;

    let next: number;
    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        next = (index + 1) % options.length;
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        next = (index - 1 + options.length) % options.length;
        break;
      case 'Home':
        next = 0;
        break;
      case 'End':
        next = options.length - 1;
        break;
      default:
        return;
    }

    event.preventDefault();
    this.select(options[next].value);
    this.buttons()[next]?.nativeElement.focus();
  }
}
