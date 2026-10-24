import { DOCUMENT } from '@angular/common';
import {
  DestroyRef,
  Directive,
  Injectable,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';

const LOCK_CLASS = 'nostos-scroll-locked';

/**
 * Stops the page behind an open overlay from scrolling.
 *
 * Several overlays can be open at once (a confirmation over a form over a
 * drawer), so the lock is counted: the first `acquire()` freezes the document,
 * the last release lets it go. The lock is a class on `<html>` (see
 * `styles.css`); the width of the scrollbar it removes is kept as padding so
 * the page does not jump sideways on a desktop.
 */
@Injectable({ providedIn: 'root' })
export class ScrollLock {
  private readonly document = inject(DOCUMENT);
  private count = 0;

  /** Takes one hold on the lock. Call the returned function once to give it back. */
  acquire(): () => void {
    this.count += 1;
    if (this.count === 1) this.apply();
    let released = false;
    return () => {
      if (released) return;
      released = true;
      this.count -= 1;
      if (this.count === 0) this.clear();
    };
  }

  /** Open holds. For tests. */
  get holds(): number {
    return this.count;
  }

  private apply(): void {
    const root = this.document.documentElement;
    const view = this.document.defaultView;
    const gutter = view ? Math.max(0, view.innerWidth - root.clientWidth) : 0;
    if (gutter > 0) root.style.setProperty('--scroll-lock-gutter', `${gutter}px`);
    root.classList.add(LOCK_CLASS);
  }

  private clear(): void {
    const root = this.document.documentElement;
    root.classList.remove(LOCK_CLASS);
    root.style.removeProperty('--scroll-lock-gutter');
  }
}

/**
 * Holds the scroll lock for as long as `active()` is true, and gives it up when
 * the owner is destroyed. With `query`, only while that media query matches
 * (a drawer on a phone is an overlay; the same sidebar on a desktop is a column).
 * Call it from a constructor or field initialiser.
 */
export function lockScrollWhile(active: () => boolean, query?: string): void {
  const lock = inject(ScrollLock);
  const view = inject(DOCUMENT).defaultView;
  const matches = signal(true);

  let list: MediaQueryList | null = null;
  const onChange = (event: MediaQueryListEvent) => matches.set(event.matches);
  if (query && view && typeof view.matchMedia === 'function') {
    list = view.matchMedia(query);
    matches.set(list.matches);
    list.addEventListener?.('change', onChange);
  }

  let release: (() => void) | null = null;
  effect(() => {
    const want = active() && matches();
    untracked(() => {
      if (want && !release) release = lock.acquire();
      else if (!want && release) {
        release();
        release = null;
      }
    });
  });
  inject(DestroyRef).onDestroy(() => {
    list?.removeEventListener?.('change', onChange);
    release?.();
    release = null;
  });
}

/** Template and host-directive form: `[appScrollLock]="open()"`. */
@Directive({ selector: '[appScrollLock]', standalone: true })
export class ScrollLockDirective {
  readonly active = input<boolean>(false, { alias: 'appScrollLock' });

  constructor() {
    lockScrollWhile(() => this.active());
  }
}
