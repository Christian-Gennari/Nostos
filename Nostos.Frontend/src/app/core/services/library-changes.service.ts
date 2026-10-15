import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import { Observable, Subject, auditTime, defer, finalize } from 'rxjs';

/** How long a burst of changes is gathered into one re-read. */
const SETTLE_MS = 250;

/**
 * "The library changed somewhere else" — Ask Nostos, another tab, an
 * import — so a screen that is showing it can re-read without a reload.
 *
 * The server's `GET /api/library/changes` stream announces the library revision,
 * which advances on every save to user-owned library state whatever made it.
 * This service compares each announced revision with the last one it saw and
 * emits when it moved. Nothing about the change is carried: subscribers re-read
 * what they show.
 *
 * The stream is opened by demand and only while the page is visible:
 *   - it is open while at least one `watch()` subscription exists;
 *   - a hidden tab drops it, and showing the tab reopens it. The first revision
 *     after reopening is compared with the last one seen, so a change made while
 *     the tab was hidden is still announced;
 *   - when the last watcher leaves, the baseline is forgotten. A screen that
 *     mounts later reads fresh data itself, and must not be told to re-read
 *     again for changes its own first read already contains.
 *
 * A subscriber's own edits are announced too. Re-reads must therefore be quiet
 * and in place, never a reset of what the user is looking at.
 */
@Injectable({ providedIn: 'root' })
export class LibraryChangesService {
  private readonly document = inject(DOCUMENT);
  private readonly changed = new Subject<void>();

  private source: EventSource | null = null;
  private lastRevision: string | null = null;
  private watchers = 0;
  private visibilityBound = false;

  /**
   * Emits after the library changed. Subscribe for as long as the screen shows
   * library data (`takeUntilDestroyed`); the feed closes with the last
   * subscription.
   */
  watch(): Observable<void> {
    return defer(() => {
      this.addWatcher();
      return this.changed.pipe(
        auditTime(SETTLE_MS),
        finalize(() => this.removeWatcher()),
      );
    });
  }

  /**
   * The EventSource is created through this seam so a unit test never opens a
   * real connection to a real server.
   */
  protected createEventSource(url: string): EventSource {
    return new EventSource(url);
  }

  private addWatcher(): void {
    this.watchers++;
    this.bindVisibility();
    this.openIfWanted();
  }

  private removeWatcher(): void {
    this.watchers = Math.max(0, this.watchers - 1);
    if (this.watchers === 0) {
      this.close();
      this.lastRevision = null;
    }
  }

  private bindVisibility(): void {
    if (this.visibilityBound) return;
    this.visibilityBound = true;

    this.document.addEventListener('visibilitychange', () => {
      if (this.document.visibilityState === 'hidden') {
        // Keep the baseline: what is on screen was read against it.
        this.close();
      } else {
        this.openIfWanted();
      }
    });
  }

  private openIfWanted(): void {
    if (this.source || this.watchers === 0) return;
    if (this.document.visibilityState === 'hidden') return;

    let source: EventSource;
    try {
      source = this.createEventSource('/api/library/changes');
    } catch {
      // A browser (or a test environment) without EventSource: screens still
      // work, they just do not update live.
      return;
    }

    this.source = source;
    // The browser reconnects by itself when the server ends a stream, honouring
    // the server's retry delay; nothing to do on error.
    source.addEventListener('library', (event) => {
      const revision = readRevision((event as MessageEvent).data);
      if (revision === null) return;

      const previous = this.lastRevision;
      this.lastRevision = revision;
      if (previous !== null && previous !== revision) this.changed.next();
    });
  }

  private close(): void {
    this.source?.close();
    this.source = null;
  }
}

function readRevision(data: unknown): string | null {
  if (typeof data !== 'string') return null;
  try {
    const parsed = JSON.parse(data) as { stateVersion?: unknown };
    return typeof parsed.stateVersion === 'string' ? parsed.stateVersion : null;
  } catch {
    return null;
  }
}
