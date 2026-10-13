/**
 * Splits a book's text into the units read aloud, keeping each unit's offset
 * so the reader can mark the words being spoken.
 *
 * A newline is a hard break: the text readers put one between blocks
 * (paragraphs, headings, list items), and a heading has no full stop to end it.
 */

export interface SentenceSpan {
  /** Offset of the first character in the text that was split. */
  start: number;
  length: number;
  /** What is said for this span: the words, without note numbers or line references. */
  text: string;
}

/** Shorter than this, a fragment such as “No.” joins its neighbour. */
const MIN_LENGTH = 24;
/** Longer than this, a sentence is cut at a pause so no single request is huge. */
const MAX_LENGTH = 420;

/** Titles and initials whose full stop does not end a sentence. */
const ABBREVIATIONS = new Set([
  'mr', 'mrs', 'ms', 'dr', 'st', 'sr', 'jr', 'prof', 'mme', 'mlle', 'hr', 'fr', 'vs', 'no',
  'sig', 'dott', 'sra', 'srta', 'ca', 'etc', 'cf', 'vol', 'fig', 'bzw', 'nr', 'm',
]);

type Segmenter = { segment(input: string): Iterable<{ segment: string; index: number }> };

function segmenterFor(language: string | null | undefined): Segmenter | null {
  const ctor = (Intl as unknown as { Segmenter?: new (l?: string, o?: object) => Segmenter }).Segmenter;
  if (!ctor) return null;
  try {
    return new ctor(language ?? undefined, { granularity: 'sentence' });
  } catch {
    try {
      return new ctor(undefined, { granularity: 'sentence' });
    } catch {
      return null;
    }
  }
}

/** Sentence boundaries without `Intl.Segmenter`: a terminator, closers, then whitespace. */
function fallbackPieces(block: string): { index: number; segment: string }[] {
  const pieces: { index: number; segment: string }[] = [];
  const boundary = /[.!?…。！？]+["'”’)»」』】]*(\s+|$)/gu;
  let from = 0;
  let match: RegExpExecArray | null;
  while ((match = boundary.exec(block))) {
    const end = match.index + match[0].length;
    pieces.push({ index: from, segment: block.slice(from, end) });
    from = end;
    if (match[0].length === 0) boundary.lastIndex++;
  }
  if (from < block.length) pieces.push({ index: from, segment: block.slice(from) });
  return pieces;
}

/** True when `text` stops at an abbreviation or an initial, not at a sentence end. */
function endsWithAbbreviation(text: string): boolean {
  const match = /(^|[\s("“‘'«])([\p{L}]{1,5})\.\s*$/u.exec(text);
  if (!match) return false;
  const word = match[2];
  if (word.length === 1 && word === word.toUpperCase()) return true;
  return ABBREVIATIONS.has(word.toLowerCase());
}

function trimmedSpan(source: string, start: number, end: number): SentenceSpan | null {
  while (start < end && /\s/.test(source[start])) start++;
  while (end > start && /\s/.test(source[end - 1])) end--;
  if (end <= start) return null;
  return { start, length: end - start, text: source.slice(start, end) };
}

/** Cuts an over-long sentence at the pause nearest the limit. */
function splitLong(source: string, span: SentenceSpan): SentenceSpan[] {
  const out: SentenceSpan[] = [];
  let start = span.start;
  const end = span.start + span.length;

  while (end - start > MAX_LENGTH) {
    const window = source.slice(start, start + MAX_LENGTH);
    let cut = Math.max(window.lastIndexOf('; '), window.lastIndexOf(': '), window.lastIndexOf(', '));
    if (cut < MAX_LENGTH / 3) cut = window.lastIndexOf(' ');
    if (cut < MAX_LENGTH / 3) cut = MAX_LENGTH - 1;
    const piece = trimmedSpan(source, start, start + cut + 1);
    if (piece) out.push(piece);
    start = start + cut + 1;
  }

  const rest = trimmedSpan(source, start, end);
  if (rest) out.push(rest);
  return out;
}

/**
 * Blanks out what is printed but never said: a note number glued to the word
 * before it ("drunk.185 So") and a bracketed line or page reference ("[1147A]").
 * Each is replaced by spaces of the same length, so every offset still points
 * at the same place in the original text.
 */
export function maskUnspokenMarkers(text: string): string {
  return text
    // Only after a word: "3.14" and "5:30" are numbers, not a note on a sentence.
    .replace(/(?<=\p{L}[.!?…,;:”"’'»)]{1,2})\d{1,3}(?=\s|$)/gu, (digits) => ' '.repeat(digits.length))
    .replace(/\[\d{1,4}[A-Za-z]?\]/g, (reference) => ' '.repeat(reference.length));
}

/** A span's text as it should be spoken: markers gone, whitespace collapsed. */
function spokenText(masked: string, start: number, end: number): string {
  return masked.slice(start, end).replace(/\s+/g, ' ').trim();
}

export function splitReadAloudSentences(
  text: string,
  language?: string | null,
): SentenceSpan[] {
  const segmenter = segmenterFor(language);
  const spans: SentenceSpan[] = [];
  let blockStart = 0;
  const original = text;
  // Split what will be heard: a note number between two sentences otherwise
  // hides the boundary ("drunk.185 So" is not a sentence end to a segmenter).
  text = maskUnspokenMarkers(original);

  for (const block of text.split('\n')) {
    const pieces = segmenter ? Array.from(segmenter.segment(block)) : fallbackPieces(block);
    const blockSpans: SentenceSpan[] = [];
    let pendingStart: number | null = null;

    for (let i = 0; i < pieces.length; i++) {
      const piece = pieces[i];
      const start: number = pendingStart ?? blockStart + piece.index;
      const end = blockStart + piece.index + piece.segment.length;
      const soFar = text.slice(start, end);
      const isLast = i === pieces.length - 1;

      // "Mr. Bennet" and "J. R. R. Tolkien" are one sentence, whatever the
      // segmenter's locale data thinks of the full stop.
      if (!isLast && endsWithAbbreviation(soFar)) {
        pendingStart = start;
        continue;
      }
      pendingStart = null;

      const span = trimmedSpan(text, start, end);
      if (!span) continue;

      const previous = blockSpans[blockSpans.length - 1];
      if (previous && previous.length < MIN_LENGTH) {
        // A fragment is spoken together with what follows it.
        const joinedEnd = span.start + span.length;
        blockSpans[blockSpans.length - 1] = {
          start: previous.start,
          length: joinedEnd - previous.start,
          text: text.slice(previous.start, joinedEnd),
        };
      } else {
        blockSpans.push(span);
      }
    }

    // A trailing fragment has nothing after it in the block: it joins the
    // sentence before it instead.
    if (blockSpans.length > 1 && blockSpans[blockSpans.length - 1].length < MIN_LENGTH) {
      const last = blockSpans.pop()!;
      const previous = blockSpans[blockSpans.length - 1];
      const joinedEnd = last.start + last.length;
      blockSpans[blockSpans.length - 1] = {
        start: previous.start,
        length: joinedEnd - previous.start,
        text: text.slice(previous.start, joinedEnd),
      };
    }

    for (const span of blockSpans) {
      if (span.length > MAX_LENGTH) spans.push(...splitLong(text, span));
      else spans.push(span);
    }

    blockStart += block.length + 1;
  }

  return spans
    .map((span) => ({ ...span, text: spokenText(text, span.start, span.start + span.length) }))
    .filter((span) => /[\p{L}\p{N}]/u.test(span.text));
}

/** Index of the sentence containing `offset`, or the first one after it. */
export function sentenceIndexAtOffset(spans: readonly SentenceSpan[], offset: number): number {
  for (let i = 0; i < spans.length; i++) {
    if (spans[i].start + spans[i].length > offset) return i;
  }
  return spans.length;
}
