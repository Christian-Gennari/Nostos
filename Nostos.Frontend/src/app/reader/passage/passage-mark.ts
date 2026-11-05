/**
 * One end of a passage being marked (issue #1014). Marking is order-free: the
 * reader marks one end, reads on or turns back, and marks the other; the two
 * are put in book order when the passage is built. A mark is the whole
 * selection it was made from, so either end of it can become an end of the
 * passage.
 */
export interface PassageMark {
  /** Which reader made it; a mark only pairs with one from the same book and reader. */
  format: 'epub' | 'pdf' | 'audio';
  /** Where the marked selection starts: a point CFI, a page number or seconds. */
  start: string;
  /** Where it ends, in the same terms. */
  end: string;
  /** A few words of the marked text, to recognise the mark by. */
  excerpt: string;
  /** The chapter (or page) it is in, when known. */
  label: string | null;
}

/** A passage ready to be saved: both ends in book order, and its text. */
export interface PassageDraft {
  anchorKind: 'epub_cfi' | 'pdf_page' | 'audio_timestamp';
  start: string;
  end: string;
  /** Suggested title: the chapter the passage begins in. */
  title: string;
  /** Reading markup (EPUB). */
  html: string | null;
  /** Plain text; null when the source has none (a scanned PDF, an audiobook). */
  text: string | null;
  words: number;
  /** The first and last words, shown on the save sheet. */
  opening: string;
  closing: string;
  /** How far an end may be moved on the save sheet: an audiobook's length. */
  limit?: number;
}

const STORAGE_PREFIX = 'nostos.reader.passageMark.';

/** The mark kept for a book, surviving page turns, chapters and closing the book. */
export function readPassageMark(bookId: string): PassageMark | null {
  try {
    const raw = localStorage.getItem(STORAGE_PREFIX + bookId);
    if (!raw) return null;
    const value = JSON.parse(raw) as Partial<PassageMark>;
    if (
      (value.format === 'epub' || value.format === 'pdf' || value.format === 'audio') &&
      typeof value.start === 'string' &&
      typeof value.end === 'string'
    ) {
      return {
        format: value.format,
        start: value.start,
        end: value.end,
        excerpt: typeof value.excerpt === 'string' ? value.excerpt : '',
        label: typeof value.label === 'string' ? value.label : null,
      };
    }
  } catch {
    // Unreadable storage is no mark.
  }
  return null;
}

export function writePassageMark(bookId: string, mark: PassageMark | null): void {
  try {
    if (mark) localStorage.setItem(STORAGE_PREFIX + bookId, JSON.stringify(mark));
    else localStorage.removeItem(STORAGE_PREFIX + bookId);
  } catch {
    // Private mode or a full store: the mark lives for this visit only.
  }
}

/**
 * A short line that is not a sentence: "Figure 3", "BERT (Ours)", the labels
 * a figure leaves in a PDF's text layer. At most five words and forty
 * characters, and no closing punctuation.
 */
function isLabelLine(line: string): boolean {
  const flat = line.replace(/\s+/g, ' ').trim();
  return flat.length <= 40 && flat.split(' ').length <= 5 && !/[.!?:;,…"”]$/u.test(flat);
}

/**
 * A passage's text without the figure-label lines it starts with, so the quote
 * opens on prose. Lines are the text's own line breaks. Nothing is dropped when
 * no prose follows, so a passage of labels alone is kept whole.
 */
export function withoutLeadingLabels(text: string): string {
  const lines = text.split('\n');
  let skip = 0;
  while (skip < lines.length && (!lines[skip].trim() || isLabelLine(lines[skip]))) skip++;
  return skip > 0 && skip < lines.length ? lines.slice(skip).join('\n').trim() : text;
}

/** A few words from the start of a text, for a mark's label. */
export function excerptOf(text: string, length = 60): string {
  const flat = text.replace(/\s+/g, ' ').trim();
  if (flat.length <= length) return flat;
  const cut = flat.lastIndexOf(' ', length);
  return `${flat.slice(0, cut > length / 2 ? cut : length).trimEnd()}…`;
}

/** The last few words of a text, for the save sheet. */
export function closingOf(text: string, length = 90): string {
  const flat = text.replace(/\s+/g, ' ').trim();
  if (flat.length <= length) return flat;
  const from = flat.indexOf(' ', flat.length - length);
  return `…${flat.slice(from > 0 ? from + 1 : flat.length - length)}`;
}

export function countWords(text: string | null | undefined): number {
  const flat = text?.trim();
  return flat ? flat.split(/\s+/).length : 0;
}
