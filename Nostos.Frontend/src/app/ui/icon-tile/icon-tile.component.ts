/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type IconTileTone = 'neutral' | 'quiet' | 'danger';

/**
 * The small rounded square behind an icon: the mark beside a dialog title, the
 * format glyph on an edition row.
 *
 * It is decoration around a `nostos-icon`, never a control, so the call site
 * usually marks it `aria-hidden`. The glyph inside keeps its own size.
 *
 *   <span appIconTile tone="danger" [size]="26" aria-hidden="true">
 *     <nostos-icon name="trash" [size]="14" />
 *   </span>
 *
 * WHY THIS EXISTS: seven stylesheets each declared this square (the confirm
 * and replacement dialogs with byte-identical rules), which is how their
 * corners came to disagree before the shape pass.
 *
 * - `neutral`: a hairline box on the alternate surface (dialog and panel marks)
 * - `quiet`: a borderless hover-tone fill (inside a bordered row)
 * - `danger`: the destructive mark
 */
@Component({
  selector: '[appIconTile]',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<ng-content />',
  host: {
    class: 'nostos-icon-tile',
    '[class.nostos-icon-tile--quiet]': "tone() === 'quiet'",
    '[class.nostos-icon-tile--danger]': "tone() === 'danger'",
    '[style.--icon-tile-size.px]': 'size()',
  },
  styles: [
    `
      :host {
        --icon-tile-size: 32px;

        box-sizing: border-box;
        display: inline-flex;
        flex: 0 0 var(--icon-tile-size);
        align-items: center;
        justify-content: center;
        width: var(--icon-tile-size);
        height: var(--icon-tile-size);
        border: 1px solid var(--border-color);
        border-radius: var(--radius-lg);
        background: var(--bg-surface-alt);
        color: var(--color-text-main);
      }

      :host(.nostos-icon-tile--quiet) {
        border-color: transparent;
        background: var(--bg-hover);
        color: var(--color-text-muted);
      }

      :host(.nostos-icon-tile--danger) {
        border-color: var(--danger-hairline);
        background: var(--danger-surface);
        color: var(--danger-ink);
      }
    `,
  ],
})
export class IconTileComponent {
  readonly tone = input<IconTileTone>('neutral');
  /**
   * Side length in px. Leave it unset to take 32px, or to let the call site's
   * stylesheet set `--icon-tile-size` (which a media query can then change).
   */
  readonly size = input<number | null>(null);
}
