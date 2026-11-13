import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';

import { TransferCancelledError } from './upload.models';
import {
  readUploadErrorBody,
  toUploadFailure,
  UploadFailure,
} from './upload-error';

function httpFailure(
  body: unknown,
  status: number,
  headers: Record<string, string> = {},
): HttpErrorResponse {
  return new HttpErrorResponse({
    error: body,
    status,
    statusText: 'Failed',
    headers: new HttpHeaders(headers),
  });
}

describe('toUploadFailure', () => {
  it('reads the book shape { error, message }', () => {
    const failure = toUploadFailure({
      error: 'book_upload_part_hash_mismatch',
      message: 'The part did not match its checksum.',
      status: 422,
    });
    expect(failure).toBeInstanceOf(UploadFailure);
    expect(failure.code).toBe('book_upload_part_hash_mismatch');
    expect(failure.status).toBe(422);
    expect(failure.message).toBe('The part did not match its checksum.');
  });

  it('reads the Calibre shape { code, message, missingParts }', () => {
    const failure = toUploadFailure({
      code: 'calibre_upload_incomplete',
      message: 'The upload is missing parts.',
      missingParts: [2, 5],
      status: 409,
    });
    expect(failure.code).toBe('calibre_upload_incomplete');
    expect(failure.missingParts).toEqual([2, 5]);
  });

  it('reads an Angular HTTP error with either body spelling', () => {
    const book = toUploadFailure(
      httpFailure({ error: 'book_upload_expired', message: 'Gone.' }, 410, {
        'Retry-After': '5',
      }),
    );
    expect(book.code).toBe('book_upload_expired');
    expect(book.status).toBe(410);
    expect(book.retryAfterMs).toBe(5000);

    const calibre = toUploadFailure(
      httpFailure({ code: 'calibre_upload_expired', message: 'Gone.' }, 410),
    );
    expect(calibre.code).toBe('calibre_upload_expired');
  });

  it('reads a body Angular handed over as text', () => {
    const failure = toUploadFailure(
      httpFailure(JSON.stringify({ code: 'calibre_upload_not_calibre', message: 'Not it.' }), 422),
    );
    expect(failure.code).toBe('calibre_upload_not_calibre');
    expect(failure.message).toBe('Not it.');
  });

  it('names a connection that never reached the server', () => {
    const failure = toUploadFailure(httpFailure(null, 0));
    expect(failure.code).toBe('network_error');
    expect(failure.status).toBe(0);
  });

  it('reads a browser cancellation as request_aborted, never retryable', () => {
    const failure = toUploadFailure(new TransferCancelledError());
    expect(failure.code).toBe('request_aborted');
    expect(failure.retryable).toBe(false);
  });

  it.each([
    [0, true],
    [408, true],
    [429, true],
    [503, true],
    [413, false],
    [500, false],
    [422, false],
  ])('marks status %i retryable=%s', (status, retryable) => {
    expect(toUploadFailure({ code: 'book_upload_part_conflict', status }).retryable).toBe(
      retryable,
    );
  });

  it('keeps an unknown failure readable', () => {
    const failure = toUploadFailure(new Error('odd'));
    expect(failure.code).toBe('unexpected_error');
    expect(failure.message).toBe('odd');
    expect(failure.status).toBe(0);
  });
});

describe('readUploadErrorBody', () => {
  it('accepts both spellings and ignores a body that carries neither', () => {
    expect(readUploadErrorBody({ code: 'x', message: 'm' })).toEqual({
      code: 'x',
      message: 'm',
      missingParts: undefined,
    });
    expect(readUploadErrorBody({ error: 'y', message: 'm' })).toEqual({
      code: 'y',
      message: 'm',
      missingParts: undefined,
    });
    expect(readUploadErrorBody({ detail: 'nothing' })).toBeNull();
    expect(readUploadErrorBody('<html>')).toBeNull();
  });
});
