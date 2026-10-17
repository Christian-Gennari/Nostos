import { Injectable, computed, effect, signal } from '@angular/core';

/**
 * The Studio page's own settings (issue #1041): theme, typeface, size,
 * spacing and measure, and how the page behaves. Saved per device, like the
 * library preferences; syncing them across devices is out of scope.
 */
export type StudioTheme = 'match' | 'study' | 'mist' | 'dark' | 'black' | 'sepia' | 'paper';

export interface StudioThemeOption {
  value: StudioTheme;
  label: string;
}

/** Each theme's colours are its token set in styles.css (`.studio-theme-<value>`). */
export const STUDIO_THEMES: readonly StudioThemeOption[] = [
  { value: 'match', label: 'Match app' },
  { value: 'study', label: "Writer's Study" },
  { value: 'mist', label: 'Mist' },
  { value: 'dark', label: 'Dark' },
  { value: 'black', label: 'Black' },
  { value: 'sepia', label: 'Sepia' },
  { value: 'paper', label: 'Paper' },
];

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
  { value: 'mono', label: 'Monospace', group: 'Typewriter', stack: "'IBM Plex Mono', 'SFMono-Regular', Consolas, monospace" },
  { value: 'opendyslexic', label: 'OpenDyslexic', group: 'Accessibility', stack: "'Studio OpenDyslexic', sans-serif" },
];

export interface StudioPreferences {
  theme: StudioTheme;
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
}

export const STUDIO_PREFERENCES_KEY = 'nostos.studio.preferences';
/** Where typewriter centring lived before it joined the page settings. */
const LEGACY_TYPEWRITER_KEY = 'nostos.typewriter';

export const DEFAULT_STUDIO_PREFERENCES: StudioPreferences = {
  theme: 'match',
  font: 'newsreader',
  fontSize: 18,
  lineSpacing: 1.8,
  textWidth: null,
  spellCheck: true,
  typewriter: false,
  sounds: false,
  soundVolume: 0.6,
};

export const FONT_SIZE_RANGE = { min: 14, max: 28, step: 1 } as const;
export const LINE_SPACING_RANGE = { min: 1.2, max: 2.4, step: 0.1 } as const;
export const TEXT_WIDTH_RANGE = { min: 40, max: 100, step: 2 } as const;

function clamp(value: unknown, min: number, max: number, fallback: number): number {
  const n = typeof value === 'number' ? value : Number.NaN;
  return Number.isFinite(n) ? Math.min(max, Math.max(min, n)) : fallback;
}

/** Stored values are untrusted: anything out of range or unknown falls back. */
export function normalizeStudioPreferences(raw: unknown, legacyTypewriter = false): StudioPreferences {
  const value = (raw && typeof raw === 'object' ? raw : {}) as Partial<Record<keyof StudioPreferences, unknown>>;
  const d = DEFAULT_STUDIO_PREFERENCES;
  return {
    theme: STUDIO_THEMES.some((t) => t.value === value.theme) ? (value.theme as StudioTheme) : d.theme,
    font: STUDIO_FONTS.some((f) => f.value === value.font) ? (value.font as string) : d.font,
    fontSize: Math.round(clamp(value.fontSize, FONT_SIZE_RANGE.min, FONT_SIZE_RANGE.max, d.fontSize)),
    lineSpacing:
      Math.round(clamp(value.lineSpacing, LINE_SPACING_RANGE.min, LINE_SPACING_RANGE.max, d.lineSpacing) * 10) / 10,
    textWidth:
      value.textWidth === null || value.textWidth === undefined
        ? d.textWidth
        : Math.round(clamp(value.textWidth, TEXT_WIDTH_RANGE.min, TEXT_WIDTH_RANGE.max, 66)),
    spellCheck: typeof value.spellCheck === 'boolean' ? value.spellCheck : d.spellCheck,
    typewriter: typeof value.typewriter === 'boolean' ? value.typewriter : legacyTypewriter,
    sounds: typeof value.sounds === 'boolean' ? value.sounds : d.sounds,
    soundVolume: Math.round(clamp(value.soundVolume, 0, 1, d.soundVolume) * 100) / 100,
  };
}

function readStored(): StudioPreferences {
  try {
    const raw = localStorage.getItem(STUDIO_PREFERENCES_KEY);
    const legacy = localStorage.getItem(LEGACY_TYPEWRITER_KEY) === '1';
    return normalizeStudioPreferences(raw ? JSON.parse(raw) : null, legacy);
  } catch {
    return { ...DEFAULT_STUDIO_PREFERENCES };
  }
}

@Injectable({ providedIn: 'root' })
export class StudioPreferencesService {
  private readonly state = signal<StudioPreferences>(readStored());
  readonly preferences = this.state.asReadonly();

  readonly theme = computed(() => this.state().theme);
  readonly fontStack = computed(
    () => STUDIO_FONTS.find((f) => f.value === this.state().font)?.stack ?? STUDIO_FONTS[3].stack,
  );

  constructor() {
    effect(() => {
      const prefs = this.state();
      try {
        localStorage.setItem(STUDIO_PREFERENCES_KEY, JSON.stringify(prefs));
      } catch {
        // The page still changes for this session when storage is unavailable.
      }
    });
  }

  update(change: Partial<StudioPreferences>): void {
    this.state.update((prefs) => normalizeStudioPreferences({ ...prefs, ...change }));
  }

  reset(): void {
    this.state.set({ ...DEFAULT_STUDIO_PREFERENCES });
  }
}
