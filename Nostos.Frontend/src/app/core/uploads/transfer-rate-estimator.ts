/**
 * Rolling transfer-rate estimator (plan §23).
 *
 * Samples aggregate bytes (durable receipts plus in-flight loaded bytes) and
 * reports a rate over a 5-10 second window. Because the first sample defines
 * the baseline, bytes already transferred before a resume never inflate the
 * rate; when the window is too young (or stalled) the rate and ETA are null
 * rather than a fabricated number.
 */

export interface TransferRateEstimatorOptions {
  windowMs?: number;
  minSpanMs?: number;
  now?: () => number;
}

interface Sample {
  at: number;
  bytes: number;
}

export class TransferRateEstimator {
  private readonly windowMs: number;
  private readonly minSpanMs: number;
  private readonly now: () => number;
  private samples: Sample[] = [];

  constructor(options: TransferRateEstimatorOptions = {}) {
    this.windowMs = options.windowMs ?? 8_000;
    this.minSpanMs = options.minSpanMs ?? 1_500;
    this.now = options.now ?? (() => Date.now());
  }

  sample(bytesComplete: number): void {
    const at = this.now();
    this.samples.push({ at, bytes: bytesComplete });
    const cutoff = at - this.windowMs;
    while (this.samples.length > 2 && this.samples[0].at < cutoff) this.samples.shift();
  }

  reset(): void {
    this.samples = [];
  }

  rateBytesPerSecond(): number | null {
    if (this.samples.length < 2) return null;
    const first = this.samples[0];
    const last = this.samples.at(-1)!;
    const spanMs = last.at - first.at;
    const deltaBytes = last.bytes - first.bytes;
    if (spanMs < this.minSpanMs || deltaBytes <= 0) return null;
    return (deltaBytes * 1000) / spanMs;
  }

  etaSeconds(remainingBytes: number): number | null {
    if (remainingBytes <= 0) return 0;
    const rate = this.rateBytesPerSecond();
    if (rate === null || rate <= 0) return null;
    return remainingBytes / rate;
  }
}
