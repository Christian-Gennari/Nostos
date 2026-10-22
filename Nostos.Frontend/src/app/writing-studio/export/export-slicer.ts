/**
 * Cooperative time slicing for the export resolver (issue #1164): long work
 * calls `tick()` often, and every `budgetMs` it gives the page a turn, so a
 * 100,000-word writing resolves without freezing typing, scrolling or the
 * dialog's own spinner. Everything the resolver needs (citeproc, the book
 * details, the Studio's Markdown reader) lives on the page, which is why it is
 * sliced here rather than moved to a worker.
 */
export interface SlicerOptions {
  /** How long one stretch of work may run before the page gets a turn. */
  budgetMs?: number;
  /** Gives the page a turn. Defaults to a macrotask (`setTimeout(0)`). */
  yieldToPage?: () => Promise<void>;
  now?: () => number;
  /** Stops the work at the next tick (the dialog closed or the options changed). */
  signal?: AbortSignal;
}

export class Slicer {
  private readonly budget: number;
  private readonly yieldToPage: () => Promise<void>;
  private readonly now: () => number;
  private readonly signal: AbortSignal | undefined;
  private started: number;
  /** How many times the page was given a turn. */
  yields = 0;
  /** The longest stretch of work between two turns, in ms. */
  longest = 0;

  constructor(options: SlicerOptions = {}) {
    this.budget = options.budgetMs ?? 12;
    this.yieldToPage = options.yieldToPage ?? (() => new Promise<void>((resolve) => setTimeout(resolve, 0)));
    this.now = options.now ?? (() => performance.now());
    this.signal = options.signal;
    this.started = this.now();
  }

  async tick(): Promise<void> {
    this.signal?.throwIfAborted();
    const elapsed = this.now() - this.started;
    if (elapsed < this.budget) return;
    this.longest = Math.max(this.longest, elapsed);
    this.yields++;
    await this.yieldToPage();
    this.signal?.throwIfAborted();
    this.started = this.now();
  }

  /** Records the last stretch, for a caller measuring `longest`. */
  finish(): void {
    this.longest = Math.max(this.longest, this.now() - this.started);
  }
}
