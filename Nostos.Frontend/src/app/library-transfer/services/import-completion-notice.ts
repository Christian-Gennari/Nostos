/**
 * A finished import hard-reloads the page two seconds later, because every
 * cached entity and open reader still refers to the replaced library. That
 * reload also discarded "Import complete" and the recovery-copy date before a
 * customer could read them. This carries the confirmation across the reload in
 * session storage, so it is shown once in this tab until acknowledged.
 */
export const IMPORT_COMPLETION_NOTICE_KEY = 'nostos.library-transfer.import-completed.v1';

export interface ImportCompletionNotice {
  readonly jobId: string;
  /** Server-reported recovery-copy expiry; null when the server gave none. */
  readonly recoveryExpiresAtUtc: string | null;
}

export function saveImportCompletionNotice(
  storage: Storage | null | undefined,
  notice: ImportCompletionNotice,
): void {
  try {
    storage?.setItem(IMPORT_COMPLETION_NOTICE_KEY, JSON.stringify(notice));
  } catch {
    // Storage can be blocked or full; the import itself has still completed.
  }
}

export function loadImportCompletionNotice(
  storage: Storage | null | undefined,
): ImportCompletionNotice | null {
  try {
    const raw = storage?.getItem(IMPORT_COMPLETION_NOTICE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<ImportCompletionNotice> | null;
    if (!parsed || typeof parsed.jobId !== 'string' || !parsed.jobId) return null;
    return {
      jobId: parsed.jobId,
      recoveryExpiresAtUtc:
        typeof parsed.recoveryExpiresAtUtc === 'string' ? parsed.recoveryExpiresAtUtc : null,
    };
  } catch {
    return null;
  }
}

export function clearImportCompletionNotice(storage: Storage | null | undefined): void {
  try {
    storage?.removeItem(IMPORT_COMPLETION_NOTICE_KEY);
  } catch {
    // Nothing to clear when storage is unavailable.
  }
}
