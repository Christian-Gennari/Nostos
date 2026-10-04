import { Injectable, signal } from '@angular/core';

/**
 * Shared open/closed state for the narrow-viewport More sheet.
 *
 * The dock owns the trigger but not the overlay: a fixed-position sheet rendered
 * inside the dock's own fixed stacking context could not rise above the
 * assistant's layer, so the sheet lives beside the dock in the workspace layout.
 * This tiny service is the one hand-off between the two.
 */
@Injectable({ providedIn: 'root' })
export class UtilitySheetService {
  readonly open = signal(false);

  /**
   * Whether the shell behind the sheet is inert. The sheet sets this only after
   * its focus trap has captured the More trigger, so applying inertness can
   * never steal that capture; `close()` clears it before `open`, so the
   * background is interactive again by the time the trap restores focus.
   */
  readonly backgroundInert = signal(false);

  toggle(): void {
    if (this.open()) this.close();
    else this.open.set(true);
  }

  close(): void {
    this.backgroundInert.set(false);
    this.open.set(false);
  }
}
