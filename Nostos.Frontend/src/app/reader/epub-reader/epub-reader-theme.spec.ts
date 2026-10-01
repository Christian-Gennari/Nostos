import { Contents } from 'epubjs';
// epub.js does not export Themes from its entry point; the class is the real
// one the rendition uses (epubjs/src/rendition.js constructs it the same way).
// @ts-expect-error untyped deep import of the epub.js source module
import Themes from 'epubjs/src/themes';

import { registerNostosReaderThemes, selectNostosReaderTheme } from './epub-reader.component';

/**
 * In-book light/dark switching against the REAL epub.js Themes and Contents
 * (the reader spec mocks epub.js, which is how this bug stayed green).
 *
 * The bug: dark -> light -> dark left the book page white in a dark app until a
 * reload. epub.js keeps one <style> per theme in each rendered section and
 * re-selecting a theme reuses its existing, earlier element, so the later
 * light stylesheet kept winning.
 */
describe('EPUB reader theme switching with real epub.js', () => {
  let iframe: HTMLIFrameElement;
  let section: { destroy(): void } | null = null;

  afterEach(() => {
    // Stop epub.js's resize/expand listeners before the iframe goes away.
    section?.destroy();
    section = null;
    iframe?.remove();
  });

  /** A rendered section: an iframe document wrapped in epub.js Contents. */
  function renderedSection() {
    iframe = document.createElement('iframe');
    document.body.appendChild(iframe);
    const doc = iframe.contentDocument!;
    doc.open();
    doc.write('<html><head></head><body><p>Chapter 1</p></body></html>');
    doc.close();

    // Contents measures text with Range#getBoundingClientRect, which jsdom
    // lacks; the Range here is the iframe window's own.
    const frameRange = (iframe.contentWindow as unknown as { Range: typeof Range }).Range;
    if (!('getBoundingClientRect' in frameRange.prototype)) {
      Object.defineProperty(frameRange.prototype, 'getBoundingClientRect', {
        configurable: true,
        value: () => new DOMRect(0, 0, 0, 0),
      });
    }

    const contents = new Contents(doc, doc.body, '', 0);
    section = contents as unknown as { destroy(): void };
    const rendition = {
      hooks: { content: { register: () => undefined } },
      getContents: () => [contents],
    };
    const themes = new Themes(rendition);
    registerNostosReaderThemes(themes);
    return { doc, contents, themes };
  }

  /** Ids of the Nostos theme stylesheets in <head>, in cascade order. */
  function nostosSheets(doc: Document): string[] {
    return Array.from(doc.head.querySelectorAll('style'))
      .map((style) => style.id)
      .filter((id) => id.startsWith('epubjs-inserted-css-nostos-'));
  }

  it('reproduces the cause: plain themes.select leaves the light sheet winning after dark -> light -> dark', () => {
    const { doc, themes } = renderedSection();

    themes.select('nostos-dark');
    themes.select('nostos-light');
    themes.select('nostos-dark');

    // Both sheets remain, and the light one is LAST, so its identical
    // !important rules win the cascade: the white page.
    expect(nostosSheets(doc)).toEqual([
      'epubjs-inserted-css-nostos-dark',
      'epubjs-inserted-css-nostos-light',
    ]);
  });

  it('dark -> light -> dark leaves only the dark stylesheet and class', () => {
    const { doc, contents, themes } = renderedSection();

    selectNostosReaderTheme(themes, [contents], 'dark');
    selectNostosReaderTheme(themes, [contents], 'light');
    selectNostosReaderTheme(themes, [contents], 'dark');

    expect(nostosSheets(doc)).toEqual(['epubjs-inserted-css-nostos-dark']);
    expect(doc.body.classList.contains('nostos-dark')).toBe(true);
    expect(doc.body.classList.contains('nostos-light')).toBe(false);

    const rules = Array.from(
      (doc.getElementById('epubjs-inserted-css-nostos-dark') as HTMLStyleElement).sheet!.cssRules,
    ).map((rule) => rule.cssText);
    // The dark ground #121318 (CSSOM may serialize it as rgb()).
    expect(
      rules.some((rule) => rule.startsWith('body {') && /#121318|rgb\(18, 19, 24\)/.test(rule)),
    ).toBe(true);
    // The re-created sheet holds one copy of the rules, not one per switch.
    expect(rules.filter((rule) => rule.startsWith('body {')).length).toBe(1);
  });

  it('light -> dark -> light leaves only the light stylesheet', () => {
    const { doc, contents, themes } = renderedSection();

    selectNostosReaderTheme(themes, [contents], 'light');
    selectNostosReaderTheme(themes, [contents], 'dark');
    selectNostosReaderTheme(themes, [contents], 'light');

    expect(nostosSheets(doc)).toEqual(['epubjs-inserted-css-nostos-light']);
    expect(doc.body.classList.contains('nostos-light')).toBe(true);
    expect(doc.body.classList.contains('nostos-dark')).toBe(false);
  });

  it('many switches never accumulate stylesheets', () => {
    const { doc, contents, themes } = renderedSection();

    for (let i = 0; i < 6; i++) {
      selectNostosReaderTheme(themes, [contents], i % 2 === 0 ? 'dark' : 'light');
    }

    expect(nostosSheets(doc)).toEqual(['epubjs-inserted-css-nostos-light']);
  });
});
