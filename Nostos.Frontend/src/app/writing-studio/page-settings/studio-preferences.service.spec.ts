import { TestBed } from '@angular/core/testing';

import {
  DEFAULT_STUDIO_PREFERENCES,
  STUDIO_PREFERENCES_KEY,
  StudioPreferencesService,
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

  it('saves changes and reads them back in a later visit', () => {
    const service = TestBed.inject(StudioPreferencesService);
    service.update({ theme: 'study', font: 'lora' });
    TestBed.tick();
    expect(JSON.parse(localStorage.getItem(STUDIO_PREFERENCES_KEY)!)).toEqual(
      expect.objectContaining({ theme: 'study', font: 'lora' }),
    );
    expect(service.fontStack()).toContain('Studio Lora');

    service.reset();
    expect(service.preferences()).toEqual(DEFAULT_STUDIO_PREFERENCES);
  });

  it('survives corrupt storage', () => {
    localStorage.setItem(STUDIO_PREFERENCES_KEY, '{not json');
    expect(TestBed.inject(StudioPreferencesService).preferences()).toEqual(DEFAULT_STUDIO_PREFERENCES);
  });
});
