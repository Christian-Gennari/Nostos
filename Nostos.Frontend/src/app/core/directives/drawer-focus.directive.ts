import { DOCUMENT } from '@angular/common';
import { DestroyRef, Directive, ElementRef, effect, inject, input, output, untracked } from '@angular/core';

import { PHONE_QUERY } from '../../ui/breakpoints';
import { ModalStack } from '../../ui/modal-shell/modal-stack.service';

/**
 * The focus and Escape contract of a phone drawer, in one place. It is the
 * contract the reader's panels keep (remember the opener, move focus into the
 * panel, give it back on close, Escape closes the top layer only), lifted so
 * the Library's collections and the Brain's index do not each rewrite it.
 *
 *   <div [appDrawerFocus]="open()" drawerPanel=".sidebar" (drawerDismiss)="close()">
 *
 * - Opening (after the first render) remembers the focused control and moves
 *   focus to the current place in the panel, or its first control.
 * - Closing gives focus back to that control.
 * - Escape closes the drawer, unless a control already used the key
 *   (`defaultPrevented`) or a dialog is open above the drawer.
 *
 * It only acts while `drawerQuery` matches: on a wide screen the same sidebar
 * is a column, not a drawer, and must not steal focus or answer Escape.
 */
@Directive({
  selector: '[appDrawerFocus]',
  standalone: true,
  host: {
    '(document:keydown.escape)': 'onEscape($event)',
  },
})
export class DrawerFocusDirective {
  readonly open = input.required<boolean>({ alias: 'appDrawerFocus' });
  /** Selector, inside the host, of the drawer's panel. */
  readonly drawerPanel = input.required<string>();
  /** The viewport in which the panel is a drawer. */
  readonly drawerQuery = input(PHONE_QUERY);
  readonly drawerDismiss = output<void>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly document = inject(DOCUMENT);
  private readonly stack = inject(ModalStack);
  private opener: HTMLElement | null = null;
  private wasOpen: boolean | null = null;
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    effect(() => {
      const open = this.open();
      untracked(() => this.onOpenChange(open));
    });
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  private isDrawer(): boolean {
    const view = this.document.defaultView;
    return !!view?.matchMedia?.(this.drawerQuery()).matches;
  }

  private onOpenChange(open: boolean): void {
    const previous = this.wasOpen;
    this.wasOpen = open;
    // The first value is the state the page loaded in, not something the user did.
    if (previous === null || previous === open || !this.isDrawer()) return;

    clearTimeout(this.timer);
    if (open) {
      const active = this.document.activeElement;
      this.opener = active instanceof HTMLElement && active !== this.document.body ? active : null;
      this.timer = setTimeout(() => this.focusPanel());
    } else {
      const opener = this.opener;
      this.opener = null;
      // After the render that shows the opener again (the Library hides it while open).
      this.timer = setTimeout(() => {
        if (opener?.isConnected) opener.focus({ preventScroll: true });
      });
    }
  }

  private focusPanel(): void {
    const panel = this.host.nativeElement.querySelector<HTMLElement>(this.drawerPanel());
    if (!panel) return;
    const target =
      panel.querySelector<HTMLElement>('[aria-current="page"], [aria-current="true"], .active') ??
      panel.querySelector<HTMLElement>('a[href], button:not([disabled]), input:not([disabled])');
    (target ?? panel).focus({ preventScroll: true });
  }

  protected onEscape(event: Event): void {
    if (!this.open() || !this.isDrawer() || event.defaultPrevented || this.stack.hasOpen()) return;
    event.preventDefault();
    event.stopPropagation();
    this.drawerDismiss.emit();
  }
}
