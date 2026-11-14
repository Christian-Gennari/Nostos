import { Injectable, inject } from '@angular/core';
import { ReaderTimers } from './reader-timers';

/**
 * Focus bookkeeping for the reader's overlays.
 *
 * A drawer or popover takes focus when it opens and gives it back to whatever
 * held it when it closes. Both moves are deferred by a turn, so they used to be
 * untracked `setTimeout` calls inside the shell (issue #1607); they now go
 * through {@link ReaderTimers} and cannot fire after the reader is gone.
 *
 * Provided at shell scope: one return target for the reader's overlays.
 */
@Injectable()
export class ReaderOverlayFocus {
  private readonly timers = inject(ReaderTimers);
  private returnFocus: HTMLElement | null = null;

  /** Remembers what had focus before an overlay opened. */
  remember(): void {
    const active = typeof document !== 'undefined' ? document.activeElement : null;
    this.returnFocus = active instanceof HTMLElement ? active : null;
  }

  /** Moves focus into the overlay after its first render. */
  focus(host: HTMLElement, selector: string): void {
    this.timers.after(0, () => host.querySelector<HTMLElement>(selector)?.focus());
  }

  /** Gives focus back to the remembered control, if it is still on the page. */
  restore(): void {
    const target = this.returnFocus;
    this.returnFocus = null;
    if (target?.isConnected) this.timers.after(0, () => target.focus());
  }

  /** Drops the return target: the overlay closed by another route. */
  forget(): void {
    this.returnFocus = null;
  }
}
