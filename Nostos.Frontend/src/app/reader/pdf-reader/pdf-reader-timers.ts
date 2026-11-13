/**
 * Tracks all deferred timers, animation frames, and generation counters for
 * `PdfReader` so that a book switch (`bookId` change) or component teardown
 * cancels every pending callback immediately with no leaked work.
 */
export class PdfReaderTimers {
  private readonly namedTimeouts = new Map<string, ReturnType<typeof setTimeout>>();
  private readonly pooledTimeouts = new Set<ReturnType<typeof setTimeout>>();
  private readonly animationFrames = new Map<string, number>();
  private generation = 0;

  /** Monotonic generation token, bumped on every `cancelAll()`. */
  currentGeneration(): number {
    return this.generation;
  }

  /** Advance the generation token without cancelling timers. */
  bumpGeneration(): number {
    return ++this.generation;
  }

  /** True when `generation` still matches the active book/lifecycle generation. */
  isCurrent(generation: number): boolean {
    return generation === this.generation;
  }

  /**
   * Schedule a named timeout, cancelling any previously pending timeout in the
   * same slot first. The slot is cleared before `fn` runs so recursive retries
   * in the same slot can schedule cleanly.
   */
  replaceTimeout(slot: string, fn: () => void, delayMs: number): ReturnType<typeof setTimeout> {
    this.clearTimeout(slot);
    const generation = this.generation;
    const handle = setTimeout(() => {
      if (this.namedTimeouts.get(slot) === handle) {
        this.namedTimeouts.delete(slot);
      }
      if (this.generation !== generation) return;
      fn();
    }, delayMs);
    this.namedTimeouts.set(slot, handle);
    return handle;
  }

  /** Cancel a named timeout slot if one is active. */
  clearTimeout(slot: string): void {
    const existing = this.namedTimeouts.get(slot);
    if (existing !== undefined) {
      clearTimeout(existing);
      this.namedTimeouts.delete(slot);
    }
  }

  /**
   * Schedule an independent timeout (for overlapping short-lived suppressions
   * such as read-aloud move counters) that is still cancelled on `cancelAll()`.
   */
  scheduleTimeout(fn: () => void, delayMs: number): ReturnType<typeof setTimeout> {
    const generation = this.generation;
    const handle = setTimeout(() => {
      this.pooledTimeouts.delete(handle);
      if (this.generation !== generation) return;
      fn();
    }, delayMs);
    this.pooledTimeouts.add(handle);
    return handle;
  }

  /**
   * Schedule a named `requestAnimationFrame` callback if one is not already
   * pending in `slot`. Returns true if a new frame was scheduled.
   */
  requestAnimationFrameOnce(slot: string, fn: () => void): boolean {
    if (this.animationFrames.has(slot)) return false;
    const generation = this.generation;
    const frameId = requestAnimationFrame(() => {
      if (this.animationFrames.get(slot) === frameId) {
        this.animationFrames.delete(slot);
      }
      if (this.generation !== generation) return;
      fn();
    });
    this.animationFrames.set(slot, frameId);
    return true;
  }

  /** Cancel a pending animation frame in `slot` if present. */
  cancelAnimationFrame(slot: string): void {
    const frameId = this.animationFrames.get(slot);
    if (frameId !== undefined) {
      cancelAnimationFrame(frameId);
      this.animationFrames.delete(slot);
    }
  }

  /** True when any timeout or animation frame is currently pending. */
  hasPending(): boolean {
    return (
      this.namedTimeouts.size > 0
      || this.pooledTimeouts.size > 0
      || this.animationFrames.size > 0
    );
  }

  /** Cancel every tracked timer and animation frame and invalidate active generations. */
  cancelAll(): void {
    this.generation++;

    for (const handle of this.namedTimeouts.values()) {
      clearTimeout(handle);
    }
    this.namedTimeouts.clear();

    for (const handle of this.pooledTimeouts) {
      clearTimeout(handle);
    }
    this.pooledTimeouts.clear();

    for (const frameId of this.animationFrames.values()) {
      cancelAnimationFrame(frameId);
    }
    this.animationFrames.clear();
  }
}
