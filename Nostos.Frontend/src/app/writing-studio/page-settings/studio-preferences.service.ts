import { Injectable, computed, effect, signal } from '@angular/core';

import { SOUND_PACKS, SoundPack } from '../../ui/markdown-editor/typewriter-sounds';

/**
 * The Studio page's own settings (issue #1041): typeface, size, spacing and
 * measure, and how the page behaves. Saved per device, like the library
 * preferences; syncing them across devices is out of scope. The colours are
 * the app theme's (issue #1074): the Studio no longer has page themes of its
 * own, and a page theme saved before is carried over once by ThemeService.
 */

export interface StudioFont {
  value: string;
  label: string;
  group: 'Classic serif' | 'Contemporary serif' | 'Sans' | 'Typewriter' | 'Accessibility';
  /** The CSS font-family stack the page uses. */
  stack: string;
}

export const STUDIO_FONTS: readonly StudioFont[] = [
  { value: 'eb-garamond', label: 'EB Garamond', group: 'Classic serif', stack: "'Studio EB Garamond', Georgia, serif" },
  { value: 'pt-serif', label: 'PT Serif', group: 'Classic serif', stack: "'Studio PT Serif', Georgia, serif" },
  { value: 'libre-baskerville', label: 'Libre Baskerville', group: 'Classic serif', stack: "'Studio Libre Baskerville', Georgia, serif" },
  { value: 'newsreader', label: 'Newsreader', group: 'Contemporary serif', stack: "Newsreader, Georgia, 'Times New Roman', serif" },
  { value: 'crimson-pro', label: 'Crimson Pro', group: 'Contemporary serif', stack: "'Studio Crimson Pro', Georgia, serif" },
  { value: 'literata', label: 'Literata', group: 'Contemporary serif', stack: "'Studio Literata', Georgia, serif" },
  { value: 'source-serif-4', label: 'Source Serif 4', group: 'Contemporary serif', stack: "'Studio Source Serif 4', Georgia, serif" },
  { value: 'lora', label: 'Lora', group: 'Contemporary serif', stack: "'Studio Lora', Georgia, serif" },
  { value: 'alegreya', label: 'Alegreya', group: 'Contemporary serif', stack: "'Studio Alegreya', Georgia, serif" },
  { value: 'hanken-grotesk', label: 'Hanken Grotesk', group: 'Sans', stack: "'Hanken Grotesk', system-ui, sans-serif" },
  { value: 'inter', label: 'Inter', group: 'Sans', stack: "'Studio Inter', system-ui, sans-serif" },
  { value: 'special-elite', label: 'Special Elite', group: 'Typewriter', stack: "'Studio Special Elite', 'Courier New', monospace" },
  // The device's own monospace face: honest about not being a bundled font.
  { value: 'mono', label: 'Monospace (system)', group: 'Typewriter', stack: "ui-monospace, 'SFMono-Regular', Menlo, Consolas, 'Liberation Mono', monospace" },
  { value: 'opendyslexic', label: 'OpenDyslexic', group: 'Accessibility', stack: "'Studio OpenDyslexic', sans-serif" },
];

export interface StudioPreferences {
  font: string;
  /** Page text size in px. */
  fontSize: number;
  lineSpacing: number;
  /** Line length in characters, or null for the standard 820px measure. */
  textWidth: number | null;
  spellCheck: boolean;
  /** Keep the caret line near the middle of the page. */
  typewriter: boolean;
  /** Typewriter sounds (issue #1042). */
  sounds: boolean;
  /** 0–1. */
  soundVolume: number;
  /** Which recordings the sounds use (issue #1065). */
  soundPack: SoundPack;
}

export const STUDIO_PREFERENCES_KEY = 'nostos.studio.preferences';
/** Where typewriter centring lived before it joined the page settings. */
const LEGACY_TYPEWRITER_KEY = 'nostos.typewriter';

export const DEFAULT_STUDIO_PREFERENCES: StudioPreferences = {
  font: 'newsreader',
  fontSize: 18,
  lineSpacing: 1.8,
  textWidth: null,
  spellCheck: true,
  typewriter: false,
  sounds: false,
  soundVolume: 0.6,
  soundPack: 'typewriter',
};

export const FONT_SIZE_RANGE = { min: 14, max: 28, step: 1 } as const;
export const LINE_SPACING_RANGE = { min: 1.2, max: 2.4, step: 0.1 } as const;
export const TEXT_WIDTH_RANGE = { min: 40, max: 100, step: 2 } as const;
/**
 * Where the line-length slider rests while the page keeps its standard 820px
 * measure: about the characters of the standard face on that measure.
 */
export const STANDARD_TEXT_WIDTH = 86;

/** The three line spacings the settings offer (issue #1060). */
export const LINE_SPACING_PRESETS = [
  { label: 'Tight', value: 1.5 },
  { label: 'Normal', value: 1.8 },
  { label: 'Airy', value: 2.2 },
] as const;

/** The three line lengths the settings offer; null is the standard measure. */
export const TEXT_WIDTH_PRESETS: readonly { label: string; value: number | null }[] = [
  { label: 'Narrow', value: 66 },
  { label: 'Standard', value: null },
  { label: 'Wide', value: 100 },
];

function nearest<T>(options: readonly T[], distance: (option: T) => number): T {
  return options.reduce((best, option) => (distance(option) < distance(best) ? option : best));
}

/** The preset a line spacing snaps to: the nearest one. */
export function nearestLineSpacing(value: number): number {
  return nearest(LINE_SPACING_PRESETS, (p) => Math.abs(p.value - value)).value;
}

/** The preset a line length snaps to; the standard measure counts as its characters. */
export function nearestTextWidth(value: number | null): number | null {
  if (value === null) return null;
  return nearest(TEXT_WIDTH_PRESETS, (p) => Math.abs((p.value ?? STANDARD_TEXT_WIDTH) - value)).value;
}

function clamp(value: unknown, min: number, max: number, fallback: number): number {
  const n = typeof value === 'number' ? value : Number.NaN;
  return Number.isFinite(n) ? Math.min(max, Math.max(min, n)) : fallback;
}

/** Stored values are untrusted: anything out of range or unknown falls back. */
export function normalizeStudioPreferences(raw: unknown, legacyTypewriter = false): StudioPreferences {
  const value = (raw && typeof raw === 'object' ? raw : {}) as Partial<Record<keyof StudioPreferences, unknown>>;
  const d = DEFAULT_STUDIO_PREFERENCES;
  return {
    font: STUDIO_FONTS.some((f) => f.value === value.font) ? (value.font as string) : d.font,
    fontSize: Math.round(clamp(value.fontSize, FONT_SIZE_RANGE.min, FONT_SIZE_RANGE.max, d.fontSize)),
    // The settings offer three spacings and three lengths (#1060); a value
    // saved from the old sliders snaps to the nearest, so what the page shows
    // is what the settings say.
    lineSpacing: nearestLineSpacing(clamp(value.lineSpacing, LINE_SPACING_RANGE.min, LINE_SPACING_RANGE.max, d.lineSpacing)),
    textWidth:
      value.textWidth === null || value.textWidth === undefined
        ? d.textWidth
        : nearestTextWidth(clamp(value.textWidth, TEXT_WIDTH_RANGE.min, TEXT_WIDTH_RANGE.max, STANDARD_TEXT_WIDTH)),
    spellCheck: typeof value.spellCheck === 'boolean' ? value.spellCheck : d.spellCheck,
    typewriter: typeof value.typewriter === 'boolean' ? value.typewriter : legacyTypewriter,
    sounds: typeof value.sounds === 'boolean' ? value.sounds : d.sounds,
    soundVolume: Math.round(clamp(value.soundVolume, 0, 1, d.soundVolume) * 100) / 100,
    soundPack: SOUND_PACKS.includes(value.soundPack as SoundPack) ? (value.soundPack as SoundPack) : d.soundPack,
  };
}

function readStored(): StudioPreferences {
  try {
    const raw = localStorage.getItem(STUDIO_PREFERENCES_KEY);
    const legacy = localStorage.getItem(LEGACY_TYPEWRITER_KEY) === '1';
    const prefs = normalizeStudioPreferences(raw ? JSON.parse(raw) : null, legacy);
    // Carried over once; the old key has no other reader.
    localStorage.removeItem(LEGACY_TYPEWRITER_KEY);
    return prefs;
  } catch {
    return { ...DEFAULT_STUDIO_PREFERENCES };
  }
}

@Injectable({ providedIn: 'root' })
export class StudioPreferencesService {
  private readonly state = signal<StudioPreferences>(readStored());
  readonly preferences = this.state.asReadonly();

  readonly font = computed(
    () => STUDIO_FONTS.find((f) => f.value === this.state().font) ?? STUDIO_FONTS.find((f) => f.value === DEFAULT_STUDIO_PREFERENCES.font)!,
  );
  readonly fontStack = computed(() => this.font().stack);

  /** A change waiting for the debounce; written at once if the page goes away. */
  private unsaved: StudioPreferences | null = null;

  constructor() {
    // Saved once a slider has come to rest, not on every step of a drag.
    effect((onCleanup) => {
      const prefs = this.state();
      this.unsaved = prefs;
      const timer = setTimeout(() => this.flush(), 250);
      onCleanup(() => clearTimeout(timer));
    });
    // A reload or a closed tab inside the debounce must not lose the change.
    if (typeof window !== 'undefined') window.addEventListener('pagehide', () => this.flush());
  }

  /** Writes a waiting change now. */
  flush(): void {
    const prefs = this.unsaved;
    if (!prefs) return;
    this.unsaved = null;
    try {
      localStorage.setItem(STUDIO_PREFERENCES_KEY, JSON.stringify(prefs));
    } catch {
      // The page still changes for this session when storage is unavailable.
    }
  }

  update(change: Partial<StudioPreferences>): void {
    this.state.update((prefs) => normalizeStudioPreferences({ ...prefs, ...change }));
  }

  reset(): void {
    this.state.set({ ...DEFAULT_STUDIO_PREFERENCES });
  }
}
