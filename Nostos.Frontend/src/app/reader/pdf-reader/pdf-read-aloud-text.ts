/**
 * Turns a PDF page's text fragments into text that can be read aloud, and
 * remembers where each fragment sits so the sentence being spoken can be
 * marked on the page.
 *
 * A PDF has no paragraphs or sentences, only positioned runs of glyphs. Three
 * things make the raw run order unpleasant to listen to, and are handled here:
 * a word hyphenated across a line break, a page number or running header in
 * the margin, and the missing space between two runs on one line.
 */

/** One text run as pdf.js reports it (`getTextContent().items`). */
export interface PdfTextItem {
  str: string;
  /** [a, b, c, d, e, f]; e and f are the baseline's left end in PDF units. */
  transform: number[];
  width: number;
  height: number;
  hasEOL?: boolean;
}

/** The page's viewport at scale 1: its size and the PDF-to-viewport transform. */
export interface PdfPageGeometry {
  width: number;
  height: number;
  transform: number[];
}

/** A rectangle as fractions (0 to 1) of the page, like a saved PDF highlight's. */
export interface PageRect {
  left: number;
  top: number;
  width: number;
  height: number;
}

interface TextRun extends PageRect {
  /** Offsets into {@link PdfPageText.text}. */
  start: number;
  end: number;
}

export interface PdfPageText {
  /** The page's reading text. A newline separates blocks (a heading, a paragraph gap). */
  text: string;
  runs: TextRun[];
  /** Margin lines with their digits removed, for spotting a running header on the next page. */
  marginLines: string[];
}

interface Line {
  pieces: Array<{ str: string } & PageRect>;
  text: string;
  top: number;
  bottom: number;
  height: number;
}

/** How much of the page, at the top and at the bottom, counts as margin. */
const MARGIN_ZONE = 0.075;
const PAGE_NUMBER = /^[\s\W]*(\d{1,4}|[ivxlcdm]{1,7})[\s\W]*$/i;

function marginKey(text: string): string {
  return text.replace(/\d+/g, '').replace(/\s+/g, ' ').trim().toLowerCase();
}

function toLines(items: readonly PdfTextItem[], page: PdfPageGeometry): Line[] {
  const [a, b, c, d, e, f] = page.transform;
  const lines: Line[] = [];
  let current: Line | null = null;
  let previousBaseline = Number.NaN;
  let breakNext = false;

  for (const item of items) {
    const str = typeof item?.str === 'string' ? item.str : '';
    if (!str.trim()) {
      // An empty run carries the line break and nothing else.
      if (item?.hasEOL) breakNext = true;
      continue;
    }

    const x = item.transform[4];
    const y = item.transform[5];
    const height = Math.abs(item.height) || Math.abs(item.transform[3]) || 10;
    // The viewport transform maps PDF space (origin bottom-left) to the page
    // as drawn (origin top-left). (x, y) is the left end of the baseline.
    const left = (a * x + c * y + e) / page.width;
    const baseline = (b * x + d * y + f) / page.height;
    const piece = {
      str,
      left,
      top: baseline - height / page.height,
      width: Math.abs(item.width) / page.width,
      height: height / page.height,
    };

    // A note number set small and raised beside a word ("civil¹⁷") is printed
    // but not read, and must not start a line of its own.
    const lastPiece = current?.pieces[current.pieces.length - 1];
    if (
      lastPiece &&
      /^\d{1,3}$/.test(str.trim()) &&
      piece.height < lastPiece.height * 0.8 &&
      baseline < previousBaseline &&
      previousBaseline - baseline < lastPiece.height
    ) {
      if (item.hasEOL) breakNext = true;
      continue;
    }

    const sameLine =
      current !== null &&
      !breakNext &&
      Math.abs(baseline - previousBaseline) < (0.5 * height) / page.height;

    if (!current || !sameLine) {
      current = { pieces: [], text: '', top: piece.top, bottom: baseline, height: piece.height };
      lines.push(current);
    }

    current.pieces.push(piece);
    current.top = Math.min(current.top, piece.top);
    current.bottom = Math.max(current.bottom, baseline);
    previousBaseline = baseline;
    breakNext = item.hasEOL === true;
  }

  for (const line of lines) {
    line.text = line.pieces.map((piece) => piece.str).join('');
    // The typical run height, not the tallest: a scanned page reports a
    // different height for every word, and one tall word is not a heading.
    line.height = median(line.pieces.map((piece) => piece.height));
  }
  return lines;
}

/** The lower median: with few lines on a page, a paragraph gap must not pass for the usual spacing. */
function median(values: number[]): number {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((x, y) => x - y);
  return sorted[(sorted.length - 1) >> 1];
}

/**
 * Builds the reading text of one page.
 *
 * `neighbourMargins` are the margin lines of an adjacent page: a margin line
 * that also stands there (apart from its number) is a running header or footer
 * and is left out.
 */
export function buildPdfPageText(
  items: readonly PdfTextItem[],
  page: PdfPageGeometry,
  neighbourMargins: readonly string[] = [],
): PdfPageText {
  const lines = toLines(items, page);
  const repeated = new Set(neighbourMargins);
  const marginLines: string[] = [];
  const kept: Line[] = [];

  for (const line of lines) {
    const inMargin = line.bottom < MARGIN_ZONE || line.top > 1 - MARGIN_ZONE;
    if (inMargin && line.text.trim().length <= 80) {
      const key = marginKey(line.text);
      if (key.length >= 3) marginLines.push(key);
      if (PAGE_NUMBER.test(line.text) || (key.length >= 3 && repeated.has(key))) continue;
    }
    kept.push(line);
  }

  const spacing = median(
    kept.slice(1).map((line, index) => line.bottom - kept[index].bottom).filter((gap) => gap > 0),
  );

  let text = '';
  const runs: TextRun[] = [];

  kept.forEach((line, index) => {
    if (index > 0) {
      const previous = kept[index - 1];
      const gap = line.bottom - previous.bottom;
      const newBlock =
        (spacing > 0 && gap > spacing * 1.8) ||
        // The text jumped back up the page: the next column.
        gap < -spacing ||
        // A clearly different type size: a heading, or the text after one.
        Math.abs(line.height - previous.height) > Math.max(line.height, previous.height) * 0.3;

      const hyphenated = /\p{L}[-‐]$/u.test(text) && /^\p{Ll}/u.test(line.text.trimStart());
      if (newBlock) {
        text += '\n';
      } else if (hyphenated) {
        // "philo-" + "sophy": the hyphen only marked the line break.
        text = text.slice(0, -1);
        const last = runs[runs.length - 1];
        if (last && last.end > text.length) last.end = text.length;
      } else if (!/\s$/.test(text)) {
        text += ' ';
      }
    }

    line.pieces.forEach((piece, position) => {
      let str = piece.str;
      if (position === 0) str = str.trimStart();
      if (position === line.pieces.length - 1) str = str.trimEnd();
      if (!str) return;

      // Two runs on one line with a visible gap between them are two words.
      const previousPiece = line.pieces[position - 1];
      if (
        previousPiece &&
        !/\s$/.test(text) &&
        !/^\s/.test(str) &&
        piece.left - (previousPiece.left + previousPiece.width) > piece.height * 0.15
      ) {
        text += ' ';
      }

      const start = text.length;
      text += str;
      runs.push({
        start,
        end: text.length,
        left: piece.left,
        top: piece.top,
        width: piece.width,
        height: piece.height,
      });
    });
  });

  return { text, runs, marginLines };
}

/** Where the characters `[start, start + length)` of the page text are drawn. */
export function rectsForPdfSpan(page: PdfPageText, start: number, length: number): PageRect[] {
  const end = start + length;
  const rects: PageRect[] = [];

  for (const run of page.runs) {
    if (run.end <= start || run.start >= end) continue;

    const size = run.end - run.start || 1;
    const from = (Math.max(start, run.start) - run.start) / size;
    const to = (Math.min(end, run.end) - run.start) / size;
    const rect = {
      left: run.left + run.width * from,
      top: run.top,
      width: run.width * (to - from),
      height: run.height,
    };

    // Runs on one line become one rectangle, so the mark reads as a line of
    // text and not as a row of tiles.
    const previous = rects[rects.length - 1];
    if (
      previous &&
      Math.abs(previous.top - rect.top) < rect.height * 0.5 &&
      rect.left >= previous.left
    ) {
      const right = Math.max(previous.left + previous.width, rect.left + rect.width);
      const top = Math.min(previous.top, rect.top);
      const bottom = Math.max(previous.top + previous.height, rect.top + rect.height);
      previous.width = right - previous.left;
      previous.top = top;
      previous.height = bottom - top;
    } else {
      rects.push(rect);
    }
  }

  return rects;
}
