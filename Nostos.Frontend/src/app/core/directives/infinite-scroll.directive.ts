/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { booleanAttribute, Directive, effect, ElementRef, inject, input, output } from '@angular/core';

/** Whether this browser can watch the list end at all. */
export function canObserveIntersections(): boolean {
  return typeof IntersectionObserver !== 'undefined';
}

/**
 * The paging sentinel: emits `reached` when the host (the end of a list)
 * comes within `rootMargin` of `root` (the viewport by default). Library and
 * Brain page through it, by way of `nostos-paging-status` (issue #1202).
 *
 * While `paused` is true (a page is loading, or the last one failed) nothing
 * is emitted. When it turns false the sentinel is observed afresh, so a list
 * end that is still in view after a short page asks for the next page at once
 * instead of waiting for a scroll that may never come.
 */
@Directive({
  selector: '[appInfiniteScroll]',
  standalone: true,
})
export class InfiniteScrollDirective {
  /** The scrolling element; null watches the viewport. */
  readonly root = input<Element | null>(null);
  /** How far ahead of the list end to ask for the next page. */
  readonly rootMargin = input('100px');
  readonly paused = input(false, { transform: booleanAttribute });

  readonly reached = output<void>();

  private readonly el = inject<ElementRef<HTMLElement>>(ElementRef);

  constructor() {
    effect((onCleanup) => {
      if (this.paused() || !canObserveIntersections()) return;
      const observer = new IntersectionObserver(
        (entries) => {
          if (entries.some((entry) => entry.isIntersecting)) this.reached.emit();
        },
        { root: this.root(), rootMargin: this.rootMargin(), threshold: 0 },
      );
      observer.observe(this.el.nativeElement);
      onCleanup(() => observer.disconnect());
    });
  }
}
