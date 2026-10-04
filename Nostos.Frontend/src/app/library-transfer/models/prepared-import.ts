/**
 * Server-derived prepared-import facts (plan §27, review-724 counts item).
 *
 * The pre-upload manifest counts are provisional: some count fields are not in
 * older portable manifests and read zero client-side. The final replacement
 * confirmation must therefore prefer the counts the server derived from the
 * verified staging (where the host reports them) and label manifest-derived
 * numbers as estimates. The transport DTO carries this as `unknown` until the
 * #679/#681 contract freezes it, so the reader below fails closed rather than
 * trusting a shape it did not get.
 */

import type { MigrationArchiveCountsDto } from './migration-http.dtos';

export interface PreparedImportFacts {
  /** Counts the server derived from the verified, prepared import. */
  incomingCounts?: MigrationArchiveCountsDto;
  /** Recovery snapshot size the host reserved for the replacement, if known. */
  recoveryBytes?: number;
  /** Destination revision bound to this prepared import, when exposed. */
  destinationRevision?: string;
}

/** Reads the known fields out of the DTO's currently-untyped `preparedImport`. */
export function readPreparedImportFacts(value: unknown): PreparedImportFacts | null {
  if (typeof value !== 'object' || value === null) return null;

  const record = value as Record<string, unknown>;
  const facts: PreparedImportFacts = {};

  const counts = record['incomingCounts'];
  if (isArchiveCounts(counts)) facts.incomingCounts = counts;

  const recoveryBytes = record['recoveryBytes'];
  if (typeof recoveryBytes === 'number' && Number.isFinite(recoveryBytes)) {
    facts.recoveryBytes = recoveryBytes;
  }

  const destinationRevision = record['destinationRevision'];
  if (typeof destinationRevision === 'string') {
    facts.destinationRevision = destinationRevision;
  }

  return Object.keys(facts).length > 0 ? facts : null;
}

function isArchiveCounts(value: unknown): value is MigrationArchiveCountsDto {
  if (typeof value !== 'object' || value === null) return false;
  const record = value as Record<string, unknown>;
  return (
    typeof record['books'] === 'number' &&
    typeof record['notes'] === 'number' &&
    typeof record['collections'] === 'number'
  );
}
