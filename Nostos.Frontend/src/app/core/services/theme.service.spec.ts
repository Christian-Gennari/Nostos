import { TestBed } from '@angular/core/testing';
import {
  DAY_THEME_STORAGE_KEY,
  NIGHT_THEME_STORAGE_KEY,
  THEMES,
  THEME_STORAGE_KEY,
  ThemeService,
  readStoredChoice,
} from './theme.service';

/**
 * The theme service is the only writer of `data-theme`, `data-palette` and
 * the theme-color meta tag. These tests pin the contracts a refactor could
 * silently break:
 *   1. light themes remove `data-theme` rather than setting "light" (so
 *      `:root` stays the single owner of the light values), dark ones set it;
 *   2. Sepia, Mist and Writer's Study add `data-palette`, Light and Dark don't;
 *   3. the choice persists, and nothing stored means Follow system;
 *   4. Follow system wears the day or night theme, and follows the OS live;
 *   5. a Studio page theme saved before #1074 is carried over once.
 */

/** A controllable `prefers-color-scheme: dark` query. */
function fakeColourScheme(dark: boolean) {
  const listeners: ((event: { matches: boolean }) => void)[] = [];
  const query = {
    matches: dark,
    addEventListener: (_: string, listener: (event: { matches: boolean }) => void) => listeners.push(listener),
    removeEventListener: () => undefined,
  };
  const original = window.matchMedia;
  window.matchMedia = (() => query) as unknown as typeof window.matchMedia;
  return {
    set(next: boolean) {
      query.matches = next;
      for (const listener of listeners) listener({ matches: next });
    },
    restore() {
      window.matchMedia = original;
    },
  };
}

describe('ThemeService', () => {
  let scheme: ReturnType<typeof fakeColourScheme>;
  const root = document.documentElement;

  beforeEach(() => {
    localStorage.clear();
    root.removeAttribute('data-theme');
    root.removeAttribute('data-palette');
    scheme = fakeColourScheme(false);
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    scheme.restore();
    localStorage.clear();
    root.removeAttribute('data-theme');
    root.removeAttribute('data-palette');
  });

  it('follows the system with Light by day when nothing is stored', () => {
    const service = TestBed.inject(ThemeService);
    expect(service.choice()).toBe('system');
    expect(service.active()).toBe('light');
    expect(service.mode()).toBe('light');
    expect(root.hasAttribute('data-theme')).toBe(false);
    expect(root.hasAttribute('data-palette')).toBe(false);
  });

  it('follows the system with Dark by night when nothing is stored', () => {
    scheme.set(true);
    const service = TestBed.inject(ThemeService);
    expect(service.active()).toBe('dark');
    expect(root.getAttribute('data-theme')).toBe('dark');
  });

  it('switches live when the OS changes, without saving anything', () => {
    const service = TestBed.inject(ThemeService);
    scheme.set(true);
    expect(service.active()).toBe('dark');
    expect(root.getAttribute('data-theme')).toBe('dark');
    scheme.set(false);
    expect(service.active()).toBe('light');
    expect(root.hasAttribute('data-theme')).toBe(false);
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();
  });

  it('wears the chosen day and night themes under Follow system', () => {
    const service = TestBed.inject(ThemeService);
    service.setDayTheme('sepia');
    service.setNightTheme('study');
    expect(service.active()).toBe('sepia');
    expect(root.getAttribute('data-palette')).toBe('sepia');
    expect(root.hasAttribute('data-theme')).toBe(false);

    scheme.set(true);
    expect(service.active()).toBe('study');
    expect(root.getAttribute('data-palette')).toBe('study');
    expect(root.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem(DAY_THEME_STORAGE_KEY)).toBe('sepia');
    expect(localStorage.getItem(NIGHT_THEME_STORAGE_KEY)).toBe('study');
  });

  it('refuses a day theme that is dark, or a night theme that is light', () => {
    const service = TestBed.inject(ThemeService);
    service.setDayTheme('mist');
    service.setNightTheme('sepia');
    expect(service.dayTheme()).toBe('light');
    expect(service.nightTheme()).toBe('dark');
  });

  for (const theme of THEMES) {
    it(`applies and persists ${theme.label}`, () => {
      const service = TestBed.inject(ThemeService);
      service.setChoice(theme.value);

      expect(service.active()).toBe(theme.value);
      expect(service.mode()).toBe(theme.mode);
      expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(theme.value);
      if (theme.mode === 'dark') expect(root.getAttribute('data-theme')).toBe('dark');
      else expect(root.hasAttribute('data-theme')).toBe(false);
      const palette = theme.value === 'light' || theme.value === 'dark' ? null : theme.value;
      expect(root.getAttribute('data-palette')).toBe(palette);
    });
  }

  it('keeps a fixed theme when the OS changes', () => {
    const service = TestBed.inject(ThemeService);
    service.setChoice('sepia');
    scheme.set(true);
    expect(service.active()).toBe('sepia');
  });

  it('keeps the day and night themes for when Follow system comes back', () => {
    const service = TestBed.inject(ThemeService);
    service.setDayTheme('sepia');
    service.setChoice('mist');
    service.setChoice('system');
    expect(service.active()).toBe('sepia');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('system');
  });

  it('sets the browser chrome colour to the theme ground', () => {
    const meta = document.createElement('meta');
    meta.name = 'theme-color';
    document.head.appendChild(meta);
    try {
      const service = TestBed.inject(ThemeService);
      service.setChoice('study');
      expect(meta.getAttribute('content')).toBe(THEMES.find((t) => t.value === 'study')!.chrome);
    } finally {
      meta.remove();
    }
  });

  it('toggles to the night theme from a light one, and to the day theme from a dark one', () => {
    const service = TestBed.inject(ThemeService);
    service.setDayTheme('sepia');
    service.setNightTheme('study');
    service.setChoice('light');
    service.toggle();
    expect(service.active()).toBe('study');
    service.toggle();
    expect(service.active()).toBe('sepia');
  });

  it('reads a stored choice on boot, including the old light and dark values', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'dark');
    expect(readStoredChoice()).toBe('dark');
    localStorage.setItem(THEME_STORAGE_KEY, 'light');
    expect(readStoredChoice()).toBe('light');
    localStorage.setItem(THEME_STORAGE_KEY, 'study');
    expect(readStoredChoice()).toBe('study');
  });

  it('ignores an unknown stored value instead of trusting it', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'chartreuse');
    expect(readStoredChoice()).toBe('system');
    localStorage.setItem(DAY_THEME_STORAGE_KEY, 'paper');
    expect(TestBed.inject(ThemeService).dayTheme()).toBe('light');
  });

  describe('carrying over a Studio page theme (#1041, #1060)', () => {
    const studio = (theme: string) =>
      localStorage.setItem('nostos.studio.preferences', JSON.stringify({ theme, font: 'lora' }));

    for (const theme of ['sepia', 'mist', 'study'] as const) {
      it(`makes ${theme} the app theme once, and saves it`, () => {
        studio(theme);
        const service = TestBed.inject(ThemeService);
        expect(service.choice()).toBe(theme);
        expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(theme);
      });
    }

    it('drops Paper and Follow app: they have nothing to carry over', () => {
      studio('paper');
      expect(TestBed.inject(ThemeService).choice()).toBe('system');
      expect(localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();
    });

    it('never overrides an app theme the person already chose', () => {
      localStorage.setItem(THEME_STORAGE_KEY, 'light');
      studio('study');
      expect(TestBed.inject(ThemeService).choice()).toBe('light');
    });

    it('survives unreadable Studio settings', () => {
      localStorage.setItem('nostos.studio.preferences', '{not json');
      expect(TestBed.inject(ThemeService).choice()).toBe('system');
    });
  });
});
