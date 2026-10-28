import { TocItem } from '../reader.interface';
import { normalizeEpubHrefForComparison } from './epub-grounded-source';

/**
 * Chapter progress for the EPUB reader's bar (#1269). Pure functions; the
 * component only wires them to epub.js.
 *
 * A chapter is a run of spine sections, [startSpine, endSpine): from the
 * section a TOC entry opens to the next section another TOC entry opens.
 * Untitled sections in between (a chapter split into `ch07a.xhtml`,
 * `ch07b.xhtml`) belong to the chapter before them. Sections before the first
 * TOC entry (cover, title page) are front matter: a range with no label.
 *
 * Known limit, accepted: several TOC entries inside ONE file (`ch.xhtml#s1`,
 * `#s2`) collapse into one range covering the file. Placing each fragment
 * would mean loading every section document.
 */
export interface EpubChapterRange {
  /** The TOC entry the range starts with; null for front matter. */
  label: string | null;
  startSpine: number;
  /** Exclusive. */
  endSpine: number;
}

/** Location indexes `[first, end)` of a chapter in epub.js's locations list. */
export interface EpubChapterLocations {
  first: number;
  end: number;
}

/** Chapter ranges for a book, in spine order. Empty when the TOC places nothing. */
export function epubChapterRanges(toc: TocItem[], spineHrefs: string[]): EpubChapterRange[] {
  const spineByHref = new Map<string, number>();
  spineHrefs.forEach((href, index) => {
    const key = normalizeEpubHrefForComparison(href);
    if (!spineByHref.has(key)) spineByHref.set(key, index);
  });

  // The first entry (reading order) that opens a section names it, as the
  // progress label's own TOC match does.
  const labelBySpine = new Map<number, string>();
  const visit = (items: TocItem[]) => {
    for (const item of items) {
      const spine = spineByHref.get(normalizeEpubHrefForComparison(String(item.target)));
      if (spine !== undefined && !labelBySpine.has(spine)) labelBySpine.set(spine, item.label);
      visit(item.children ?? []);
    }
  };
  visit(toc);

  const starts = [...labelBySpine.keys()].sort((a, b) => a - b);
  if (starts.length === 0) return [];

  const ranges: EpubChapterRange[] = [];
  if (starts[0] > 0) ranges.push({ label: null, startSpine: 0, endSpine: starts[0] });
  starts.forEach((start, i) => {
    ranges.push({
      label: labelBySpine.get(start)!.trim() || null,
      startSpine: start,
      endSpine: starts[i + 1] ?? spineHrefs.length,
    });
  });
  return ranges;
}

/** The range a spine section belongs to, or null when there is none. */
export function chapterRangeForSpine(
  ranges: EpubChapterRange[],
  spineIndex: number | null | undefined,
): EpubChapterRange | null {
  if (spineIndex == null) return null;
  let found: EpubChapterRange | null = null;
  for (const range of ranges) {
    if (range.startSpine <= spineIndex) found = range;
    else break;
  }
  return found;
}

/** Spine index a CFI points into (`epubcfi(/6/N…` is spine item N/2 - 1). */
export function spineIndexFromCfi(cfi: string): number | null {
  const match = /^epubcfi\(\/6\/(\d+)/.exec(cfi);
  if (!match) return null;
  const step = Number(match[1]);
  return step >= 2 && step % 2 === 0 ? step / 2 - 1 : null;
}

/**
 * Each range's location indexes, from the ordered list of location CFIs, in
 * one pass. Built once per book when locations exist.
 */
export function chapterLocationBounds(
  ranges: EpubChapterRange[],
  locationCfis: string[],
): EpubChapterLocations[] {
  const spines = locationCfis.map((cfi) => spineIndexFromCfi(cfi) ?? -1);
  const firstAtOrAfter = (spine: number, from: number) => {
    let i = from;
    while (i < spines.length && spines[i] < spine) i++;
    return i;
  };
  const bounds: EpubChapterLocations[] = [];
  let cursor = 0;
  for (const range of ranges) {
    const first = firstAtOrAfter(range.startSpine, cursor);
    const end = firstAtOrAfter(range.endSpine, first);
    bounds.push({ first, end });
    cursor = first;
  }
  return bounds;
}

const clamp01 = (value: number) => Math.min(1, Math.max(0, value));

/**
 * Fraction through a chapter from location indexes: 0 on the page the chapter
 * starts on, 1 once the page shows the chapter's last location.
 */
export function chapterFractionFromLocations(
  bounds: EpubChapterLocations,
  pageStart: number,
  pageEnd: number | null,
): number {
  const span = bounds.end - bounds.first;
  if (span <= 0) return 0;
  if (pageEnd !== null && pageEnd >= bounds.end - 1) return 1;
  return clamp01((pageStart - bounds.first) / span);
}

/**
 * Coarse fraction before locations exist: the rendition's own page within the
 * section, scaled by the chapter's section count.
 */
export function chapterFractionFromPages(
  range: EpubChapterRange,
  spineIndex: number,
  page: number | null | undefined,
  total: number | null | undefined,
): number {
  const sections = range.endSpine - range.startSpine;
  if (sections <= 0) return 0;
  const known = !!total && total > 0 && !!page && page > 0;
  const pages = known ? total! : 1;
  const current = known ? Math.min(page!, pages) : 1;
  if (known && spineIndex >= range.endSpine - 1 && current >= pages) return 1;
  return clamp01((spineIndex - range.startSpine + (current - 1) / pages) / sections);
}
