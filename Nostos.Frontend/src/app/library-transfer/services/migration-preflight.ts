/**
 * Browser-reduced mirror of `MigrationPreflightEvaluator` for the mock
 * transport (plan B1). The server remains authoritative; this exists so the
 * mock rejects the same obvious cases and returns the same decision/capacity
 * shape the real `POST /preflight` will.
 */

import {
  MigrationArchiveCountsDto,
  MigrationDestinationStatus,
  MigrationExistingCountsDto,
  MigrationPreflightDecision,
  MigrationPreflightRequestDto,
  MigrationPreflightResultDto,
  MIGRATION_LIMITS,
} from '../models/migration-http.dtos';

const ESTIMATED_RECOVERY_BYTES_PER_EXISTING_BOOK = 50_000_000;

const SUPPORTED_FORMAT_VERSIONS = new Set([1]);
const SUPPORTED_DATA_VERSIONS = new Set([1, 2, 3]);

const OPERATIONAL_BACKUP_NAMES = new Set(['nostos-operational-backup', 'operational-backup']);

export interface PreflightEvaluationInput {
  destinationStatus: MigrationDestinationStatus;
  existingCounts: MigrationExistingCountsDto;
  availableStorageBytes: number;
  destinationRevision: string;
  operationalBackup: boolean;
}

export function estimateRecoveryBytes(
  destinationStatus: MigrationDestinationStatus,
  existingCounts: MigrationExistingCountsDto,
): number {
  if (destinationStatus !== 'Populated') return 0;
  return (
    existingCounts.books * ESTIMATED_RECOVERY_BYTES_PER_EXISTING_BOOK +
    existingCounts.totalRows * 1024
  );
}

export function calculateRequiredStorageBytes(
  request: MigrationPreflightRequestDto,
  destinationStatus: MigrationDestinationStatus,
  existingCounts: MigrationExistingCountsDto,
): number {
  const recovery = estimateRecoveryBytes(destinationStatus, existingCounts);
  return request.declaredArchiveBytes + request.declaredMediaBytes + recovery;
}

export function evaluatePreflight(
  request: MigrationPreflightRequestDto,
  input: PreflightEvaluationInput,
): MigrationPreflightResultDto {
  const errors: string[] = [];
  const warnings: string[] = [];
  const incomingCounts: MigrationArchiveCountsDto = { ...request.incomingCounts };
  const estimatedRecoveryBytes = estimateRecoveryBytes(
    input.destinationStatus,
    input.existingCounts,
  );
  const requiredStorageBytes =
    calculateRequiredStorageBytes(request, input.destinationStatus, input.existingCounts) || 0;

  const result = (decision: MigrationPreflightDecision, isCompatible: boolean) => ({
    decision,
    isCompatible,
    isAllowed: decision === 'AllowedEmpty' || decision === 'AllowedReplacementRequired',
    incomingCounts,
    existingCounts: input.existingCounts,
    declaredArchiveBytes: request.declaredArchiveBytes,
    declaredMediaBytes: request.declaredMediaBytes,
    estimatedRecoveryBytes,
    requiredStorageBytes,
    availableStorageBytes: input.availableStorageBytes,
    errors: [...errors],
    warnings: [...warnings],
    destinationRevision: input.destinationRevision,
  });

  const formatName = request.declaredFormatName?.toLowerCase();
  if (input.operationalBackup || (formatName && OPERATIONAL_BACKUP_NAMES.has(formatName))) {
    errors.push('Operational backup archives are not portable migration archives.');
    return result('RejectedOperationalBackupNotPortable', false);
  }

  if (!SUPPORTED_FORMAT_VERSIONS.has(request.declaredFormatVersion)) {
    errors.push(`Archive format version ${request.declaredFormatVersion} is not supported.`);
  }
  if (!SUPPORTED_DATA_VERSIONS.has(request.declaredDataVersion)) {
    errors.push(`Archive data version ${request.declaredDataVersion} is not supported.`);
  }
  if (request.declaredArchiveBytes < 0) {
    errors.push('Declared archive bytes cannot be negative.');
  } else if (request.declaredArchiveBytes > MIGRATION_LIMITS.maxArchiveBytes) {
    errors.push('Declared archive size exceeds the migration contract limit.');
  }
  if (request.declaredMediaBytes < 0) {
    errors.push('Declared media bytes cannot be negative.');
  } else if (request.declaredMediaBytes > MIGRATION_LIMITS.maxMediaBytes) {
    errors.push('Declared media size exceeds the migration contract limit.');
  }
  if (request.maxSingleEntryBytes < 0) {
    errors.push('Maximum single-entry size cannot be negative.');
  } else if (request.maxSingleEntryBytes > MIGRATION_LIMITS.maxSingleEntryBytes) {
    errors.push('Archive contains or declares an entry exceeding the single-entry limit.');
  }
  if (errors.length > 0) return result('RejectedIncompatible', false);

  if (
    request.clientDestinationRevision != null &&
    request.clientDestinationRevision !== input.destinationRevision
  ) {
    errors.push('Destination contents changed since the client observed the destination revision.');
    return result('RejectedDestinationConflict', true);
  }

  if (input.availableStorageBytes < requiredStorageBytes) {
    errors.push(
      `Migration requires ${requiredStorageBytes} bytes of available storage, ` +
        `but only ${input.availableStorageBytes} bytes are available.`,
    );
    return result('RejectedInsufficientStorage', true);
  }

  if (input.destinationStatus === 'Populated') {
    warnings.push(
      'Destination contains existing portable data. Activation requires replacement and a ' +
        'mandatory recovery snapshot.',
    );
    return result('AllowedReplacementRequired', true);
  }

  return result('AllowedEmpty', true);
}
