/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { Collection } from '../../core/dtos/collection.dtos';
import type { FlatTreeNode } from '../../ui/flat-tree/flat-tree.helper';
import { SelectionBarComponent } from '../../ui/selection-bar/selection-bar.component';
import { ButtonComponent } from '../../ui/button/button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { NOSTOS_MENU } from '../../ui/menu/menu.component';

/**
 * The bar that appears while the shelf is in Select mode: how many books are
 * ticked, and what can be done with them.
 *
 * It owns the "Add to collection" menu too — the collection tree, indented by
 * depth — because that menu belongs to this bar and to nothing else. The bar's
 * material and its phone rules are `nostos-selection-bar`'s; the caller supplies
 * the buttons, exactly as the Brain does.
 */
@Component({
  selector: 'app-library-selection-bar',
  standalone: true,
  imports: [SelectionBarComponent, ButtonComponent, NostosIconComponent, ...NOSTOS_MENU],
  templateUrl: './library-selection-bar.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LibrarySelectionBar {
  readonly count = input.required<number>();
  /** Every collection in tree order with its depth, for the "Add to collection" menu. */
  readonly collectionChoices = input.required<readonly FlatTreeNode<Collection>[]>();
  /** True while the shelf is filtered to one collection, which can then be removed. */
  readonly inCollection = input(false);

  readonly closed = output<void>();
  readonly addToCollection = output<string>();
  readonly removeFromCollection = output<void>();
  readonly markFinished = output<void>();
  readonly deleteRequested = output<void>();
}
