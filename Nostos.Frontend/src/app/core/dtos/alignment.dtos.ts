/** Request for audiobook -> EPUB passage alignment. */
export interface LocateTextRequest {
  positionSeconds: number;
  targetBookId: string;
}

/** Request for EPUB -> audiobook position alignment. */
export interface LocateAudioRequest {
  targetBookId: string;
  excerpt: string;
}

/**
 * EPUB position returned by the alignment matcher.
 * The fields correspond to the reader shell's grounded-source navigation.
 */
export interface AlignmentEpubLocator {
  resourceHref?: string | null;
  spineIndex?: number | null;
  textOffset?: number | null;
  cfi?: string | null;
}

/** Where a located passage ends in a PDF edition. */
export interface AlignmentPdfLocator {
  /** Zero-based physical page. */
  pageIndex: number;
  /** One-based physical page, as the reader navigates. */
  pageNumber: number;
  pageLabel?: string | null;
  textOffset?: number | null;
}

export interface AlignmentLocatedResult {
  located: true;
  score: number;
  excerpt: string;
  /** Present for an EPUB target. */
  locator?: AlignmentEpubLocator | null;
  /** Present for a PDF target. */
  pdf?: AlignmentPdfLocator | null;
  /** The book's own wording for the passage, for highlighting it. */
  sourceText?: string | null;
  anchorStored?: boolean;
}

export interface AlignmentDeclinedResult {
  located: false;
  reason: string | null;
}

export type LocateTextResult =
  | AlignmentLocatedResult
  | AlignmentDeclinedResult;

/** Confident EPUB -> audiobook match. All positions are in seconds. */
export interface AlignmentAudioLocatedResult {
  located: true;
  positionSeconds: number;
  probes: number;
  score: number;
  anchorStored: boolean;
}

/** No confident match; a coarse position may still be available. */
export interface AlignmentAudioDeclinedResult {
  located: false;
  reason: string | null;
  approximatePositionSeconds?: number | null;
  probes: number;
}

export type LocateAudioResult =
  | AlignmentAudioLocatedResult
  | AlignmentAudioDeclinedResult;

/**
 * ASP.NET Core ProblemDetails responses put the typed error code in `title`.
 * Do not display arbitrary server-provided details or exception messages.
 */
export interface AlignmentProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
}

/**
 * Errors indicating that transcription cannot be used in this deployment
 * or configuration, rather than an ordinary passage-matching failure.
 */
export const ALIGNMENT_UNAVAILABLE_CODES = new Set([
  'stt_disabled',
  'stt_not_configured',
  'stt_unavailable',
  'ai_disabled',
  'ai_temporarily_unavailable',
  'ai_access_denied',
  'stt_access_denied',
  'alignment_transcription_disabled',
  'alignment_transcription_not_configured',
  'alignment_transcription_unavailable',
  'audio_clip_unavailable',
]);

export function isAlignmentUnavailableCode(code: unknown): boolean {
  return typeof code === 'string' && ALIGNMENT_UNAVAILABLE_CODES.has(code);
}
