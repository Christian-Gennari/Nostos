/**
 * Wire contract for resumable book-file uploads (#1266 Phase A).
 *
 * The server is authoritative for the part size, part count and which parts
 * it has already stored. A client resumes by sending the same file identity:
 * size, name and whole-file SHA-256. No book row is hidden while a session is
 * active; the session endpoint is the only record that an upload is running.
 */

export type BookUploadState =
  | 'Created'
  | 'Receiving'
  | 'Paused'
  | 'Completing'
  | 'Complete'
  | 'Failed'
  | 'Discarded'
  | 'Expired';

export interface BookUploadStatusDto {
  sessionId: string;
  bookId: string;
  state: BookUploadState;
  totalBytes: number;
  chunkSize: number;
  totalChunks: number;
  fileName: string;
  fileSha256: string;
  /** Part indexes the server has durably stored. */
  receivedParts: number[];
  receivedBytes: number;
  createdAtUtc: string;
  expiresAtUtc: string;
  completedAtUtc?: string | null;
  failureCode?: string | null;
}

export interface BookUploadCreateRequestDto {
  totalBytes: number;
  fileName: string;
  contentType: string;
  sha256: string;
  clientFingerprint?: string | null;
}

export interface BookUploadPartResultDto {
  sessionId: string;
  partIndex: number;
  alreadyPresent: boolean;
}

/** Mirrors `BookUploadLimits` on the server. */
export const BOOK_UPLOAD_LIMITS = {
  maxBookBytes: 4 * 1024 * 1024 * 1024,
  partBytes: 16 * 1024 * 1024,
} as const;

/** Stable failure codes: server codes plus the two this client can produce. */
export type BookUploadErrorCode =
  | 'book_upload_not_found'
  | 'book_upload_book_not_found'
  | 'book_upload_invalid_request'
  | 'book_upload_unsupported_type'
  | 'book_upload_invalid_state'
  | 'book_upload_in_progress'
  | 'book_upload_part_conflict'
  | 'book_upload_part_hash_mismatch'
  | 'book_upload_part_range_invalid'
  | 'book_upload_file_hash_mismatch'
  | 'book_upload_expired'
  | 'book_upload_storage_exhausted'
  | 'book_upload_publish_failed'
  | 'request_aborted'
  | 'network_error'
  | 'unexpected_error';
