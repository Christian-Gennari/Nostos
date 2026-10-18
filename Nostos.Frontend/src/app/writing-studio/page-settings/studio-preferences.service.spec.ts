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
      soundPack: 'kazoo',
    });
    expect(prefs.font).toBe('newsreader');
    expect(prefs.fontSize).toBe(28);
    expect(prefs.lineSpacing).toBe(1.8);
    expect(prefs.textWidth).toBe(66);
    expect(prefs.spellCheck).toBe(true);
    expect(prefs.soundVolume).toBe(1);
    expect(prefs.soundPack).toBe('typewriter');
  });

  it('defaults to the real typewriter and keeps the keyboard when chosen (#1065)', () => {
    expect(DEFAULT_STUDIO_PREFERENCES.soundPack).toBe('typewriter');
    expect(DEFAULT_STUDIO_PREFERENCES.sounds).toBe(false);
    expect(normalizeStudioPreferences({ soundPack: 'keyboard' })).toEqual(
      expect.objectContaining({ soundPack: 'keyboard' }),
    );
  });

  it('keeps a valid choice', () => {
    const prefs = normalizeStudioPreferences({ font: 'eb-garamond', fontSize: 21, lineSpacing: 2.2, textWidth: null });
    expect(prefs).toEqual(
      expect.objectContaining({ font: 'eb-garamond', fontSize: 21, lineSpacing: 2.2, textWidth: null }),
    );
  });

  it('saves changes once they settle, and reads them back in a later visit', async () => {
    const service = TestBed.inject(StudioPreferencesService);
    service.update({ font: 'lora' });
    TestBed.tick();
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(JSON.parse(localStorage.getItem(STUDIO_PREFERENCES_KEY)!)).toEqual(
      expect.objectContaining({ font: 'lora' }),
    );
    expect(service.fontStack()).toContain('Studio Lora');

    service.reset();
    expect(service.preferences()).toEqual(DEFAULT_STUDIO_PREFERENCES);
  });

  it('writes a waiting change at once when the page goes away', () => {
    const service = TestBed.inject(StudioPreferencesService);
    service.update({ fontSize: 22 });
    TestBed.tick();
    window.dispatchEvent(new Event('pagehide'));
    expect(JSON.parse(localStorage.getItem(STUDIO_PREFERENCES_KEY)!).fontSize).toBe(22);
  });

  it('carries the old typewriter flag over once and removes it', () => {
    localStorage.setItem('nostos.typewriter', '1');
    expect(TestBed.inject(StudioPreferencesService).preferences().typewriter).toBe(true);
    expect(localStorage.getItem('nostos.typewriter')).toBeNull();
  });

  it('no longer keeps a page theme of its own: the app theme is the one (#1074)', () => {
    expect(normalizeStudioPreferences({ theme: 'mist' })).not.toHaveProperty('theme');
  });

  it('snaps spacing and width saved from the old sliders to the nearest preset (#1060)', () => {
    expect(normalizeStudioPreferences({ lineSpacing: 1.6, textWidth: 70 })).toEqual(
      expect.objectContaining({ lineSpacing: 1.5, textWidth: 66 }),
    );
    expect(normalizeStudioPreferences({ textWidth: 88 }).textWidth).toBeNull();

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
