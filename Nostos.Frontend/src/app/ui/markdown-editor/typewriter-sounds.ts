/**
 * Optional typewriter sounds for the Studio page (issues #1042, #1065), ported
 * from Scriptorium's audio.js onto CC0 recordings (see public/audio/typewriter).
 *
 * Nothing is fetched and no AudioContext exists until sounds are turned on and
 * a key is pressed, and the context sleeps while the page is hidden.
 */
export type TypewriterSound = 'key' | 'space' | 'return' | 'backspace';

/** Which recordings play: a real typewriter, or a quiet office keyboard. */
export type SoundPack = 'typewriter' | 'keyboard';
export const SOUND_PACKS: readonly SoundPack[] = ['typewriter', 'keyboard'];

interface PackSamples {
  folder: string;
  keys: readonly string[];
}

const keyNames = (count: number) => Array.from({ length: count }, (_, i) => `key-${i + 1}`);

const PACKS: Record<SoundPack, PackSamples> = {
  typewriter: { folder: 'olivetti-lettera-22', keys: keyNames(12) },
  keyboard: { folder: 'cherry-kc1000', keys: keyNames(5) },
};
const OTHER_SAMPLES = ['space', 'return', 'backspace'];

const BASE = '/audio/typewriter/';
/**
 * One key event can arrive twice (keydown, then beforeinput); closer than this
 * is one sound. Kept short: at 100+ wpm two keys can land within a few tens of
 * milliseconds, and a slight overlap sounds more natural than a dropped strike.
 */
const THROTTLE_MS = 12;

type AudioContextCtor = typeof AudioContext;

function samplesOf(pack: SoundPack): string[] {
  const { folder, keys } = PACKS[pack];
  return [...keys, ...OTHER_SAMPLES].map((name) => `${folder}/${name}`);
}

export class TypewriterSounds {
  private context: AudioContext | null = null;
  private pack: SoundPack = 'typewriter';
  /** Decoded samples by path (`<folder>/<name>`), for every pack used so far. */
  private readonly buffers = new Map<string, AudioBuffer>();
  /** The encoded samples, fetched ahead without needing an AudioContext. */
  private readonly files = new Map<string, ArrayBuffer>();
  private fetching: Promise<void> | null = null;
  private decoding: Promise<void> | null = null;
  private lastPlayed = 0;
  private readonly onVisibility = () => {
    if (!this.context) return;
    if (document.visibilityState === 'hidden') void this.context.suspend();
    else void this.context.resume();
  };

  constructor(private readonly random: () => number = Math.random) {}

  /** Switches the recordings; the new pack is fetched on the next prefetch or key. */
  setPack(pack: SoundPack): void {
    if (pack === this.pack) return;
    this.pack = pack;
    this.fetching = null;
    this.decoding = null;
  }

  /**
   * Plays the sound for one keystroke. `charCode` picks the key variant, so
   * the same letter always sounds the same, like a real machine.
   */
  play(sound: TypewriterSound, volume: number, charCode = 0): void {
    const now = performance.now();
    // The carriage return is its own event: a key struck with it neither
    // silences it nor is silenced by it.
    const throttled = sound !== 'return';
    if (throttled && now - this.lastPlayed < THROTTLE_MS) return;

    const context = this.ensureContext();
    if (!context) return;
    void this.ensureDecoded(context);

    const { folder, keys } = PACKS[this.pack];
    const name = sound === 'key' ? keys[Math.abs(charCode) % keys.length] : sound;
    const buffer = this.buffers.get(`${folder}/${name}`);
    if (!buffer) return;
    // Only a sound that plays starts the throttle window.
    if (throttled) this.lastPlayed = now;

    // Each sound is its own source, so a carriage return plays out in full
    // while the next line is already being typed.
    const source = context.createBufferSource();
    source.buffer = buffer;
    // Small variation in pitch and level keeps a run of keys from sounding
    // like one sample on a loop; the carriage barely varies.
    source.playbackRate.value = this.vary(sound === 'return' ? 0.01 : 0.03);
    const gain = context.createGain();
    const level = Math.max(0, Math.min(1, volume));
    gain.gain.value = sound === 'key' ? level * (0.8 + this.random() * 0.2) : level;
    source.connect(gain);
    gain.connect(context.destination);
    source.start();
  }

  /**
   * Plays one sound once it has loaded, for the settings to let a writer hear
   * the level or the pack without typing. The context still starts inside
   * the gesture that asked for it; only the sound waits for the sample.
   */
  async preview(sound: TypewriterSound, volume: number): Promise<void> {
    const context = this.ensureContext();
    if (!context) return;
    await this.ensureDecoded(context);
    this.lastPlayed = 0;
    this.play(sound, volume, Math.floor(this.random() * 1000));
  }

  /**
   * Fetches the pack's samples ahead of the first key once sounds are turned
   * on. No AudioContext is made here: browsers expect one to start with a gesture.
   */
  prefetch(): Promise<void> {
    this.fetching ??= Promise.all(
      samplesOf(this.pack)
        .filter((path) => !this.files.has(path) && !this.buffers.has(path))
        .map(async (path) => {
          try {
            const response = await fetch(`${BASE}${path}.mp3`);
            if (!response.ok) throw new Error(String(response.status));
            this.files.set(path, await response.arrayBuffer());
          } catch {
            // Missing for now; tried again on the next key (see ensureDecoded).
          }
        }),
    ).then(() => undefined);
    return this.fetching;
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

  /** Decodes what has been fetched; a sample that failed is fetched again later. */
  private ensureDecoded(context: AudioContext): Promise<void> {
    if (this.decoding) return this.decoding;
    const samples = samplesOf(this.pack);
    const decoding: Promise<void> = this.prefetch()
      .then(() =>
        Promise.all(
          [...this.files].map(async ([path, data]) => {
            try {
              // decodeAudioData detaches its input; decode a copy so a retry can.
              this.buffers.set(path, await context.decodeAudioData(data.slice(0)));
            } catch {
              // Fetched again on a later key.
            }
            this.files.delete(path);
          }),
        ),
      )
      .then(() => {
        // Anything still missing (offline, a bad file) is retried on a later
        // key, unless the pack changed meanwhile and reset this already.
        if (this.decoding === decoding && samples.some((path) => !this.buffers.has(path))) {
          this.fetching = null;
          this.decoding = null;
        }
      });
    this.decoding = decoding;
    return decoding;
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
