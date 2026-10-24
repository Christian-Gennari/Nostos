/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The Nostos lockup: the kit v1 tile mark and the wordmark as one piece of
 * artwork, 40px tall on the mark.
 *
 *   <nostos-brand-lockup />
 *
 * The tile is token-driven (`--brand-shape` fills it, `--brand-doorway` is the
 * arch knockout), so one mark serves every theme; the kit's reversed variant is
 * not used. The wordmark is live Hanken Grotesk 500 at the kit's own metrics,
 * inked in `--color-text-main` (ink in light, porcelain in dark), deliberately
 * not `--color-primary`, which in dark is a desaturated green that tints the
 * word instead of reading neutral.
 *
 * Two custom properties let a host fade the wordmark without reaching inside
 * (the Library's collapsed rail fades the word and keeps the tile):
 *   --brand-lockup-word-opacity    the wordmark's opacity (default 1)
 *   --brand-lockup-word-transition how that opacity changes (default none)
 *
 * WHY THIS EXISTS: the same SVG was pasted into the Library sidebar, the Brain
 * sidebar and the Cloud entry card, each with its own copy of the four rules
 * that ink it.
 */
@Component({
  selector: 'nostos-brand-lockup',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg class="lockup" viewBox="0 0 1840 512" role="img" aria-label="Nostos" focusable="false">
      <g transform="scale(0.5)">
        <rect class="tile" x="0" y="0" width="1024" height="1024" rx="205" />
        <path
          class="arch"
          d="M 282 764 L 282 509 A 229 234 0 0 1 740 509 L 740 764 Z M 358 764 L 358 509 A 110 112 0 0 1 578 509 L 578 764 Z"
          fill-rule="evenodd"
        />
      </g>
      <text class="word" x="625" y="345" font-size="255" font-weight="500" letter-spacing="-8">nostos</text>
    </svg>
  `,
  styles: [
    `
      :host {
        display: block;
        flex: none;
        line-height: 0;
      }

      .lockup {
        display: block;
        height: 40px;
        width: auto;
      }

      .tile {
        fill: var(--brand-shape);
      }

      .arch {
        fill: var(--brand-doorway);
      }

      .word {
        fill: var(--color-text-main);
        font-family: 'Hanken Grotesk', sans-serif;
        opacity: var(--brand-lockup-word-opacity, 1);
        transition: var(--brand-lockup-word-transition, none);
      }
    `,
  ],
})
export class BrandLockupComponent {}
