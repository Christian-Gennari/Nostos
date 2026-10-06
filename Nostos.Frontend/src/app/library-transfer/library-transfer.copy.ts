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
import type { MigrationActivationPhase } from './models/migration-http.dtos';

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
    case 'migration_storage_contended':
      return {
        title: 'The host is busy',
        message:
          'Another transfer is using this host’s storage. Try again in a moment.',
        action: 'retry',
      };
    case 'migration_activation_busy':
      return {
        title: 'The library is finishing another operation',
        message:
          'This host is completing a library operation right now. Nostos is retrying ' +
          'automatically and nothing has been lost.',
        action: 'retry',
      };
    case 'migration_maintenance_timeout':
      return {
        title: 'The host stayed busy',
        message:
          'This host was busy finishing another library operation for several minutes. ' +
          'Nothing was lost; try again to continue where the import stopped.',
        action: 'retry',
      };
    case 'migration_not_supported':
      return {
        title: 'Library migration is not available',
        message:
          'This Nostos host does not support library migration. Update the server to use ' +
          'this feature.',
        action: 'none',
      };
    case 'migration_transport_mode_unavailable':
      return {
        title: 'The host didn’t answer in time',
        message:
          'Nostos could not ask this host how to upload the library. Nothing was sent; ' +
          'try again in a moment.',
        action: 'retry',
      };
    case 'direct_upload_target_invalid':
      return {
        title: 'The host returned an unusable upload address',
        message:
          'Nostos refused the storage address this host returned because it is not a safe ' +
          'external HTTPS location. Nothing was uploaded; update or contact the host operator.',
        action: 'none',
      };
    case 'direct_upload_ticket_invalid':
      return {
        title: 'The host returned an unusable upload ticket',
        message:
          'An upload ticket did not match this archive’s session, so Nostos stopped before ' +
          'sending the part. Retry the import; if it keeps happening, update the host.',
        action: 'retry',
      };
    case 'direct_upload_rejected':
      return {
        title: 'The storage target refused part of the archive',
        message:
          'The host’s storage target kept refusing an archive part. Nothing in your library ' +
          'was changed. Retry the import; if it keeps happening, contact the host operator.',
        action: 'retry',
      };
    case 'direct_upload_receipt_pending':
      return {
        title: 'The host didn’t confirm an uploaded part',
        message:
          'Nostos uploaded an archive part but the host did not confirm it. Nothing is lost; ' +
          'Nostos can retry this part from where it stopped.',
        action: 'retry',
      };
    case 'migration_lease_conflict':
      return {
        title: 'Another process is working on this import',
        message: 'This import is being processed elsewhere. Try again in a moment.',
        action: 'retry',
      };
    case 'migration_import_preparation_unavailable':
      return {
        title: 'Import is not available on this host yet',
        message:
          'This Nostos host cannot prepare imported libraries yet. Update the server, or ' +
          'import on a host with library migration enabled.',
        action: 'none',
      };
    case 'migration_export_artifact_unavailable':
      return {
        title: 'Export is not available on this host yet',
        message:
          'This Nostos host cannot prepare a downloadable export archive yet. Update the ' +
          'server, or export from a host with library migration enabled.',
        action: 'none',
      };
    case 'migration_export_not_available':
      return {
        title: 'The export isn’t ready yet',
        message: 'This export has no downloadable archive yet. Wait for it to finish, then try again.',
        action: 'start-over',
      };
    case 'migration_export_expired':
      return {
        title: 'This export expired',
        message: 'The downloadable archive has expired. Prepare a new export.',
        action: 'start-over',
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
    case 'migration_replacement_confirmation_required':
      // The activation dialog owns this refusal (it carries fresh counts and
      // the revision to confirm); this copy only guards a raw surfacing.
      return {
        title: 'Confirm the replacement',
        message:
          'This library already contains data, so Nostos did not replace it. Review the ' +
          'current library and confirm the replacement again.',
        action: 'none',
      };
    case 'migration_activation_failed':
      return failure.retryable
        ? {
            title: 'Couldn’t finish the import',
            message:
              'Activation failed before the library switch. Your original library is ' +
              'unchanged. Retry to continue where the import stopped.',
            action: 'retry',
          }
        : {
            title: 'The import could not be activated',
            message:
              'Activation failed before the library switch and this host cannot retry it ' +
              'right now. Your original library is unchanged. Restart Nostos, or ask the ' +
              'host operator to review it before trying again.',
            action: 'none',
          };
    case 'migration_activation_recovery_failed':
      return {
        title: 'The library needs host recovery',
        message:
          'Activation could not be completed or rolled back in-process. Nostos kept the ' +
          'host in maintenance to protect your data. Restart the Nostos server, or ask the ' +
          'host operator to reconcile the library, before importing again.',
        action: 'none',
      };
    case 'portable_export_failed':
      return {
        title: 'Couldn’t prepare the export',
        message: 'Nostos could not prepare the archive. Try the export again.',
        action: 'start-over',
      };
    case 'portable_import_provider_limit_reached':
      return {
        title: 'The host’s download limit was reached',
        message:
          'The storage provider blocked the server from reading your archive. ' +
          'Ask the host to make download capacity available, then retry this import.',
        action: 'retry',
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
    case 'migration_invalid_state':
      return {
        title: 'The import is still active or cannot continue',
        message:
          'The host rejected this operation because of the import’s current state. ' +
          'Resume the existing import, or cancel it before starting another.',
        action: 'retry',
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
        message: 'Nostos could not finish this transfer. Try again to continue.',
        action: failure.retryable ? 'retry' : 'choose-file',
      };
  }
}

export type TransferProgressPhase =
  | 'hashing'
  | 'preparing'
  | 'uploading'
  | 'checking'
  | 'preparing-library'
  | 'activating';

/** Visible and announced label per progress phase (plan §25, §43). */
export const TRANSFER_PROGRESS_LABELS: Record<TransferProgressPhase, string> = {
  hashing: 'Checking file',
  preparing: 'Preparing import',
  uploading: 'Uploading library',
  checking: 'Checking the archive',
  'preparing-library': 'Preparing your library',
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

/**
 * Status line while the coordinator waits out exclusive server maintenance
 * (`migration_activation_busy` / `migration_storage_contended`) and re-attempts
 * the interrupted operation.
 */
export function maintenanceRetryMessage(): string {
  return 'Nostos is busy finishing another library operation. Retrying automatically…';
}

/**
 * Truthful status line for the server's activation phase (plan §30). The
 * browser never invents progress: `null` means the run was accepted but the
 * server has not reported a phase yet.
 */
export function activationPhaseMessage(phase: MigrationActivationPhase | null): string {
  switch (phase) {
    case 'Queued':
      return 'Waiting for the host to start the switch…';
    case 'Preparing':
      return 'Preparing your imported library…';
    case 'Activating':
      return 'Switching libraries…';
    case 'Finalizing':
      return 'Finishing the library switch…';
    default:
      return 'The host accepted the switch and is starting…';
  }
}

/** Completion note for the retained previous library, using the server's expiry. */
export function activationRecoveryNote(expiresAtUtc: string | null | undefined): string | null {
  if (!expiresAtUtc) return null;
  const expires = Date.parse(expiresAtUtc);
  if (Number.isNaN(expires)) return null;
  return (
    'Your previous library was kept as a recovery copy until ' +
    `${new Date(expires).toLocaleString()}.`
  );
}
