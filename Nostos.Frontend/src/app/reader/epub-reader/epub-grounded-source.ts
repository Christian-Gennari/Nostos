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
