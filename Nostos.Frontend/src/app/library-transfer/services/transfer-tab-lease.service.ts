/**
 * Cross-tab coordination for the single foreground import (plan §50,
 * review-724 B4 item).
 *
 * The resume record is one shared localStorage key, so two tabs can each load
 * it and start an import; the second writer silently overwrites the first
 * tab's record and the first transfer becomes locally undiscoverable. The
 * server remains authoritative, so this is a UX lease, not a correctness
 * boundary: a tab that owns the lease refreshes it on a heartbeat, other tabs
 * see "an import is already in progress" and keep their controls closed, and a
 * crashed tab's lease expires so the flow can be recovered elsewhere.
 *
 * localStorage writes do not fire `storage` in the writing tab, so the owning
 * tab also refreshes its own view after claim/touch/release.
 */

import { Injectable, OnDestroy, computed, signal } from '@angular/core';

export const TRANSFER_TAB_LEASE_KEY = 'nostos.library-transfer.tab.v1';

/** A lease not refreshed for this long is considered abandoned. */
export const TRANSFER_TAB_LEASE_TTL_MS = 15_000;

/** How often the owning tab refreshes its lease. */
export const TRANSFER_TAB_HEARTBEAT_MS = 5_000;

export interface TransferTabLeaseRecord {
  tabId: string;
  updatedAt: number;
  fileName?: string;
}

@Injectable({ providedIn: 'root' })
export class TransferTabLease implements OnDestroy {
  /** Test seams; production uses the exported defaults. */
  leaseTtlMs = TRANSFER_TAB_LEASE_TTL_MS;
  heartbeatMs = TRANSFER_TAB_HEARTBEAT_MS;
  now: () => number = () => Date.now();

  private readonly tabId = randomTabId();
  private readonly recordSignal = signal<TransferTabLeaseRecord | null>(null);
  private heartbeatTimer: ReturnType<typeof setInterval> | null = null;

  private readonly onStorage = (event: StorageEvent): void => {
    if (event.key === null || event.key === TRANSFER_TAB_LEASE_KEY) this.refresh();
  };

  /** True when a live lease is held by a different tab. */
  readonly otherTabActive = computed(() => {
    const record = this.recordSignal();
    return record !== null && record.tabId !== this.tabId && this.isFresh(record);
  });

  /** The archive the other tab is importing, when it wrote one. */
  readonly otherTabFileName = computed(() => {
    const record = this.recordSignal();
    return record !== null && record.tabId !== this.tabId ? (record.fileName ?? null) : null;
  });

  constructor() {
    if (typeof window !== 'undefined') {
      window.addEventListener('storage', this.onStorage);
      this.refresh();
    }
  }

  /**
   * Claims the lease for this tab. Returns false (and records the other tab's
   * claim) when a live lease is already held elsewhere; the caller must not
   * start a transfer in that case.
   */
  claim(fileName?: string): boolean {
    const current = this.read();
    if (current && current.tabId !== this.tabId && this.isFresh(current)) {
      this.recordSignal.set(current);
      return false;
    }

    this.write({ tabId: this.tabId, updatedAt: this.now(), fileName });
    this.startHeartbeat();
    return true;
  }

  /** Refreshes this tab's lease, if it owns one. */
  touch(): void {
    const current = this.read();
    if (!current || current.tabId !== this.tabId) return;
    this.write({ ...current, updatedAt: this.now() });
  }

  /** Releases the lease; a different tab's lease is never removed. */
  release(): void {
    const current = this.read();
    if (current && current.tabId === this.tabId) {
      this.storage()?.removeItem(TRANSFER_TAB_LEASE_KEY);
    }
    this.stopHeartbeat();
    this.refresh();
  }

  /** Re-reads the lease from storage (storage events use this too). */
  refresh(): void {
    this.recordSignal.set(this.read());
  }

  ngOnDestroy(): void {
    if (typeof window !== 'undefined') {
      window.removeEventListener('storage', this.onStorage);
    }
    this.stopHeartbeat();
  }

  private isFresh(record: TransferTabLeaseRecord): boolean {
    return this.now() - record.updatedAt <= this.leaseTtlMs;
  }

  private read(): TransferTabLeaseRecord | null {
    const raw = this.storage()?.getItem(TRANSFER_TAB_LEASE_KEY);
    if (!raw) return null;
    try {
      const parsed = JSON.parse(raw) as unknown;
      if (!isLeaseRecord(parsed)) return null;
      return parsed;
    } catch {
      return null;
    }
  }

  private write(record: TransferTabLeaseRecord): void {
    this.storage()?.setItem(TRANSFER_TAB_LEASE_KEY, JSON.stringify(record));
    this.recordSignal.set(record);
  }

  private startHeartbeat(): void {
    if (this.heartbeatTimer !== null) return;
    this.heartbeatTimer = setInterval(() => this.touch(), this.heartbeatMs);
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer !== null) clearInterval(this.heartbeatTimer);
    this.heartbeatTimer = null;
  }

  private storage(): Storage | null {
    try {
      return typeof localStorage === 'undefined' ? null : localStorage;
    } catch {
      return null;
    }
  }
}

function isLeaseRecord(value: unknown): value is TransferTabLeaseRecord {
  if (typeof value !== 'object' || value === null) return false;
  const record = value as Record<string, unknown>;
  return (
    typeof record['tabId'] === 'string' &&
    record['tabId'].length > 0 &&
    typeof record['updatedAt'] === 'number' &&
    Number.isFinite(record['updatedAt'])
  );
}

let fallbackTab = 0;

function randomTabId(): string {
  const uuid = globalThis.crypto?.randomUUID;
  if (typeof uuid === 'function') return uuid.call(globalThis.crypto);
  fallbackTab += 1;
  return `tab-${fallbackTab}-${Math.random().toString(36).slice(2, 10)}`;
}
