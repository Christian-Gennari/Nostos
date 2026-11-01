import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import { HttpContextToken } from '@angular/common/http';
import { Observable, Subject, auditTime, defer, finalize, merge } from 'rxjs';

/** How long a burst of changes is gathered into one re-read. */
const SETTLE_MS = 250;

/**
 * How long reading-position changes are gathered into one re-read. An
 * audiobook saves its position every two seconds; a screen elsewhere showing
 * progress bars catches up at this pace instead of re-reading every time.
 */
export const POSITION_SETTLE_MS = 30_000;

/** The response header naming the library revisions a write committed. */
export const LIBRARY_REVISIONS_HEADER = 'X-Library-Revisions';

/**
 * Set on a write whose caller applies the result itself (the response, plus
 * whatever else the screen needs). The revisions that write caused are then
 * recognised in the feed and do not ask the screens to re-read. A write that
 * leaves this off keeps the old behaviour: its announcement triggers re-reads,
 * which is what Ask Nostos and other writers that do not patch the screen rely on.
 */
export const LIBRARY_WRITE_HANDLED = new HttpContextToken<boolean>(() => false);

/** A burst announced across more revisions than this is not worth matching one by one. */
const MAX_GAP = 64;

/** Own revisions remembered while waiting for the feed to announce them. */
const MAX_OWN = 128;

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
 *
 * A write that handles its own result (`LIBRARY_WRITE_HANDLED`) is told apart
 * exactly, not by a time window: the server names the revisions the write
 * committed in a response header (`noteOwnWrite`), and an announcement is
 * dropped only when every revision it covers, from the last one seen up to the
 * announced one, is one of this tab's own. A change from another tab or device
 * falls in that range as a revision that is not ours, so it still asks for a
 * re-read, even when it lands between two of our own writes.
 *
 * A change the server marks `progressOnly` (a reading position moved, nothing
 * else) is gathered for `POSITION_SETTLE_MS` before it asks for a re-read; any
 * other change asks at once.
 */
@Injectable({ providedIn: 'root' })
export class LibraryChangesService {
  private readonly document = inject(DOCUMENT);
  private readonly changed = new Subject<void>();
  private readonly positionMoved = new Subject<void>();
  private readonly settled = new Subject<void>();

  /** Revisions this tab's handled writes committed and the feed has not yet accounted for. */
  private readonly ownRevisions = new Set<number>();
  /** Revisions announced since the last re-read request, not yet explained as our own. */
  private readonly unexplained = new Set<number>();
  /** An announcement whose range could not be listed: always asks for a re-read. */
  private unexplainedUnknown = false;

  private source: EventSource | null = null;
  private lastRevision: string | null = null;
  private watchers = 0;
  private visibilityBound = false;

  constructor() {
    // One gate for every watcher: the burst is gathered, then own writes are
    // subtracted once, so each screen is told or none is.
    this.changed.pipe(auditTime(SETTLE_MS)).subscribe(() => {
      if (this.takeUnexplained()) this.settled.next();
    });
  }

  /**
   * Records the revisions one of this tab's handled writes committed, as the
   * server named them in `X-Library-Revisions`. Anything unparsable is ignored,
   * which only means the announcement asks for a re-read as it always did.
   */
  noteOwnWrite(header: string | null): void {
    for (const part of (header ?? '').split(',')) {
      const revision = parseRevision(part.trim());
      if (revision === null) continue;
      this.ownRevisions.add(revision);
      if (this.ownRevisions.size > MAX_OWN) {
        this.ownRevisions.delete(this.ownRevisions.values().next().value as number);
      }
    }
  }

  /**
   * Emits after the library changed. Subscribe for as long as the screen shows
   * library data (`takeUntilDestroyed`); the feed closes with the last
   * subscription.
   */
  watch(): Observable<void> {
    return defer(() => {
      this.addWatcher();
      return merge(
        this.settled,
        this.positionMoved.pipe(auditTime(POSITION_SETTLE_MS)),
      ).pipe(finalize(() => this.removeWatcher()));
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
      this.unexplained.clear();
      this.unexplainedUnknown = false;
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
      const announced = readRevision((event as MessageEvent).data);
      if (announced === null) return;

      const previous = this.lastRevision;
      this.lastRevision = announced.revision;
      if (previous === null || previous === announced.revision) return;
      if (announced.progressOnly) {
        this.positionMoved.next();
      } else {
        this.noteAnnounced(previous, announced.revision);
        this.changed.next();
      }
    });
  }

  /** Remembers which revisions this announcement covers, to be matched against our own. */
  private noteAnnounced(previous: string, announced: string): void {
    const from = parseRevision(previous);
    const to = parseRevision(announced);
    if (from === null || to === null || to <= from || to - from > MAX_GAP) {
      this.unexplainedUnknown = true;
      return;
    }
    for (let revision = from + 1; revision <= to; revision++) this.unexplained.add(revision);
  }

  /** True when something announced since the last call was not a write of ours. */
  private takeUnexplained(): boolean {
    let foreign = this.unexplainedUnknown;
    for (const revision of this.unexplained) {
      if (this.ownRevisions.delete(revision)) continue;
      foreign = true;
    }
    this.unexplained.clear();
    this.unexplainedUnknown = false;
    return foreign;
  }

  private close(): void {
    this.source?.close();
    this.source = null;
  }
}

function parseRevision(value: string): number | null {
  if (!/^[0-9]{1,15}$/.test(value)) return null;
  return Number(value);
}

function readRevision(data: unknown): { revision: string; progressOnly: boolean } | null {
  if (typeof data !== 'string') return null;
  try {
    const parsed = JSON.parse(data) as { stateVersion?: unknown; progressOnly?: unknown };
    return typeof parsed.stateVersion === 'string'
      ? { revision: parsed.stateVersion, progressOnly: parsed.progressOnly === true }
      : null;
  } catch {
    return null;
  }
}
