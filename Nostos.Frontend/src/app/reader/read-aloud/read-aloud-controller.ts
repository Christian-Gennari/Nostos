import { Injectable, OnDestroy, computed, effect, signal, untracked } from '@angular/core';
import {
  READ_ALOUD_RATES,
  ReadAloudSegment,
  ReadAloudVoice,
  ReaderReadAloud,
} from './read-aloud.types';

export type ReadAloudStatus = 'idle' | 'starting' | 'playing' | 'paused';

const RATE_KEY = 'nostos.readAloud.rate';

function readStoredRate(): number {
  try {
    const rates = READ_ALOUD_RATES as readonly number[];
    const stored = Number(localStorage.getItem(RATE_KEY));
    // A speed above the fastest one offered (2× once was) becomes the fastest.
    if (stored > rates[rates.length - 1]) return rates[rates.length - 1];
    return rates.includes(stored) ? stored : 1;
  } catch {
    return 1;
  }
}

/**
 * Drives read-aloud for one open book: asks the reader for the next sentence,
 * hands it to a voice, and moves on when the voice is done.
 *
 * One sentence is in flight at a time. Every command that changes what should
 * be heard (pause, skip, a new speed, the person turning the page) bumps
 * `generation`, which makes the loop that was waiting on the old sentence step
 * aside without touching the state the new command has set.
 *
 * Provided by the reader shell, so it lives and dies with the open book.
 */
@Injectable()
export class ReadAloudController implements OnDestroy {
  readonly status = signal<ReadAloudStatus>('idle');
  readonly rate = signal(readStoredRate());
  /** A short message for the person: why the voice changed, or why it stopped. */
  readonly notice = signal<string | null>(null);
  /** True while a sentence has been asked for and no sound has started yet. */
  readonly buffering = signal(false);
  /** Which voice is speaking, by its id. */
  readonly activeVoiceId = signal<string | null>(null);

  readonly active = computed(() => this.status() !== 'idle');
  readonly playing = computed(() => this.status() === 'playing' || this.status() === 'starting');

  private readonly source = signal<ReaderReadAloud | null>(null);
  private voice: ReadAloudVoice | null = null;
  private fallback: ReadAloudVoice | null = null;
  private fallbackNotice: (code: string | null) => string = () =>
    'Reading with this device’s voice.';
  private speaking: ReadAloudVoice | null = null;
  private current: ReadAloudSegment | null = null;
  private generation = 0;
  private wakeLock: { release(): Promise<void> } | null = null;

  constructor() {
    // The person turned the page (or jumped) while the book was being read:
    // carry on from where they went.
    let seen: number | null = null;
    effect(() => {
      const moved = this.source()?.userMoved() ?? null;
      if (moved === null) {
        seen = null;
        return;
      }
      if (seen !== null && moved !== seen) untracked(() => void this.restartFromPage());
      seen = moved;
    });
  }

  /** The reader whose book is read. Null while no text reader is mounted. */
  attach(source: ReaderReadAloud | null): void {
    if (this.source() === source) return;
    this.stop();
    this.source.set(source);
  }

  /**
   * `voice` is tried first. When it cannot speak a sentence, `fallback` takes
   * over for the rest of the session and `fallbackNotice` says why, given the
   * code the first voice failed with.
   */
  setVoices(
    voice: ReadAloudVoice | null,
    fallback: ReadAloudVoice | null = null,
    fallbackNotice?: (code: string | null) => string,
  ): void {
    this.voice = voice;
    this.fallback = fallback && fallback !== voice ? fallback : null;
    if (fallbackNotice) this.fallbackNotice = fallbackNotice;
  }

  readonly available = computed(() => this.source() !== null);

  toggle(): void {
    if (this.status() === 'idle') void this.start();
    else if (this.status() === 'paused') this.resume();
    else this.pause();
  }

  async start(): Promise<void> {
    const source = this.source();
    if (!source || !this.voice) return;

    const generation = ++this.generation;
    this.notice.set(null);
    this.speaking = this.voice;
    this.voice.prime?.();
    this.activeVoiceId.set(this.voice.id);
    this.status.set('starting');

    const segment = await this.safely(() => source.begin());
    if (generation !== this.generation) return;
    if (!segment) {
      this.finish('There is nothing to read aloud from here.');
      return;
    }

    void this.run(segment, generation);
  }

  pause(): void {
    if (!this.playing()) return;
    this.generation++;
    this.speaking?.cancel();
    this.buffering.set(false);
    this.status.set('paused');
    this.releaseWakeLock();
  }

  resume(): void {
    if (this.status() !== 'paused' || !this.current) return;
    this.speaking?.prime?.();
    void this.run(this.current, ++this.generation);
  }

  stop(): void {
    if (this.status() === 'idle') return;
    this.generation++;
    this.speaking?.cancel();
    this.source()?.end();
    this.current = null;
    this.buffering.set(false);
    this.status.set('idle');
    this.releaseWakeLock();
  }

  next(): void {
    void this.skip(1);
  }

  previous(): void {
    void this.skip(-1);
  }

  setRate(rate: number): void {
    this.rate.set(rate);
    try {
      localStorage.setItem(RATE_KEY, String(rate));
    } catch {
      // The speed still applies for this session.
    }

    // A sentence already being spoken keeps its speed, so say it again.
    if (this.status() === 'playing' && this.current) {
      this.speaking?.cancel();
      void this.run(this.current, ++this.generation);
    }
  }

  cycleRate(): void {
    const rates = READ_ALOUD_RATES as readonly number[];
    const index = rates.indexOf(this.rate());
    this.setRate(rates[(index + 1) % rates.length]);
  }

  dismissNotice(): void {
    this.notice.set(null);
  }

  ngOnDestroy(): void {
    this.stop();
  }

  private async skip(direction: 1 | -1): Promise<void> {
    const source = this.source();
    const status = this.status();
    if (!source || status === 'idle' || status === 'starting') return;

    const generation = ++this.generation;
    this.speaking?.cancel();

    const segment = await this.safely(() => source.step(direction));
    if (generation !== this.generation) return;
    if (!segment) {
      // Nothing that way: stay on the sentence we were on.
      if (status === 'playing' && this.current) void this.run(this.current, generation);
      return;
    }

    if (status === 'playing') void this.run(segment, generation);
    else this.current = segment;
  }

  private async restartFromPage(): Promise<void> {
    const source = this.source();
    const status = this.status();
    if (!source || status === 'idle' || status === 'starting') return;

    const generation = ++this.generation;
    this.speaking?.cancel();

    const segment = await this.safely(() => source.begin());
    if (generation !== this.generation) return;
    if (!segment) {
      this.finish(null);
      return;
    }

    if (status === 'playing') void this.run(segment, generation);
    else this.current = segment;
  }

  private async run(first: ReadAloudSegment, generation: number): Promise<void> {
    const source = this.source();
    if (!source) return;

    this.status.set('playing');
    void this.holdWakeLock();
    let segment: ReadAloudSegment | null = first;

    while (segment) {
      this.current = segment;
      const voice = this.speaking;
      if (!voice) break;

      const options = { language: source.language(), rate: this.rate() };
      this.buffering.set(true);
      // The sentence to say now is asked for first. A speech server that
      // renders one request at a time would otherwise make it wait behind the
      // ones that are only being fetched ahead.
      const speaking = voice.speak(segment.text, {
        ...options,
        onStart: () => {
          if (generation === this.generation) this.buffering.set(false);
        },
      });
      voice.prepare?.(source.upcoming(2), options);

      const outcome = await speaking;
      if (generation !== this.generation) return;
      this.buffering.set(false);
      if (outcome === 'cancelled') return;

      if (outcome === 'failed') {
        if (this.fallback && voice !== this.fallback) {
          this.speaking = this.fallback;
          this.activeVoiceId.set(this.fallback.id);
          this.notice.set(this.fallbackNotice(voice.lastFailure?.() ?? null));
          continue;
        }
        this.finish('Read aloud is not available on this device.');
        return;
      }

      segment = await this.safely(() => source.step(1));
      if (generation !== this.generation) return;
    }

    this.finish(null);
  }

  private finish(notice: string | null): void {
    this.generation++;
    this.source()?.end();
    this.current = null;
    this.buffering.set(false);
    this.status.set('idle');
    if (notice) this.notice.set(notice);
    this.releaseWakeLock();
  }

  private async safely<T>(work: () => Promise<T | null>): Promise<T | null> {
    try {
      return await work();
    } catch (error) {
      console.warn('Read aloud could not continue:', error);
      return null;
    }
  }

  /** The device voice stops when the screen locks, so keep the screen on while reading. */
  private async holdWakeLock(): Promise<void> {
    if (this.wakeLock) return;
    try {
      const lock = await (navigator as any).wakeLock?.request?.('screen');
      if (!lock) return;
      if (this.status() !== 'playing') {
        void lock.release();
        return;
      }
      this.wakeLock = lock;
    } catch {
      // Not granted: reading still works while the screen is on.
    }
  }

  private releaseWakeLock(): void {
    const lock = this.wakeLock;
    this.wakeLock = null;
    void lock?.release().catch(() => undefined);
  }
}
