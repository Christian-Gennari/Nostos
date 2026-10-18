import { TestBed } from '@angular/core/testing';
import { ThemeService, THEME_STORAGE_KEY, readStoredTheme } from './theme.service';

/**
 * The theme service is the only writer of `data-theme` on <html>. These tests
 * pin the two contracts that a future refactor could silently break:
 *   1. light removes the attribute rather than setting `data-theme="light"`
 *      (so `:root` stays the single owner of the light values);
 *   2. the choice persists, and a fresh read honours it.
 */
describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    document.documentElement.removeAttribute('data-room-theme');
  });

  it('presents a room theme on <html> without saving it, and gives the app its own back', () => {
    const service = TestBed.inject(ThemeService);
    const root = document.documentElement;

    service.present({ name: 'study', mode: 'dark' });
    expect(root.getAttribute('data-room-theme')).toBe('study');
    expect(root.getAttribute('data-theme')).toBe('dark');
    expect(service.theme()).toBe('light');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();

    // The person's own choice still lands while a room is presented...
    service.setTheme('dark');
    service.present({ name: 'paper', mode: 'light' });
    expect(root.hasAttribute('data-theme')).toBe(false);

    // ...and is what comes back when the room ends.
    service.present(null);
    expect(root.hasAttribute('data-room-theme')).toBe(false);
    expect(root.getAttribute('data-theme')).toBe('dark');
  });

  it('defaults to light when nothing is stored and the OS reports no preference', () => {
    const service = TestBed.inject(ThemeService);
    expect(service.theme()).toBe('light');
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });

  it('applies and persists dark', () => {
    const service = TestBed.inject(ThemeService);
    service.setTheme('dark');

    expect(service.theme()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
  });

  it('removes the attribute for light rather than setting data-theme="light"', () => {
    const service = TestBed.inject(ThemeService);
    service.setTheme('dark');
    service.setTheme('light');

    expect(service.theme()).toBe('light');
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');
  });

  it('toggles between the two themes', () => {
    const service = TestBed.inject(ThemeService);
    service.setTheme('light');
    service.toggle();
    expect(service.theme()).toBe('dark');
    service.toggle();
    expect(service.theme()).toBe('light');
  });

  it('reads a stored dark choice on boot', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'dark');
    expect(readStoredTheme()).toBe('dark');
  });

  it('ignores an unknown stored value instead of trusting it', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'chartreuse');
    // Falls back to the OS preference, which is not dark under the test runner.
    expect(readStoredTheme()).toBe('light');
  });
});
