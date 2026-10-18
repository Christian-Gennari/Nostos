import { Injectable, signal } from '@angular/core';

export type Theme = 'light' | 'dark';

/**
 * A room that wears its own colours while it is open: the Studio's page
 * themes. `name` selects `:root[data-room-theme='<name>']` in styles.css;
 * `mode` is the light or dark base those colours sit on.
 */
export interface RoomTheme {
  name: string;
  mode: Theme;
}

const STORAGE_KEY = 'nostos.theme';
const DARK = 'dark';
const LIGHT = 'light';

/**
 * App-wide colour theme.
 *
 * Ownership: this service is the ONLY writer of `data-theme` (and
 * `data-room-theme`) on `<html>`. Everything else reads the resulting CSS custom properties, so a
 * component never needs to know which theme is active.
 *
 * The stylesheet is also the source of truth for what each theme *looks like*;
 * this file only decides which one is on. `styles.css` declares light on
 * `:root` and dark on `:root[data-theme='dark']`, which is what makes the
 * `data-theme="light"` case work without a second light block: removing the
 * attribute falls back to the `:root` defaults.
 *
 * Anti-flash: `index.html` runs a tiny inline script that applies the stored
 * theme before first paint. That script must stay in sync with
 * `readStoredTheme`/`STORAGE_KEY` below.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly _theme = signal<Theme>(readStoredTheme());
  readonly theme = this._theme.asReadonly();
  private room: RoomTheme | null = null;

  constructor() {
    this.apply(this._theme());
  }

  setTheme(theme: Theme): void {
    this._theme.set(theme);
    this.apply(theme);
    try {
      localStorage.setItem(STORAGE_KEY, theme);
    } catch {
      // Private mode / storage disabled: the theme still applies for this
      // session, it just will not be remembered. Not worth failing over.
    }
  }

  /**
   * Dress the whole app in a room's colours while that room is open, or
   * `null` to give the app its own theme back. Set on `<html>` rather than on
   * the room's element so everything on screen with it (the dock, the
   * assistant, popovers, toasts) takes the same colours. Never saved: the
   * person's own light or dark choice is untouched.
   */
  present(room: RoomTheme | null): void {
    this.room = room;
    this.apply(this._theme());
  }

  toggle(): void {
    this.setTheme(this._theme() === DARK ? LIGHT : DARK);
  }

  private apply(chosen: Theme): void {
    const root = document.documentElement;
    const theme = this.room?.mode ?? chosen;
    if (this.room) root.setAttribute('data-room-theme', this.room.name);
    else root.removeAttribute('data-room-theme');
    if (theme === DARK) {
      root.setAttribute('data-theme', DARK);
    } else {
      // Removing the attribute (rather than setting 'light') keeps `:root` as
      // the single owner of the light values.
      root.removeAttribute('data-theme');
    }
  }
}

/**
 * Read the persisted choice, defaulting to the OS preference on first visit.
 * Deliberately does NOT persist the OS-derived value: a user who never chose
 * should keep following their system when it changes.
 */
export function readStoredTheme(): Theme {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === DARK || stored === LIGHT) return stored;
  } catch {
    // fall through to the OS preference
  }
  return prefersDark() ? DARK : LIGHT;
}

export function prefersDark(): boolean {
  return (
    typeof window !== 'undefined' &&
    typeof window.matchMedia === 'function' &&
    window.matchMedia('(prefers-color-scheme: dark)').matches
  );
}

export const THEME_STORAGE_KEY = STORAGE_KEY;
