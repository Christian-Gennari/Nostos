/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';

import { ChipComponent } from '../../ui/chip/chip.component';
import type { KindLegendEntry } from './topic-map.helpers';

/**
 * The kind legend, as a filter: every kind in scope with its count, plus the
 * untyped row and its "Sort them" link, and one escape hatch when kinds are
 * hidden.
 *
 * It is a component of its own because the same legend lives in two places:
 * on the map for a wide screen, and inside the phone's Controls sheet — the
 * four rows of chips covered the top of a 390 px map and its nodes. Both copies
 * therefore carry the same markup, `aria-pressed` states and aria-labels.
 */
@Component({
  selector: 'app-map-kind-legend',
  standalone: true,
  imports: [RouterLink, ChipComponent],
  templateUrl: './map-kind-legend.component.html',
  styleUrl: './map-kind-legend.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MapKindLegendComponent {
  readonly entries = input<readonly KindLegendEntry[]>([]);
  /** Topics in scope that the filter hides; 0 means every kind is shown. */
  readonly hiddenByKindCount = input(0);

  readonly toggled = output<string>();
  readonly cleared = output<void>();
}
