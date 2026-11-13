import { computed, signal } from '@angular/core';
import type { Contents, Rendition } from 'epubjs';

import { type ThemeMode, type ThemeName, themeOption } from '../../core/services/theme.service';
import { registerNostosReaderThemes, selectNostosReaderTheme } from './epub-reader-theme';
import { scaleAbsoluteFontSizes, setTextScale } from './epub-text-size';
import {
  DEFAULT_TYPOGRAPHY,
  EPUB_LINE_OPTIONS,
  LIBRON_FACES,
  fontFamilyFromStored,
  libronFaceUrls,
  libronFontFaceCss,
  marginInsetPercent,
  markFloatedIllustrations,
  markGlyphElements,
  typographyCss,
  type EpubTypography,
} from './epub-typography';

export const TYPOGRAPHY_STYLE_ID = 'nostos-typography';
export const READER_FONTS_STYLE_ID = 'nostos-reader-fonts';

/**
 * Quiet window for coalescing a burst of typography changes into ONE
 * re-pagination.
 */
export const TYPOGRAPHY_APPLY_QUIET_MS = 180;

/**
 * Reader-wide typography preference: one key for every EPUB, because the reader
 * should remember the setting rather than the book. Earlier versions wrote
 * `nostos.epub-typography.<bookId>`; `restoreSavedTypography` adopts that value
 * once so an existing choice is not lost.
 */
export const TYPOGRAPHY_STORAGE_KEY = 'nostos.epub-typography';

/**
 * Text size follows the same reader-wide persistence semantics as the controls
 * beside it in View settings. Older builds stored size per book; the first
 * valid legacy value encountered is adopted into this key.
 */
export const FONT_SIZE_STORAGE_KEY = 'nostos.epub-font-size';

export interface EpubTypographyHost {
  bookId(): string;
  rendition(): Rendition | null;
  renderedContents(): Contents[];
  pageElement(): HTMLElement | null;
  activeTheme(): ThemeName;
  releasePageHold(restore?: boolean): void;
  onThemeChanged(mode: ThemeMode): void;
}

/**
 * Owns EPUB typography & font-size persistence (including legacy per-book
 * migration), coalesced re-pagination timers, style/font-face injection,
 * and rendition theme registration/switching.
 */
export class EpubTypographyController {
  private readonly currentFontSize = signal(100);

  /** Reader typography (typeface, line height, margins), persisted reader-wide. */
  readonly typography = signal<EpubTypography>({ ...DEFAULT_TYPOGRAPHY });

  /** Outer margin for the current preset, as a percentage of the capped spread. */
  readonly marginInset = computed(() => marginInsetPercent(this.typography().margin));

  /** Text size in percent. */
  readonly fontSizePercent = this.currentFontSize.asReadonly();

  /** Size applied to the current rendition — lets a coalesced no-op be skipped. */
  private appliedFontSize: number | null = null;

  /** Typography key applied to the open contents — same purpose as above. */
  private appliedTypographyKey: string | null = null;

  /** Pending trailing applies; see requestFontSizeApply / requestTypographyApply. */
  private fontApplyTimer: ReturnType<typeof setTimeout> | null = null;
  private typographyApplyTimer: ReturnType<typeof setTimeout> | null = null;
  private marginApplyTimer: ReturnType<typeof setTimeout> | null = null;

  /** Libron is fetched once per reader instance; see preloadLibron. */
  private libronPreloaded = false;
  private destroyed = false;

  constructor(private readonly host: EpubTypographyHost) {}

  zoomIn(): void {
    this.currentFontSize.update((s) => Math.min(s + 10, 200));
    this.requestFontSizeApply();
  }

  zoomOut(): void {
    this.currentFontSize.update((s) => Math.max(s - 10, 50));
    this.requestFontSizeApply();
  }

  /** Merge a patch into the typography, persist it, and repaint open sections. */
  setTypography(patch: Partial<EpubTypography>): void {
    const next = { ...this.typography(), ...patch };
    this.typography.set(next);
    if (next.fontFamily === 'libron') this.preloadLibron();
    try {
      localStorage.setItem(TYPOGRAPHY_STORAGE_KEY, JSON.stringify(next));
    } catch {
      // Private-mode storage can throw — the setting still applies for the session.
    }
    this.requestTypographyApply();
  }

  resetTypography(): void {
    this.currentFontSize.set(100);
    this.requestFontSizeApply();
    this.setTypography({ ...DEFAULT_TYPOGRAPHY });
  }

  /**
   * Reset rendition-applied guards and cancel pending timers before opening a
   * fresh book so the new rendition always receives the current preferences.
   */
  resetForBookLoad(): void {
    this.cancelTimers();
    this.appliedFontSize = null;
    this.appliedTypographyKey = null;
  }

  /** Restore saved settings, register themes, and apply initial font size. */
  initializeRendition(): void {
    this.restoreSavedFontSize();
    this.registerReaderThemes();
    this.applyFontSize();
    this.restoreSavedTypography();
    if (this.typography().fontFamily === 'libron') this.preloadLibron();
  }

  applyFontSize(): void {
    const size = this.currentFontSize();
    const rendition = this.host.rendition();
    if (rendition && this.appliedFontSize !== size) {
      this.appliedFontSize = size;
      // Set the scale first so the re-layout epub.js starts for the body size
      // already sees the publisher's absolute sizes at the new scale.
      for (const c of this.host.renderedContents()) {
        if (c?.document) this.applyTextScale(c.document);
      }
      // A re-layout invalidates the held offset (#1254).
      this.host.releasePageHold(false);
      rendition.themes.fontSize(`${size}%`);
    }
    this.persistFontSize();
  }

  injectCustomStyles(contents: Contents): void {
    this.upsertTypographyStyle(contents.document);
    try {
      markGlyphElements(contents.document);
      markFloatedIllustrations(contents.document);
      scaleAbsoluteFontSizes(contents.document);
    } catch {
      // A section mid-teardown has no stylesheets to read — skip it.
    }
    this.applyTextScale(contents.document);
  }

  upsertTypographyStyle(doc: Document): void {
    try {
      if (!doc.getElementById(READER_FONTS_STYLE_ID)) {
        const fonts = doc.createElement('style');
        fonts.id = READER_FONTS_STYLE_ID;
        fonts.textContent = libronFontFaceCss(document.baseURI);
        doc.head.appendChild(fonts);
      }
      let style = doc.getElementById(TYPOGRAPHY_STYLE_ID);
      if (!style) {
        style = doc.createElement('style');
        style.id = TYPOGRAPHY_STYLE_ID;
        doc.head.appendChild(style);
      }
      style.textContent = typographyCss(this.typography());
    } catch {
      // A section mid-teardown has no head to write to — skip it.
    }
  }

  applyTypographyToOpenContents(): void {
    try {
      for (const c of this.host.renderedContents()) {
        if (c?.document) this.upsertTypographyStyle(c.document);
      }
    } catch {
      // Best-effort repaint — the content hook covers newly rendered sections.
    }
  }

  registerReaderThemes(): void {
    const themes = this.host.rendition()?.themes;
    if (!themes) return;

    registerNostosReaderThemes(themes);
    this.selectReaderTheme(this.host.activeTheme());
  }

  selectReaderTheme(theme: ThemeName): void {
    const themes = this.host.rendition()?.themes;
    if (!themes) return;
    selectNostosReaderTheme(themes, this.host.renderedContents(), theme);
    this.host.onThemeChanged(themeOption(theme).mode);
  }

  destroy(): void {
    this.destroyed = true;
    this.cancelTimers();
  }

  private cancelTimers(): void {
    if (this.fontApplyTimer !== null) {
      clearTimeout(this.fontApplyTimer);
      this.fontApplyTimer = null;
    }
    if (this.typographyApplyTimer !== null) {
      clearTimeout(this.typographyApplyTimer);
      this.typographyApplyTimer = null;
    }
    if (this.marginApplyTimer !== null) {
      clearTimeout(this.marginApplyTimer);
      this.marginApplyTimer = null;
    }
  }

  private requestFontSizeApply(): void {
    this.persistFontSize();
    if (!this.fontApplyTimer) {
      this.applyFontSize();
    } else {
      clearTimeout(this.fontApplyTimer);
    }
    this.fontApplyTimer = setTimeout(() => {
      this.fontApplyTimer = null;
      if (this.destroyed) return;
      this.applyFontSize();
    }, TYPOGRAPHY_APPLY_QUIET_MS);
  }

  private requestTypographyApply(): void {
    if (!this.typographyApplyTimer) {
      this.applyTypography();
    } else {
      clearTimeout(this.typographyApplyTimer);
    }
    this.typographyApplyTimer = setTimeout(() => {
      this.typographyApplyTimer = null;
      if (this.destroyed) return;
      this.applyTypography();
    }, TYPOGRAPHY_APPLY_QUIET_MS);
  }

  private applyTypography(): void {
    const key = JSON.stringify(this.typography());
    if (key === this.appliedTypographyKey) return;
    this.appliedTypographyKey = key;
    // A re-layout invalidates the held offset (#1254).
    this.host.releasePageHold(false);
    this.applyTypographyToOpenContents();
    // The margin is padding on our own container; the binding updates in the
    // next change-detection pass, so measure the page after that.
    if (this.marginApplyTimer !== null) {
      clearTimeout(this.marginApplyTimer);
    }
    this.marginApplyTimer = setTimeout(() => {
      this.marginApplyTimer = null;
      if (this.destroyed) return;
      this.applyMarginInset();
    }, 0);
  }

  private applyTextScale(doc: Document): void {
    try {
      setTextScale(doc, this.currentFontSize());
    } catch {
      // A section mid-teardown has no root to write to — skip it.
    }
  }

  private persistFontSize(): void {
    try {
      localStorage.setItem(FONT_SIZE_STORAGE_KEY, String(this.currentFontSize()));
    } catch {
      // Private-mode storage can throw — the size still applies for the session.
    }
  }

  private restoreSavedFontSize(): void {
    try {
      const read = (key: string): number | null => {
        const raw = localStorage.getItem(key);
        if (raw == null) return null;
        const parsed = parseInt(raw, 10);
        if (!Number.isFinite(parsed)) return null;
        return Math.min(200, Math.max(50, parsed));
      };

      const stored = read(FONT_SIZE_STORAGE_KEY);
      if (stored !== null) {
        this.currentFontSize.set(stored);
        return;
      }

      const legacy = read(`nostos.epub-font-size.${this.host.bookId()}`);
      if (legacy === null) return;

      this.currentFontSize.set(legacy);
      localStorage.setItem(FONT_SIZE_STORAGE_KEY, String(legacy));
    } catch {
      // Storage unreadable — fall back to 100%.
    }
  }

  private readStoredTypography(key: string): EpubTypography | null {
    try {
      const raw = localStorage.getItem(key);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as Partial<EpubTypography>;
      return {
        fontFamily: fontFamilyFromStored(parsed.fontFamily),
        lineHeight:
          typeof parsed.lineHeight === 'number' && EPUB_LINE_OPTIONS.includes(parsed.lineHeight)
            ? parsed.lineHeight
            : DEFAULT_TYPOGRAPHY.lineHeight,
        margin:
          parsed.margin === 'narrow' || parsed.margin === 'wide' ? parsed.margin : 'normal',
      };
    } catch {
      // Corrupt or unreadable storage — treat it as "nothing stored".
      return null;
    }
  }

  private restoreSavedTypography(): void {
    const stored = this.readStoredTypography(TYPOGRAPHY_STORAGE_KEY);
    if (stored) {
      this.typography.set(stored);
      return;
    }

    const legacy = this.readStoredTypography(`nostos.epub-typography.${this.host.bookId()}`);
    if (!legacy) return;
    this.typography.set(legacy);
    try {
      localStorage.setItem(TYPOGRAPHY_STORAGE_KEY, JSON.stringify(legacy));
    } catch {
      // Adopting in memory is enough — the next change persists it.
    }
  }

  private applyMarginInset(): void {
    const page = this.host.pageElement();
    const rendition = this.host.rendition();
    if (!page || !rendition) return;
    const { clientWidth, clientHeight } = page;
    if (clientWidth <= 0 || clientHeight <= 0) return;
    try {
      // A re-layout invalidates the held offset (#1254).
      this.host.releasePageHold(false);
      rendition.resize(clientWidth, clientHeight);
    } catch {
      // Not laid out yet — the ResizeObserver path will catch up.
    }
  }

  private preloadLibron(): void {
    if (this.libronPreloaded || typeof FontFace === 'undefined') return;
    this.libronPreloaded = true;
    const urls = libronFaceUrls(document.baseURI);
    LIBRON_FACES.forEach((face, i) => {
      new FontFace('Libron', `url("${urls[i]}") format("woff2")`, {
        style: face.style,
        weight: String(face.weight),
      })
        .load()
        .catch(() => {
          // A missing asset must not break reading — the serif fallback in
          // the font stack takes over.
        });
    });
  }
}
