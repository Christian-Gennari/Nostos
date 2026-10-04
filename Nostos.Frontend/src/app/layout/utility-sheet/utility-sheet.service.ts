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

  toggle(): void {
    this.open.update((open) => !open);
  }

  close(): void {
    this.open.set(false);
  }
}
