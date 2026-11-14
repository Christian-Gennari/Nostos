import { describe, expect, it } from 'vitest';

import { ALIGNMENT_UNAVAILABLE_CODES, isAlignmentUnavailableCode } from './alignment.dtos';

describe('isAlignmentUnavailableCode', () => {
  it.each([...ALIGNMENT_UNAVAILABLE_CODES])('recognises %s as an availability problem', (code) => {
    expect(isAlignmentUnavailableCode(code)).toBe(true);
  });

  it.each(['not_found', 'passage_not_found', 'bad_request', '', null, undefined, 42, {}])(
    'does not mistake %s for an availability problem',
    (code) => {
      expect(isAlignmentUnavailableCode(code)).toBe(false);
    },
  );
});
