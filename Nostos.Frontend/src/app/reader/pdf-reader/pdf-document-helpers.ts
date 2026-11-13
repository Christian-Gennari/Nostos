import { buildPdfPageText } from './pdf-read-aloud-text';
import {
  PassageDraft,
  PassageMark,
  closingOf,
  countWords,
  excerptOf,
  withoutLeadingLabels,
} from '../passage/passage-mark';
import { TocItem } from '../reader.interface';

/** How far past a page with almost no text the excerpt for another edition may look. */
const PDF_EXCERPT_LOOKAHEAD_PAGES = 2;

/**
 * The longest page range whose text is gathered for a PDF passage. Longer
 * ranges are kept as pages alone: the viewer still shows them, but search does
 * not read them.
 */
const PDF_PASSAGE_TEXT_PAGE_LIMIT = 400;

/** Enough text to be one place in a book: more than a caption or a chapter title. */
export function isSubstantialPageText(text: string): boolean {
  return text.trim().length >= 100;
}

export async function readPdfPageExcerptText(
  doc: any,
  pageNumber: number,
  isCurrent: () => boolean,
): Promise<string | null> {
  try {
    const page = await doc.getPage(pageNumber);
    const content = await page.getTextContent();
    if (!isCurrent()) return null;

    const text = (Array.isArray(content?.items) ? content.items : [])
      .map((item: { str?: unknown }) => (typeof item?.str === 'string' ? item.str : ''))
      .join(' ')
      .replace(/\s+/g, ' ')
      .trim();

    return text || null;
  } catch {
    return null;
  }
}

/**
 * The first text on the current page, for finding the same place in another
 * edition. Read from the document itself rather than the rendered layer, so
 * it works before the page has painted. Null for a page with no text layer.
 */
export async function extractPdfVisibleExcerpt(
  doc: any,
  currentPage: number,
  totalPages: number,
  isCurrent: () => boolean,
): Promise<string | null> {
  if (!doc || totalPages <= 0) return null;

  // A plate or a chapter title page says too little to be found in another
  // edition, and a caption often repeats a line of the text. Reading resumes
  // on the next page with text, so that page speaks for this one.
  let sparse: string | null = null;
  const lastPage = Math.min(totalPages, currentPage + PDF_EXCERPT_LOOKAHEAD_PAGES);
  for (let pageNumber = currentPage; pageNumber <= lastPage; pageNumber++) {
    const text = await readPdfPageExcerptText(doc, pageNumber, isCurrent);
    if (!isCurrent()) return null;
    if (text && isSubstantialPageText(text)) return text.slice(0, 280);
    if (pageNumber === currentPage) sparse = text;
  }

  return sparse ? sparse.slice(0, 280) : null;
}

export async function extractPdfPageLabels(pdfDoc: any): Promise<(string | null)[]> {
  if (typeof pdfDoc?.getPageLabels !== 'function') return [];

  try {
    const labels = await pdfDoc.getPageLabels();
    return Array.isArray(labels)
      ? labels.map((label) => (typeof label === 'string' && label.trim() ? label.trim() : null))
      : [];
  } catch {
    return [];
  }
}

/**
 * Detect whether text-dependent tools are meaningful without parsing the
 * entire document. A bounded, evenly distributed sample avoids turning a
 * 900-page scan into a second ingestion pass while still handling blank cover
 * pages in ordinary books. Returns `null` when the document becomes stale or
 * cannot be inspected.
 */
export async function detectPdfTextCapability(
  pdfDoc: any,
  knownPageCount: number,
  isCurrent: () => boolean,
): Promise<'available' | 'unavailable' | 'unknown' | null> {
  if (typeof pdfDoc?.getPage !== 'function' || knownPageCount <= 0) return null;

  const sampleCount = Math.min(knownPageCount, 8);
  const pages = [
    ...new Set(
      Array.from({ length: sampleCount }, (_, index) =>
        sampleCount === 1
          ? 1
          : 1 + Math.round((index * (knownPageCount - 1)) / (sampleCount - 1)),
      ),
    ),
  ];

  let inspected = 0;
  for (const pageNumber of pages) {
    try {
      const page = await pdfDoc.getPage(pageNumber);
      const textContent = await page.getTextContent();
      if (!isCurrent()) return null;
      inspected += 1;
      const hasText =
        Array.isArray(textContent?.items)
        && textContent.items.some(
          (item: { str?: unknown }) => typeof item?.str === 'string' && item.str.trim().length > 0,
        );
      if (hasText) {
        return 'available';
      }
    } catch {
      // An unreadable sample is not evidence that the PDF has no text. An
      // obsolete document must also not overwrite the capability state of a
      // PDF that replaced it while this async sample was in flight.
      if (!isCurrent()) return null;
      return 'unknown';
    }
  }

  return inspected > 0 && isCurrent() ? 'unavailable' : null;
}

export async function mapPdfOutline(outline: any[], pdfDoc: any): Promise<TocItem[]> {
  const items: TocItem[] = [];
  for (const item of outline) {
    let pageNumber = 1;
    try {
      if (item.dest) {
        let dest = item.dest;
        // Named destination — resolve to explicit destination array
        if (typeof dest === 'string') {
          dest = await pdfDoc.getDestination(dest);
        }
        if (Array.isArray(dest) && dest.length > 0) {
          // dest[0] is a page reference object — resolve to 0-based page index
          const pageIndex = await pdfDoc.getPageIndex(dest[0]);
          pageNumber = pageIndex + 1; // convert to 1-based
        }
      }
    } catch {
      // If resolution fails, fall back to page 1
      pageNumber = 1;
    }
    const children = item.items?.length ? await mapPdfOutline(item.items, pdfDoc) : [];
    items.push({ label: item.title, target: pageNumber, children });
  }
  return items;
}

export async function buildPdfPassageDraft(params: {
  a: PassageMark;
  b: PassageMark;
  pdfDoc: any;
  totalPages: number;
  isCurrent: () => boolean;
  pageName: (pageNumber: number) => string;
  chapterTitleAt: (pageNumber: number) => string | null;
}): Promise<PassageDraft | null> {
  const { a, b, pdfDoc, totalPages, isCurrent, pageName, chapterTitleAt } = params;
  if (a.format !== 'pdf' || b.format !== 'pdf' || !pdfDoc) return null;

  const pages = [a.start, a.end, b.start, b.end]
    .map(Number)
    .filter((n) => Number.isInteger(n) && n > 0);
  if (pages.length !== 4) return null;

  const first = Math.min(...pages);
  const last = Math.max(...pages);

  const blocks: string[] = [];
  if (last - first < PDF_PASSAGE_TEXT_PAGE_LIMIT) {
    // Read one page past each end too: a running head is recognised by
    // standing on a neighbouring page, and the range's own first and last
    // pages have one neighbour inside it at most.
    const from = Math.max(1, first - 1);
    const to = Math.min(totalPages || last, last + 1);
    const read: Array<{ pageNumber: number; items: any[]; geometry: any } | null> = [];
    for (let pageNumber = from; pageNumber <= to; pageNumber++) {
      try {
        const page = await pdfDoc.getPage(pageNumber);
        const content = await page.getTextContent();
        if (!isCurrent()) return null;
        const viewport = page.getViewport({ scale: 1 });
        read.push({
          pageNumber,
          items: Array.isArray(content?.items) ? content.items : [],
          geometry: {
            width: viewport.width,
            height: viewport.height,
            transform: viewport.transform,
          },
        });
      } catch {
        // A page that cannot be read leaves only its place in the range.
        read.push(null);
      }
    }
    const margins = read.map((page) =>
      page ? buildPdfPageText(page.items, page.geometry).marginLines : [],
    );
    read.forEach((page, index) => {
      if (!page || page.pageNumber < first || page.pageNumber > last) return;
      const neighbours = [...(margins[index - 1] ?? []), ...(margins[index + 1] ?? [])];
      const text = buildPdfPageText(page.items, page.geometry, neighbours).text.trim();
      if (text) blocks.push(text);
    });
  }

  const joined = blocks.join('\n\n').trim();
  const text = (joined && withoutLeadingLabels(joined)) || null;
  const range =
    first === last
      ? `Page ${pageName(first)}`
      : `Pages ${pageName(first)}–${pageName(last)}`;
  return {
    anchorKind: 'pdf_page',
    start: String(first),
    end: String(last),
    title: chapterTitleAt(first) ?? range,
    html: null,
    text,
    words: countWords(text),
    opening: text ? excerptOf(text, 140) : range,
    closing: text ? closingOf(text, 140) : range,
  };
}
