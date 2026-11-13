import { PdfReaderTimers } from './pdf-reader-timers';

describe('PdfReaderTimers', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('replaces a named timeout slot and cancels the earlier timer', () => {
    const timers = new PdfReaderTimers();
    const first = vi.fn();
    const second = vi.fn();

    timers.replaceTimeout('scroll', first, 100);
    vi.advanceTimersByTime(60);
    timers.replaceTimeout('scroll', second, 100);

    vi.advanceTimersByTime(60);
    expect(first).not.toHaveBeenCalled();
    expect(second).not.toHaveBeenCalled();

    vi.advanceTimersByTime(40);
    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenCalledTimes(1);
    expect(timers.hasPending()).toBe(false);
  });

  it('cancels named, pooled, and animation frame callbacks on cancelAll() and bumps generation', () => {
    const timers = new PdfReaderTimers();
    const initialGen = timers.currentGeneration();
    const named = vi.fn();
    const pooled = vi.fn();
    const rafFn = vi.fn();

    vi.stubGlobal('requestAnimationFrame', vi.fn(() => 42));
    const cancelRaf = vi.fn();
    vi.stubGlobal('cancelAnimationFrame', cancelRaf);

    timers.replaceTimeout('initialLoad', named, 500);
    timers.scheduleTimeout(pooled, 400);
    expect(timers.requestAnimationFrameOnce('hover', rafFn)).toBe(true);
    expect(timers.requestAnimationFrameOnce('hover', rafFn)).toBe(false);
    expect(timers.hasPending()).toBe(true);

    timers.cancelAll();

    expect(timers.hasPending()).toBe(false);
    expect(timers.isCurrent(initialGen)).toBe(false);
    expect(cancelRaf).toHaveBeenCalledWith(42);

    vi.advanceTimersByTime(1000);
    expect(named).not.toHaveBeenCalled();
    expect(pooled).not.toHaveBeenCalled();
    expect(rafFn).not.toHaveBeenCalled();
  });

  it('allows recursive scheduling inside a named slot callback without clobbering the new timer', () => {
    const timers = new PdfReaderTimers();
    const runs: number[] = [];

    const step = (attempt: number) => {
      timers.replaceTimeout(
        'retry',
        () => {
          runs.push(attempt);
          if (attempt < 2) step(attempt + 1);
        },
        50,
      );
    };

    step(0);
    vi.advanceTimersByTime(150);
    expect(runs).toEqual([0, 1, 2]);
    expect(timers.hasPending()).toBe(false);
  });
});
