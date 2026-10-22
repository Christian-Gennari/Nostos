import { isCitationStyleId } from '../citations/citation-styles';
import { STUDIO_FONTS, type StudioPreferences, nearestLineSpacing } from '../page-settings/studio-preferences.service';
import type { PageSetup, PaperSize } from './export-model';

/**
 * Page setup for an export (issue #1164). Saved per writing on the server;
 * until a writing has one, it starts from the Studio's own preferences, so
 * the first export already looks like the page the writer has been using.
 */

export const EXPORT_MARGINS = [
  { label: 'Narrow', value: 12.7 },
  { label: 'Normal', value: 25.4 },
  { label: 'Wide', value: 38.1 },
] as const;

/** Document line spacings. The Studio's screen spacings map onto these. */
export const EXPORT_LINE_SPACINGS = [
  { label: 'Single', value: 1 },
  { label: '1.15', value: 1.15 },
  { label: '1.5', value: 1.5 },
  { label: 'Double', value: 2 },
] as const;

/** Tight, Normal and Airy on screen read as these on paper. */
const FROM_STUDIO_SPACING: Record<number, number> = { 1.5: 1.15, 1.8: 1.5, 2.2: 2 };

/** The regions that use US Letter; everywhere else uses A4. */
const LETTER_REGIONS = new Set(['US', 'CA', 'MX', 'PH', 'CL', 'CO', 'VE', 'CR', 'GT', 'PA', 'PR', 'SV', 'DO', 'NI']);

export function defaultPaper(locale: string | null | undefined): PaperSize {
  const region = /[-_]([A-Za-z]{2})\b/.exec(locale ?? '')?.[1]?.toUpperCase();
  return region && LETTER_REGIONS.has(region) ? 'letter' : 'a4';
}

export function pageSetupFromStudio(prefs: StudioPreferences, locale: string | null | undefined): PageSetup {
  return {
    paper: defaultPaper(locale),
    marginMm: 25.4,
    font: prefs.font,
    lineSpacing: FROM_STUDIO_SPACING[nearestLineSpacing(prefs.lineSpacing)] ?? 1.5,
    citationStyle: prefs.citationStyle,
  };
}

function near<T extends { value: number }>(options: readonly T[], value: number): number {
  return options.reduce((best, option) => (Math.abs(option.value - value) < Math.abs(best.value - value) ? option : best)).value;
}

/** A saved setup is untrusted: anything unknown or out of range takes the fallback's value. */
export function normalizePageSetup(raw: unknown, fallback: PageSetup): PageSetup {
  const value = (raw && typeof raw === 'object' ? raw : {}) as Partial<Record<keyof PageSetup, unknown>>;
  const margin = typeof value.marginMm === 'number' && Number.isFinite(value.marginMm) ? value.marginMm : null;
  const spacing = typeof value.lineSpacing === 'number' && Number.isFinite(value.lineSpacing) ? value.lineSpacing : null;
  return {
    paper: value.paper === 'a4' || value.paper === 'letter' ? value.paper : fallback.paper,
    marginMm: margin === null ? fallback.marginMm : near(EXPORT_MARGINS, margin),
    font: STUDIO_FONTS.some((f) => f.value === value.font) ? (value.font as string) : fallback.font,
    lineSpacing: spacing === null ? fallback.lineSpacing : near(EXPORT_LINE_SPACINGS, spacing),
    citationStyle: isCitationStyleId(value.citationStyle) ? value.citationStyle : fallback.citationStyle,
  };
}
