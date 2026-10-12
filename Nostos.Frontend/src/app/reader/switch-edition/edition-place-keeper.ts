import { Subscription } from 'rxjs';

import { AlignmentService } from '../../core/services/alignment.service';

/** Where the reader is in a text edition that has an audiobook edition. */
export interface KeeperPlace {
  bookId: string;
  audioEditionId: string;
  /** The first text on the visible page. */
  excerpt: string;
}

const SwitchedKey = 'nostos.switch-edition.used';
const MaximumRemembered = 200;

function readSwitched(): string[] {
  try {
    const stored = JSON.parse(localStorage.getItem(SwitchedKey) ?? '[]');
    return Array.isArray(stored) ? stored.filter((id): id is string => typeof id === 'string') : [];
  } catch {
    return [];
  }
}

/** Records that the reader moved between these editions at the same sentence. */
export function rememberSwitched(...bookIds: string[]): void {
  try {
    const known = readSwitched().filter((id) => !bookIds.includes(id));
    localStorage.setItem(
      SwitchedKey,
      JSON.stringify([...bookIds, ...known].slice(0, MaximumRemembered)),
    );
  } catch {
    // Without storage the keeper stays idle; switching itself is unaffected.
  }
}

export function hasSwitched(bookId: string): boolean {
  return readSwitched().includes(bookId);
}

/**
 * Keeps the audiobook's place known near the page being read, so that
 * "continue in the audiobook" finds a stored anchor beside the page instead of
 * searching the recording for it.
 *
 * It locates the page the reader has settled on, quietly, through the same
 * request the dialog makes. Each one may use transcription allowance, so it is
 * deliberately slow and bounded: only for a book the reader has already
 * switched editions on, only a page that has stayed put, at most once in
 * `MinimumGapMs`, at most `MaximumPerSession` times, and never again in this
 * session after any failure.
 */
export class EditionPlaceKeeper {
  /** A page counts as settled once it has been shown this long. */
  static readonly SettleMs = 20_000;
  static readonly MinimumGapMs = 5 * 60_000;
  static readonly MaximumPerSession = 24;

  private timer: ReturnType<typeof setTimeout> | null = null;
  private request: Subscription | null = null;
  private generation = 0;
  private lastLocatedAt: number | null = null;
  private lastExcerpt: string | null = null;
  private located = 0;
  private gaveUp = false;

  constructor(
    private readonly alignment: AlignmentService,
    private readonly place: () => KeeperPlace | null | Promise<KeeperPlace | null>,
  ) {}

  /** The reader moved: wait for the new page to settle. */
  moved(): void {
    this.clearTimer();
    if (this.gaveUp || this.located >= EditionPlaceKeeper.MaximumPerSession) return;

    const generation = ++this.generation;
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.settled(generation);
    }, EditionPlaceKeeper.SettleMs);
  }

  stop(): void {
    ++this.generation;
    this.clearTimer();
    this.request?.unsubscribe();
    this.request = null;
  }

  private async settled(generation: number): Promise<void> {
    if (this.request || this.gaveUp) return;
    if (typeof document !== 'undefined' && document.hidden) return;

    const now = Date.now();
    if (
      this.lastLocatedAt !== null &&
      now - this.lastLocatedAt < EditionPlaceKeeper.MinimumGapMs
    ) {
      return;
    }

    let place: KeeperPlace | null;
    try {
      place = await this.place();
    } catch {
      return;
    }

    const excerpt = place?.excerpt.trim();
    if (generation !== this.generation || !place || !excerpt) return;
    if (excerpt === this.lastExcerpt || !hasSwitched(place.bookId)) return;

    this.lastLocatedAt = now;
    this.lastExcerpt = excerpt;
    this.located++;

    try {
      this.request = this.alignment
        .locateAudio(place.bookId, { targetBookId: place.audioEditionId, excerpt })
        .subscribe({
          // The answer is not needed here: the server keeps what it learned.
          next: () => {},
          error: () => {
            this.request = null;
            this.gaveUp = true;
          },
          complete: () => {
            this.request = null;
          },
        });
    } catch {
      this.request = null;
      this.gaveUp = true;
    }
  }

  private clearTimer(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
}
