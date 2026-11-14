import { DestroyRef, Injectable, inject } from '@angular/core';

/**
 * Every deferred callback the reader starts goes through here.
 *
 * The shell used to start focus, scroll and notice timers with a bare
 * `setTimeout`, and a handle it did not keep could not be cleared: a focus
 * callback could run after the reader was gone and reach into a dead view
 * (see issue #1607). One registry, owned by the shell, makes leaving the reader
 * cancel everything it started.
 *
 * Provided at shell scope: it lives exactly as long as the reader does.
 */
@Injectable()
export class ReaderTimers {
  private readonly destroyRef = inject(DestroyRef);
  private readonly pending = new Set<ReturnType<typeof setTimeout>>();

  constructor() {
    this.destroyRef.onDestroy(() => this.clearAll());
  }

  /**
   * Runs `callback` after `ms`, unless the reader is destroyed first. The
   * returned handle can be passed to `clear` when the work is superseded.
   */
  after(ms: number, callback: () => void): ReturnType<typeof setTimeout> {
    const handle = setTimeout(() => {
      this.pending.delete(handle);
      callback();
    }, ms);
    this.pending.add(handle);
    return handle;
  }

  /** Cancels one callback; a null handle is nothing to do. */
  clear(handle: ReturnType<typeof setTimeout> | null | undefined): void {
    if (handle == null) return;
    clearTimeout(handle);
    this.pending.delete(handle);
  }

  /** How many callbacks are still waiting. */
  get size(): number {
    return this.pending.size;
  }

  private clearAll(): void {
    for (const handle of this.pending) clearTimeout(handle);
    this.pending.clear();
  }
}
