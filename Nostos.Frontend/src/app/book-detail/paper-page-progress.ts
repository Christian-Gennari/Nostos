/**
 * Where a reader is in a paper book (issue #1091). A paper book has no reader to
 * save its place, so the page is set by hand on the book detail page and stored
 * like the other formats: the page in `lastLocation` as `{"page":n}` (the PDF
 * reader's shape), the derived percentage in `progressPercent`.
 */

/** The page stored in a paper book's `lastLocation`, or null when none is. */
export function readPaperPage(location: string | null | undefined): number | null {
  if (!location) return null;
  try {
    const parsed = JSON.parse(location) as { page?: unknown };
    const page = parsed?.page;
    return typeof page === 'number' && Number.isInteger(page) && page >= 1 ? page : null;
  } catch {
    return null;
  }
}

export function writePaperPage(page: number): string {
  return JSON.stringify({ page });
}

/** A typed or stepped page, kept inside the book: 1..pageCount, or 1.. without one. */
export function clampPaperPage(page: number, pageCount: number | null | undefined): number {
  const whole = Math.floor(page);
  const upper = pageCount && pageCount > 0 ? pageCount : Number.MAX_SAFE_INTEGER;
  return Math.min(Math.max(whole, 1), upper);
}

/**
 * The percentage for a page, by the PDF reader's rule: floor(page / pageCount * 100).
 * Without a page count there is no honest percentage, so the book counts as
 * 1%: enough to be "Reading" for the Library's filter, and the detail page
 * shows the page instead of the figure.
 */
export function paperPagePercent(page: number, pageCount: number | null | undefined): number {
  if (!pageCount || pageCount <= 0) return 1;
  return Math.min(100, Math.max(1, Math.floor((Math.min(page, pageCount) / pageCount) * 100)));
}
