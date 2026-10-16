import { EpubCFI } from 'epubjs';

/**
 * EPUB passages (issue #1014). epub.js lays out one spine section at a time,
 * so a selection never reaches past the chapter on screen; a passage is
 * instead two marks, and its text is lifted from the section documents
 * between them without rendering any of them.
 */

/**
 * A range CFI's two ends as point CFIs: `epubcfi(P,/s,/e)` becomes
 * `epubcfi(P/s)` and `epubcfi(P/e)`. A point CFI is both of its own ends.
 * Commas escaped with `^` belong to the path and are not separators.
 */
export function cfiRangeEnds(cfi: string): { start: string; end: string } | null {
  const match = /^epubcfi\((.*)\)$/.exec(cfi.trim());
  if (!match) return null;
  const parts = match[1].split(/(?<!\^),/);
  if (parts.length === 1) return { start: cfi.trim(), end: cfi.trim() };
  if (parts.length !== 3) return null;
  const [parent, start, end] = parts;
  return { start: `epubcfi(${parent}${start})`, end: `epubcfi(${parent}${end})` };
}

/** Negative when `a` comes before `b` in the book, positive after, 0 at the same place. */
export function compareCfi(a: string, b: string): number {
  return new EpubCFI().compare(a, b);
}

/** The spine section a CFI points into. */
export function cfiSpineIndex(cfi: string): number {
  return new EpubCFI(cfi).spinePos;
}

/**
 * The passage spanned by two marks, whichever was made first: from the earlier
 * of their starts to the later of their ends.
 */
export function passageEnds(
  a: { start: string; end: string },
  b: { start: string; end: string },
): { start: string; end: string } {
  return {
    start: compareCfi(a.start, b.start) <= 0 ? a.start : b.start,
    end: compareCfi(a.end, b.end) >= 0 ? a.end : b.end,
  };
}

const BLOCK_ELEMENTS = new Set([
  'address', 'article', 'aside', 'blockquote', 'dd', 'div', 'dl', 'dt', 'figcaption',
  'figure', 'footer', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'header', 'hr', 'li', 'main',
  'nav', 'ol', 'p', 'pre', 'section', 'table', 'tr', 'ul',
]);

/**
 * The text of a passage's markup as paragraphs separated by blank lines, so
 * the plain copy kept for search keeps the book's paragraphs.
 */
export function passagePlainText(root: Node): string {
  const blocks: string[] = [];
  let current = '';

  const flush = () => {
    const text = current.replace(/ +/g, ' ').replace(/ *\n */g, '\n').trim();
    if (text) blocks.push(text);
    current = '';
  };

  const walk = (node: Node) => {
    if (node.nodeType === Node.TEXT_NODE || node.nodeType === Node.CDATA_SECTION_NODE) {
      // A book's source line breaks are not the book's; only <br> and blocks are.
      current += (node as CharacterData).data.replace(/\s+/g, ' ');
      return;
    }
    if (node.nodeType !== Node.ELEMENT_NODE && node.nodeType !== Node.DOCUMENT_FRAGMENT_NODE) return;

    const name = node.nodeType === Node.ELEMENT_NODE ? (node as Element).localName.toLowerCase() : '';
    if (name === 'script' || name === 'style') return;
    if (name === 'br') {
      current += '\n';
      return;
    }
    const block = BLOCK_ELEMENTS.has(name);
    if (block) flush();
    node.childNodes.forEach(walk);
    if (block) flush();
  };

  walk(root);
  flush();
  return blocks.join('\n\n');
}

/**
 * The markup of a fragment cut from a book's XHTML, serialised as HTML. Moved
 * into an HTML document first, so the result carries no XML namespaces.
 */
export function fragmentHtml(fragment: DocumentFragment | Element): string {
  const target = document.implementation.createHTMLDocument('');
  const holder = target.createElement('div');
  holder.appendChild(target.importNode(fragment, true));
  return holder.innerHTML;
}

/**
 * Cut the part of one section document that belongs to a passage. `start` and
 * `end` are point CFIs inside this document, or null for its beginning or end.
 */
export function cutSection(
  doc: Document,
  start: string | null,
  end: string | null,
): DocumentFragment | null {
  const body = doc.body ?? doc.getElementsByTagName('body')[0] ?? doc.documentElement;
  if (!body) return null;
  const range = doc.createRange();
  range.selectNodeContents(body);
  if (start) {
    const at = new EpubCFI(start).toRange(doc);
    if (!at) return null;
    range.setStart(at.startContainer, at.startOffset);
  }
  if (end) {
    const at = new EpubCFI(end).toRange(doc);
    if (!at) return null;
    range.setEnd(at.startContainer, at.startOffset);
  }
  return range.cloneContents();
}
