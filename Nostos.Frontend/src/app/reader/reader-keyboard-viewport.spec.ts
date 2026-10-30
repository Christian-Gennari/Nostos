import { keyboardInset, overlayKeyboardWhileReading, withInteractiveWidget } from './reader-keyboard-viewport';

const APP_VIEWPORT =
  'width=device-width, initial-scale=1, viewport-fit=cover, interactive-widget=resizes-content';

/** The on-screen keyboard while a book is open (#1270). */
describe('reader keyboard viewport (#1270)', () => {
  it('switches the app viewport to let the keyboard overlay the page', () => {
    expect(withInteractiveWidget(APP_VIEWPORT, 'resizes-visual')).toBe(
      'width=device-width, initial-scale=1, viewport-fit=cover, interactive-widget=resizes-visual',
    );
  });

  it('rewrites the key wherever it sits, and adds it when it is missing', () => {
    expect(withInteractiveWidget('interactive-widget=resizes-content, width=device-width', 'resizes-visual')).toBe(
      'interactive-widget=resizes-visual, width=device-width',
    );
    expect(withInteractiveWidget('width=device-width', 'resizes-visual')).toBe(
      'width=device-width, interactive-widget=resizes-visual',
    );
    expect(withInteractiveWidget('', 'resizes-visual')).toBe('interactive-widget=resizes-visual');
  });

  it('measures the keyboard from visualViewport', () => {
    // Pixel 7 portrait: 915 px layout, keyboard leaves 575 px visible.
    expect(keyboardInset(915, { height: 575, offsetTop: 0 })).toBe(340);
    // The visual viewport scrolled to keep the field in view.
    expect(keyboardInset(915, { height: 575, offsetTop: 40 })).toBe(300);
    expect(keyboardInset(915, { height: 914.6, offsetTop: 0 })).toBe(0);
  });

  it('never lifts the bar below the screen, and stays put without visualViewport', () => {
    expect(keyboardInset(915, { height: 915, offsetTop: 0 })).toBe(0);
    expect(keyboardInset(800, { height: 760, offsetTop: 60.4 })).toBe(0);
    expect(keyboardInset(915, null)).toBe(0);
    expect(keyboardInset(915, undefined)).toBe(0);
  });

  describe('while the reader is mounted', () => {
    let meta: HTMLMetaElement;
    let host: HTMLElement;
    let listeners: Map<string, () => void>;
    let visual: { height: number; offsetTop: number; addEventListener: any; removeEventListener: any };
    let win: Window;

    beforeEach(() => {
      meta = document.createElement('meta');
      meta.name = 'viewport';
      meta.setAttribute('content', APP_VIEWPORT);
      document.head.appendChild(meta);
      host = document.createElement('div');
      listeners = new Map();
      visual = {
        height: 844,
        offsetTop: 0,
        addEventListener: vi.fn((type: string, fn: () => void) => listeners.set(type, fn)),
        removeEventListener: vi.fn((type: string) => listeners.delete(type)),
      };
      win = { document, innerHeight: 844, visualViewport: visual } as unknown as Window;
    });

    afterEach(() => meta.remove());

    it('overlays the keyboard, tracks its height, and puts everything back on teardown', () => {
      const teardown = overlayKeyboardWhileReading(host, win);
      expect(meta.getAttribute('content')).toContain('interactive-widget=resizes-visual');
      expect(host.style.getPropertyValue('--reader-keyboard-inset')).toBe('0px');

      visual.height = 500;
      listeners.get('resize')!();
      expect(host.style.getPropertyValue('--reader-keyboard-inset')).toBe('344px');

      visual.offsetTop = 44;
      listeners.get('scroll')!();
      expect(host.style.getPropertyValue('--reader-keyboard-inset')).toBe('300px');

      teardown();
      expect(meta.getAttribute('content')).toBe(APP_VIEWPORT);
      expect(host.style.getPropertyValue('--reader-keyboard-inset')).toBe('');
      expect(listeners.size).toBe(0);
    });
  });
});
