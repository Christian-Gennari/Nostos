/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { coverImageUrl } from '../core/cover-url';
import type { Book } from '../core/dtos/book.dtos';

/** How long the pointer rests on a card before its hero art is prefetched. */
export const HERO_PREFETCH_INTENT_MS = 120;

/** True when the primary input can hover; a touch screen's mouseenter is only a tap. */
function canHover(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(hover: hover)').matches;
}

/**
 * Warms the HTTP cache for a book's hero art once the pointer signals intent, so
 * opening the detail page does not flash an empty dark band.
 *
 * WHY THIS IS ITS OWN OBJECT. The shelf's two views (grid card, list row) both
 * hover, and the detail page's 640px hero cannot start its own fetch until the
 * book JSON arrives, because `heroArtUrl` is derived from `coverUrl` — the two
 * requests are serialised. Prefetching on hover overlaps them, so by the time the
 * click lands the 640px thumbnail is in flight or already cached and the band
 * paints on its first frame.
 *
 * With a mouse the fetch waits for HERO_PREFETCH_INTENT_MS of resting on the card
 * (`cancel()` on leave drops it), so sweeping across the shelf does not start one
 * 640px download per book passed. A touch screen has no hover: its only
 * `mouseenter` is the one emulated by a tap, right before the click, so it
 * fetches at once.
 *
 * It is a plain object rather than a `providedIn: 'root'` service on purpose: the
 * in-flight map belongs to the shelf that is on screen, and a root singleton would
 * carry one library's covers into the next mount (and into every test).
 */
export class LibraryCoverPrefetcher {
  /** Hero art in flight, keyed by URL, so a hover does not re-request it. */
  private readonly prefetched = new Map<string, HTMLImageElement>();

  /** The hover-intent timer of the card the pointer is on, if its delay has not run out. */
  private timer: ReturnType<typeof setTimeout> | null = null;

  /** A pointer settled on a card: fetch its hero art, after the intent delay on a mouse. */
  prefetch(book: Book): void {
    const url = coverImageUrl(book, 640);
    if (!url || this.prefetched.has(url)) return;

    this.cancel();
    if (!canHover()) {
      this.start(url);
      return;
    }
    this.timer = setTimeout(() => {
      this.timer = null;
      this.start(url);
    }, HERO_PREFETCH_INTENT_MS);
  }

  /** The pointer left the card before the intent delay: no download. */
  cancel(): void {
    if (this.timer === null) return;
    clearTimeout(this.timer);
    this.timer = null;
  }

  /** Leaving the shelf: drop the pending fetch and the references that keep one alive. */
  destroy(): void {
    this.cancel();
    this.prefetched.clear();
  }

  private start(url: string): void {
    if (this.prefetched.has(url)) return;
    const img = new Image();
    img.decoding = 'async';
    const release = () => this.prefetched.delete(url);
    img.addEventListener('load', release);
    img.addEventListener('error', release);
    img.src = url;
    // Hold a reference while in flight — an Image collected mid-request can have
    // the fetch cancelled before it reaches the cache.
    this.prefetched.set(url, img);
  }
}
