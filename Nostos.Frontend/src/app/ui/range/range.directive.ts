/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { Directive, DoCheck, ElementRef, inject } from '@angular/core';

/**
 * Canonical Nostos slider.
 *
 * Like appCheckbox, the NATIVE range input stays the host, so value, min,
 * max, step, (input), the arrow keys and labelling stay the browser's and the
 * caller's. The directive adds the class styles.css draws (a thin track, the
 * filled part in the theme's accent, a round thumb) and keeps `--range-fill`
 * at the value's place on the track, which WebKit cannot paint by itself.
 *
 * Before this, the Studio's sound volume was the browser's own slider with
 * `accent-color` set, which looked like nothing else in the app.
 */
@Directive({
  selector: 'input[type=range][appRange]',
  standalone: true,
  host: {
    class: 'nostos-range',
    '(input)': 'paint()',
  },
})
export class RangeDirective implements DoCheck {
  private readonly el = inject<ElementRef<HTMLInputElement>>(ElementRef).nativeElement;
  private painted = '';

  /**
   * A [value] binding changes the property without an input event, and a
   * scrubber's [max] (a duration that arrives once the media loads) moves the
   * same value to another place on the track.
   */
  ngDoCheck(): void {
    if (this.key() !== this.painted) this.paint();
  }

  paint(): void {
    const min = Number(this.el.min || 0);
    const max = Number(this.el.max || 100);
    const value = Number(this.el.value);
    const share = max > min ? Math.min(1, Math.max(0, (value - min) / (max - min))) : 0;
    this.painted = this.key();
    this.el.style.setProperty('--range-fill', `${share * 100}%`);
  }

  private key(): string {
    return `${this.el.min}|${this.el.max}|${this.el.value}`;
  }
}
