/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { DropdownOption } from '../../ui/dropdown/dropdown.component';
import { DropdownComponent } from '../../ui/dropdown/dropdown.component';
import { ButtonComponent } from '../../ui/button/button.component';
import { ChipComponent } from '../../ui/chip/chip.component';
import { InputDirective } from '../../ui/form-control/form-control.directive';
import { SearchFieldComponent } from '../../ui/search-field/search-field.component';
import { ViewToggleComponent, type ViewToggleOption } from '../../ui/view-toggle/view-toggle.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';

/** One clearable active filter, as the chip row renders it. */
export interface LibraryFilterChip {
  /** Which filter the chip clears: status, format, collection or search. */
  readonly key: string;
  readonly label: string;
}

/**
 * The shelf's toolbar and its active-filter strip: heading, search, sort, view,
 * select mode, Add book, and the chips that name the shelf on screen.
 *
 * WHY THIS EXISTS. It is presentational only — every value in and every intent out
 * — so the shelf's 1,400-line component stops owning the toolbar's markup and half
 * its stylesheet. The one thing it deliberately does NOT own is the sticky
 * positioning rules: the header must keep sticking to the scroller for the whole
 * page, and a host box would become the sticky containing block and unstick it as
 * soon as the toolbar's own height scrolled past. `:host` is therefore
 * `display: contents`, which keeps the header and the chip strip direct children of
 * the results column exactly as they were before the split.
 *
 * The phone drawer opener lives here too, because it is the row's leading control;
 * `scopeLabel` is the shelf's name that the chip row carries at touch widths.
 */
@Component({
  selector: 'app-library-toolbar',
  standalone: true,
  imports: [
    SearchFieldComponent,
    InputDirective,
    DropdownComponent,
    ButtonComponent,
    ChipComponent,
    ViewToggleComponent,
    NostosIconComponent,
  ],
  templateUrl: './library-toolbar.component.html',
  styleUrls: ['./library-toolbar.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'library-toolbar' },
})
export class LibraryToolbar {
  readonly searchQuery = input.required<string>();
  readonly activeSort = input.required<string>();
  readonly sortOptions = input.required<readonly DropdownOption[]>();
  readonly viewMode = input.required<string>();
  readonly viewToggleOptions = input.required<readonly ViewToggleOption[]>();
  readonly selecting = input(false);
  readonly sidebarExpanded = input(false);
  /** The shelf on screen ("Favourites · Poetry"), for the phone's chip row. */
  readonly scopeLabel = input('All books');
  readonly chips = input.required<readonly LibraryFilterChip[]>();

  /** The field's `input` event, debounced by the shelf. */
  readonly search = output<Event>();
  readonly sortChanged = output<string>();
  readonly viewModeChanged = output<string>();
  readonly sidebarToggled = output<void>();
  readonly selectingToggled = output<void>();
  readonly addBook = output<void>();
  readonly chipCleared = output<string>();
}
