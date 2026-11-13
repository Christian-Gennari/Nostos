import type { Contents, Rendition } from 'epubjs';

import type { ThemeMode, ThemeName } from '../../core/services/theme.service';
import {
  EpubTypographyController,
  FONT_SIZE_STORAGE_KEY,
  TYPOGRAPHY_STORAGE_KEY,
} from './epub-typography-controller';
import { DEFAULT_TYPOGRAPHY } from './epub-typography';

describe('EpubTypographyController', () => {
  function createHarness() {
    const doc = document.implementation.createHTMLDocument('Chapter');
    const page = document.createElement('div');
    Object.defineProperty(page, 'clientWidth', { value: 800, configurable: true });
    Object.defineProperty(page, 'clientHeight', { value: 600, configurable: true });

    let activeTheme: ThemeName = 'light';
    const themes = {
      register: vi.fn(),
      select: vi.fn(),
      fontSize: vi.fn(),
    };
    const rendition = {
      themes,
      getContents: vi.fn(() => [{ document: doc }]),
      resize: vi.fn(),
    } as unknown as Rendition;

    const releasePageHold = vi.fn();
    const onThemeChanged = vi.fn<(mode: ThemeMode) => void>();

    const controller = new EpubTypographyController({
      bookId: () => 'book-1',
      rendition: () => rendition,
      renderedContents: () => [{ document: doc } as unknown as Contents],
      pageElement: () => page,
      activeTheme: () => activeTheme,
      releasePageHold,
      onThemeChanged,
    });

    return {
      doc,
      page,
      themes,
      rendition,
      controller,
      releasePageHold,
      onThemeChanged,
      setTheme: (t: ThemeName) => {
        activeTheme = t;
      },
    };
  }

  beforeEach(() => {
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('restores saved preferences, migrates legacy per-book values, and registers themes', () => {
    localStorage.setItem('nostos.epub-font-size.book-1', '140');
    localStorage.setItem(
      'nostos.epub-typography.book-1',
      JSON.stringify({ fontFamily: 'serif', lineHeight: 1.8, margin: 'wide' }),
    );

    const h = createHarness();
    h.setTheme('dark');
    h.controller.initializeRendition();

    expect(h.controller.fontSizePercent()).toBe(140);
    expect(localStorage.getItem(FONT_SIZE_STORAGE_KEY)).toBe('140');
    expect(h.controller.typography()).toEqual({
      fontFamily: 'libron',
      lineHeight: 1.8,
      margin: 'wide',
    });
    expect(JSON.parse(localStorage.getItem(TYPOGRAPHY_STORAGE_KEY)!)).toEqual({
      fontFamily: 'libron',
      lineHeight: 1.8,
      margin: 'wide',
    });
    expect(h.themes.register).toHaveBeenCalledTimes(5);
    expect(h.themes.select).toHaveBeenCalledWith('nostos-dark');
    expect(h.onThemeChanged).toHaveBeenCalledWith('dark');
    expect(h.themes.fontSize).toHaveBeenCalledWith('140%');
  });

  it('coalesces rapid font-size and typography changes and cancels pending timers on destroy', () => {
    vi.useFakeTimers();
    const h = createHarness();
    h.controller.initializeRendition();
    h.themes.fontSize.mockClear();

    h.controller.zoomIn();
    h.controller.zoomIn();
    h.controller.zoomIn();
    expect(h.themes.fontSize).toHaveBeenCalledTimes(1);
    expect(h.themes.fontSize).toHaveBeenLastCalledWith('110%');

    // Destroy cancels pending trailing font-size and margin-inset timers.
    h.controller.setTypography({ margin: 'wide' });
    h.controller.destroy();
    vi.advanceTimersByTime(500);

    expect(h.themes.fontSize).toHaveBeenCalledTimes(1);
    expect(h.rendition.resize).not.toHaveBeenCalled();
  });

  it('resets typography and font size to defaults', () => {
    vi.useFakeTimers();
    const h = createHarness();
    h.controller.initializeRendition();

    h.controller.setTypography({ fontFamily: 'mono', margin: 'wide' });
    h.controller.zoomIn();
    h.controller.resetTypography();
    vi.runAllTimers();

    expect(h.controller.typography()).toEqual(DEFAULT_TYPOGRAPHY);
    expect(h.controller.fontSizePercent()).toBe(100);
    expect(h.rendition.resize).toHaveBeenCalledWith(800, 600);
  });
});
