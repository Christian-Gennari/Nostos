/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { CdkMenuItem } from '@angular/cdk/menu';
import { ChangeDetectionStrategy, Component, Directive, computed, input, output } from '@angular/core';

import { ButtonComponent, type ButtonVariant } from '../button/button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { NOSTOS_MENU } from '../menu/menu.component';

/**
 * A menu item that keeps the menu open when used, for a stepper that is
 * pressed several times in a row. Everything else (focus, arrow keys, the
 * `role="menuitem"`) is the CDK item's.
 */
@Directive({
  selector: 'button[nostosMenuStepItem]',
  standalone: true,
  providers: [{ provide: CdkMenuItem, useExisting: MenuStepItemDirective }],
})
export class MenuStepItemDirective extends CdkMenuItem {
  override trigger(): void {
    super.trigger({ keepOpen: true });
  }
}

/** Two rates closer than this are the same rate (0.05 steps add up in floating point). */
const SAME = 1e-6;

/** `1.25×`, `1×`, `0.8×`: at most two decimals, the multiplication sign. */
export function formatPlaybackRate(rate: number): string {
  return `${Number(rate.toFixed(2))}×`;
}

/**
 * The one playback-speed control, for read aloud and the audiobook player.
 *
 * A real button (appButton) shows the current rate and opens a nostos-menu:
 * the common rates as choices, the current one checked, and, when `step` is
 * set, a "− rate +" row under them for a rate between the presets. The menu
 * is the CDK's, so Escape, outside clicks, arrow keys and focus return come
 * with it. The stepper keeps the menu open, so it can be pressed again.
 *
 * Each player owns its range: read aloud offers only the rates its voices
 * support (no step), the audiobook a wider range with a 0.05 step.
 *
 *   <nostos-playback-speed-menu
 *     [rate]="rate()" [rates]="[0.8, 1, 1.25, 1.5, 2]"
 *     [step]="0.05" [min]="0.5" [max]="3"
 *     (rateChange)="setRate($event)" />
 */
@Component({
  selector: 'nostos-playback-speed-menu',
  standalone: true,
  imports: [ButtonComponent, NostosIconComponent, ...NOSTOS_MENU, MenuStepItemDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'nostos-playback-speed-menu' },
  template: `
    <button
      appButton
      [variant]="variant()"
      size="sm"
      type="button"
      class="playback-speed-menu__trigger"
      [nostosMenuTriggerFor]="speedMenu"
      [title]="label()"
      [attr.aria-label]="label() + ': ' + rateLabel() + '. Change speed'"
      data-testid="playback-speed-trigger"
    >
      <span class="playback-speed-menu__value">{{ rateLabel() }}</span>
      <nostos-icon name="caret-down" [size]="12" class="playback-speed-menu__caret" />
    </button>

    <ng-template #speedMenu>
      <nostos-menu
        class="playback-speed-menu"
        [attr.aria-label]="label()"
        data-testid="playback-speed-menu"
      >
        <div nostosMenuGroup>
          @for (option of options(); track option) {
            <button
              nostosMenuItemRadio
              [checked]="isCurrent(option)"
              (triggered)="choose(option)"
            >{{ format(option) }}</button>
          }
        </div>
        @if (step(); as by) {
          <hr nostosMenuSeparator />
          <div class="playback-speed-menu__fine" role="group" [attr.aria-label]="'Fine ' + label().toLowerCase()">
            <button
              nostosMenuStepItem
              type="button"
              class="nostos-menu-item playback-speed-menu__step"
              [cdkMenuItemDisabled]="rate() <= lo() + SAME"
              (cdkMenuItemTriggered)="nudge(-by)"
              [attr.aria-label]="'Slower by ' + format(by)"
              title="Slower"
              data-testid="playback-speed-slower"
            >
              <nostos-icon name="minus" [size]="16" />
            </button>
            <span class="playback-speed-menu__readout" aria-live="polite" data-testid="playback-speed-readout">{{
              rateLabel()
            }}</span>
            <button
              nostosMenuStepItem
              type="button"
              class="nostos-menu-item playback-speed-menu__step"
              [cdkMenuItemDisabled]="rate() >= hi() - SAME"
              (cdkMenuItemTriggered)="nudge(by)"
              [attr.aria-label]="'Faster by ' + format(by)"
              title="Faster"
              data-testid="playback-speed-faster"
            >
              <nostos-icon name="plus" [size]="16" />
            </button>
          </div>
        }
      </nostos-menu>
    </ng-template>
  `,
  styleUrl: './playback-speed-menu.component.css',
})
export class PlaybackSpeedMenuComponent {
  /** The rate playing now. */
  readonly rate = input.required<number>();
  /** The common rates, listed as choices. */
  readonly rates = input.required<readonly number[]>();
  /** The fine step between presets; `null` offers the presets only. */
  readonly step = input<number | null>(null);
  /** The slowest rate the player supports (defaults to the slowest preset). */
  readonly min = input<number | null>(null);
  /** The fastest rate the player supports (defaults to the fastest preset). */
  readonly max = input<number | null>(null);
  /** Names the control and its menu ("Reading speed", "Playback speed"). */
  readonly label = input('Playback speed');
  readonly variant = input<ButtonVariant>('ghost');

  readonly rateChange = output<number>();

  protected readonly SAME = SAME;

  protected readonly options = computed(() =>
    this.rates().filter((r) => r >= this.lo() - SAME && r <= this.hi() + SAME),
  );
  protected readonly rateLabel = computed(() => formatPlaybackRate(this.rate()));

  protected readonly lo = computed(() => this.min() ?? Math.min(...this.rates()));
  protected readonly hi = computed(() => this.max() ?? Math.max(...this.rates()));

  protected isCurrent(option: number): boolean {
    return Math.abs(option - this.rate()) < SAME;
  }

  protected format(rate: number): string {
    return formatPlaybackRate(rate);
  }

  protected choose(rate: number): void {
    if (!this.isCurrent(rate)) this.rateChange.emit(rate);
  }

  /** One step, landing on the step's grid and inside the player's range. */
  protected nudge(delta: number): void {
    const by = Math.abs(delta);
    const stepped = Math.round((this.rate() + delta) / by) * by;
    const next = Number(Math.min(this.hi(), Math.max(this.lo(), stepped)).toFixed(2));
    if (!this.isCurrent(next)) this.rateChange.emit(next);
  }
}
