/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import type { ConnectedPosition } from '@angular/cdk/overlay';
import {
  ChangeDetectionStrategy,
  Component,
  Directive,
  ViewEncapsulation,
  inject,
  input,
} from '@angular/core';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import type { NostosIconName } from '../icon/nostos-icons';

export type MenuItemTone = 'default' | 'danger';

/**
 * Canonical action menu: the place for a row's secondary actions, so the row
 * itself can show one.
 *
 * Behaviour is the CDK's menu (overlay, arrow keys, type-ahead, Escape, focus
 * return, `role="menu"`); this file only gives it the Nostos surface and a
 * single import for call sites:
 *
 *   <button appIconButton icon="dots-three" aria-label="More"
 *           [nostosMenuTriggerFor]="more"></button>
 *   <ng-template #more>
 *     <nostos-menu>
 *       <button nostosMenuItem icon="pencil-simple" (triggered)="edit()">Edit</button>
 *       <hr nostosMenuSeparator />
 *       <button nostosMenuItem icon="trash" tone="danger" (triggered)="remove()">Remove</button>
 *     </nostos-menu>
 *   </ng-template>
 *
 * A single-choice value belongs in `app-dropdown`, not here.
 */
@Component({
  selector: 'nostos-menu',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The menu renders in the CDK overlay, outside any host element.
  encapsulation: ViewEncapsulation.None,
  hostDirectives: [CdkMenu],
  host: { class: 'nostos-menu' },
  template: `<ng-content />`,
  styleUrl: './menu.component.css',
})
export class MenuComponent {}

@Component({
  selector: 'button[nostosMenuItem]',
  standalone: true,
  imports: [NostosIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  hostDirectives: [
    {
      directive: CdkMenuItem,
      inputs: ['cdkMenuItemDisabled: disabled'],
      outputs: ['cdkMenuItemTriggered: triggered'],
    },
  ],
  host: {
    class: 'nostos-menu-item',
    type: 'button',
    '[class.nostos-menu-item--danger]': "tone() === 'danger'",
  },
  template: `
    @if (icon(); as name) {
      <nostos-icon [name]="name" [size]="16" />
    }
    <span class="nostos-menu-item__label"><ng-content /></span>
  `,
})
export class MenuItemComponent {
  readonly icon = input<NostosIconName | null>(null);
  readonly tone = input<MenuItemTone>('default');
}

@Directive({
  selector: 'hr[nostosMenuSeparator]',
  standalone: true,
  host: { class: 'nostos-menu-separator', role: 'separator' },
})
export class MenuSeparatorDirective {}

/** Opens a `nostos-menu` template from any native button. */
@Directive({
  selector: '[nostosMenuTriggerFor]',
  standalone: true,
  hostDirectives: [
    {
      directive: CdkMenuTrigger,
      inputs: ['cdkMenuTriggerFor: nostosMenuTriggerFor'],
      outputs: ['cdkMenuOpened: menuOpened', 'cdkMenuClosed: menuClosed'],
    },
  ],
})
export class MenuTriggerDirective {
  constructor() {
    // A "more" button sits at the trailing edge of its row, so the menu hangs
    // from that edge and opens inward. The CDK falls through the list when a
    // position would leave the viewport.
    inject(CdkMenuTrigger).menuPosition = MENU_POSITIONS;
  }
}

const MENU_POSITIONS: ConnectedPosition[] = [
  { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 4 },
  { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom', offsetY: -4 },
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 4 },
  { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -4 },
];

/** Everything a template needs to declare and open a menu. */
export const NOSTOS_MENU = [
  MenuComponent,
  MenuItemComponent,
  MenuSeparatorDirective,
  MenuTriggerDirective,
] as const;
