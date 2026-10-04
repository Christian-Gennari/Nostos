/**
 * Shared export-library flow (plan §37–§40; slice B4).
 *
 * The flow prepares the archive on the host, shows the server's phase rather
 * than a fake download percentage, and offers a native download link. When the
 * archive becomes ready the component may click that link once; a blocked
 * automatic download is not a failure, so the explicit `Download archive`
 * anchor stays on screen (plan §39, §40). The archive is never fetched into
 * Angular memory.
 */

import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';

import { LibraryExportCoordinator, LibraryExportState } from '../services/library-export-coordinator.service';
import {
  TransferFailureCopy,
  TransferProgressPhase,
  libraryTransferFailureCopy,
  maintenanceRetryMessage,
} from '../library-transfer.copy';
import { LibraryTransferProgressComponent } from './library-transfer-progress.component';
import { ButtonComponent } from '../../ui/button/button.component';

const EXPIRY_FORMAT = new Intl.DateTimeFormat(undefined, {
  dateStyle: 'medium',
  timeStyle: 'short',
});

/**
 * A download URL is only safe to assign to an anchor (and auto-click) when it
 * is the current origin over http(s) or any https URL. The transport owns the
 * URL, but the automatic path bypasses Angular's sanitizer, so the component
 * re-checks it (review-730 item 4b).
 */
export function isSafeDownloadUrl(url: string | null | undefined): boolean {
  if (!url) return false;
  try {
    const base = typeof globalThis.location !== 'undefined' ? globalThis.location.href : undefined;
    const parsed = new URL(url, base);
    if (parsed.protocol === 'https:') return true;
    if (parsed.protocol !== 'http:') return false;
    return (
      typeof globalThis.location !== 'undefined' && parsed.origin === globalThis.location.origin
    );
  } catch {
    return false;
  }
}

@Component({
  selector: 'app-library-export-flow',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ButtonComponent, LibraryTransferProgressComponent],
  templateUrl: './library-export-flow.component.html',
  styleUrls: ['./library-transfer.shared.css', './library-export-flow.component.css'],
})
export class LibraryExportFlowComponent {
  /** Attempts one native anchor click when the archive becomes ready. */
  readonly autoDownload = input(true);

  private readonly coordinator = inject(LibraryExportCoordinator);

  readonly state = this.coordinator.state;
  readonly preparing = computed(() => asKind(this.state(), 'preparing'));
  readonly checking = computed(() => asKind(this.state(), 'checking'));
  readonly ready = computed(() => asKind(this.state(), 'ready'));
  readonly failed = computed(() => asKind(this.state(), 'failed'));
  readonly cancelled = computed(() => asKind(this.state(), 'cancelled'));

  /** True while the coordinator waits out server maintenance and re-attempts. */
  readonly maintenanceWaiting = this.coordinator.maintenanceWaiting;
  readonly maintenanceMessage = maintenanceRetryMessage();

  readonly preparingHeadline = computed(() => {
    const state = this.preparing();
    return state?.progress?.phase === 'Transferring'
      ? 'Adding library files…'
      : 'Preparing archive…';
  });

  readonly preparingPhase = computed<TransferProgressPhase>(() =>
    this.preparing()?.progress?.phase === 'Transferring' ? 'uploading' : 'preparing',
  );

  readonly failureCopy = computed<TransferFailureCopy | null>(() => {
    const state = this.failed();
    return state ? libraryTransferFailureCopy(state.failure) : null;
  });

  /** True when the ready download URL may be rendered and clicked. */
  readonly downloadUrlSafe = computed(() => isSafeDownloadUrl(this.ready()?.downloadUrl));

  readonly expiryLabel = computed(() => {
    const expiresAt = this.ready()?.expiresAt;
    if (!expiresAt) return null;
    const date = new Date(expiresAt);
    if (Number.isNaN(date.getTime())) return null;
    return `This download link is available until ${EXPIRY_FORMAT.format(date)}.`;
  });

  private autoDownloadedJobId: string | null = null;

  constructor() {
    effect(() => {
      const ready = this.ready();
      if (!ready || !this.autoDownload() || !this.downloadUrlSafe()) return;
      if (this.autoDownloadedJobId === ready.jobId) return;
      this.autoDownloadedJobId = ready.jobId;
      this.attemptNativeDownload(ready.downloadUrl);
    });
  }

  start(): void {
    void this.coordinator.startExport();
  }

  cancel(): void {
    void this.coordinator.cancelExport();
  }

  dismiss(): void {
    this.coordinator.dismiss();
  }

  retry(): void {
    void this.coordinator.startExport();
  }

  /**
   * Clicks a transient native anchor. Never fetches, so the browser owns
   * streaming, ranges and memory; a blocked click leaves the visible link.
   * Refuses any URL `isSafeDownloadUrl` rejects.
   */
  attemptNativeDownload(url?: string): void {
    const href = url ?? this.ready()?.downloadUrl;
    if (!href || !isSafeDownloadUrl(href) || typeof document === 'undefined') return;
    const anchor = document.createElement('a');
    anchor.href = href;
    anchor.download = '';
    anchor.rel = 'noopener';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  }
}

function asKind<K extends LibraryExportState['kind']>(
  state: LibraryExportState,
  kind: K,
): Extract<LibraryExportState, { kind: K }> | null {
  return state.kind === kind ? (state as Extract<LibraryExportState, { kind: K }>) : null;
}
