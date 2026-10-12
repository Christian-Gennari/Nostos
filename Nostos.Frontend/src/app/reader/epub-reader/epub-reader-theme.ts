import type { Contents } from 'epubjs';

import type { Theme } from '../../core/services/theme.service';

/**
 * Rendition theme names. Both Nostos normalizations are registered once per
 * rendition; the selected one always mirrors the app theme (ThemeService),
 * so the page never renders white inside a dark UI or vice versa.
 */
const NOSTOS_LIGHT_THEME = 'nostos-light';
const NOSTOS_DARK_THEME = 'nostos-dark';
/**
 * Color-only rules for the epub.js iframe, mirroring the Nostos light tokens
 * from styles.css (the iframe is a separate document and cannot read the
 * parent's CSS variables). Only foreground, background, links, and selection
 * colors are overridden; book typography, layout, emphasis, and images are
 * left untouched (images are never inverted).
 */
const NOSTOS_LIGHT_RULES: Record<string, Record<string, string>> = {
  html: { background: '#ffffff !important', color: '#1a1a1a !important' },
  body: { background: '#ffffff !important', color: '#1a1a1a !important' },
  // Publisher CSS often sets explicit text colors (e.g. h1 { color: #000 });
  // normalize every element to inherit the theme text color so headings and
  // body text stay readable. `a` comes AFTER `body *` so the link color wins
  // for links (and their descendants).
  'body *': { color: 'inherit !important' },
  a: { color: '#60a5fa !important' },
  '::selection': { background: 'rgba(96, 165, 250, 0.3) !important' },
};

/**
 * Color-only rules for the dark UI, mirroring the `:root[data-theme='dark']`
 * tokens from styles.css (ground #121318, ink #f0f1f4). Same contract as the
 * light rules: colors only, book typography and images untouched.
 */
const NOSTOS_DARK_RULES: Record<string, Record<string, string>> = {
  html: { background: '#121318 !important', color: '#f0f1f4 !important' },
  body: { background: '#121318 !important', color: '#f0f1f4 !important' },
  'body *': { color: 'inherit !important' },
  a: { color: '#8fbfae !important' },
  '::selection': { background: 'rgba(143, 191, 174, 0.35) !important' },
};

/** The theme surface of `rendition.themes` the reader drives. */
interface ReaderThemes {
  register(name: string, rules: Record<string, Record<string, string>>): void;
  select(name: string): void;
}

/** Registers both Nostos themes on a rendition (once per rendition). */
export function registerNostosReaderThemes(themes: ReaderThemes): void {
  themes.register(NOSTOS_LIGHT_THEME, NOSTOS_LIGHT_RULES);
  themes.register(NOSTOS_DARK_THEME, NOSTOS_DARK_RULES);
}

/**
 * Selects the Nostos theme matching the app theme in every rendered section.
 *
 * epub.js (0.3.93) keeps one `<style id="epubjs-inserted-css-<theme>">` per
 * theme in each section document and never removes or reorders it: selecting
 * a theme again appends its rules to the EXISTING element. Both Nostos themes
 * use the same `!important` selectors, so whichever element sits later in
 * <head> wins. After dark -> light -> dark the light element is the later one,
 * and the page stayed white inside a dark app until a reload. Removing both
 * Nostos stylesheets first means epub.js re-creates only the selected one,
 * last in <head>; sections rendered later only ever receive the current theme.
 */
export function selectNostosReaderTheme(
  themes: ReaderThemes,
  contents: readonly Pick<Contents, 'document'>[],
  theme: Theme,
): void {
  for (const content of contents) {
    for (const name of [NOSTOS_LIGHT_THEME, NOSTOS_DARK_THEME]) {
      content.document?.getElementById(`epubjs-inserted-css-${name}`)?.remove();
    }
  }
  themes.select(theme === 'dark' ? NOSTOS_DARK_THEME : NOSTOS_LIGHT_THEME);
}
