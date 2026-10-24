/**
 * Push-to-talk voice capture for the assistant (issue #262 §1).
 *
 * This service is the ONLY place the microphone is opened. It is mechanical:
 * request the stream on an explicit tap, record one clip, upload that clip to
 * the existing `POST /api/assistant/transcribe` seam, and hand the text back.
 * It is NOT a second assistant — the transcript re-enters the exact typed
 * pipeline (see `AssistantService.insertTranscript`).
 *
 * THE RULES IT KEEPS
 * ------------------
 *  - `getUserMedia` is requested only on `start()`. The stream is never opened
 *    speculatively, never kept on a timer, and every track is stopped the moment
 *    the recording ends or is cancelled.
 *  - The recorded blob lives only as a local object URL, revoked the instant
 *    the upload settles. Raw audio is never written anywhere and never retained.
 *  - Cancel discards: it stops the tracks, unsubscribes the upload and revokes
 *    the URL, so nothing is sent and no state is left behind.
 *  - Every failure is returned as a human message on `error`; the service always
 *    returns to `idle`, so a denial or a bad upload can never wedge the surface.
 *
 * The client never sees the STT credential: the browser uploads audio and the
 * backend talks to the provider (issue #262 §3).
 */
import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Subscription } from 'rxjs';

/** The existing server-side transcription seam. Do not change the backend. */
export const TRANSCRIPTION_ENDPOINT = '/api/assistant/transcribe';

/** The recording lifecycle. `idle` is both the resting and the error state. */
export type AssistantVoiceStatus = 'idle' | 'requesting' | 'recording' | 'transcribing';

/** Why a voice action failed, so the surface can phrase it without jargon. */
export type AssistantVoiceErrorKind =
  | 'unsupported'
  | 'denied'
  | 'no-device'
  | 'recorder'
  | 'empty'
  | 'failed';

export interface AssistantVoiceError {
  kind: AssistantVoiceErrorKind;
  message: string;
}

/** The success body of `POST /api/assistant/transcribe`. */
export interface TranscriptionResponse {
  text: string;
  language: string | null;
  durationSeconds: number | null;
}

@Injectable({ providedIn: 'root' })
export class AssistantVoiceService {
  private readonly http = inject(HttpClient);

  readonly status = signal<AssistantVoiceStatus>('idle');
  readonly error = signal<AssistantVoiceError | null>(null);
  readonly elapsedSeconds = signal(0);

  readonly isRecording = computed(() => this.status() === 'recording');
  readonly isTranscribing = computed(() => this.status() === 'transcribing');
  readonly isBusy = computed(() => this.status() !== 'idle');

  /**
   * Called once, with the trimmed transcript, after a successful upload. The
   * assistant surface wires this to `AssistantService.insertTranscript`; the
   * service itself stays ignorant of the conversation so it cannot fork one.
   */
  onTranscript: ((text: string) => void) | null = null;

  private stream: MediaStream | null = null;
  private recorder: MediaRecorder | null = null;
  private chunks: Blob[] = [];
  private audioUrl: string | null = null;
  private upload: Subscription | null = null;
  private timer: ReturnType<typeof setInterval> | null = null;
  /** Bumped on every start/cancel, so a late async result is ignored. */
  private session = 0;

  /** Request the microphone and begin recording. A no-op unless idle. */
  start(): void {
    void this.begin();
  }

  /** Stop the live recording and upload it for transcription. */
  stop(): void {
    const recorder = this.recorder;
    if (!recorder || this.status() !== 'recording') return;
    this.stopTimer();
    try {
      recorder.stop();
    } catch {
      // A recorder that never started still needs its state cleared.
      this.finishRecording();
    }
  }

  /**
   * Drop the recording or the in-flight transcription. Stops every track, stops
   * the upload, revokes the object URL and resets to `idle`. Nothing is sent.
   */
  cancel(): void {
    this.session += 1;
    this.stopTimer();
    this.upload?.unsubscribe();
    this.upload = null;

    const recorder = this.recorder;
    this.recorder = null;
    if (recorder) {
      recorder.ondataavailable = null;
      recorder.onstop = null;
      recorder.onerror = null;
      if (recorder.state !== 'inactive') {
        try {
          recorder.stop();
        } catch {
          // Nothing left to stop; the tracks are released below.
        }
      }
    }

    this.chunks = [];
    this.releaseStream();
    this.revokeAudioUrl();
    this.elapsedSeconds.set(0);
    this.error.set(null);
    this.status.set('idle');
  }

  private async begin(): Promise<void> {
    if (this.status() !== 'idle') return;
    const session = (this.session += 1);
    this.error.set(null);
    this.elapsedSeconds.set(0);

    const mediaDevices = typeof navigator !== 'undefined' ? navigator.mediaDevices : undefined;
    if (!mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') {
      this.fail({
        kind: 'unsupported',
        message: 'Recording is not available in this browser.',
      });
      return;
    }

    this.status.set('requesting');
    let stream: MediaStream;
    try {
      stream = await mediaDevices.getUserMedia({ audio: true });
    } catch (error) {
      if (session !== this.session) return;
      this.fail(errorFromUserMedia(error));
      return;
    }

    // Cancelled while the permission prompt was open: keep nothing.
    if (session !== this.session) {
      stream.getTracks().forEach((track) => track.stop());
      return;
    }

    this.stream = stream;
    let recorder: MediaRecorder;
    try {
      recorder = new MediaRecorder(stream);
    } catch {
      this.releaseStream();
      this.fail({ kind: 'recorder', message: 'Could not start recording. Try again.' });
      return;
    }

    this.recorder = recorder;
    this.chunks = [];
    recorder.ondataavailable = (event) => {
      if (event.data && event.data.size > 0) this.chunks.push(event.data);
    };
    recorder.onstop = () => this.finishRecording();
    recorder.onerror = () => {
      this.releaseStream();
      this.stopTimer();
      this.recorder = null;
      this.fail({ kind: 'recorder', message: 'Recording stopped unexpectedly. Try again.' });
    };
    recorder.start();
    this.status.set('recording');
    this.startTimer();
  }

  /** The recorder stopped: build the blob and upload it. */
  private finishRecording(): void {
    const recorder = this.recorder;
    this.recorder = null;
    this.stopTimer();
    this.releaseStream();

    const chunks = this.chunks;
    this.chunks = [];
    const blob = new Blob(chunks, { type: recorder?.mimeType || 'audio/webm' });

    if (blob.size === 0) {
      this.fail({ kind: 'empty', message: "We couldn't hear anything. Try again." });
      return;
    }

    const session = this.session;
    this.status.set('transcribing');
    this.transcribe(blob, session);
  }

  private transcribe(blob: Blob, session: number): void {
    this.audioUrl = this.createAudioUrl(blob);

    const form = new FormData();
    form.append('file', blob, recordingFileName(blob.type));

    this.upload = this.http.post<TranscriptionResponse>(TRANSCRIPTION_ENDPOINT, form).subscribe({
      next: (response) => {
        if (session !== this.session) return;
        this.upload = null;
        this.revokeAudioUrl();
        const text = response?.text?.trim() ?? '';
        if (!text) {
          this.fail({ kind: 'empty', message: "We couldn't hear anything. Try again." });
          return;
        }
        this.settle();
        this.onTranscript?.(text);
      },
      error: (error: unknown) => {
        if (session !== this.session) return;
        this.upload = null;
        this.revokeAudioUrl();
        this.fail(errorFromTranscription(error));
      },
    });
  }

  private settle(): void {
    this.error.set(null);
    this.elapsedSeconds.set(0);
    this.status.set('idle');
  }

  private fail(error: AssistantVoiceError): void {
    this.error.set(error);
    this.elapsedSeconds.set(0);
    this.status.set('idle');
  }

  private releaseStream(): void {
    this.stream?.getTracks().forEach((track) => track.stop());
    this.stream = null;
  }

  private startTimer(): void {
    this.stopTimer();
    this.elapsedSeconds.set(0);
    this.timer = setInterval(() => this.elapsedSeconds.update((value) => value + 1), 1000);
  }

  private stopTimer(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  private createAudioUrl(blob: Blob): string | null {
    if (typeof URL === 'undefined' || typeof URL.createObjectURL !== 'function') return null;
    try {
      return URL.createObjectURL(blob);
    } catch {
      return null;
    }
  }

  private revokeAudioUrl(): void {
    if (this.audioUrl === null) return;
    if (typeof URL !== 'undefined' && typeof URL.revokeObjectURL === 'function') {
      URL.revokeObjectURL(this.audioUrl);
    }
    this.audioUrl = null;
  }
}

/**
 * Keep the multipart filename consistent with the browser's actual recording
 * container. Some STT providers inspect both the Content-Type and extension.
 */
export function recordingFileName(mimeType: string): string {
  const mediaType = mimeType.split(';', 1)[0].trim().toLowerCase();
  switch (mediaType) {
    case 'audio/mp4':
      return 'voice-note.m4a';
    case 'audio/ogg':
      return 'voice-note.ogg';
    case 'audio/mpeg':
      return 'voice-note.mp3';
    case 'audio/wav':
    case 'audio/x-wav':
      return 'voice-note.wav';
    default:
      return 'voice-note.webm';
  }
}

/** Turn a backend ProblemDetails response into an actionable voice error. */
export function errorFromTranscription(error: unknown): AssistantVoiceError {
  if (!(error instanceof HttpErrorResponse)) {
    return { kind: 'failed', message: "Could not transcribe that recording. Try again." };
  }

  if (error.status === 0) {
    return {
      kind: 'failed',
      message: "Could not reach Nostos to transcribe that recording. Check the connection and try again.",
    };
  }

  const body =
    error.error && typeof error.error === 'object'
      ? (error.error as { title?: unknown; detail?: unknown })
      : null;
  const code = typeof body?.title === 'string' ? body.title : '';

  switch (code) {
    case 'stt_disabled':
      return {
        kind: 'failed',
        message: 'Voice transcription is disabled. Enable it in Settings, then try again.',
      };
    case 'stt_not_configured':
      return {
        kind: 'failed',
        message: 'Voice transcription is not configured yet. Check the STT provider in Settings.',
      };
    case 'stt_unsupported_format':
      return {
        kind: 'failed',
        message: "This browser's recording format is not supported by the transcription provider.",
      };
    case 'stt_permission_denied':
      return {
        kind: 'failed',
        message: 'The transcription provider rejected the request or is rate limited. Check its settings and try again.',
      };
    case 'stt_audio_too_large':
    case 'stt_audio_too_long':
      return {
        kind: 'failed',
        message: 'That recording is too long to transcribe. Try a shorter recording.',
      };
    case 'stt_provider_error':
    case 'stt_response_invalid':
      return {
        kind: 'failed',
        message: 'The transcription provider failed to process that recording. Try again or check its settings.',
      };
    default:
      return { kind: 'failed', message: "Could not transcribe that recording. Try again." };
  }
}

/** Turn a raw `getUserMedia` rejection into a calm, non-technical message. */
export function errorFromUserMedia(error: unknown): AssistantVoiceError {
  const name =
    error && typeof error === 'object' && 'name' in error
      ? String((error as { name?: unknown }).name)
      : '';
  switch (name) {
    case 'NotAllowedError':
    case 'SecurityError':
      return {
        kind: 'denied',
        message: 'Microphone access is blocked. Allow it in your browser, then try again.',
      };
    case 'NotFoundError':
    case 'DevicesNotFoundError':
      return {
        kind: 'no-device',
        message: 'No microphone was found. Connect one, then try again.',
      };
    case 'NotReadableError':
    case 'TrackStartError':
      return {
        kind: 'denied',
        message: 'The microphone is in use by another app. Close it, then try again.',
      };
    case 'AbortError':
      return { kind: 'failed', message: 'Recording was interrupted. Try again.' };
    default:
      return { kind: 'failed', message: 'Could not start recording. Try again.' };
  }
}
