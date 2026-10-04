/**
 * Local resume bookkeeping for an in-flight import (plan §13, §49).
 *
 * Persists only operational metadata — job/session ids, the file identity and
 * the preflight request — never file bytes, object URLs or credentials. The
 * file itself cannot survive a reload on any browser; on reattachment the user
 * must reselect the same archive and the engine verifies its identity against
 * this record before sending any missing chunk.
 */

import { Injectable } from '@angular/core';

import { PersistedTransferResumeState } from '../models/library-transfer.models';

export const TRANSFER_RESUME_STORAGE_KEY = 'nostos.library-transfer.active.v1';

@Injectable({ providedIn: 'root' })
export class TransferResumeStore {
  load(): PersistedTransferResumeState | null {
    const storage = this.storage();
    if (!storage) return null;

    const raw = storage.getItem(TRANSFER_RESUME_STORAGE_KEY);
    if (!raw) return null;

    try {
      const parsed = JSON.parse(raw) as unknown;
      if (!isResumeState(parsed)) {
        storage.removeItem(TRANSFER_RESUME_STORAGE_KEY);
        return null;
      }
      return parsed;
    } catch {
      storage.removeItem(TRANSFER_RESUME_STORAGE_KEY);
      return null;
    }
  }

  save(state: PersistedTransferResumeState): void {
    this.storage()?.setItem(TRANSFER_RESUME_STORAGE_KEY, JSON.stringify(state));
  }

  update(patch: Partial<PersistedTransferResumeState>): PersistedTransferResumeState | null {
    const current = this.load();
    if (!current) return null;
    const next: PersistedTransferResumeState = { ...current, ...patch, schemaVersion: 1 };
    this.save(next);
    return next;
  }

  clear(): void {
    this.storage()?.removeItem(TRANSFER_RESUME_STORAGE_KEY);
  }

  private storage(): Storage | null {
    try {
      return typeof localStorage === 'undefined' ? null : localStorage;
    } catch {
      return null;
    }
  }
}

function isResumeState(value: unknown): value is PersistedTransferResumeState {
  if (typeof value !== 'object' || value === null) return false;
  const record = value as Record<string, unknown>;
  const identity = record['fileIdentity'];
  const preflight = record['preflightRequest'];
  const jobId = record['jobId'];
  return (
    record['schemaVersion'] === 1 &&
    (jobId === undefined || typeof jobId === 'string') &&
    typeof record['jobCreationIdempotencyKey'] === 'string' &&
    (record['jobCreationIdempotencyKey'] as string).length > 0 &&
    record['direction'] === 'import' &&
    typeof record['fileName'] === 'string' &&
    typeof record['createdAt'] === 'string' &&
    typeof identity === 'object' &&
    identity !== null &&
    typeof (identity as Record<string, unknown>)['totalSizeBytes'] === 'number' &&
    typeof (identity as Record<string, unknown>)['sha256Checksum'] === 'string' &&
    typeof preflight === 'object' &&
    preflight !== null
  );
}
