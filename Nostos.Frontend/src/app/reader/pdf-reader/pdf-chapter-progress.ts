import { TocItem } from '../reader.interface';

/** Where a page sits in the PDF outline: its chapter and how far through it. */
export interface PdfChapterRange {
  /** The deepest outline entry starting on or before the page; null for front matter. */
  label: string | null;
  /** First page of the chapter. */
  start: number;
  /** First page after the chapter (exclusive). */
  end: number;
  /** 0–1 through the chapter: the first page is 1/length, the last page 1. */
  fraction: number;
}

/**
 * The chapter a page belongs to (#1269). Chapter starts are the outline's
 * pages, sorted and de-duplicated; when several entries start on the same
 * page, the last one in outline order (the deepest) names it. Pages before
 * the first entry are front matter, with no label.
 *
 * Measured like the book bar (`page / totalPages`): the current page counts as
 * read, so the bar is full on a chapter's last page.
 */
export function chapterRangeAt(
  toc: TocItem[],
  page: number,
  totalPages: number,
): PdfChapterRange | null {
  const labelByStart = new Map<number, string>();
  const visit = (items: TocItem[]) => {
    for (const item of items) {
      const start = Number(item.target);
      if (Number.isInteger(start) && start >= 1) labelByStart.set(start, item.label);
      visit(item.children ?? []);
    }
  };
  visit(toc);
  if (labelByStart.size === 0 || totalPages < 1) return null;

  const starts = [...labelByStart.keys()].sort((a, b) => a - b);
  let start = 1;
  let label: string | null = null;
  let end = starts[0];
  for (let i = 0; i < starts.length && starts[i] <= page; i++) {
    start = starts[i];
    label = labelByStart.get(start)!.trim() || null;
    end = starts[i + 1] ?? totalPages + 1;
  }
  if (end <= start) end = start + 1;

  const fraction = Math.min(1, Math.max(0, (page - start + 1) / (end - start)));
  return { label, start, end, fraction };
}
