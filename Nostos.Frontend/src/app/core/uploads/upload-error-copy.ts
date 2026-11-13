/**
 * The one map from an upload failure code to the sentence a reader sees
 * (#1646).
 *
 * Book uploads and Calibre uploads used to carry two near-identical switches,
 * so the same failure could read differently depending on which flow hit it.
 * Every prefix is stripped to one canonical code and matched exactly once here;
 * the background is the plan's copy contract: say what happened, then what the
 * reader can do next.
 */

import { planLimitMessage } from '../plan-limit-message';
import { toUploadFailure, UploadFailure } from './upload-error';

export interface UploadFailureContext {
  /** The book row already exists, so say so instead of implying the file arrived. */
  bookSaved?: boolean;
  /**
   * The same file resumes a session (default). `false` for the legacy
   * whole-request upload, which starts over rather than resuming a part.
   */
  resumable?: boolean;
}

type UploadErrorCopy = string | ((failure: UploadFailure, hint: string) => string);

/** A code family with its prefix removed; `book_upload_expired` -> `expired`. */
export function canonicalUploadCode(code: string | undefined | null): string | null {
  if (!code) return null;
  if (code === 'book_import_in_progress') return 'in_progress';
  for (const prefix of ['book_upload_', 'calibre_upload_', 'book_', 'calibre_']) {
    if (code.startsWith(prefix)) return code.slice(prefix.length);
  }
  return code;
}

const PART_REJECTED = 'The server rejected a part of the file.';
const STORAGE_EXHAUSTED = 'There is not enough free storage for this file.';
const CANCELLED = 'The upload was cancelled.';

/**
 * Code -> copy. One entry per canonical code, shared by both flows; a function
 * receives the failure (for a server-supplied sentence) and the recovery hint
 * ("Choose the same file to resume.").
 */
const UPLOAD_ERROR_COPY: Record<string, UploadErrorCopy> = {
  // Shared between book and Calibre uploads.
  part_hash_mismatch:
    'A part of the file did not match its checksum. The upload will retry that part.',
  file_hash_mismatch: (_failure, hint) => `The file changed while it was uploading. ${hint}`,
  expired: (_failure, hint) => `This upload expired. ${hint}`,
  storage_exhausted: STORAGE_EXHAUSTED,
  part_conflict: (_failure, hint) => `${PART_REJECTED} ${hint}`,
  part_range_invalid: (_failure, hint) => `${PART_REJECTED} ${hint}`,
  incomplete: (_failure, hint) => `The upload is missing part of the file. ${hint}`,
  request_aborted: CANCELLED,

  // Book uploads.
  book_not_found: (_failure, hint) => `This upload is no longer available. ${hint}`,
  not_found: (_failure, hint) => `This upload is no longer available. ${hint}`,
  has_file: 'This book already has a file. Your new file was not added.',
  in_progress: 'This book is already importing or uploading a file. Wait until it finishes.',
  unsupported_type: 'This file type cannot be uploaded as a book.',
  invalid_state: (_failure, hint) => `The server rejected this upload in its current state. ${hint}`,
  publish_failed: 'The file was received but could not be added to the book. Try the upload again.',
  invalid_file: (failure) =>
    `${failure.message || 'This file could not be read as a book.'} Choose a different file.`,
  invalid_request: (failure) =>
    failure.status === 413
      ? 'This file is larger than this server allows. Choose a smaller file to continue.'
      : 'The server could not accept this file. Check the file and try again.',

  // Calibre uploads, whose server is the only source of the specific reason.
  invalid_archive: (failure) =>
    serverMessage(failure, 'This is not a Calibre library zip.'),
  unsafe_entry: (failure, hint) =>
    serverMessage(failure, `The library zip contains an unsafe file. ${hint}`),
  too_many_files: (failure) =>
    serverMessage(failure, 'This library has too many files to import.'),
  metadata_missing: (failure) =>
    serverMessage(failure, 'This library has no metadata to import.'),
  not_calibre: (failure) => serverMessage(failure, 'This zip is not a Calibre library.'),
  too_large: (failure) =>
    serverMessage(
      failure,
      'This file is larger than this server allows. Choose a smaller file to continue.',
    ),
  extracted_too_large: (failure) =>
    serverMessage(
      failure,
      'This library is too large once unpacked. Remove some formats and try again.',
    ),
};

/** The failure's own sentence when the server wrote one, else ours. */
function serverMessage(failure: UploadFailure, fallback: string): string {
  return failure.message?.trim() ? failure.message : fallback;
}

function recoveryHint(options: UploadFailureContext): string {
  if (options.resumable === false) {
    return options.bookSaved ? 'Your book is saved; try the file again.' : 'Try the upload again.';
  }
  return options.bookSaved
    ? 'Your book is saved; choose the same file to resume.'
    : 'Choose the same file to resume.';
}

/** What a failure with no code still tells us: its HTTP status. */
function statusCopy(status: number, hint: string): string {
  if (status === 0) return `The connection was interrupted. ${hint}`;
  if (status === 408 || status === 504) return `The upload timed out. ${hint}`;
  if (status === 413) {
    return 'This file is larger than this server allows. Choose a smaller file to continue.';
  }
  if (status === 429) return `Too many uploads are running right now. ${hint}`;
  if (status === 507) return STORAGE_EXHAUSTED;
  if (status === 409) {
    return 'This book is already importing or uploading a file. Wait until it finishes.';
  }
  if (status >= 500) return `The upload was interrupted. ${hint}`;
  return 'The server did not accept this file. Check the file and try again.';
}

/**
 * Reader-facing copy for any failed upload. A hosting plan's own limit wording
 * wins over ours, since it knows the amount that was reached.
 */
export function describeUploadFailure(
  error: unknown,
  options: UploadFailureContext = {},
): string {
  const limit =
    planLimitMessage(error) ?? planLimitMessage((error as { cause?: unknown } | null)?.cause);
  if (limit) return limit;

  const failure = toUploadFailure(error);
  const canonical = canonicalUploadCode(failure.code);
  const hint = recoveryHint(options);

  const copy = canonical ? UPLOAD_ERROR_COPY[canonical] : undefined;
  if (typeof copy === 'string') return copy;
  if (copy) return copy(failure, hint);

  return statusCopy(failure.status, hint);
}
