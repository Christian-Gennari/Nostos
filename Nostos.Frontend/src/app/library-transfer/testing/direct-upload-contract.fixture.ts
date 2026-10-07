/**
 * Wire-contract fixture for direct part upload (#680 slice B9).
 *
 * These literal shapes are the hosted transport contract the browser must
 * match: the application server signs a bounded window of storage targets for
 * 1-based part numbers, and verifies browser-reported parts during
 * reconciliation. The transport specs build their fake server responses from
 * these factories, so a field-name or casing drift fails a test instead of
 * silently failing against the real host. No storage-provider detail is part
 * of the fixture.
 */

import type {
  MigrationPartTicketBatchDto,
  MigrationPartTicketDto,
} from '../models/direct-part-upload.dtos';
import { chunkLength } from '../models/migration-http.dtos';

export const FIXTURE_JOB_ID = '11111111-1111-4111-8111-111111111111';
export const FIXTURE_SESSION_ID = '22222222-2222-4222-8222-222222222222';

/** Route segments under `{base}/jobs/{id}/upload-session`. */
export const PART_TICKETS_SEGMENT = 'part-tickets';
export const RECONCILE_SEGMENT = 'reconcile';

/** One signed target for an exact part and byte range. */
export function partTicketFixture(
  chunkIndex: number,
  session: { totalBytes: number; chunkSize: number },
): MigrationPartTicketDto {
  const lengthBytes = chunkLength(chunkIndex, session.totalBytes, session.chunkSize);
  return {
    partNumber: chunkIndex + 1,
    chunkIndex,
    offsetBytes: chunkIndex * session.chunkSize,
    lengthBytes,
    uploadUrl: `https://storage.example/part-${chunkIndex + 1}?sig=test-signature`,
  };
}

/** Response body of `POST .../upload-session/part-tickets`. */
export function partTicketBatchFixture(
  sessionId: string,
  session: { totalBytes: number; chunkSize: number },
  chunkIndexes: number[],
  expiresAtUtc: string,
  jobId = FIXTURE_JOB_ID,
): MigrationPartTicketBatchDto {
  return {
    sessionId,
    jobId,
    expiresAtUtc,
    tickets: chunkIndexes.map((index) => partTicketFixture(index, session)),
  };
}
