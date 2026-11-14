import { describe, expect, it } from 'vitest';

import { uploadFinishedProgress, uploadStartingProgress } from './upload-progress';

describe('upload progress frames', () => {
  it('starts in the preparing phase, at zero, with nothing moved', () => {
    expect(uploadStartingProgress(2048)).toEqual({
      phase: 'preparing',
      percent: 0,
      uploadedBytes: 0,
      movedBytes: 0,
      totalBytes: 2048,
    });
  });

  it('ends in the finishing phase, with every byte both sent and receipted', () => {
    expect(uploadFinishedProgress(2048)).toEqual({
      phase: 'finishing',
      percent: 100,
      uploadedBytes: 2048,
      movedBytes: 2048,
      totalBytes: 2048,
    });
  });

  it('handles an empty file without a division', () => {
    expect(uploadFinishedProgress(0)).toEqual({
      phase: 'finishing',
      percent: 100,
      uploadedBytes: 0,
      movedBytes: 0,
      totalBytes: 0,
    });
  });
});
