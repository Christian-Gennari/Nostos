/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import {
  CdkMenu,
  CdkMenuGroup,
  CdkMenuItem,
  CdkMenuItemRadio,
  CdkMenuTrigger,
  MENU_SCROLL_STRATEGY,
} from '@angular/cdk/menu';
import { Overlay, type ConnectedPosition, type OverlayRef, type ScrollStrategy } from '@angular/cdk/overlay';
import {
  ChangeDetectionStrategy,
  Component,
  Directive,
  ElementRef,
  Injector,
  NgZone,
  OnChanges,
  ViewEncapsulation,
  inject,
  input,
} from '@angular/core';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import type { NostosIconName } from '../icon/nostos-icons';
import { isPhoneViewport } from '../breakpoints';

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
 *
 * At phone width (`PHONE_QUERY`) the menu opens as a bottom sheet instead of
 * hanging from its trigger: the same surface as the settings panel's sheet
 * (`.nostos-surface` inside a `.nostos-sheet-pane`, one keyframe, one grab
 * handle). The trigger decides when it opens, so a resize while open leaves it
 * as it was. A row that navigates is `<a nostosMenuItem href>` or a button that
 * calls the router; either closes the menu.
 */
@Component({
  selector: 'nostos-menu',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The menu renders in the CDK overlay, outside any host element.
  encapsulation: ViewEncapsulation.None,
  hostDirectives: [CdkMenu],
  host: { class: 'nostos-menu nostos-surface' },
  template: `<ng-content />`,
  styleUrl: './menu.component.css',
})
export class MenuComponent {}

@Component({
  selector: 'button[nostosMenuItem], a[nostosMenuItem]',
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
    '[attr.type]': 'isLink ? null : "button"',
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
  /** A link row (`<a nostosMenuItem href>`) keeps its own navigation. */
  protected readonly isLink = inject(ElementRef).nativeElement.tagName === 'A';
  readonly icon = input<NostosIconName | null>(null);
  readonly tone = input<MenuItemTone>('default');
  /**
   * A second, quieter line under the label, for a menu whose labels alone do
   * not say what each choice is for ("Editions": group or split editions).
   */
  readonly hint = input<string | null>(null);
}

/**
 * One choice of several (a voice, a speed): `role="menuitemradio"` with
 * `aria-checked`, and a check in a column every item keeps, so the labels
 * line up whichever one is chosen. Wrap a set in `nostosMenuGroup`.
 *
 *   <div nostosMenuGroup>
 *     @for (rate of rates; track rate) {
 *       <button nostosMenuItemRadio [checked]="rate === current()"
 *               (triggered)="choose(rate)">{{ rate }}×</button>
 *     }
 *   </div>
 */
@Component({
  selector: 'button[nostosMenuItemRadio]',
  standalone: true,
  imports: [NostosIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  hostDirectives: [
    {
      directive: CdkMenuItemRadio,
      inputs: ['cdkMenuItemDisabled: disabled'],
      outputs: ['cdkMenuItemTriggered: triggered'],
    },
  ],
  host: {
    class: 'nostos-menu-item nostos-menu-item--radio',
    type: 'button',
    '[class.nostos-menu-item--checked]': 'checked()',
    '[class.nostos-menu-item--with-hint]': '!!hint()',
  },
  template: `
    <span class="nostos-menu-item__check" aria-hidden="true">
      @if (checked()) {
        <nostos-icon name="check" [size]="16" />
      }
    </span>
    <span class="nostos-menu-item__label"><ng-content /></span>
    @if (hint(); as text) {
      <span class="nostos-menu-item__hint">{{ text }}</span>
    }
  `,
})
export class MenuItemRadioComponent implements OnChanges {
  readonly checked = input(false);
  readonly hint = input<string | null>(null);
  private readonly radio = inject(CdkMenuItemRadio, { self: true });

  /** The caller's state is the truth; the CDK only reflects it as aria-checked. */
  ngOnChanges(): void {
    this.radio.checked = this.checked();
  }
}

/** A set of `nostosMenuItemRadio` choices (`role="group"`). */
@Directive({
  selector: '[nostosMenuGroup]',
  standalone: true,
  hostDirectives: [CdkMenuGroup],
  host: { class: 'nostos-menu-group' },
})
export class MenuGroupDirective {}

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
    const trigger = inject(CdkMenuTrigger);
    trigger.menuPosition = MENU_POSITIONS;
    // The overlay exists only once the CDK has attached it, which is right
    // after `opened` fires; a microtask later it is there, before the first
    // paint. Every open decides again, because the overlay is kept between
    // opens and the viewport may have changed.
    const overlay = inject(Overlay);
    trigger.opened.subscribe(() =>
      queueMicrotask(() => {
        const ref: OverlayRef | null = trigger['overlayRef'];
        if (!ref) return;
        const sheet = isPhoneViewport();
        ref.updatePositionStrategy(
          sheet ? overlay.position().global().bottom('0').centerHorizontally() : trigger['_getOverlayPositionStrategy'](),
        );
        if (sheet) ref.addPanelClass(SHEET_PANE);
        else ref.removePanelClass(SHEET_PANE);
      }),
    );
  }
}

/** The overlay pane of a menu opened as a bottom sheet (shared with the settings panel). */
const SHEET_PANE = 'nostos-sheet-pane';

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
  MenuItemRadioComponent,
  MenuGroupDirective,
  MenuSeparatorDirective,
  MenuTriggerDirective,
] as const;
