import { ReadAloudOutcome, ReadAloudSpeakOptions, ReadAloudVoice } from './read-aloud.types';

/** The subset of a `SpeechSynthesisVoice` the choice is made from. */
export interface BrowserVoiceInfo {
  name: string;
  lang: string;
  default?: boolean;
  localService?: boolean;
}

const BETTER_VOICE = /natural|neural|enhanced|premium|siri|google|online/i;

function normalizeTag(tag: string): string {
  return tag.replace(/_/g, '-').toLowerCase();
}

/**
 * The device voice to use for a book in `language`.
 *
 * A voice for the wrong language reads every word wrongly, so language decides
 * first; among voices for the right language the ones a platform marks as its
 * better tier win. Null leaves the choice to the browser.
 */
export function pickBrowserVoice<T extends BrowserVoiceInfo>(
  voices: readonly T[],
  language: string | null,
): T | null {
  if (!language || voices.length === 0) return null;

  const wanted = normalizeTag(language);
  const primary = wanted.split('-')[0];
  let best: T | null = null;
  let bestScore = 0;

  for (const voice of voices) {
    const tag = normalizeTag(voice.lang ?? '');
    let score = 0;
    if (tag === wanted) score = 8;
    else if (tag.split('-')[0] === primary) score = 4;
    else continue;

    if (BETTER_VOICE.test(voice.name)) score += 2;
    if (voice.default) score += 1;

    if (score > bestScore) {
      best = voice;
      bestScore = score;
    }
  }

  return best;
}

interface UtteranceLike {
  lang: string;
  rate: number;
  voice: unknown;
  onend: (() => void) | null;
  onerror: ((event: { error?: string }) => void) | null;
}

// Held so the engine's `end` event still has a target: Chrome drops the
// callback of an utterance that has been garbage collected.
const liveUtterances = new Set<UtteranceLike>();

interface SynthLike {
  speak(utterance: any): void;
  cancel(): void;
  getVoices(): BrowserVoiceInfo[];
}

/**
 * Reads with the voices built into the device (`speechSynthesis`).
 *
 * It costs nothing and needs no server. Two platform habits shape the code:
 * an utterance whose `end` never arrives (seen on Android) must not stall the
 * book, so each one has a deadline; and pausing is unreliable across engines,
 * so there is no pause here — the controller stops and later says the sentence
 * again from its start.
 */
export class BrowserVoice implements ReadAloudVoice {
  readonly id = 'browser';

  private settle: ((outcome: ReadAloudOutcome) => void) | null = null;
  private deadline: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly synth: SynthLike | null =
      (globalThis as { speechSynthesis?: SynthLike }).speechSynthesis ?? null,
    private readonly createUtterance: (text: string) => UtteranceLike = (text) =>
      new (globalThis as any).SpeechSynthesisUtterance(text),
  ) {}

  static isSupported(): boolean {
    const scope = globalThis as { speechSynthesis?: unknown; SpeechSynthesisUtterance?: unknown };
    return !!scope.speechSynthesis && typeof scope.SpeechSynthesisUtterance === 'function';
  }

  speak(text: string, options: ReadAloudSpeakOptions): Promise<ReadAloudOutcome> {
    this.cancel();
    const synth = this.synth;
    if (!synth) return Promise.resolve('failed');

    let utterance: UtteranceLike;
    try {
      utterance = this.createUtterance(text);
    } catch {
      return Promise.resolve('failed');
    }

    const rate = Math.min(2, Math.max(0.5, options.rate));
    utterance.rate = rate;
    if (options.language) utterance.lang = options.language;
    const voice = pickBrowserVoice(synth.getVoices(), options.language);
    if (voice) utterance.voice = voice;

    return new Promise<ReadAloudOutcome>((resolve) => {
      const finish = (outcome: ReadAloudOutcome) => {
        if (this.settle !== finish) return;
        this.settle = null;
        liveUtterances.delete(utterance);
        if (this.deadline) clearTimeout(this.deadline);
        this.deadline = null;
        resolve(outcome);
      };

      this.settle = finish;
      liveUtterances.add(utterance);
      utterance.onend = () => finish('ended');
      utterance.onerror = (event) =>
        finish(event?.error === 'canceled' || event?.error === 'interrupted' ? 'cancelled' : 'failed');

      // Generous: about 3x a slow reading of the text, plus time to start.
      this.deadline = setTimeout(() => finish('ended'), (text.length * 240) / rate + 8000);

      try {
        synth.speak(utterance);
        // The device voice starts at once; there is nothing to wait for.
        options.onStart?.();
      } catch {
        finish('failed');
      }
    });
  }

  cancel(): void {
    const settle = this.settle;
    try {
      this.synth?.cancel();
    } catch {
      // Nothing to stop.
    }
    settle?.('cancelled');
  }
}
