export interface EpubSpineSource {
  href: string;
  index: number;
}

/**
 * Normalize an EPUB resource href for deterministic path-only comparison.
 *
 * Grounded locators can come from the current manifest-relative index or from
 * an older archive-root-relative index. Fragments and queries are not part of
 * the resource identity, separators are normalized, percent-encoding is
 * decoded when valid, and dot segments are collapsed without allowing a
 * malformed escape to make source navigation throw.
 */
export function normalizeEpubHrefForComparison(value: string): string {
  const pathOnly = value.split('#')[0].split('?')[0].replace(/\\/g, '/');
  let decoded = pathOnly;
  try {
    decoded = decodeURIComponent(pathOnly);
  } catch {
    // A malformed escape should not make source navigation throw. Comparison
    // can still use the literal path and fail closed if it does not match.
  }

  const parts: string[] = [];
  for (const part of decoded.split('/')) {
    if (!part || part === '.') continue;
    if (part === '..') {
      if (parts.length > 0) parts.pop();
      continue;
    }
    parts.push(part);
  }
  return parts.join('/');
}

/**
 * Resolve a stored grounded EPUB href against epub.js's OPF-relative spine.
 *
 * New v2 book-text indexes store the manifest href directly. Older indexes
 * stored the archive-root path, so a nested OPF can leave a deterministic
 * directory prefix in front of the href epub.js knows. Comparison is path-only
 * and exact/suffix based; ambiguity fails closed and a supplied spine index is
 * treated as an additional provenance constraint, never as permission to guess.
 */
export function resolveGroundedEpubResourceHref(
  storedHref: string,
  storedSpineIndex: number | null | undefined,
  spineItems: readonly EpubSpineSource[],
): string | null {
  const stored = normalizeEpubHrefForComparison(storedHref);
  if (!stored) return null;

  const matches = spineItems.filter((item) => {
    const candidate = normalizeEpubHrefForComparison(item.href);
    if (!candidate) return false;
    return (
      candidate === stored ||
      stored.endsWith(`/${candidate}`) ||
      candidate.endsWith(`/${stored}`)
    );
  });

  if (storedSpineIndex !== null && storedSpineIndex !== undefined) {
    const indexed = matches.filter((item) => item.index === storedSpineIndex);
    return indexed.length === 1 ? indexed[0].href : null;
  }

  return matches.length === 1 ? matches[0].href : null;
}

/**
 * Normalize extracted EPUB source text using the same whitespace model as the
 * book-text locator offsets.
 */
export function normalizeEpubSourceText(value: string): string {
  return value
    .replace(/\r\n?/g, '\n')
    .replace(/[ \t\f\v]+/g, ' ')
    .replace(/ *\n+ */g, '\n')
    .trim();
}

const EPUB_TEXT_BLOCK_SELECTOR = 'h1,h2,h3,h4,h5,h6,p,li,blockquote,pre,figcaption,dt,dd,aside';
const EPUB_TEXT_IGNORED_SELECTOR = 'script,style,nav,svg,math';

function epubTextBlocks(document: Document): Element[] {
  return Array.from(document.body?.querySelectorAll(EPUB_TEXT_BLOCK_SELECTOR) ?? []).filter((element) => {
    if (element.closest(EPUB_TEXT_IGNORED_SELECTOR)) return false;
    return !element.parentElement?.closest(EPUB_TEXT_BLOCK_SELECTOR);
  });
}

/**
 * Extract the searchable text of one EPUB resource using exactly the same
 * block ordering and separators as grounded-source offsets.
 */
export function normalizedEpubResourceText(document: Document): string {
  return epubTextBlocks(document)
    .map((block) => normalizeEpubSourceText(block.textContent ?? ''))
    .filter(Boolean)
    .join('\n');
}

/**
 * Resolve an offset in one normalized block back to its raw DOM text position.
 */
export function rangeAtNormalizedElementOffset(
  document: Document,
  element: Element,
  targetOffset: number,
): Range | null {
  const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
  let normalizedOffset = 0;
  let pendingSpace = false;
  let node = walker.nextNode();

  while (node) {
    const text = node.textContent ?? '';
    for (let rawOffset = 0; rawOffset < text.length; rawOffset++) {
      const char = text[rawOffset];
      if (/\s/.test(char)) {
        pendingSpace = true;
        continue;
      }

      if (pendingSpace && normalizedOffset > 0) {
        if (normalizedOffset >= targetOffset) {
          const range = document.createRange();
          range.setStart(node, rawOffset);
          range.collapse(true);
          return range;
        }
        normalizedOffset++;
        pendingSpace = false;
      }

      if (normalizedOffset >= targetOffset) {
        const range = document.createRange();
        range.setStart(node, rawOffset);
        range.collapse(true);
        return range;
      }
      normalizedOffset++;
    }
    node = walker.nextNode();
  }

  return null;
}

/**
 * Resolve a normalized resource-wide text offset to a collapsed DOM Range.
 *
 * The block set, ignored ancestors, nested-block ownership and one-character
 * separator between normalized blocks intentionally match the existing reader
 * locator semantics.
 */
export function rangeAtNormalizedResourceOffset(
  document: Document,
  targetOffset: number,
): Range | null {
  let resourceOffset = 0;
  for (const block of epubTextBlocks(document)) {
    const normalized = normalizeEpubSourceText(block.textContent ?? '');
    if (!normalized) continue;

    const end = resourceOffset + normalized.length;
    if (targetOffset <= end) {
      const local = Math.max(0, Math.min(normalized.length - 1, targetOffset - resourceOffset));
      return rangeAtNormalizedElementOffset(document, block, local);
    }
    resourceOffset = end + 1;
  }
  return null;
}


/**
 * Resolve a normalized resource span to a DOM Range. Search queries are
 * trimmed, so the final normalized character is non-whitespace; advancing one
 * raw text position from that character gives the correct exclusive end.
 */
export function rangeForNormalizedResourceSpan(
  document: Document,
  startOffset: number,
  length: number,
): Range | null {
  if (length <= 0) return null;

  const start = rangeAtNormalizedResourceOffset(document, startOffset);
  const last = rangeAtNormalizedResourceOffset(document, startOffset + length - 1);
  if (!start || !last) return null;

  const startNode = start.startContainer;
  const endNode = last.startContainer;
  const endOffset = last.startOffset;

  if (endNode.nodeType !== Node.TEXT_NODE) return null;
  const textLength = endNode.textContent?.length ?? 0;
  if (endOffset >= textLength) return null;

  const range = document.createRange();
  range.setStart(startNode, start.startOffset);
  range.setEnd(endNode, endOffset + 1);
  return range;
}

export interface EpubPassageMatch {
  /** Position of the matched section in the list that was searched. */
  section: number;
  offset: number;
  length: number;
}

// Enough words to be a sentence opening rather than a common phrase.
const PASSAGE_LEAD_WORDS = 8;

function passageMatcher(normalizedPassage: string): RegExp {
  const pattern = normalizedPassage
    .split('\n')
    .map((line) =>
      line
        .split(' ')
        .map((word) => word.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
        .join('\\s+'),
    )
    // Another reading app marks every line break in what it copies, including
    // a <br> that contributes no character at all to this text.
    .join('\\s*');
  return new RegExp(pattern, 'gu');
}

function passageMatches(
  sections: readonly { text: string }[],
  normalizedPassage: string,
): EpubPassageMatch[] {
  const matcher = passageMatcher(normalizedPassage);
  const matches: EpubPassageMatch[] = [];
  sections.forEach((section, index) => {
    matcher.lastIndex = 0;
    let match: RegExpExecArray | null;
    while ((match = matcher.exec(section.text)) !== null) {
      matches.push({ section: index, offset: match.index, length: match[0].length });
      if (match[0].length === 0) matcher.lastIndex += 1;
    }
  });
  return matches;
}

export interface EpubPassageCandidates {
  /** Every place the whole passage occurs, in reading order. */
  whole: EpubPassageMatch[];
  /** Where its opening words occur, when the whole passage occurs nowhere. */
  lead: EpubPassageMatch[];
}

/**
 * Find a quoted passage in the normalized text of a book's sections. This is
 * how a highlight made in another reading app is located: its text is known
 * but its position is in that app's own terms.
 *
 * When a spine index is given and the passage occurs in that section, only
 * those occurrences are returned. The opening words are tried only when the
 * whole passage is not present, which happens when the other app extracted
 * the text slightly differently.
 */
export function epubPassageCandidates(
  sections: readonly { index: number; text: string }[],
  passage: string,
  preferredSpineIndex?: number | null,
): EpubPassageCandidates {
  const normalized = normalizeEpubSourceText(passage);
  if (!normalized) return { whole: [], lead: [] };

  const narrowed = (matches: EpubPassageMatch[]) => {
    const preferred = matches.filter(
      (match) => sections[match.section].index === preferredSpineIndex,
    );
    return preferred.length > 0 ? preferred : matches;
  };

  const whole = narrowed(passageMatches(sections, normalized));
  if (whole.length > 0) return { whole, lead: [] };

  const words = normalized.split(/\s+/);
  if (words.length <= PASSAGE_LEAD_WORDS) return { whole, lead: [] };
  return {
    whole,
    lead: narrowed(passageMatches(sections, words.slice(0, PASSAGE_LEAD_WORDS).join(' '))),
  };
}

/**
 * Resolve the start of a KOReader xpointer to a node of the section it names:
 * `/body/DocFragment[13]/body/p[62]/text()[1].0` is the first text node of the
 * 62nd `p` child of that section's body. crengine builds the same element tree
 * as the XHTML, so element steps are exact. It drops whitespace-only text
 * nodes, so those are not counted. Returns the deepest node the path reaches.
 */
export function nodeAtKoreaderXPointer(document: Document, xpointer: string): Node | null {
  const path = /\/DocFragment(?:\[\d+\])?\/body((?:\/[^/]+)*)$/.exec(xpointer.trim());
  if (!path || !document.body) return null;

  let node: Node = document.body;
  for (const rawStep of path[1].split('/').filter(Boolean)) {
    // The character offset rides on the last step: `text()[2].141`, `h2[9].0`.
    const step = /^([^.\[]+)(?:\[(\d+)\])?/.exec(rawStep);
    if (!step) break;
    const position = step[2] ? Number(step[2]) : 1;

    const children = Array.from(node.childNodes).filter((child) =>
      step[1] === 'text()'
        ? child.nodeType === Node.TEXT_NODE && (child.textContent ?? '').trim().length > 0
        : child.nodeType === Node.ELEMENT_NODE &&
          (child as Element).localName.toLowerCase() === step[1].toLowerCase(),
    );
    const next = children[position - 1];
    if (!next) break;
    node = next;
  }
  return node === document.body ? null : node;
}

/**
 * Where a node falls in the normalized resource text: the start of the text
 * block that contains it, or of the first block after it.
 */
export function normalizedResourceOffsetOfNode(document: Document, node: Node): number | null {
  let resourceOffset = 0;
  for (const block of epubTextBlocks(document)) {
    const normalized = normalizeEpubSourceText(block.textContent ?? '');
    if (!normalized) continue;

    const containsOrFollows =
      block === node ||
      block.contains(node) ||
      node.contains(block) ||
      (node.compareDocumentPosition(block) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
    if (containsOrFollows) return resourceOffset;
    resourceOffset += normalized.length + 1;
  }
  return null;
}

/** Titles and initials whose full stop does not end a sentence. */
const SENTENCE_ABBREVIATIONS = new Set([
  'mr', 'mrs', 'ms', 'dr', 'st', 'sr', 'jr', 'prof', 'mme', 'mlle', 'hr', 'fr', 'vs', 'no',
]);

const SENTENCE_TERMINATORS = '.!?…。！？';
const SENTENCE_CLOSERS = '"\'”’)»」』】';
const SENTENCE_SPAN_MAX = 600;

/** Is the terminator at `index` the end of a sentence in `text`? */
function endsSentenceAt(text: string, index: number): number | null {
  const char = text[index];
  if (!SENTENCE_TERMINATORS.includes(char)) return null;

  let after = index + 1;
  while (after < text.length && SENTENCE_CLOSERS.includes(text[after])) after++;

  // Scripts written without spaces end a sentence at the mark itself.
  const unspaced = '。！？'.includes(char);
  if (!unspaced && after < text.length && !/\s/.test(text[after])) return null;

  // A mark inside a quotation that the sentence carries on from: “Bah!” said he.
  let next = after;
  while (next < text.length && /[^\S\n]/.test(text[next])) next++;
  if (next < text.length && /\p{Ll}/u.test(text[next])) return null;

  if (char === '.') {
    const word = /([\p{L}]+)$/u.exec(text.slice(Math.max(0, index - 12), index))?.[1] ?? '';
    if (word.length === 1 || SENTENCE_ABBREVIATIONS.has(word.toLowerCase())) return null;
  }

  return after;
}

/**
 * The sentence around a position in one resource's normalized text, for
 * marking where the reader landed. `offset` is the end of the last word that
 * was matched, so the sentence is the one that word belongs to. A paragraph
 * break always ends a sentence, and a sentence longer than a screenful is cut
 * to the part around the position rather than marking a whole page.
 */
export function sentenceSpanAt(
  text: string,
  offset: number,
): { start: number; length: number } | null {
  if (!text || !Number.isFinite(offset)) return null;

  const anchor = Math.max(0, Math.min(text.length - 1, Math.trunc(offset) - 1));
  if (text[anchor] === '\n') return null;

  let start = anchor;
  const earliest = Math.max(0, anchor - SENTENCE_SPAN_MAX);
  while (start > earliest && text[start - 1] !== '\n' && sentenceEndBefore(text, start) === null) {
    start--;
  }
  // Cut off by the length limit rather than a boundary: do not split a word.
  if (start === earliest && start > 0 && text[start - 1] !== '\n') {
    const space = text.indexOf(' ', start);
    if (space >= 0 && space < anchor) start = space + 1;
  }

  let end = anchor + 1;
  const latest = Math.min(text.length, anchor + SENTENCE_SPAN_MAX);
  let closed = false;
  while (end < latest && text[end] !== '\n') {
    const after = endsSentenceAt(text, end);
    if (after !== null) {
      end = after;
      closed = true;
      break;
    }
    end++;
  }
  if (!closed && end === latest && end < text.length && text[end] !== '\n') {
    const space = text.lastIndexOf(' ', end);
    if (space > anchor) end = space;
  }

  while (start < end && /\s/.test(text[start])) start++;
  while (end > start && /\s/.test(text[end - 1])) end--;

  return end - start >= 2 ? { start, length: end - start } : null;
}

/** Does a sentence end immediately before `start` (allowing closers and spaces)? */
function sentenceEndBefore(text: string, start: number): number | null {
  let index = start - 1;
  while (index >= 0 && /[^\S\n]/.test(text[index])) index--;
  while (index >= 0 && SENTENCE_CLOSERS.includes(text[index])) index--;
  if (index < 0) return null;

  const after = endsSentenceAt(text, index);
  return after !== null && after <= start ? after : null;
}
