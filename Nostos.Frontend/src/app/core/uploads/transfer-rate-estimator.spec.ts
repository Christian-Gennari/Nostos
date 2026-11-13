import { TransferRateEstimator } from './transfer-rate-estimator';

describe('TransferRateEstimator', () => {
  it('returns null until the minimum sample span exists', () => {
    let now = 0;
    const estimator = new TransferRateEstimator({ now: () => now, minSpanMs: 1_000 });

    estimator.sample(0);
    expect(estimator.rateBytesPerSecond()).toBeNull();

    now = 500;
    estimator.sample(500);
    expect(estimator.rateBytesPerSecond()).toBeNull();

    now = 1_500;
    estimator.sample(1_500);
    expect(estimator.rateBytesPerSecond()).toBe(1_000);
  });

  it('ignores bytes already uploaded before the first sample', () => {
    let now = 0;
    const estimator = new TransferRateEstimator({ now: () => now, minSpanMs: 1_000 });

    // A resumed transfer starts at 100 MiB; only new bytes count.
    estimator.sample(100 * 1024 * 1024);
    now = 1_000;
    estimator.sample(100 * 1024 * 1024 + 1_024);
    now = 2_000;
    estimator.sample(100 * 1024 * 1024 + 2_048);

    expect(estimator.rateBytesPerSecond()).toBe(1_024);
  });

  it('evicts samples outside the rolling window', () => {
    let now = 0;
    const estimator = new TransferRateEstimator({ now: () => now, windowMs: 5_000, minSpanMs: 1_000 });

    estimator.sample(0);
    for (let second = 1; second <= 20; second += 1) {
      now = second * 1_000;
      estimator.sample(second * 100);
    }

    // The evicted early samples had a different rate; only the recent window
    // (the last ~5 seconds) contributes.
    expect(estimator.rateBytesPerSecond()).toBe(100);
  });

  it('suppresses ETA when the rate is unknown or stalled', () => {
    const estimator = new TransferRateEstimator();
    expect(estimator.etaSeconds(1_000)).toBeNull();

    estimator.reset();
    expect(estimator.etaSeconds(0)).toBe(0);
  });

  it('computes ETA from the rolling rate', () => {
    let now = 0;
    const estimator = new TransferRateEstimator({ now: () => now, minSpanMs: 1_000 });
    estimator.sample(0);
    now = 1_000;
    estimator.sample(1_000);
    now = 2_000;
    estimator.sample(2_000);

    expect(estimator.etaSeconds(5_000)).toBe(5);
  });
});
