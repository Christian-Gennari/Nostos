/**
 * Provider-neutral direct part-upload contract for library migration
 * (#680 slice B9; plan §42, §78).
 *
 * A host may answer the per-chunk `uploadChunk` operation with a two-step
 * direct flow instead of accepting the bytes itself:
 *
 * 1. the browser asks the application server for a bounded window of
 *    short-lived storage targets for 1-based part numbers
 *    (`POST .../upload-session/part-tickets`);
 * 2. the browser PUTs each part slice straight to its target with no
 *    application credentials and no custom headers;
 * 3. the browser reports the finished parts to the application server
 *    (`POST .../upload-session/reconcile`), whose session response is the
 *    authority on which parts are received. The server verifies the bytes
 *    itself; the browser never asserts a provider receipt token.
 *
 * Nothing storage-provider-specific crosses this boundary: a ticket is only a
 * part number, its session-derived range and a URL. The application server
 * remains the receipt authority and the finalizer.
 */

/** One browser-reported finished part: number, exact bytes and SHA-256. */
export interface MigrationPartClaimDto {
  partNumber: number;
  sha256: string;
  lengthBytes: number;
}

/** Batch request for a bounded window of 1-based part numbers. */
export interface MigrationPartTicketRequestDto {
  sessionId: string;
  partNumbers: number[];
}

/** One signed storage target for an exact part and byte range. */
export interface MigrationPartTicketDto {
  partNumber: number;
  chunkIndex: number;
  offsetBytes: number;
  lengthBytes: number;
  uploadUrl: string;
}

/** The bounded part-target window the browser sends parts to. */
export interface MigrationPartTicketBatchDto {
  sessionId: string;
  jobId: string;
  expiresAtUtc: string;
  tickets: MigrationPartTicketDto[];
}

/** Reconcile request: the finished parts the server should verify. */
export interface MigrationPartReconcileRequestDto {
  sessionId: string;
  parts: MigrationPartClaimDto[];
}
