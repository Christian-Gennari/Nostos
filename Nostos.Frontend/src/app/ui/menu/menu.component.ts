/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
import { CdkMenu, CdkMenuItem, CdkMenuTrigger, MENU_SCROLL_STRATEGY } from '@angular/cdk/menu';
import type { ConnectedPosition, OverlayRef, ScrollStrategy } from '@angular/cdk/overlay';
import {
  ChangeDetectionStrategy,
  Component,
  Directive,
  ElementRef,
  Injector,
  NgZone,
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
 *       <button nostosMenuItem icon="stack" hint="Group or split editions"
 *               (triggered)="editions()">Editions</button>
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
    '[class.nostos-menu-item--with-hint]': '!!hint()',
  },
  template: `
    @if (icon(); as name) {
      <nostos-icon [name]="name" [size]="16" />
    }
    <span class="nostos-menu-item__label"><ng-content /></span>
    @if (hint(); as text) {
      <span class="nostos-menu-item__hint">{{ text }}</span>
    }
  `,
})
export class MenuItemComponent {
  readonly icon = input<NostosIconName | null>(null);
  readonly tone = input<MenuItemTone>('default');
  /**
   * A second, quieter line under the label, for a menu whose labels alone do
   * not say what each choice is for ("Editions": group or split editions).
   */
  readonly hint = input<string | null>(null);
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
  providers: [
    {
      provide: MENU_SCROLL_STRATEGY,
      useFactory: () => {
        const host: HTMLElement = inject(ElementRef).nativeElement;
        const zone = inject(NgZone);
        const injector = inject(Injector);
        // Resolved lazily: the trigger is what injects this token.
        const close = () => zone.run(() => injector.get(CdkMenuTrigger).close());
        return () => new FollowAnyScrollStrategy(host, zone, close);
      },
    },
  ],
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

/**
 * Keeps an open menu attached to its trigger through any scroll.
 *
 * The CDK's own reposition strategy only hears the window and containers
 * registered as `cdkScrollable`. Pages here scroll inside their own container
 * (`.layout-content` on book detail), so the menu stayed pinned to the viewport
 * while its trigger scrolled away. A capturing listener on the document hears
 * every scroll, whichever element owns it. Once the trigger leaves the viewport
 * the menu closes rather than hanging from nothing.
 */
class FollowAnyScrollStrategy implements ScrollStrategy {
  private overlayRef: OverlayRef | null = null;
  private frame = 0;

  constructor(
    private readonly trigger: HTMLElement,
    private readonly zone: NgZone,
    private readonly close: () => void,
  ) {}

  attach(overlayRef: OverlayRef): void {
    this.overlayRef = overlayRef;
  }

  enable(): void {
    this.zone.runOutsideAngular(() =>
      document.addEventListener('scroll', this.onScroll, { capture: true, passive: true }),
    );
  }

  disable(): void {
    document.removeEventListener('scroll', this.onScroll, { capture: true });
    cancelAnimationFrame(this.frame);
    this.frame = 0;
  }

  detach(): void {
    this.disable();
    this.overlayRef = null;
  }

  private readonly onScroll = (event: Event): void => {
    // Scrolling a long menu's own list is not the page moving under it.
    const pane = this.overlayRef?.overlayElement;
    if (pane && event.target instanceof Node && pane.contains(event.target)) return;
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      if (!this.overlayRef) return;
      const rect = this.trigger.getBoundingClientRect();
      if (rect.bottom < 0 || rect.top > window.innerHeight) {
        this.close();
        return;
      }
      this.overlayRef.updatePosition();
    });
  };
}

/** Everything a template needs to declare and open a menu. */
export const NOSTOS_MENU = [
  MenuComponent,
  MenuItemComponent,
  MenuSeparatorDirective,
  MenuTriggerDirective,
] as const;
