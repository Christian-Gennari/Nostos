import type { Contents } from 'epubjs';

import { THEMES, type ThemeName } from '../../core/services/theme.service';

/** Anchors that are links. See the note on `pageRules`. */
const LINK_SELECTOR = 'a:any-link';

/**
 * The page colours of each app theme (issue #1074), mirroring its tokens in
 * styles.css: the iframe is a separate document and cannot read the parent's
 * CSS variables. Light keeps its white page and Dark its #121318 ground; Sepia
 * and Writer's Study use their reading paper (`--manuscript-paper`), the richer
 * cream and the warm dark desk, and Mist its slate.
 */
const PAGE: Record<ThemeName, { paper: string; ink: string; link: string; selection: string }> = {
  light: { paper: '#ffffff', ink: '#1a1a1a', link: '#60a5fa', selection: 'rgba(96, 165, 250, 0.3)' },
  sepia: { paper: '#fbf4e5', ink: '#2f241a', link: '#84492d', selection: 'rgba(132, 73, 45, 0.2)' },
  mist: { paper: '#1e2026', ink: '#c8ccd4', link: '#a4b2cb', selection: 'rgba(132, 148, 176, 0.32)' },
  study: { paper: '#1a1814', ink: '#e4d5bf', link: '#d4a556', selection: 'rgba(201, 149, 62, 0.3)' },
  dark: { paper: '#121318', ink: '#f0f1f4', link: '#8fbfae', selection: 'rgba(143, 191, 174, 0.35)' },
};

/** Rendition theme names: one Nostos normalization per app theme. */
const themeName = (theme: ThemeName) => `nostos-${theme}`;

/**
 * Color-only rules for the epub.js iframe. Only foreground, background, links,
 * and selection colors are overridden; book typography, layout, emphasis, and
 * images are left untouched (images are never inverted).
 */
function pageRules(theme: ThemeName): Record<string, Record<string, string>> {
  const page = PAGE[theme];
  return {
    html: { background: `${page.paper} !important`, color: `${page.ink} !important` },
    body: { background: `${page.paper} !important`, color: `${page.ink} !important` },
    // Publisher CSS often sets explicit text colors (e.g. h1 { color: #000 });
    // normalize every element to inherit the theme text color so headings and
    // body text stay readable. The link rule comes AFTER `body *` so the link
    // color wins for links (and their descendants).
    'body *': { color: 'inherit !important' },
    // `:any-link`, not `a`: only anchors with an href are links. Books mark page
    // breaks with `<a id="page_8"/>`, and epub.js hands the XHTML to the HTML
    // parser (iframe.srcdoc), where that anchor is never closed and swallows
    // the rest of the chapter. A bare `a` rule then colored all of it as a link.
    [LINK_SELECTOR]: { color: `${page.link} !important` },
    '::selection': { background: `${page.selection} !important` },
  };
}

/** The theme surface of `rendition.themes` the reader drives. */
interface ReaderThemes {
  register(name: string, rules: Record<string, Record<string, string>>): void;
  select(name: string): void;
}

/** Registers every Nostos theme on a rendition (once per rendition). */
export function registerNostosReaderThemes(themes: ReaderThemes): void {
  for (const { value } of THEMES) themes.register(themeName(value), pageRules(value));
}

/**
 * Selects the Nostos theme matching the app theme in every rendered section.
 *
 * epub.js (0.3.93) keeps one `<style id="epubjs-inserted-css-<theme>">` per
 * theme in each section document and never removes or reorders it: selecting
 * a theme again appends its rules to the EXISTING element. All Nostos themes
 * use the same `!important` selectors, so whichever element sits later in
 * <head> wins. After dark -> light -> dark the light element is the later one,
 * and the page stayed white inside a dark app until a reload. Removing every
 * Nostos stylesheet first means epub.js re-creates only the selected one,
 * last in <head>; sections rendered later only ever receive the current theme.
 */
export function selectNostosReaderTheme(
  themes: ReaderThemes,
  contents: readonly Pick<Contents, 'document'>[],
  theme: ThemeName,
): void {
  for (const content of contents) {
    for (const { value } of THEMES) {
      content.document?.getElementById(`epubjs-inserted-css-${themeName(value)}`)?.remove();
    }
  }
  themes.select(themeName(theme));
}
