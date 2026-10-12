/**
 * Extract a short passage starting with the first visible reading text.
 *
 * epub.js renders text inside iframes, so Range rectangles are measured in
 * the contents viewport and translated through the iframe rectangle.
 *
 * Never fall back to the whole chapter: sending text from an earlier page
 * would produce a confidently incorrect audiobook position.
 */
export interface VisibleEpubContent {
  document: Document;
  window?: Window;
}

interface TextCandidate {
  node: Text;
  offset: number;
  top: number;
  left: number;
  content: VisibleEpubContent;
}

const EXCERPT_MAX = 280;

function isReadingText(node: Text): boolean {
  const parent = node.parentElement;

  if (!parent || !node.textContent?.trim()) return false;

  // rt and rp are a ruby's pronunciation gloss and its fallback brackets:
  // printed beside the words, never read aloud, and not part of the sentence.
  if (parent.closest(
    'script, style, noscript, svg, math, nav, head, rt, rp, ' +
    '[hidden], [aria-hidden="true"]',
  )) {
    return false;
  }

  const view = node.ownerDocument.defaultView;
  if (view) {
    const style = view.getComputedStyle(parent);
    if (style.display === 'none' || style.visibility === 'hidden') {
      return false;
    }
  }

  return true;
}

function intersects(
  rect: DOMRect,
  viewport: DOMRect,
): boolean {
  return rect.width > 0 &&
    rect.height > 0 &&
    rect.right > viewport.left &&
    rect.left < viewport.right &&
    rect.bottom > viewport.top &&
    rect.top < viewport.bottom;
}

/**
 * Get a visible character within a text node.
 *
 * Checking character rectangles, rather than just the containing paragraph,
 * matters when a paginated spread starts halfway through a paragraph.
 */
function firstVisibleCharacter(
  node: Text,
  content: VisibleEpubContent,
  viewport: DOMRect,
  frameRect: DOMRect,
): TextCandidate | null {
  const value = node.textContent ?? '';
  const range = content.document.createRange();

  try {
    range.selectNodeContents(node);

    const bounds = Array.from(range.getClientRects());
    if (!bounds.some(rect => intersects(
      new DOMRect(
        rect.left + frameRect.left,
        rect.top + frameRect.top,
        rect.width,
        rect.height,
      ),
      viewport,
    ))) {
      return null;
    }

    for (let offset = 0; offset < value.length; offset++) {
      if (!value[offset] || /\s/.test(value[offset])) continue;

      range.setStart(node, offset);
      range.setEnd(node, offset + 1);

      for (const rect of Array.from(range.getClientRects())) {
        const translated = new DOMRect(
          rect.left + frameRect.left,
          rect.top + frameRect.top,
          rect.width,
          rect.height,
        );

        if (intersects(translated, viewport)) {
          return {
            node,
            offset,
            top: Math.max(translated.top, viewport.top),
            left: Math.max(translated.left, viewport.left),
            content,
          };
        }
      }
    }
  } finally {
    range.detach?.();
  }

  return null;
}

function textStartingAt(
  start: TextCandidate,
  maxLength: number,
): string {
  const document = start.content.document;
  const root = document.body;

  if (!root) return '';

  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  let node = walker.nextNode() as Text | null;
  let started = false;
  let collected = '';

  while (node && collected.length < maxLength) {
    if (node === start.node) started = true;

    if (started && isReadingText(node)) {
      const value = node.textContent ?? '';
      collected += node === start.node
        ? value.slice(start.offset)
        : ` ${value}`;
    }

    node = walker.nextNode() as Text | null;
  }

  return collected;
}

function normalizeExcerpt(
  text: string,
  startsMidSentence: boolean,
): string {
  let flat = text.replace(/\s+/g, ' ').trim();

  if (!flat) return '';

  // When the first visible character is in the middle of a sentence,
  // prefer the next full sentence if one exists near the page start.
  if (startsMidSentence) {
    const boundary = /[.!?]["'”’)]?\s+(?=[^\W\d_])/u.exec(flat);

    if (boundary && boundary.index < 140) {
      const next = flat.slice(boundary.index + boundary[0].length);
      if (next.length >= 40) flat = next;
    }
  }

  if (flat.length <= EXCERPT_MAX) return flat;

  const cut = flat.slice(0, EXCERPT_MAX);
  const lastSpace = cut.lastIndexOf(' ');

  return (lastSpace >= 200 ? cut.slice(0, lastSpace) : cut).trimEnd();
}

/**
 * Returns null for covers, image-only pages, unloaded renditions and pages
 * where the actual visible text cannot be established.
 *
 * `contents` must be rendition.getContents() from the active rendition.
 * The iframe is obtained from the epub.js Contents object.
 */
export function visibleEpubExcerpt(
  contents: VisibleEpubContent[],
  viewport: DOMRect | null,
): string | null {
  if (!viewport || viewport.width <= 0 || viewport.height <= 0) {
    return null;
  }

  const candidates: TextCandidate[] = [];

  for (const content of contents) {
    const document = content?.document;
    if (!document?.body) continue;

    const frame = content.window?.frameElement as HTMLElement | null;
    if (!frame || typeof frame.getBoundingClientRect !== 'function') {
      continue;
    }

    const frameRect = frame.getBoundingClientRect();
    const walker = document.createTreeWalker(
      document.body,
      NodeFilter.SHOW_TEXT,
    );

    let node = walker.nextNode() as Text | null;

    while (node) {
      if (isReadingText(node)) {
        const candidate = firstVisibleCharacter(
          node,
          content,
          viewport,
          frameRect,
        );

        if (candidate) candidates.push(candidate);
      }

      node = walker.nextNode() as Text | null;
    }
  }

  if (!candidates.length) return null;

  // A spread may have multiple rendered contents. Use visual page order,
  // not the order in which epub.js returned its section objects.
  candidates.sort((a, b) =>
    a.top - b.top || a.left - b.left,
  );

  const first = candidates[0];
  const prefix = first.node.textContent?.slice(0, first.offset) ?? '';

  const startsMidSentence =
    first.offset > 0 &&
    /[^\s]/.test(prefix) &&
    !/[.!?]["'”’)]?\s*$/u.test(prefix);

  const passage = textStartingAt(first, 700);
  const excerpt = normalizeExcerpt(passage, startsMidSentence);

  return excerpt || null;
}
