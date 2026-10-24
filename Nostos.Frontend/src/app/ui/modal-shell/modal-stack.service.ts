import { Injectable } from '@angular/core';

/**
 * The layers that answer Escape, in the order they opened.
 *
 * Every dialog, sheet and palette listens for Escape on the document, so with
 * two open at once a single press used to close both. A layer registers while
 * it is open; Escape belongs to the top one only. A layer that is not on top
 * leaves the key alone, and the top layer consumes it (preventDefault and
 * stopPropagation) so nothing behind it reacts either.
 */
@Injectable({ providedIn: 'root' })
export class ModalStack {
  private readonly layers: object[] = [];

  /** Puts the layer on top. Returns the call that takes it off again. */
  register(layer: object): () => void {
    this.unregister(layer);
    this.layers.push(layer);
    return () => this.unregister(layer);
  }

  unregister(layer: object): void {
    const index = this.layers.indexOf(layer);
    if (index >= 0) this.layers.splice(index, 1);
  }

  isTop(layer: object): boolean {
    return this.layers.length > 0 && this.layers[this.layers.length - 1] === layer;
  }

  /** True while any layer is open: surfaces behind them should not claim Escape. */
  hasOpen(): boolean {
    return this.layers.length > 0;
  }

  /**
   * The shared Escape rule. Returns true when `layer` should close for this
   * event, after claiming it. A key a focused control already used
   * (`defaultPrevented`, e.g. a dropdown closing its list) closes nothing.
   */
  claimEscape(layer: object, event: Event): boolean {
    if (event.defaultPrevented || !this.isTop(layer)) return false;
    event.preventDefault();
    event.stopPropagation();
    return true;
  }
}
