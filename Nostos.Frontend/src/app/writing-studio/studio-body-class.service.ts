import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, inject } from '@angular/core';

const ZEN_BODY_CLASS = 'nostos-zen';
const TYPING_BODY_CLASS = 'nostos-studio-typing';

/**
 * Coordinates the Studio's global `document.body` layout classes (`nostos-zen`
 * and `nostos-studio-typing`) needed by app-shell rules (dock / assistant)
 * and guarantees cleanup on component teardown via `DestroyRef`.
 */
@Injectable()
export class StudioBodyClassService {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    this.destroyRef.onDestroy(() => this.clearAll());
  }

  setZen(active: boolean): void {
    this.document.body.classList.toggle(ZEN_BODY_CLASS, active);
  }

  setTyping(active: boolean): void {
    this.document.body.classList.toggle(TYPING_BODY_CLASS, active);
  }

  clearAll(): void {
    this.document.body.classList.remove(ZEN_BODY_CLASS, TYPING_BODY_CLASS);
  }
}
