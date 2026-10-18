import { Injectable, computed, signal } from '@angular/core';

/** The five app-wide colour themes (issue #1074). */
export type ThemeName = 'light' | 'sepia' | 'mist' | 'study' | 'dark';

/** The light or dark base a theme sits on. */
export type ThemeMode = 'light' | 'dark';

/** What the person chose: one theme, or follow the system's light and dark. */
export type ThemeChoice = ThemeName | 'system';

export interface ThemeOption {
  value: ThemeName;
  label: string;
  mode: ThemeMode;
  /** Its paper, ink and accent, so a swatch can show the theme without wearing it. */
  swatch: { paper: string; ink: string; accent: string };
  /** The browser chrome colour (`<meta name="theme-color">`): its ground. */
  chrome: string;
}

/**
 * The themes, lightest to darkest. Each one's colours are its token set in
 * styles.css: Light on `:root`, Dark on `:root[data-theme='dark']`, and the
 * others on `:root[data-palette='<name>']` layered over their mode's base.
 * The swatch and chrome values here repeat a few of those tokens for places
 * that cannot read them (a swatch of a theme that is not on, and the meta tag).
 */
export const THEMES: readonly ThemeOption[] = [
  { value: 'light', label: 'Light', mode: 'light', swatch: { paper: '#ffffff', ink: '#121316', accent: '#293E32' }, chrome: '#FBFBFC' },
  { value: 'sepia', label: 'Sepia', mode: 'light', swatch: { paper: '#fbf4e5', ink: '#2b2118', accent: '#293E32' }, chrome: '#fbf5eb' },
  { value: 'mist', label: 'Mist', mode: 'dark', swatch: { paper: '#1e2026', ink: '#c8ccd4', accent: '#8494b0' }, chrome: '#16181c' },
  { value: 'study', label: "Writer's Study", mode: 'dark', swatch: { paper: '#1a1814', ink: '#e4d5bf', accent: '#c9953e' }, chrome: '#141210' },
  { value: 'dark', label: 'Dark', mode: 'dark', swatch: { paper: '#121318', ink: '#f0f1f4', accent: '#8fbfae' }, chrome: '#0d0e11' },
];

/** The themes Follow system may use by day, and by night. */
export const DAY_THEMES = THEMES.filter((t) => t.mode === 'light');
export const NIGHT_THEMES = THEMES.filter((t) => t.mode === 'dark');

export const THEME_STORAGE_KEY = 'nostos.theme';
export const DAY_THEME_STORAGE_KEY = 'nostos.theme.day';
export const NIGHT_THEME_STORAGE_KEY = 'nostos.theme.night';
/** Where the Studio kept its own page theme before themes went app-wide. */
const STUDIO_PREFERENCES_KEY = 'nostos.studio.preferences';

const SYSTEM = 'system';
const DEFAULT_DAY: ThemeName = 'light';
const DEFAULT_NIGHT: ThemeName = 'dark';

export function themeOption(name: ThemeName): ThemeOption {
  return THEMES.find((t) => t.value === name)!;
}

/**
 * App-wide colour theme.
 *
 * Ownership: this service is the ONLY writer of `data-theme` and
 * `data-palette` on `<html>`, and of the `theme-color` meta tag. Everything
 * else reads the resulting CSS custom properties, or `mode()` when it only
 * needs to know light from dark, so a component never needs to know which
 * theme is active.
 *
 * The stylesheet is the source of truth for what each theme *looks like*; this
 * file only decides which one is on. `data-theme="dark"` is set for every dark
 * theme (so dark-only rules and `color-scheme` follow the mode), and removed
 * for light ones so `:root` stays the single owner of the light values.
 * `data-palette` names Sepia, Mist or Writer's Study, whose blocks sit on top.
 *
 * Follow system (the default) wears the day theme while the OS is light and
 * the night theme while it is dark: Light and Dark unless the person chose
 * others. It switches live when the OS does.
 *
 * Anti-flash: `index.html` runs a tiny inline script that applies the stored
 * theme before first paint. That script must stay in sync with the storage
 * keys and resolution here.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly _choice = signal<ThemeChoice>(readStoredChoice());
  private readonly _day = signal<ThemeName>(readStoredSlot(DAY_THEME_STORAGE_KEY, 'light', DEFAULT_DAY));
  private readonly _night = signal<ThemeName>(readStoredSlot(NIGHT_THEME_STORAGE_KEY, 'dark', DEFAULT_NIGHT));
  private readonly systemDark = signal(prefersDark());

  /** What the person chose, `system` included. */
  readonly choice = this._choice.asReadonly();
  /** The themes Follow system uses by day and by night. */
  readonly dayTheme = this._day.asReadonly();
  readonly nightTheme = this._night.asReadonly();

  /** The theme on screen now. */
  readonly active = computed<ThemeName>(() => {
    const choice = this._choice();
    if (choice !== SYSTEM) return choice;
    return this.systemDark() ? this._night() : this._day();
  });

  /** Light or dark: all most components need. */
  readonly mode = computed<ThemeMode>(() => themeOption(this.active()).mode);

  constructor() {
    migrateStudioTheme(this._choice);
    this.apply();
    if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
      window.matchMedia('(prefers-color-scheme: dark)').addEventListener?.('change', (event) => {
        this.systemDark.set(event.matches);
        this.apply();
      });
    }
  }

  setChoice(choice: ThemeChoice): void {
    this._choice.set(choice);
    store(THEME_STORAGE_KEY, choice);
    this.apply();
  }

  /** The theme Follow system wears by day: Light or Sepia. */
  setDayTheme(name: ThemeName): void {
    if (themeOption(name)?.mode !== 'light') return;
    this._day.set(name);
    store(DAY_THEME_STORAGE_KEY, name);
    this.apply();
  }

  /** The theme Follow system wears by night: Mist, Writer's Study or Dark. */
  setNightTheme(name: ThemeName): void {
    if (themeOption(name)?.mode !== 'dark') return;
    this._night.set(name);
    store(NIGHT_THEME_STORAGE_KEY, name);
    this.apply();
  }

  /** Swap light for dark: to the night theme from a light one, the day theme from a dark one. */
  toggle(): void {
    this.setChoice(this.mode() === 'dark' ? this._day() : this._night());
  }

  private apply(): void {
    applyTheme(document, this.active());
  }
}

/** Puts a theme on a document: the one place the attributes are written. */
export function applyTheme(doc: Document, name: ThemeName): void {
  const option = themeOption(name);
  const root = doc.documentElement;
  if (option.mode === 'dark') root.setAttribute('data-theme', 'dark');
  // Removing the attribute (rather than setting 'light') keeps `:root` as the
  // single owner of the light values.
  else root.removeAttribute('data-theme');
  if (name === 'light' || name === 'dark') root.removeAttribute('data-palette');
  else root.setAttribute('data-palette', name);
  doc.querySelector('meta[name="theme-color"]')?.setAttribute('content', option.chrome);
}

function isTheme(value: unknown): value is ThemeName {
  return THEMES.some((t) => t.value === value);
}

/**
 * Read the persisted choice. Nothing stored means Follow system, so a person
 * who never chose keeps following their OS. The old `light`/`dark` values are
 * fixed choices, exactly as before.
 */
export function readStoredChoice(): ThemeChoice {
  const stored = read(THEME_STORAGE_KEY);
  return isTheme(stored) ? stored : SYSTEM;
}

function readStoredSlot(key: string, mode: ThemeMode, fallback: ThemeName): ThemeName {
  const stored = read(key);
  return isTheme(stored) && themeOption(stored).mode === mode ? stored : fallback;
}

/**
 * The Studio used to wear its own page theme (issues #1041, #1060). A person
 * who picked Sepia, Mist or Writer's Study there, and never chose an app
 * theme, gets it app-wide once; it is saved so this never runs again. Paper
 * and Follow app have nothing to carry over.
 */
function migrateStudioTheme(choice: { set(value: ThemeChoice): void }): void {
  if (read(THEME_STORAGE_KEY) !== null) return;
  try {
    const studio = JSON.parse(read(STUDIO_PREFERENCES_KEY) ?? 'null') as { theme?: unknown } | null;
    const theme = studio?.theme;
    if (theme === 'sepia' || theme === 'mist' || theme === 'study') {
      choice.set(theme);
      store(THEME_STORAGE_KEY, theme);
    }
  } catch {
    // Unreadable Studio settings: nothing to carry over.
  }
}

export function prefersDark(): boolean {
  return (
    typeof window !== 'undefined' &&
    typeof window.matchMedia === 'function' &&
    window.matchMedia('(prefers-color-scheme: dark)').matches
  );
}

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function store(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Private mode / storage disabled: the theme still applies for this
    // session, it just will not be remembered. Not worth failing over.
  }
}
