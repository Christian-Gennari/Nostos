/** Request for audiobook -> EPUB passage alignment. */
export interface LocateTextRequest {
  positionSeconds: number;
  targetBookId: string;
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

export interface AlignmentLocatedResult {
  located: true;
  score: number;
  excerpt: string;
  locator: AlignmentEpubLocator;
}

export interface AlignmentDeclinedResult {
  located: false;
  reason: string | null;
}

export type LocateTextResult =
  | AlignmentLocatedResult
  | AlignmentDeclinedResult;

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
