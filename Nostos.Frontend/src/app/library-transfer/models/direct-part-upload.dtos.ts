/**
 * Provider-neutral direct part-upload contract for library migration
 * (#680 slice B9; plan §42, §78).
 *
 * A host may answer the per-chunk `uploadChunk` operation with a two-step
 * direct flow instead of accepting the bytes itself:
 *
 * 1. the browser asks the application server for a short-lived upload ticket
 *    for one archive part (`POST .../upload-session/parts/{index}/ticket`);
 * 2. the browser sends the `Blob` slice straight to the ticket target with
 *    exactly the ticket's headers and no application credentials;
 * 3. the browser reports the target's completion token (the response header
 *    named `DIRECT_UPLOAD_COMPLETION_HEADER`) back to the application server
 *    (`POST .../upload-session/parts/{index}/complete`).
 *
 * Nothing storage-provider-specific crosses this boundary: the ticket is only
 * a URL, method, required headers and expiry. The application server remains
 * the receipt authority; the target is never asked to authenticate the user.
 */

/** Response header the upload target must expose for a completed part. */
export const DIRECT_UPLOAD_COMPLETION_HEADER = 'ETag';

/**
 * Body of the part-ticket request. The part index is carried in the request
 * path; the server derives the exact byte range from the session contract.
 */
export interface MigrationPartTicketRequestDto {
  lengthBytes: number;
  sha256: string;
}

/** Short-lived authorization to send one part to a storage target. */
export interface MigrationPartTicketDto {
  /** Absolute or relative target URL. */
  url: string;
  /** HTTP method the target expects; object-storage presigned parts use PUT. */
  method: string;
  /**
   * The complete set of headers the target expects. The browser sends exactly
   * these and adds no application credential (cookie, bearer token) of its own.
   */
  requiredHeaders: Readonly<Record<string, string>>;
  /** ISO-8601 UTC instant after which the ticket must not be used. */
  expiresAtUtc: string;
}

/**
 * Body that reports a target-accepted part to the application server. The
 * target's completion token is provider evidence, not authorization.
 */
export interface MigrationPartCompletionRequestDto {
  etag: string;
  sha256: string;
  byteLength: number;
}

/** True when the ticket is already past its expiry at `nowMs`. */
export function isPartTicketExpired(ticket: MigrationPartTicketDto, nowMs: number): boolean {
  const expires = Date.parse(ticket.expiresAtUtc);
  return Number.isFinite(expires) && expires <= nowMs;
}
