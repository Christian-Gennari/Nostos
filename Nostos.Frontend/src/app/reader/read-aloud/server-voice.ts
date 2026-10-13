import { ReadAloudOutcome, ReadAloudSpeakOptions, ReadAloudVoice } from './read-aloud.types';

/** The part of an `HTMLAudioElement` this voice drives. */
export interface AudioElementLike {
  src: string;
  playbackRate: number;
  preservesPitch?: boolean;
  onended: ((...args: any[]) => any) | null;
  onerror: ((...args: any[]) => any) | null;
  play(): Promise<void>;
  pause(): void;
}

/** A moment of silence, played inside the click that starts reading. */
const SILENCE =
  'data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAIA+AAACABAAZGF0YQAAAAA=';

/** How many rendered sentences are kept: the one playing, those ahead, and a few behind for "previous". */
const KEEP = 12;

/**
 * Reads with audio the server renders, one sentence per request.
 *
 * It plays through ONE audio element, the way the audiobook player does: a
 * phone keeps an element that is already playing alive when the screen locks,
 * while a new one would need a fresh tap. For the same reason `prime` must be
 * called from the click that starts reading.
 *
 * Sentences ahead are fetched while the current one plays, so there is no gap
 * between them. Speed is applied on playback, so one rendering serves every
 * speed and changing it costs nothing.
 */
export class ServerVoice implements ReadAloudVoice {
  readonly id = 'server';

  private readonly rendered = new Map<string, Promise<Blob>>();
  private settle: ((outcome: ReadAloudOutcome) => void) | null = null;
  private objectUrl: string | null = null;
  private failure: string | null = null;
  private turn = 0;

  constructor(
    private readonly fetchAudio: (text: string) => Promise<Blob>,
    private readonly audio: AudioElementLike = new Audio(),
    private readonly urls: Pick<typeof URL, 'createObjectURL' | 'revokeObjectURL'> = URL,
  ) {}

  /** Unlocks playback on platforms that only let audio start from a tap. */
  prime(): void {
    try {
      this.audio.src = SILENCE;
      void this.audio.play().catch(() => undefined);
    } catch {
      // Playback will be attempted anyway; a refusal there falls back.
    }
  }

  prepare(texts: string[]): void {
    for (const text of texts) void this.render(text).catch(() => undefined);
  }

  async speak(text: string, options: ReadAloudSpeakOptions): Promise<ReadAloudOutcome> {
    this.cancel();
    const turn = ++this.turn;
    this.failure = null;

    let blob: Blob;
    try {
      blob = await this.render(text);
    } catch (error) {
      if (turn !== this.turn) return 'cancelled';
      this.failure = codeOf(error);
      return 'failed';
    }
    if (turn !== this.turn) return 'cancelled';

    return new Promise<ReadAloudOutcome>((resolve) => {
      const finish = (outcome: ReadAloudOutcome) => {
        if (this.settle !== finish) return;
        this.settle = null;
        this.audio.onended = null;
        this.audio.onerror = null;
        this.release();
        resolve(outcome);
      };
      this.settle = finish;

      this.objectUrl = this.urls.createObjectURL(blob);
      this.audio.onended = () => finish('ended');
      this.audio.onerror = () => {
        this.failure = 'read_aloud_playback_failed';
        finish('failed');
      };
      this.audio.src = this.objectUrl;
      this.audio.preservesPitch = true;
      this.audio.playbackRate = Math.min(2, Math.max(0.5, options.rate));
      this.audio.play().then(
        () => {
          if (this.settle === finish) options.onStart?.();
        },
        () => {
          this.failure = 'read_aloud_playback_blocked';
          finish('failed');
        },
      );
    });
  }

  cancel(): void {
    this.turn++;
    const settle = this.settle;
    if (!settle) return;
    try {
      this.audio.pause();
    } catch {
      // Nothing was playing.
    }
    settle('cancelled');
  }

  lastFailure(): string | null {
    return this.failure;
  }

  private render(text: string): Promise<Blob> {
    const known = this.rendered.get(text);
    if (known) {
      // Most recently used last, so trimming drops what is furthest behind.
      this.rendered.delete(text);
      this.rendered.set(text, known);
      return known;
    }

    const request = this.fetchAudio(text);
    this.rendered.set(text, request);
    // A failed request is not remembered: the next attempt asks again.
    request.catch(() => {
      if (this.rendered.get(text) === request) this.rendered.delete(text);
    });

    while (this.rendered.size > KEEP) {
      const oldest = this.rendered.keys().next().value as string;
      this.rendered.delete(oldest);
    }
    return request;
  }

  private release(): void {
    if (!this.objectUrl) return;
    this.urls.revokeObjectURL(this.objectUrl);
    this.objectUrl = null;
  }
}

function codeOf(error: unknown): string | null {
  const code = (error as { code?: unknown } | null)?.code;
  return typeof code === 'string' ? code : null;
}
