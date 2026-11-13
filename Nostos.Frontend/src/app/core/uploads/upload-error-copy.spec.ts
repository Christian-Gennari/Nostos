import { describeUploadFailure, canonicalUploadCode } from './upload-error-copy';
import { TransferCancelledError } from './upload.models';

describe('canonicalUploadCode', () => {
  it.each([
    ['book_upload_expired', 'expired'],
    ['calibre_upload_expired', 'expired'],
    ['book_upload_part_hash_mismatch', 'part_hash_mismatch'],
    ['calibre_upload_part_hash_mismatch', 'part_hash_mismatch'],
    ['book_import_in_progress', 'in_progress'],
    ['book_has_file', 'has_file'],
    ['request_aborted', 'request_aborted'],
  ])('reads %s as %s', (code, canonical) => {
    expect(canonicalUploadCode(code)).toBe(canonical);
  });

  it('has nothing to read in a missing code', () => {
    expect(canonicalUploadCode(null)).toBeNull();
    expect(canonicalUploadCode(undefined)).toBeNull();
  });
});

describe('describeUploadFailure', () => {
  it('shows the server reason when it rejected the file bytes', () => {
    const copy = describeUploadFailure({
      code: 'book_upload_invalid_file',
      status: 400,
      message: 'This file is empty.',
    });
    expect(copy).toBe('This file is empty. Choose a different file.');
  });

  it.each([
    ['book_upload_expired', 410, 'expired', 'same file'],
    ['book_upload_file_hash_mismatch', 422, 'changed', 'same file'],
    ['book_upload_storage_exhausted', 507, 'not enough free storage', null],
    ['book_upload_unsupported_type', 400, 'file type', null],
    ['book_upload_in_progress', 409, 'already importing', null],
    ['book_upload_part_hash_mismatch', 422, 'retry that part', null],
  ] as const)('maps %s to reader-facing copy', (code, statusCode, phrase, second) => {
    const copy = describeUploadFailure({ code, status: statusCode });
    expect(copy).toContain(phrase);
    if (second) expect(copy).toContain(second);
  });

  it.each([
    'part_hash_mismatch',
    'file_hash_mismatch',
    'expired',
    'storage_exhausted',
    'part_conflict',
    'part_range_invalid',
  ])('reads the same for a book and a Calibre %s', (canonical) => {
    const book = describeUploadFailure({ code: `book_upload_${canonical}`, status: 400 });
    const calibre = describeUploadFailure({ code: `calibre_upload_${canonical}`, status: 400 });
    expect(calibre).toBe(book);
  });

  it('reads the same dropped connection for both flows', () => {
    const book = describeUploadFailure({ code: 'network_error', status: 0 });
    const calibre = describeUploadFailure({ code: 'calibre_upload_failed', status: 0 });
    expect(calibre).toBe(book);
    expect(book).toContain('The connection was interrupted. Choose the same file to resume.');
  });

  it('tells the Add book reader that the row survives a dropped connection', () => {
    const copy = describeUploadFailure({ code: 'network_error', status: 0 }, { bookSaved: true });
    expect(copy).toContain('connection was interrupted');
    expect(copy).toContain('Your book is saved');
    expect(copy).toContain('same file to resume');
  });

  it('offers a retry, not a resume, for the legacy whole-request upload', () => {
    const copy = describeUploadFailure({ status: 0 }, { bookSaved: true, resumable: false });
    expect(copy).toBe('The connection was interrupted. Your book is saved; try the file again.');
  });

  it('maps a bare status when the server sends no code', () => {
    expect(describeUploadFailure({ status: 413 })).toBe(
      'This file is larger than this server allows. Choose a smaller file to continue.',
    );
    expect(describeUploadFailure({ status: 409 })).toContain('already importing');
  });

  it('keeps a Calibre server reason for a library it refused', () => {
    const copy = describeUploadFailure({
      code: 'calibre_upload_unsafe_entry',
      status: 422,
      message: 'The zip contains a symbolic link. Create it without links.',
    });
    expect(copy).toBe('The zip contains a symbolic link. Create it without links.');
  });

  it('treats a browser cancellation as a cancellation in every flow', () => {
    expect(describeUploadFailure(new TransferCancelledError())).toBe(
      'The upload was cancelled.',
    );
  });

  it('keeps a hosting plan’s own limit wording', () => {
    const error = Object.assign(new Error('limit'), {
      cause: {
        error: {
          code: 'storage_limit_reached',
          detail: 'Your library has no room left.',
        },
      },
      code: 'unexpected_error',
      status: 413,
    });
    expect(describeUploadFailure(error)).toBe('Your library has no room left.');
  });
});
