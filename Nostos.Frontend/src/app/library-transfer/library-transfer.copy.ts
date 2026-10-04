/**
 * Centralized user-facing copy and formatting for the shared library-transfer
 * UI (plan §33, §43, §46).
 *
 * The coordinator and transport speak in stable codes and raw byte counts;
 * this module turns them into the product language the flows render, so the
 * import flow, export flow and replacement dialog cannot drift apart and a
 * future localization pass has one file to read. No component should build a
 * failure sentence or a byte string itself.
 */

import type { LibraryTransferFailure } from './models/library-transfer.models';

/** What the flow should offer after a failure. */
export type TransferFailureAction = 'retry' | 'sign-in' | 'choose-file' | 'start-over' | 'none';

export interface TransferFailureCopy {
  title: string;
  message: string;
  action: TransferFailureAction;
}

const NUMBER = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });
const DECIMAL = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });

const BYTE_UNITS = ['B', 'KiB', 'MiB', 'GiB', 'TiB'] as const;

/**
 * Human-readable byte size on the binary scale the archive contract uses
 * (plan §46). Ten or more units keep one decimal; below ten they keep one too,
 * because "4.7 GiB of 12.9 GiB" is the shape the plan asks for.
 */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return '';

  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < BYTE_UNITS.length - 1) {
    value /= 1024;
    unit += 1;
  }

  const formatted = unit === 0 ? NUMBER.format(value) : DECIMAL.format(value);
  return `${formatted} ${BYTE_UNITS[unit]}`;
}

/** Transfer rate, e.g. "11.8 MiB/s"; null until a rate is meaningful. */
export function formatRate(bytesPerSecond: number | null | undefined): string | null {
  if (
    bytesPerSecond === null ||
    bytesPerSecond === undefined ||
    !Number.isFinite(bytesPerSecond) ||
    bytesPerSecond <= 0
  ) {
    return null;
  }
  return `${formatBytes(bytesPerSecond)}/s`;
}

/** Coarse time remaining; null while the rate is unreliable (plan §23). */
export function formatEta(seconds: number | null | undefined): string | null {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds <= 0) {
    return null;
  }
  if (seconds < 60) return 'Less than a minute remaining';

  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `About ${minutes} minute${minutes === 1 ? '' : 's'} remaining`;

  const hours = Math.round(minutes / 60);
  return `About ${hours} hour${hours === 1 ? '' : 's'} remaining`;
}

/** The three counts the replacement copy compares (plan §27). */
export interface LibraryCountsSummary {
  books: number;
  notes: number;
  collections: number;
}

export function formatLibraryCounts(counts: LibraryCountsSummary): string {
  return (
    `${NUMBER.format(counts.books)} books · ` +
    `${NUMBER.format(counts.notes)} notes · ` +
    `${NUMBER.format(counts.collections)} collections`
  );
}

/**
 * Capacity failure in the plan's exact shape: required (including temporary
 * import and recovery space) and currently available, both human readable.
 */
export function storageFailureMessage(
  requiredStorageBytes: number | undefined,
  availableStorageBytes: number | undefined,
): string {
  if (
    typeof requiredStorageBytes === 'number' &&
    typeof availableStorageBytes === 'number' &&
    Number.isFinite(requiredStorageBytes) &&
    Number.isFinite(availableStorageBytes)
  ) {
    return (
      `This import needs about ${formatBytes(requiredStorageBytes)} of available storage, ` +
      `including temporary import and recovery space. ` +
      `${formatBytes(availableStorageBytes)} is currently available.`
    );
  }
  return 'This host does not have enough free storage for this import. Free up space, then try again.';
}

/**
 * Maps a stable engine failure to displayed copy and the recovery the flow
 * should offer. Never returns the backend `message` verbatim: the copy here is
 * the product contract (plan §33).
 */
export function libraryTransferFailureCopy(failure: LibraryTransferFailure): TransferFailureCopy {
  switch (failure.code) {
    case 'archive_not_portable':
      return {
        title: 'Not a portable library archive',
        message: 'This file is not a supported Nostos portable library archive.',
        action: 'choose-file',
      };
    case 'archive_operational_backup':
      return {
        title: 'This is a backup, not a portable archive',
        message:
          'This is a SelfHosted backup, not a portable library archive. Use a portable ' +
          'Export library file to move data between Nostos installations.',
        action: 'choose-file',
      };
    case 'archive_unsupported_version':
      return {
        title: 'Unsupported Nostos version',
        message: 'This archive was created by an unsupported Nostos version.',
        action: 'choose-file',
      };
    case 'migration_storage_exhausted':
      return {
        title: 'Not enough storage on this host',
        message: storageFailureMessage(
          failure.requiredStorageBytes,
          failure.availableStorageBytes,
        ),
        action: 'retry',
      };
    case 'migration_session_expired':
      return {
        title: 'Import session expired',
        message: 'The import session expired. Retry, then select the same file again to continue.',
        action: 'retry',
      };
    case 'migration_file_identity_mismatch':
      return {
        title: 'The selected file doesn’t match',
        message: 'The selected file no longer matches this upload.',
        action: 'choose-file',
      };
    case 'migration_chunk_hash_mismatch':
      return {
        title: 'Part of the file could not be verified',
        message:
          'One part of the file could not be verified. Retry the import; if this keeps ' +
          'happening, make a fresh export and use that file.',
        action: 'retry',
      };
    case 'migration_chunk_conflict':
      return {
        title: 'A different copy was already uploaded',
        message: 'The server already has different data for this file. Start again with the original archive.',
        action: 'choose-file',
      };
    case 'migration_destination_conflict':
      return {
        title: 'This library changed',
        message:
          'This library changed after the import began, so Nostos did not replace it. ' +
          'Choose your archive again to review the current library.',
        action: 'start-over',
      };
    case 'source_media_missing':
      return {
        title: 'A book file is missing',
        message:
          'A source file disappeared while the export was being prepared. Check the library ' +
          'storage, then try the export again.',
        action: 'start-over',
      };
    case 'source_media_changed':
      return {
        title: 'A book file changed',
        message: 'A source file changed while the export was being prepared. Try the export again.',
        action: 'start-over',
      };
    case 'portable_export_failed':
      return {
        title: 'Couldn’t prepare the export',
        message: 'Nostos could not prepare the archive. Try the export again.',
        action: 'start-over',
      };
    case 'portable_import_failed':
      return {
        title: 'Couldn’t finish the import',
        message: 'The import failed on the server. Retry to continue with this archive.',
        action: 'retry',
      };
    case 'migration_not_found':
      return {
        title: 'This import is no longer available',
        message: 'Nostos no longer has this import. Start a new import with your archive.',
        action: 'choose-file',
      };
    case 'migration_cannot_cancel':
      return {
        title: 'The import is already finishing',
        message: 'The server has started finishing this import and it can no longer be cancelled.',
        action: 'none',
      };
    default:
      if (failure.status === 401 || failure.status === 403) {
        return {
          title: 'Sign in again',
          message:
            'Your session expired. Sign in again, then continue; the import is kept and ' +
            'can resume where it stopped.',
          action: 'sign-in',
        };
      }
      return {
        title: 'The transfer didn’t finish',
        message: 'The connection dropped or the host interrupted the transfer. Try again.',
        action: failure.retryable ? 'retry' : 'choose-file',
      };
  }
}

export type TransferProgressPhase =
  | 'hashing'
  | 'preparing'
  | 'uploading'
  | 'checking'
  | 'activating';

/** Visible and announced label per progress phase (plan §25, §43). */
export const TRANSFER_PROGRESS_LABELS: Record<TransferProgressPhase, string> = {
  hashing: 'Checking file',
  preparing: 'Preparing import',
  uploading: 'Uploading library',
  checking: 'Checking the archive',
  activating: 'Importing library',
};

/**
 * Accessible progress sentence for the live region. The percentage is bucketed
 * before it reaches the sentence so an XHR firing dozens of times per second
 * cannot spam a screen reader (plan §44).
 */
export function transferProgressLiveText(phase: TransferProgressPhase, percent: number): string {
  const bucket = Math.min(100, Math.floor(percent / 5) * 5);
  return `${TRANSFER_PROGRESS_LABELS[phase]}, ${bucket} percent complete.`;
}

/** `role="progressbar"` value text: the sentence a screen reader reads. */
export function transferProgressValueText(
  phase: TransferProgressPhase,
  percent: number | null,
): string {
  if (percent === null) return `${TRANSFER_PROGRESS_LABELS[phase]}…`;
  return `${TRANSFER_PROGRESS_LABELS[phase]}, ${percent} percent complete.`;
}
