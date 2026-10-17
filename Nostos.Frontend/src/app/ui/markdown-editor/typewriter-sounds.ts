/**
 * Optional typewriter sounds for the Studio page (issue #1042), ported from
 * Scriptorium's audio.js onto CC0 recordings (see public/audio/typewriter).
 *
 * Nothing is fetched and no AudioContext exists until sounds are turned on and
 * a key is pressed, and the context sleeps while the page is hidden.
 */
export type TypewriterSound = 'key' | 'space' | 'return' | 'backspace';

const BASE = '/audio/typewriter/';
const KEY_SAMPLES = ['key-1', 'key-2', 'key-3', 'key-4', 'key-5'];
const ALL_SAMPLES = [...KEY_SAMPLES, 'space', 'return', 'backspace'];
/** Keys closer together than this make one sound, as a real carriage would. */
const THROTTLE_MS = 30;

type AudioContextCtor = typeof AudioContext;

export class TypewriterSounds {
  private context: AudioContext | null = null;
  private readonly buffers = new Map<string, AudioBuffer | null>();
  private loading: Promise<void> | null = null;
  private lastPlayed = 0;
  private readonly onVisibility = () => {
    if (!this.context) return;
    if (document.visibilityState === 'hidden') void this.context.suspend();
    else void this.context.resume();
  };

  constructor(private readonly random: () => number = Math.random) {}

  /**
   * Plays the sound for one keystroke. `charCode` picks the key variant, so
   * the same letter always sounds the same, like a real keyboard.
   */
  play(sound: TypewriterSound, volume: number, charCode = 0): void {
    const now = performance.now();
    if (now - this.lastPlayed < THROTTLE_MS) return;
    this.lastPlayed = now;

    const context = this.ensureContext();
    if (!context) return;
    void this.ensureLoaded(context);

    const name = sound === 'key' ? KEY_SAMPLES[Math.abs(charCode) % KEY_SAMPLES.length] : sound;
    const buffer = this.buffers.get(name);
    if (!buffer) return;

    const source = context.createBufferSource();
    source.buffer = buffer;
    // Small variation in pitch and level keeps a run of keys from sounding
    // like one sample on a loop.
    source.playbackRate.value = this.vary(sound === 'return' ? 0.01 : 0.03);
    const gain = context.createGain();
    const level = Math.max(0, Math.min(1, volume));
    gain.gain.value = sound === 'key' ? level * (0.8 + this.random() * 0.2) : level;
    source.connect(gain);
    gain.connect(context.destination);
    source.start();
  }

  /** Loads the samples ahead of the first key once sounds are turned on. */
  preload(): void {
    const context = this.ensureContext();
    if (context) void this.ensureLoaded(context);
  }

  /** Releases the audio context and stops listening for page visibility. */
  dispose(): void {
    document.removeEventListener('visibilitychange', this.onVisibility);
    void this.context?.close();
    this.context = null;
  }

  private vary(spread: number): number {
    return 1 - spread + this.random() * spread * 2;
  }

  private ensureContext(): AudioContext | null {
    if (this.context) {
      if (this.context.state === 'suspended' && document.visibilityState !== 'hidden') void this.context.resume();
      return this.context;
    }
    const Ctor: AudioContextCtor | undefined =
      (globalThis as { AudioContext?: AudioContextCtor }).AudioContext ??
      (globalThis as { webkitAudioContext?: AudioContextCtor }).webkitAudioContext;
    if (!Ctor) return null;
    try {
      this.context = new Ctor();
    } catch {
      return null;
    }
    document.addEventListener('visibilitychange', this.onVisibility);
    return this.context;
  }

  private ensureLoaded(context: AudioContext): Promise<void> {
    this.loading ??= Promise.all(
      ALL_SAMPLES.map(async (name) => {
        try {
          const response = await fetch(`${BASE}${name}.mp3`);
          if (!response.ok) throw new Error(String(response.status));
          this.buffers.set(name, await context.decodeAudioData(await response.arrayBuffer()));
        } catch {
          // A missing sample is silence, never an error on the page.
          this.buffers.set(name, null);
        }
      }),
    ).then(() => undefined);
    return this.loading;
  }
}

/**
 * Which sound an edit makes, from the browser's own description of it. This
 * works on Android soft keyboards too, which report every key as
 * "Unidentified" on keydown.
 */
export function soundForInput(inputType: string, data: string | null): TypewriterSound | null {
  switch (inputType) {
    case 'insertText':
      return data === ' ' ? 'space' : data ? 'key' : null;
    case 'insertParagraph':
    case 'insertLineBreak':
      return 'return';
    case 'deleteContentBackward':
    case 'deleteWordBackward':
    case 'deleteSoftLineBackward':
      return 'backspace';
    default:
      return null;
  }
}
