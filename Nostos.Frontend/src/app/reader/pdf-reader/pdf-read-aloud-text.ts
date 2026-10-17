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

export interface MarginLine {
  /** The line's letters, without its page number. */
  key: string;
  /** Its type size against the page's text. */
  size: number;
  /** How far down the page it stands, from 0 at the top to 1 at the bottom. */
  at: number;
}

export interface PdfPageText {
  /** The page's reading text. A newline separates blocks (a heading, a paragraph gap). */
  text: string;
  runs: TextRun[];
  /** The lines at the top and bottom of the page, for spotting a running header on the pages nearby. */
  marginLines: MarginLine[];
}

interface Line {
  pieces: Array<{ str: string } & PageRect>;
  text: string;
  top: number;
  bottom: number;
  height: number;
  /** The line opens a paragraph with a drop cap: it starts a block whatever the spacing says. */
  opensParagraph?: boolean;
}

/** How much of the page, at the top and at the bottom, counts as margin. */
const MARGIN_ZONE = 0.075;
/** A line that is only a number is a page number this close to the top or bottom. */
const PAGE_NUMBER_ZONE = 0.12;
const PAGE_NUMBER = /^[\s\W]*(\d{1,4}|[ivxlcdm]{1,7})[\s\W]*$/i;

/**
 * The page number at either end is left out, as are spaces and punctuation: a
 * scan sets "The U ncontrollability" on one page and "The Uncontrollability"
 * on the next. Other digits stay, so the dated entries of a diary
 * ("January 22nd", "January 23rd") are not taken for a running header.
 */
function marginKey(text: string): string {
  return text
    .replace(/^[\s\d.,:;·•|–—-]+|[\s\d.,:;·•|–—-]+$/gu, '')
    .replace(/[^\p{L}\p{N}]/gu, '')
    .toLowerCase();
}

/** True when two margin lines are the same running header, allowing for a few misread letters in a scan. */
function sameMarginLine(key: string, other: string): boolean {
  if (key === other) return true;
  if (key.replace(/\D/g, '') !== other.replace(/\D/g, '')) return false;
  const longest = Math.max(key.length, other.length);
  if (Math.min(key.length, other.length) < 8) return false;
  return editDistance(key, other) <= Math.floor(longest * 0.2);
}

function editDistance(a: string, b: string): number {
  let previous = Array.from({ length: b.length + 1 }, (_, j) => j);
  for (let i = 1; i <= a.length; i++) {
    const row = [i];
    for (let j = 1; j <= b.length; j++) {
      row[j] = Math.min(previous[j] + 1, row[j - 1] + 1, previous[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
    }
    previous = row;
  }
  return previous[b.length];
}

const ENTITIES: Record<string, string> = { quot: '"', apos: "'", amp: '&', lt: '<', gt: '>' };

const LIGATURES: Record<string, string> = {
  '\uFB00': 'ff', '\uFB01': 'fi', '\uFB02': 'fl', '\uFB03': 'ffi', '\uFB04': 'ffl', '\uFB05': 'st', '\uFB06': 'st',
};

/**
 * What a run says, as a voice can read it.
 *
 * Fonts set in small capitals or old-style figures often map those glyphs to
 * Adobe's private-use letters (U+F761 is a small-capital "a", U+F731 an
 * old-style "1"); a voice skips them, and "Tiberius" is heard as "T". Some
 * scans carry HTML entities ("&quot;") into their text, which a voice spells.
 */
function speakableRun(str: string): string {
  return str
    .replace(/&(quot|apos|amp|lt|gt|#\d{2,5});/g, (entity, name: string) =>
      name.startsWith('#') ? String.fromCodePoint(Number(name.slice(1))) : (ENTITIES[name] ?? entity),
    )
    .replace(/[\uF720-\uF7FF]/g, (ch) => String.fromCharCode(ch.charCodeAt(0) - 0xf700))
    .replace(/[\uFB00-\uFB06]/g, (ch) => LIGATURES[ch] ?? ch)
    .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/g, '');
}

function toLines(items: readonly PdfTextItem[], page: PdfPageGeometry): Line[] {
  const [a, b, c, d, e, f] = page.transform;
  const lines: Line[] = [];
  let current: Line | null = null;
  let previousBaseline = Number.NaN;
  let breakNext = false;

  for (const item of items) {
    const str = typeof item?.str === 'string' ? speakableRun(item.str) : '';
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

    // Next to a drop cap, a capital more than twice the height of the text,
    // the text's own size measures the line: the cap's would take in the lines
    // below it.
    const lastHeight = lastPiece ? lastPiece.height * page.height : height;
    const capital = (text: string) => /^\p{Lu}$/u.test(text.trim());
    const dropCap =
      (capital(str) && height > 2 * lastHeight) ||
      (lastPiece !== undefined && capital(lastPiece.str) && lastHeight > 2 * height);
    const tolerance = dropCap ? Math.min(height, lastHeight) : height;
    const onBaseline = Math.abs(baseline - previousBaseline) < (0.5 * tolerance) / page.height;
    // Some OCR text layers mark every word as ending a line. A run that
    // carries on along the same baseline, a word space to the right, is still
    // the same line whatever the flag says.
    const continues =
      lastPiece !== undefined &&
      piece.left >= lastPiece.left + lastPiece.width - (0.5 * height) / page.width &&
      piece.left - (lastPiece.left + lastPiece.width) < (2 * height) / page.width;
    const sameLine = current !== null && onBaseline && (!breakNext || continues);

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
    closeUpLetterSpacing(line);
    line.text = line.pieces.map((piece) => piece.str).join('');
    // The typical run height, not the tallest: a scanned page reports a
    // different height for every word, and one tall word is not a heading.
    line.height = median(line.pieces.map((piece) => piece.height));
  }
  return joinDropCaps(lines, page);
}

/**
 * A line set letter-spaced through its runs ("b u t" " " "r a t h e r") has
 * its letters closed up; the spaces between runs still part the words.
 */
function closeUpLetterSpacing(line: Line): void {
  const closeUp = (str: string) => str.replace(/(?<=\p{L}) (?=[\p{L},.;:!?])/gu, '');
  const letters = (str: string) => {
    const words = str.split(/\s+/).filter(Boolean);
    return { words: words.length, single: words.filter((word) => /^\p{L}$/u.test(word)).length };
  };

  const whole = letters(line.pieces.map((piece) => piece.str).join(' '));
  if (whole.words >= 6 && whole.single >= whole.words * 0.7) {
    for (const piece of line.pieces) piece.str = closeUp(piece.str);
    return;
  }
  // One run spaced out on an otherwise ordinary line ("d e p th s ,").
  for (const piece of line.pieces) {
    const run = letters(piece.str);
    if (run.single >= 3 && run.single >= run.words * 0.6) piece.str = closeUp(piece.str);
  }
}

/**
 * A drop cap, the large first letter of a chapter, is its own run, often
 * listed before everything else on the page. It is put back in front of the
 * line beside it: "T" and "here are" are read as "There are".
 */
function joinDropCaps(lines: Line[], page: PdfPageGeometry): Line[] {
  const typical = median(lines.map((line) => line.height));
  return lines.filter((cap) => {
    if (cap.pieces.length !== 1 || !/^\p{Lu}$/u.test(cap.text.trim())) return true;
    if (cap.height < typical * 2) return true;

    const piece = cap.pieces[0];
    const right = piece.left + piece.width;
    const beside = lines
      .filter((line) => {
        if (line === cap || !/^\p{L}/u.test(line.text.trimStart())) return false;
        const reach = (line.height * page.height) / page.width;
        const left = line.pieces[0].left;
        return left > right - reach && left < right + reach && line.bottom > cap.top && line.bottom <= cap.bottom + line.height;
      })
      .sort((x, y) => x.top - y.top)[0];
    if (!beside) return true;

    const first = beside.pieces[0];
    first.str = cap.text.trim() + first.str.trimStart();
    first.width += first.left - piece.left;
    first.left = piece.left;
    beside.text = beside.pieces.map((p) => p.str).join('');
    beside.opensParagraph = true;
    return false;
  });
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
 * `neighbourMargins` are the margin lines of nearby pages: a margin line that
 * also stands there (apart from its number) is a running header or footer and
 * is left out. A book often heads its left pages with its title and its right
 * pages with the chapter's, so the pages two away should be among them.
 */
export function buildPdfPageText(
  items: readonly PdfTextItem[],
  page: PdfPageGeometry,
  neighbourMargins: readonly MarginLine[] = [],
): PdfPageText {
  const lines = toLines(items, page);
  const marginLines: MarginLine[] = [];
  const kept: Line[] = [];

  // A scan often sits well inside its page, its header far from the edge; the
  // header is still the line nearest the top.
  const topmost = Math.min(...lines.map((line) => line.top));
  const bottommost = Math.max(...lines.map((line) => line.bottom));
  const bodyHeight = median(lines.map((line) => line.height));

  for (const line of lines) {
    // A line that only reaches into the margin counts: a page number set a
    // little low sits half in it.
    const outermost = line.top === topmost || line.bottom === bottommost;
    const inMargin = outermost || line.top < MARGIN_ZONE || line.bottom > 1 - MARGIN_ZONE;
    const nearEdge = line.top < PAGE_NUMBER_ZONE || line.bottom > 1 - PAGE_NUMBER_ZONE;
    // Away from the edge, "(1970)" alone under a title is a year.
    const year = /\d{4}/.test(line.text);
    if ((nearEdge || (outermost && !year)) && PAGE_NUMBER.test(line.text)) continue;
    if (inMargin && line.text.trim().length <= 80) {
      const key = marginKey(line.text);
      const size = Math.max(...line.pieces.map((piece) => piece.height)) / (bodyHeight || 1);
      const at = line.top;
      if (key.length >= 3) {
        marginLines.push({ key, size, at });
        // A chapter's title is read even when the pages after it repeat it as
        // their running header: unlike the header there, it stands lower on the
        // page and is set larger. A scan's sizes vary from page to page, so a
        // line in the header's place is the header whatever its size.
        const repeated = neighbourMargins.some(
          (other) =>
            sameMarginLine(key, other.key) && (Math.abs(at - other.at) < 0.04 || size < other.size * 1.15),
        );
        if (repeated) continue;
      }
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
        line.opensParagraph === true ||
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
