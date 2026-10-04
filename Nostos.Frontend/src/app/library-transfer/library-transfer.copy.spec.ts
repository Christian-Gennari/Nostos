import {
  formatBytes,
  formatEta,
  formatLibraryCounts,
  formatRate,
  libraryTransferFailureCopy,
  storageFailureMessage,
  transferProgressLiveText,
  transferProgressValueText,
} from './library-transfer.copy';
import type { LibraryTransferFailure } from './models/library-transfer.models';
import {
  CLIENT_MIGRATION_ERROR_CODES,
  SERVER_MIGRATION_ERROR_CODES,
  type MigrationErrorCode,
} from './models/migration-http.dtos';

function failure(
  code: MigrationErrorCode,
  extra: Partial<LibraryTransferFailure> = {},
): LibraryTransferFailure {
  return { code, message: 'backend message', retryable: false, ...extra };
}

describe('library-transfer.copy', () => {
  describe('formatBytes', () => {
    it('formats bytes across the binary unit scale', () => {
      expect(formatBytes(0)).toBe('0 B');
      expect(formatBytes(512)).toBe('512 B');
      expect(formatBytes(1024)).toBe('1 KiB');
      expect(formatBytes(1536)).toBe('1.5 KiB');
      expect(formatBytes(25 * 1024 * 1024)).toBe('25 MiB');
      expect(formatBytes(2.5 * 1024 * 1024 * 1024)).toBe('2.5 GiB');
      expect(formatBytes(5 * 1024 ** 4)).toBe('5 TiB');
    });

    it('returns an empty string for unknown sizes', () => {
      expect(formatBytes(null)).toBe('');
      expect(formatBytes(undefined)).toBe('');
      expect(formatBytes(Number.NaN)).toBe('');
      expect(formatBytes(-1)).toBe('');
    });
  });

  describe('formatRate and formatEta', () => {
    it('formats a rate with a unit and suppresses non-positive rates', () => {
      expect(formatRate(12 * 1024 * 1024)).toBe('12 MiB/s');
      expect(formatRate(0)).toBeNull();
      expect(formatRate(null)).toBeNull();
    });

    it('rounds the remaining time coarsely and suppresses unreliable ETAs', () => {
      expect(formatEta(30)).toBe('Less than a minute remaining');
      expect(formatEta(90)).toBe('About 2 minutes remaining');
      expect(formatEta(7200)).toBe('About 2 hours remaining');
      expect(formatEta(0)).toBeNull();
      expect(formatEta(null)).toBeNull();
    });
  });

  it('summarizes the counts the replacement copy compares', () => {
    expect(formatLibraryCounts({ books: 74, notes: 418, collections: 12 })).toBe(
      '74 books · 418 notes · 12 collections',
    );
  });

  describe('storageFailureMessage', () => {
    it('states required and available storage in human-readable sizes', () => {
      const message = storageFailureMessage(
        38.4 * 1024 ** 3,
        22.1 * 1024 ** 3,
      );
      expect(message).toContain('38.4 GiB');
      expect(message).toContain('22.1 GiB');
      expect(message).toContain('temporary import and recovery space');
    });

    it('falls back to an actionable sentence without structured numbers', () => {
      expect(storageFailureMessage(undefined, undefined)).toContain('Free up space');
    });
  });

  describe('libraryTransferFailureCopy', () => {
    it('maps archive failures to a file-selection recovery', () => {
      expect(libraryTransferFailureCopy(failure('archive_not_portable')).action).toBe('choose-file');
      expect(libraryTransferFailureCopy(failure('archive_operational_backup')).action).toBe(
        'choose-file',
      );
      expect(libraryTransferFailureCopy(failure('archive_operational_backup')).message).toContain(
        'SelfHosted backup',
      );
      expect(libraryTransferFailureCopy(failure('archive_unsupported_version')).action).toBe(
        'choose-file',
      );
    });

    it('maps storage exhaustion with the server numbers and a retry recovery', () => {
      const copy = libraryTransferFailureCopy(
        failure('migration_storage_exhausted', {
          requiredStorageBytes: 2 * 1024 ** 3,
          availableStorageBytes: 1024 ** 3,
        }),
      );
      expect(copy.action).toBe('retry');
      expect(copy.message).toContain('2 GiB');
      expect(copy.message).toContain('1 GiB');
    });

    it('maps session expiry and checksum failures to retry', () => {
      expect(libraryTransferFailureCopy(failure('migration_session_expired')).action).toBe('retry');
      expect(libraryTransferFailureCopy(failure('migration_chunk_hash_mismatch')).action).toBe(
        'retry',
      );
    });

    it('maps destination conflict to a fresh review', () => {
      const copy = libraryTransferFailureCopy(failure('migration_destination_conflict'));
      expect(copy.action).toBe('start-over');
      expect(copy.message).toContain('did not replace it');
    });

    it('maps a failed export job to a restart', () => {
      expect(libraryTransferFailureCopy(failure('portable_export_failed')).action).toBe('start-over');
    });

    it('maps the newly surfaced host states to sensible recoveries', () => {
      expect(libraryTransferFailureCopy(failure('migration_storage_contended')).action).toBe('retry');
      expect(libraryTransferFailureCopy(failure('migration_activation_busy')).action).toBe('retry');
      expect(libraryTransferFailureCopy(failure('migration_lease_conflict')).action).toBe('retry');
      expect(
        libraryTransferFailureCopy(failure('migration_import_preparation_unavailable')).action,
      ).toBe('none');
      expect(
        libraryTransferFailureCopy(failure('migration_export_artifact_unavailable')).action,
      ).toBe('none');
    });

    it('maps a stale job to a new import', () => {
      expect(libraryTransferFailureCopy(failure('migration_not_found')).action).toBe('choose-file');
    });

    it('never surfaces the backend message verbatim and retries only retryable transport failures', () => {
      const retryable = libraryTransferFailureCopy(
        failure('network_error', { message: 'socket exploded', retryable: true }),
      );
      expect(retryable.action).toBe('retry');
      expect(retryable.message).not.toContain('socket exploded');

      const permanent = libraryTransferFailureCopy(
        failure('unexpected_error', { message: 'socket exploded' }),
      );
      expect(permanent.action).toBe('choose-file');
    });

    it('asks cloud users to sign in and keeps the import resumable instead of implying archive corruption', () => {
      const copy = libraryTransferFailureCopy(failure('unexpected_error', { status: 401 }));
      expect(copy.title).toBe('Sign in again');
      expect(copy.action).toBe('sign-in');
      expect(copy.message).toContain('kept');
      expect(copy.message).not.toContain('corrupt');
    });

    it('produces product copy and a valid recovery for every stable error code', () => {
      const codes: MigrationErrorCode[] = [
        ...SERVER_MIGRATION_ERROR_CODES,
        ...CLIENT_MIGRATION_ERROR_CODES,
      ];
      const actions = new Set(['retry', 'sign-in', 'choose-file', 'start-over', 'none']);

      for (const code of codes) {
        const copy = libraryTransferFailureCopy(failure(code, { retryable: true }));
        expect(copy.title.length, code).toBeGreaterThan(0);
        expect(copy.message.length, code).toBeGreaterThan(0);
        expect(actions.has(copy.action), `${code} -> ${copy.action}`).toBe(true);
      }
    });
  });

  describe('progress text', () => {
    it('buckets the live announcement to five-point steps', () => {
      expect(transferProgressLiveText('uploading', 3)).toBe(
        'Uploading library, 0 percent complete.',
      );
      expect(transferProgressLiveText('uploading', 42)).toBe(
        'Uploading library, 40 percent complete.',
      );
      expect(transferProgressLiveText('uploading', 99)).toBe(
        'Uploading library, 95 percent complete.',
      );
    });

    it('announces an indeterminate phase by name', () => {
      expect(transferProgressValueText('checking', null)).toBe('Checking the archive…');
      expect(transferProgressValueText('hashing', 50)).toContain('50 percent');
    });
  });
});
