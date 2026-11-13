/**
 * The visible progress every resumable upload reports (#1646): a phase, a
 * percentage for that phase, and the bytes behind it. Book and Calibre uploads
 * render the same shape, so neither flow invents its own progress maths.
 */

export type UploadPhase = 'preparing' | 'uploading' | 'finishing';

export interface UploadProgress {
  phase: UploadPhase;
  /** 0-100 for the phase the user is looking at. */
  percent: number;
  /** Bytes the server has durably receipted (0 while hashing). */
  uploadedBytes: number;
  /** Bytes moved including bodies still in flight; what `percent` tracks. */
  movedBytes: number;
  totalBytes: number;
}

export type UploadProgressReporter = (progress: UploadProgress) => void;

/** The first frame, shown while the digest starts. */
export function uploadStartingProgress(totalBytes: number): UploadProgress {
  return {
    phase: 'preparing',
    percent: 0,
    uploadedBytes: 0,
    movedBytes: 0,
    totalBytes,
  };
}

/** 100% in the final phase; the server has every byte and is finalising. */
export function uploadFinishedProgress(totalBytes: number): UploadProgress {
  return {
    phase: 'finishing',
    percent: 100,
    uploadedBytes: totalBytes,
    movedBytes: totalBytes,
    totalBytes,
  };
}
