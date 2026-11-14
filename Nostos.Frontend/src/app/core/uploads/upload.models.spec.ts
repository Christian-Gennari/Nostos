import { describe, expect, it } from 'vitest';

import { TransferCancelledError, chunkLength } from './upload.models';

describe('chunkLength', () => {
  it('gives every full part the session’s part size', () => {
    expect(chunkLength(0, 10, 4)).toBe(4);
    expect(chunkLength(1, 10, 4)).toBe(4);
  });

  it('gives the last part only the bytes that are left', () => {
    expect(chunkLength(2, 10, 4)).toBe(2);
    expect(chunkLength(0, 3, 4)).toBe(3);
  });

  it('gives nothing past the end of the file', () => {
    expect(chunkLength(3, 10, 4)).toBe(0);
    expect(chunkLength(10, 10, 4)).toBe(0);
  });
});

describe('TransferCancelledError', () => {
  it('is a deliberate stop, told apart from a transport failure', () => {
    const error = new TransferCancelledError();

    expect(error).toBeInstanceOf(Error);
    expect(error.name).toBe('TransferCancelledError');
    expect(error.message).toBe('The operation was cancelled.');
  });

  it('keeps the caller’s own wording', () => {
    expect(new TransferCancelledError('Hashing was cancelled.').message).toBe(
      'Hashing was cancelled.',
    );
  });
});
