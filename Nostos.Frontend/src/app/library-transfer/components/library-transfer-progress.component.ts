/**
 * Shared determinate/indeterminate progress surface for the transfer flows
 * (plan §43, §44).
 *
 * One component owns the progress geometry, human-readable sizes, rate and
 * ETA, and the accessible progress semantics so Settings, onboarding and the
 * replacement dialog cannot drift. The primary bar is driven by durable,
 * server-confirmed bytes; the caller never passes a volatile XHR percentage in
 * as the baseline (review-724 progress item).
 *
 * The live region text is bucketed to 5% steps inside the copy module, so a
 * screen reader hears phase changes and coarse progress rather than one
 * announcement per progress event.
 */

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import {
  TRANSFER_PROGRESS_LABELS,
  TransferProgressPhase,
  formatBytes,
  formatEta,
  formatRate,
  transferProgressLiveText,
  transferProgressValueText,
} from '../library-transfer.copy';

@Component({
  selector: 'app-library-transfer-progress',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './library-transfer-progress.component.html',
  styleUrl: './library-transfer-progress.component.css',
})
export class LibraryTransferProgressComponent {
  readonly phase = input.required<TransferProgressPhase>();
  /** Durable bytes processed; null renders an indeterminate bar. */
  readonly bytesProcessed = input<number | null>(null);
  readonly totalBytes = input<number | null>(null);
  readonly rateBytesPerSecond = input<number | null>(null);
  readonly etaSeconds = input<number | null>(null);
  readonly completedChunks = input<number | null>(null);
  readonly totalChunks = input<number | null>(null);
  readonly paused = input(false);

  readonly phaseLabel = computed(() => TRANSFER_PROGRESS_LABELS[this.phase()]);

  /** Integer percentage, or null when a total is not (yet) known. */
  readonly percentage = computed(() => {
    const total = this.totalBytes();
    const processed = this.bytesProcessed();
    if (
      total === null ||
      total <= 0 ||
      processed === null ||
      !Number.isFinite(processed) ||
      !Number.isFinite(total)
    ) {
      return null;
    }
    return Math.max(0, Math.min(100, Math.round((processed / total) * 100)));
  });

  readonly ariaValueText = computed(() =>
    transferProgressValueText(this.phase(), this.percentage()),
  );

  readonly liveText = computed(() => {
    const percent = this.percentage();
    return percent === null
      ? `${this.phaseLabel()}…`
      : transferProgressLiveText(this.phase(), percent);
  });

  /** "4.7 GiB of 12.9 GiB · 11.8 MiB/s · About 12 minutes remaining". */
  readonly detailText = computed(() => {
    const total = this.totalBytes();
    const processed = this.bytesProcessed();
    const parts: string[] = [];

    if (processed !== null && total !== null && total > 0) {
      parts.push(`${formatBytes(processed)} of ${formatBytes(total)}`);
    }

    const rate = formatRate(this.rateBytesPerSecond());
    if (rate && !this.paused()) parts.push(rate);

    const eta = formatEta(this.etaSeconds());
    if (eta && !this.paused()) parts.push(eta);

    const totalChunks = this.totalChunks();
    const completedChunks = this.completedChunks();
    if (
      completedChunks !== null &&
      totalChunks !== null &&
      totalChunks > 1 &&
      completedChunks < totalChunks
    ) {
      parts.push(`Part ${completedChunks} of ${totalChunks}`);
    }

    return parts.join(' · ');
  });
}
