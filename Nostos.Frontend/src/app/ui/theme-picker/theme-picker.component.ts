/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, viewChildren } from '@angular/core';

import { THEMES, ThemeChoice, ThemeService, themeOption } from '../../core/services/theme.service';

interface PickerOption {
  value: ThemeChoice;
  label: string;
}

/**
 * The app theme as a row of swatches (issue #1074): Follow system and the five
 * themes. It is the compact form of Settings → Colour theme, for the places a
 * theme is changed in passing: the reader's view settings and the Studio's page
 * settings. Each swatch shows its theme's real paper and ink without wearing
 * it; Follow system shows the day theme's paper beside the night one's.
 *
 * One radio group, one tab stop, arrows move and pick, like nostos-segmented.
 * The names are too long to sit under six swatches in a popover, so the chosen
 * one is spelled out in a caption (and every swatch carries its name as its
 * accessible name and tooltip).
 */
@Component({
  selector: 'nostos-theme-picker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tp-swatches" role="radiogroup" [attr.aria-label]="label()">
      @for (option of options; track option.value; let i = $index) {
        <button
          #swatch
          type="button"
          role="radio"
          class="tp-swatch"
          [attr.aria-checked]="option.value === theme.choice() ? 'true' : 'false'"
          [attr.tabindex]="option.value === theme.choice() ? 0 : -1"
          [attr.aria-label]="option.label"
          [attr.title]="option.label"
          [attr.data-theme-option]="option.value"
          (click)="pick(option.value)"
          (keydown)="onKey($event, i)"
        >
          @if (option.value === 'system') {
            <span class="tp-half" [style.background]="day().swatch.paper" aria-hidden="true"></span>
            <span class="tp-half" [style.background]="night().swatch.paper" aria-hidden="true"></span>
          } @else {
            <span class="tp-face" [style.background]="swatchOf(option.value).paper" [style.color]="swatchOf(option.value).ink" aria-hidden="true">Aa</span>
          }
        </button>
      }
    </div>
    <p class="tp-caption">{{ caption() }}</p>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: 6px;
      min-width: 0;
    }
    /* One row, always: six 32px swatches need 222px, which the reader's
       control column (about 235px at 390px wide) and the Studio popover hold. */
    .tp-swatches {
      display: flex;
      gap: 6px;
    }
    .tp-swatch {
      display: inline-flex;
      flex-shrink: 0;
      width: 32px;
      height: 32px;
      padding: 0;
      overflow: hidden;
      border: 1px solid var(--border-color);
      border-radius: 50%;
      background: none;
      cursor: pointer;
      font-family: Newsreader, Georgia, serif;
      font-size: 0.88rem;
      line-height: 1;
      transition: box-shadow var(--transition-fast), border-color var(--transition-fast);
    }
    .tp-swatch:hover {
      border-color: var(--border-focus);
    }
    /* A ring with a gap of the surface behind it, so the swatch's own edge
       never merges with the ring (as the Studio's swatches did). */
    .tp-swatch[aria-checked='true'] {
      box-shadow:
        0 0 0 2px var(--tp-gap, var(--bg-surface)),
        0 0 0 4px var(--color-text-main);
    }
    .tp-swatch:focus-visible {
      outline: var(--focus-ring-width) solid var(--focus-ring);
      outline-offset: 5px;
    }
    .tp-face,
    .tp-half {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      height: 100%;
    }
    .tp-face {
      width: 100%;
    }
    .tp-half {
      width: 50%;
    }
    .tp-caption {
      margin: 0;
      font-size: var(--text-xs);
      color: var(--color-text-light);
    }
  `,
})
export class ThemePickerComponent {
  readonly theme = inject(ThemeService);

  /** The group's accessible name. */
  readonly label = input('Colour theme');
  /** Said after the chosen theme's name in the caption, e.g. its scope. */
  readonly note = input<string | null>(null);

  readonly options: readonly PickerOption[] = [
    { value: 'system', label: 'Follow system' },
    ...THEMES.map((t) => ({ value: t.value, label: t.label })),
  ];

  private readonly swatches = viewChildren<ElementRef<HTMLButtonElement>>('swatch');

  readonly day = computed(() => themeOption(this.theme.dayTheme()));
  readonly night = computed(() => themeOption(this.theme.nightTheme()));

  readonly caption = computed(() => {
    const choice = this.theme.choice();
    const name =
      choice === 'system'
        ? `Follow system: ${this.day().label} by day, ${this.night().label} by night`
        : themeOption(choice).label;
    const note = this.note();
    return note ? `${name} · ${note}` : name;
  });

  swatchOf(value: ThemeChoice) {
    return themeOption(value as Exclude<ThemeChoice, 'system'>).swatch;
  }

  pick(value: ThemeChoice): void {
    this.theme.setChoice(value);
  }

  onKey(event: KeyboardEvent, index: number): void {
    const step =
      event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    const next = (index + step + this.options.length) % this.options.length;
    this.pick(this.options[next].value);
    this.swatches()[next]?.nativeElement.focus();
  }
}
