import { applyDocumentDirection, epubReadingDirection, isRtlLanguage } from './epub-direction';
import { pageActionForKey, surfaceActionForPoint, swipePageAction } from '../reader-keyboard';

describe('EPUB reading direction (#1388)', () => {
  it('knows the right-to-left languages, with regions and scripts', () => {
    for (const lang of ['he', 'ar', 'fa', 'ur', 'yi', 'heb', 'ar-EG', 'he_IL', 'AR', 'ku-Arab', 'pa-Arab']) {
      expect(isRtlLanguage(lang), lang).toBe(true);
    }
    for (const lang of ['en', 'sv', 'en-US', 'de', 'ja', 'ku-Latn', '', null, undefined]) {
      expect(isRtlLanguage(lang), String(lang)).toBe(false);
    }
  });

  it('lets the spine attribute win over the language', () => {
    expect(epubReadingDirection({ pageProgressionDirection: 'rtl', language: 'en' })).toBe('rtl');
    expect(epubReadingDirection({ pageProgressionDirection: 'ltr', language: 'he' })).toBe('ltr');
  });

  it('falls back to the language, then to ltr', () => {
    expect(epubReadingDirection({ pageProgressionDirection: '', language: 'he' })).toBe('rtl');
    expect(epubReadingDirection({ pageProgressionDirection: 'default', language: 'ar' })).toBe('rtl');
    expect(epubReadingDirection({ pageProgressionDirection: null, language: 'en' })).toBe('ltr');
    expect(epubReadingDirection({})).toBe('ltr');
  });

  it('sets dir=rtl on a document that has none, and leaves a stated one alone', () => {
    const bare = new DOMParser().parseFromString('<html lang="he"><body>x</body></html>', 'text/html');
    applyDocumentDirection(bare, 'rtl');
    expect(bare.documentElement.getAttribute('dir')).toBe('rtl');

    const stated = new DOMParser().parseFromString('<html><body dir="ltr">x</body></html>', 'text/html');
    applyDocumentDirection(stated, 'rtl');
    expect(stated.documentElement.hasAttribute('dir')).toBe(false);

    const ltr = new DOMParser().parseFromString('<html><body>x</body></html>', 'text/html');
    applyDocumentDirection(ltr, 'ltr');
    expect(ltr.documentElement.hasAttribute('dir')).toBe(false);
  });

  it('swaps arrows, edge taps and swipes in a right-to-left book', () => {
    expect(pageActionForKey({ key: 'ArrowLeft' }, true)).toBe('next');
    expect(pageActionForKey({ key: 'ArrowRight' }, true)).toBe('previous');
    expect(pageActionForKey({ key: 'PageDown' }, true)).toBe('next');
    expect(pageActionForKey({ key: ' ' }, true)).toBe('next');

    const tap = (clientX: number, rtl: boolean) =>
      surfaceActionForPoint({ target: null, clientX, width: 400, coarsePointer: true, edgePaging: true, rtl });
    expect(tap(10, true)).toBe('next');
    expect(tap(390, true)).toBe('previous');
    expect(tap(10, false)).toBe('previous');

    const swipe = (endX: number, rtl: boolean) =>
      swipePageAction({ target: null, startX: 200, startY: 100, endX, endY: 100, durationMs: 100, rtl });
    expect(swipe(100, true)).toBe('previous');
    expect(swipe(300, true)).toBe('next');
    expect(swipe(100, false)).toBe('next');
  });
});
