/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { BreakpointObserver } from '@angular/cdk/layout';
import {
  Overlay,
  type ConnectedPosition,
  type OverlayRef,
  type PositionStrategy,
} from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  TemplateRef,
  ViewContainerRef,
  ViewEncapsulation,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { PHONE_QUERY } from '../breakpoints';

/** Below this width the panel is a sheet along the bottom edge, not a popover. */
export const SETTINGS_POPOVER_SHEET_QUERY = PHONE_QUERY;

export type SettingsPopoverDismissal = 'escape' | 'outside';

/** Under its button, hanging from the button's trailing edge; flipped when there is no room. */
const POSITIONS: ConnectedPosition[] = [
  { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 6 },
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 6 },
  { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom', offsetY: -6 },
];

const FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select, textarea, [tabindex]:not([tabindex="-1"])';

/**
 * The settings panel a toolbar button opens: the reader's View settings, the
 * Studio's Page settings. One surface, one width (`--settings-popover-width`),
 * one set of rules, on the CDK overlay:
 *
 * - it hangs under its button, and moves with it on scroll and resize;
 * - at phone width (768px) it is a sheet along the bottom edge, full width;
 * - Escape and a press outside it say so through `dismissed`, and the caller
 *   closes it (a press on its own button is that button's toggle, not outside);
 * - focus moves in when it opens, and back to the button when it closes with
 *   focus still inside it. After a press elsewhere focus stays where it went.
 *
 * It does not dim or cover the page: a setting is judged on the page itself.
 * It exists while it is open, so the caller wraps it in `@if`:
 *
 *   @if (open()) {
 *     <nostos-settings-popover [origin]="button" label="View settings"
 *                              (dismissed)="open.set(false)">
 *       …rows…
 *     </nostos-settings-popover>
 *   }
 *
 * A layer inside it that handles Escape itself (a sub-view's "back") calls
 * `preventDefault()`, and the panel leaves that Escape alone.
 */
@Component({
  selector: 'nostos-settings-popover',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The panel renders in the overlay container, outside this host.
  encapsulation: ViewEncapsulation.None,
  template: `
    <ng-template #panel>
      <div
        class="nostos-settings-popover-panel"
        role="dialog"
        aria-modal="false"
        [attr.aria-label]="label()"
        [attr.aria-labelledby]="labelledBy()"
        [attr.data-testid]="testId()"
      >
        <ng-content />
      </div>
    </ng-template>
  `,
  styleUrl: './settings-popover.component.css',
})
export class SettingsPopoverComponent implements AfterViewInit, OnDestroy {
  /** The button that opens it: the panel hangs from it and focus returns to it. */
  readonly origin = input.required<HTMLElement | null>();
  readonly label = input<string | null>(null);
  readonly labelledBy = input<string | null>(null);
  readonly testId = input<string | null>(null);
  /**
   * What takes focus on opening: a selector inside the panel, `true` for its
   * first control, or `false` when the caller places focus itself.
   */
  readonly initialFocus = input<string | boolean>(true);
  /** Whether focus goes back to `origin` when the panel closes with focus inside it. */
  readonly restoreFocus = input(true);

  readonly dismissed = output<SettingsPopoverDismissal>();

  private readonly overlay = inject(Overlay);
  private readonly breakpoints = inject(BreakpointObserver);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly panel = viewChild.required<TemplateRef<unknown>>('panel');
  private overlayRef: OverlayRef | null = null;
  private readonly subscriptions = new Subscription();
  /**
   * Whether focus is in the panel. Tracked as it moves, because by the time
   * the panel is destroyed its content has already left the page, and focus
   * with it.
   */
  private focusInside = false;
  private readonly onFocusIn = (event: FocusEvent) => {
    this.focusInside = !!this.overlayRef?.overlayElement.contains(event.target as Node);
  };
  private readonly onFocusOut = (event: FocusEvent) => {
    if (event.relatedTarget) return; // focusin says where it went
    // Focus left for nothing (a press on the page): it is not coming back
    // here. Browsers also send this as a focused control is removed with the
    // panel, before it leaves the page, so it is judged a moment later: a
    // control that is gone did not move focus away.
    const target = event.target as Node;
    queueMicrotask(() => {
      if (target.isConnected && !this.overlayRef?.overlayElement.contains(document.activeElement)) {
        this.focusInside = false;
      }
    });
  };

  ngAfterViewInit(): void {
    const sheet = this.breakpoints.isMatched(SETTINGS_POPOVER_SHEET_QUERY);
    const ref = this.overlay.create({
      positionStrategy: this.positionFor(sheet),
      scrollStrategy: this.overlay.scrollStrategies.reposition(),
      panelClass: this.paneClasses(sheet),
    });
    this.overlayRef = ref;
    ref.attach(new TemplatePortal(this.panel(), this.viewContainerRef));
    document.addEventListener('focusin', this.onFocusIn, true);
    ref.overlayElement.addEventListener('focusout', this.onFocusOut);

    this.subscriptions.add(
      this.breakpoints.observe(SETTINGS_POPOVER_SHEET_QUERY).subscribe(({ matches }) => {
        ref.updatePositionStrategy(this.positionFor(matches));
        ref.removePanelClass(this.paneClasses(!matches));
        ref.addPanelClass(this.paneClasses(matches));
      }),
    );
    this.subscriptions.add(
      ref.keydownEvents().subscribe((event) => {
        if (event.key !== 'Escape' || event.defaultPrevented) return;
        event.preventDefault();
        this.dismissed.emit('escape');
      }),
    );
    this.subscriptions.add(
      ref.outsidePointerEvents().subscribe((event) => {
        const target = event.target as Node | null;
        // The button is its own toggle; it must not close and reopen the panel.
        if (target && this.origin()?.contains(target)) return;
        this.dismissed.emit('outside');
      }),
    );

    const focus = this.initialFocus();
    if (focus !== false) {
      const selector = typeof focus === 'string' ? focus : FOCUSABLE;
      queueMicrotask(() => ref.overlayElement.querySelector<HTMLElement>(selector)?.focus());
    }
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
    const ref = this.overlayRef;
    this.overlayRef = null;
    if (!ref) return;
    document.removeEventListener('focusin', this.onFocusIn, true);
    ref.overlayElement.removeEventListener('focusout', this.onFocusOut);
    const focusInside = this.focusInside || ref.overlayElement.contains(document.activeElement);
    ref.dispose();
    const origin = this.origin();
    if (focusInside && this.restoreFocus() && origin?.isConnected) origin.focus();
  }

  private positionFor(sheet: boolean): PositionStrategy {
    if (sheet) return this.overlay.position().global().bottom('0').centerHorizontally();
    const origin = this.origin();
    if (!origin) return this.overlay.position().global().top('64px').right('12px');
    return this.overlay
      .position()
      .flexibleConnectedTo(origin)
      .withPositions(POSITIONS)
      .withFlexibleDimensions(true)
      .withGrowAfterOpen(true)
      .withPush(false)
      .withViewportMargin(8);
  }

  private paneClasses(sheet: boolean): string[] {
    return sheet
      ? ['nostos-settings-popover-pane', 'nostos-settings-popover-pane--sheet']
      : ['nostos-settings-popover-pane', 'nostos-settings-popover-pane--popover'];
  }
}
