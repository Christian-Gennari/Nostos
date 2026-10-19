import { Injectable, inject } from '@angular/core';
import { NavigationStart, Router } from '@angular/router';

import type { NoteBookFacet, NoteSearchHit } from '../core/dtos/note.dtos';

/** A list as it was left: what was loaded, and how far down the reader was. */
export interface BrainListSnapshot {
  notes: NoteSearchHit[];
  total: number;
  books: NoteBookFacet[];
  scrollTop: number;
  /** BrainStateService.dataVersion when taken: any change since makes it stale. */
  version: number;
}

const KEPT = 12;

/**
 * Brings a list back as it was when the reader comes Back to it (browser back
 * or forward), instead of loading it again from the top. A list opened any
 * other way loads fresh.
 */
@Injectable({ providedIn: 'root' })
export class BrainListCacheService {
  private readonly snapshots = new Map<string, BrainListSnapshot>();
  private lastWasHistory = false;

  constructor() {
    inject(Router).events.subscribe((event) => {
      if (event instanceof NavigationStart) this.lastWasHistory = event.navigationTrigger === 'popstate';
    });
  }

  save(key: string, snapshot: BrainListSnapshot): void {
    // Older versions can never be restored: let them go.
    for (const [k, kept] of this.snapshots) if (kept.version !== snapshot.version) this.snapshots.delete(k);
    this.snapshots.delete(key);
    this.snapshots.set(key, snapshot);
    while (this.snapshots.size > KEPT) {
      const oldest = this.snapshots.keys().next().value;
      if (oldest === undefined) break;
      this.snapshots.delete(oldest);
    }
  }

  /**
   * The list as it was, when this navigation is a step back or forward through
   * history and nothing has changed since it was taken.
   */
  restore(key: string, version: number): BrainListSnapshot | null {
    if (!this.lastWasHistory) return null;
    const snapshot = this.snapshots.get(key);
    return snapshot && snapshot.version === version ? snapshot : null;
  }
}
