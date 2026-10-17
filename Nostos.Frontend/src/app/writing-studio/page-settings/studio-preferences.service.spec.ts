import { TestBed } from '@angular/core/testing';

import {
  DEFAULT_STUDIO_PREFERENCES,
  STUDIO_PREFERENCES_KEY,
  StudioPreferencesService,
  nearestLineSpacing,
  nearestTextWidth,
  normalizeStudioPreferences,
} from './studio-preferences.service';

describe('Studio page preferences (issue #1041)', () => {
  beforeEach(() => localStorage.clear());

  it('starts from the standard page', () => {
    expect(normalizeStudioPreferences(null)).toEqual(DEFAULT_STUDIO_PREFERENCES);
  });

  it('distrusts stored values: unknown names and out-of-range numbers fall back or clamp', () => {
    const prefs = normalizeStudioPreferences({
      theme: 'neon',
      font: 'comic-sans',
      fontSize: 400,
      lineSpacing: 'wide',
      textWidth: 5,
      spellCheck: 'yes',
      soundVolume: 3,
    });
    expect(prefs.theme).toBe('match');
    expect(prefs.font).toBe('newsreader');
    expect(prefs.fontSize).toBe(28);
    expect(prefs.lineSpacing).toBe(1.8);
    expect(prefs.textWidth).toBe(40);
    expect(prefs.spellCheck).toBe(true);
    expect(prefs.soundVolume).toBe(1);
  });

  it('keeps a valid choice', () => {
    const prefs = normalizeStudioPreferences({ theme: 'sepia', font: 'eb-garamond', fontSize: 21, lineSpacing: 2.14, textWidth: null });
    expect(prefs).toEqual(
      expect.objectContaining({ theme: 'sepia', font: 'eb-garamond', fontSize: 21, lineSpacing: 2.1, textWidth: null }),
    );
  });

  it('saves changes once they settle, and reads them back in a later visit', async () => {
    const service = TestBed.inject(StudioPreferencesService);
    service.update({ theme: 'study', font: 'lora' });
    TestBed.tick();
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(JSON.parse(localStorage.getItem(STUDIO_PREFERENCES_KEY)!)).toEqual(
      expect.objectContaining({ theme: 'study', font: 'lora' }),
    );
    expect(service.fontStack()).toContain('Studio Lora');

    service.reset();
    expect(service.preferences()).toEqual(DEFAULT_STUDIO_PREFERENCES);
  });

  it('writes a waiting change at once when the page goes away', () => {
    const service = TestBed.inject(StudioPreferencesService);
    service.update({ theme: 'sepia' });
    TestBed.tick();
    window.dispatchEvent(new Event('pagehide'));
    expect(JSON.parse(localStorage.getItem(STUDIO_PREFERENCES_KEY)!).theme).toBe('sepia');
  });

  it('carries the old typewriter flag over once and removes it', () => {
    localStorage.setItem('nostos.typewriter', '1');
    expect(TestBed.inject(StudioPreferencesService).preferences().typewriter).toBe(true);
    expect(localStorage.getItem('nostos.typewriter')).toBeNull();
  });

  it('moves the retired Dark and Black themes to following the app (#1060)', () => {
    expect(normalizeStudioPreferences({ theme: 'dark' }).theme).toBe('match');
    expect(normalizeStudioPreferences({ theme: 'black' }).theme).toBe('match');
    expect(normalizeStudioPreferences({ theme: 'mist' }).theme).toBe('mist');
  });

  it('reads any stored spacing and width as the nearest preset (#1060)', () => {
    expect(nearestLineSpacing(1.2)).toBe(1.5);
    expect(nearestLineSpacing(1.8)).toBe(1.8);
    expect(nearestLineSpacing(2.4)).toBe(2.2);
    expect(nearestTextWidth(null)).toBeNull();
    expect(nearestTextWidth(40)).toBe(66);
    expect(nearestTextWidth(84)).toBeNull();
    expect(nearestTextWidth(96)).toBe(100);
  });

  it('survives corrupt storage', () => {
    localStorage.setItem(STUDIO_PREFERENCES_KEY, '{not json');
    expect(TestBed.inject(StudioPreferencesService).preferences()).toEqual(DEFAULT_STUDIO_PREFERENCES);
  });
});
