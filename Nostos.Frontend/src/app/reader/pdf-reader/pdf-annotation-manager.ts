import { Injectable } from '@angular/core';

import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HighlightColour,
  highlightFillRef,
  highlightFillVar,
} from '../highlight-colours';
import { HighlightHit, RectLike, anchorAroundHit, rectContains } from '../highlight-hit';
import { passageOccurrences } from '../passage/note-locating';

export interface HighlightRect {
  left: number;
  top: number;
  width: number;
  height: number;
}

export interface PageHighlight {
  pageNumber: number;
  rects: HighlightRect[];
  id?: string;
  colour?: HighlightColour;
  /** The highlight carries a note; a dotted rule under it says so. */
  noted?: boolean;
}

export type PdfHighlightCapture =
  | {
      status: 'captured';
      pageNumber: number;
      rects: HighlightRect[];
      selectedText: string;
    }
  | {
      status: 'cross-page';
      selectedText: string;
    };

interface TextLayerRun {
  node: Text;
  /** Offsets into the text `textLayerRuns` returns. */
  start: number;
  end: number;
}

/**
 * A text layer's text, read the way its words run. A line break is a space
 * unless a hyphen only marked it ("philo-" + "sophy"), so a passage can cross
 * lines. Each run remembers its node, so an offset leads back to the page.
 */
export function textLayerRuns(layer: HTMLElement): { text: string; runs: TextLayerRun[] } {
  const walker = document.createTreeWalker(layer, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
  const runs: TextLayerRun[] = [];
  let text = '';
  let lineBreak = false;
  let lineTop: number | null = null;

  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (node.nodeType === Node.ELEMENT_NODE) {
      if ((node as Element).localName === 'br') lineBreak = true;
      continue;
    }

    const content = node as Text;
    if (!content.data) continue;
    const box = content.parentElement?.getBoundingClientRect();
    if (box && lineTop !== null && Math.abs(box.top - lineTop) > box.height / 2) lineBreak = true;
    if (box) lineTop = box.top;

    if (lineBreak && text) {
      if (/\p{L}[-‐\u00AD]$/u.test(text) && /^\p{Ll}/u.test(content.data)) {
        text = text.slice(0, -1);
        runs[runs.length - 1].end = text.length;
      } else if (!/\s$/.test(text)) {
        text += ' ';
      }
    }
    lineBreak = false;

    runs.push({ node: content, start: text.length, end: text.length + content.data.length });
    text += content.data;
  }

  return { text, runs };
}

/** Rectangles on one line become one, so the mark reads as a line of text. */
function mergeLineRects(rects: HighlightRect[]): HighlightRect[] {
  const merged: HighlightRect[] = [];
  for (const rect of rects) {
    if (rect.width <= 0 || rect.height <= 0) continue;
    const line = merged[merged.length - 1];
    if (line && Math.abs(line.top - rect.top) < rect.height / 2 && rect.left >= line.left) {
      const right = Math.max(line.left + line.width, rect.left + rect.width);
      const bottom = Math.max(line.top + line.height, rect.top + rect.height);
      line.top = Math.min(line.top, rect.top);
      line.width = right - line.left;
      line.height = bottom - line.top;
    } else {
      merged.push({ ...rect });
    }
  }
  return merged;
}

/** A stored highlight needs no more precision than this; it keeps the note small. */
const roundRect = (rect: HighlightRect): HighlightRect => ({
  left: Number(rect.left.toFixed(5)),
  top: Number(rect.top.toFixed(5)),
  width: Number(rect.width.toFixed(5)),
  height: Number(rect.height.toFixed(5)),
});

@Injectable({
  providedIn: 'root',
})
export class PdfAnnotationManager {
  /**
   * Draw a page's highlights. The pen arrives as a COLOUR NAME and becomes
   * `var(--highlight-<name>)`, so the value stays in the stylesheet and a
   * highlight follows CSS alone (issue #208).
   */
  paint(
    textLayerDiv: HTMLElement,
    highlights: PageHighlight[],
    colour: HighlightColour = DEFAULT_HIGHLIGHT_COLOUR,
  ) {
    let highlightLayer = textLayerDiv.querySelector('.custom-highlight-layer') as HTMLElement;

    if (!highlightLayer) {
      highlightLayer = document.createElement('div');
      highlightLayer.className = 'custom-highlight-layer';
      textLayerDiv.appendChild(highlightLayer);
    } else {
      highlightLayer.innerHTML = '';
    }

    highlights.forEach((h) => {
      const markColour = h.colour ?? colour;

      // --- NEW: Create a group container for this specific note ---
      const group = document.createElement('div');
      group.className = 'highlight-group';

      // We can attach the ID here for easier lookup later
      if (h.id) group.setAttribute('data-note-id', h.id);

      h.rects.forEach((rect) => {
        const div = document.createElement('div');
        div.className = 'highlight-box';
        // Per BOX, not per layer: this leaves room for marks made with different
        // pens on one page without a repaint.
        div.style.setProperty('--hl', highlightFillRef(markColour));
        div.style.setProperty('--hl-hover', `var(${highlightFillVar(markColour)}-hover)`);
        div.style.left = `${rect.left * 100}%`;
        div.style.top = `${rect.top * 100}%`;
        div.style.width = `${rect.width * 100}%`;
        div.style.height = `${rect.height * 100}%`;
        group.appendChild(div);
      });

      highlightLayer.appendChild(group);

      // Outside the group, so the rule is drawn at full strength rather than
      // at the wash's opacity.
      if (h.noted) {
        group.classList.add('has-note');
        h.rects.forEach((rect) => {
          const rule = document.createElement('div');
          rule.className = 'highlight-note-rule';
          rule.style.left = `${rect.left * 100}%`;
          rule.style.top = `${(rect.top + rect.height) * 100}%`;
          rule.style.width = `${rect.width * 100}%`;
          highlightLayer.appendChild(rule);
        });
      }
    });
  }

  /**
   * The saved highlights under a viewport point on one page. The marks are
   * drawn under `pointer-events: none` so text can still be selected through
   * them, which means they never receive the click themselves: the point is
   * tested against their stored rectangles instead. Each hit carries the box to
   * open the highlight card at; `measure: false` skips it for hover.
   */
  hitTest(
    textLayerDiv: HTMLElement,
    highlights: PageHighlight[],
    x: number,
    y: number,
    measure = true,
  ): HighlightHit[] {
    const page = textLayerDiv.getBoundingClientRect();
    if (!page || page.width <= 0 || page.height <= 0) return [];

    const hits: HighlightHit[] = [];
    for (const highlight of highlights) {
      if (!highlight.id) continue;
      const rects: RectLike[] = highlight.rects.map((rect) => ({
        left: page.left + rect.left * page.width,
        top: page.top + rect.top * page.height,
        right: page.left + (rect.left + rect.width) * page.width,
        bottom: page.top + (rect.top + rect.height) * page.height,
      }));
      const hit = rects.find((rect) => rect.right > rect.left && rectContains(rect, x, y));
      if (!hit) continue;
      hits.push({ identifier: highlight.id, anchor: measure ? anchorAroundHit(rects, hit) : null });
    }
    return hits;
  }


  /**
   * Draws (or, with no rectangles, removes) the mark for the sentence being
   * read aloud. It has a layer of its own so that repainting the saved
   * highlights never erases it, and nothing about it is stored.
   */
  paintReadAloudMark(textLayerDiv: HTMLElement, rects: HighlightRect[]): void {
    let layer = textLayerDiv.querySelector('.read-aloud-mark-layer') as HTMLElement | null;
    if (rects.length === 0) {
      layer?.remove();
      return;
    }

    if (!layer) {
      layer = document.createElement('div');
      layer.className = 'read-aloud-mark-layer';
      textLayerDiv.appendChild(layer);
    } else {
      layer.innerHTML = '';
    }

    for (const rect of rects) {
      const box = document.createElement('div');
      box.className = 'read-aloud-mark-box';
      box.style.left = `${rect.left * 100}%`;
      box.style.top = `${rect.top * 100}%`;
      box.style.width = `${rect.width * 100}%`;
      box.style.height = `${rect.height * 100}%`;
      layer.appendChild(box);
    }
  }

  captureHighlight(keepSelection = false): PdfHighlightCapture | null {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) {
      return null;
    }

    const range = selection.getRangeAt(0);
    const selectedText = selection.toString().trim();
    if (!selectedText) return null;

    const startTextLayer = this.getClosestTextLayer(range.startContainer);
    const endTextLayer = this.getClosestTextLayer(range.endContainer);
    if (!startTextLayer || !endTextLayer) return null;

    const startPageNumber = this.getPageNumberFromLayer(startTextLayer);
    const endPageNumber = this.getPageNumberFromLayer(endTextLayer);
    if (!startPageNumber || !endPageNumber) return null;

    // A single persisted PDF highlight currently owns one physical page. Never
    // normalize rectangles from page N+1 against page N: that creates plausible
    // looking but false provenance. Reject cross-page marks until the storage
    // model deliberately supports page-scoped geometry.
    if (startPageNumber !== endPageNumber) {
      if (!keepSelection) selection.removeAllRanges();
      return { status: 'cross-page', selectedText };
    }

    // getClientRects lists a box per text node and per element on the line:
    // merge them so one line is one rectangle, not a stack of duplicates.
    const rects = mergeLineRects(this.rangeRects(range, startTextLayer)).map(roundRect);

    if (!keepSelection) {
      selection.removeAllRanges();
    }

    return {
      status: 'captured',
      pageNumber: startPageNumber,
      rects,
      selectedText,
    };
  }

  /**
   * Return the CURRENT native PDF selection for Ask Nostos: selection is
   * reader context, not an annotation side effect.
   */
  captureSelectionText(): string | null {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return null;

    const range = selection.getRangeAt(0);
    const startTextLayer = this.getClosestTextLayer(range.startContainer);
    const endTextLayer = this.getClosestTextLayer(range.endContainer);
    if (!startTextLayer || !endTextLayer) return null;

    const selectedText = selection.toString().trim();
    return selectedText || null;
  }

  captureNoteLocation(): {
    pageNumber: number;
    yPercent: number;
  } | null {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) {
      return null;
    }

    const range = selection.getRangeAt(0);
    const textLayer = this.getClosestTextLayer(range.startContainer);
    if (!textLayer) return null;

    const pageNumber = this.getPageNumberFromLayer(textLayer);
    if (!pageNumber) return null;

    const pageRect = textLayer.getBoundingClientRect();
    const rect = range.getBoundingClientRect();
    const yPercent = (rect.top - pageRect.top) / pageRect.height;

    return { pageNumber, yPercent };
  }

  /**
   * Where a passage stands in a drawn text layer, as a saved highlight stores
   * it, or null unless it stands there exactly once. The rectangles come from
   * the page's own text, as a selection's do, so the mark sits on the words.
   */
  quoteRects(textLayer: HTMLElement, quote: string): HighlightRect[] | null {
    const passage = quote.replace(/\s+/g, ' ').trim();
    if (!passage) return null;

    const { text, runs } = textLayerRuns(textLayer);
    const found = passageOccurrences(text, passage);
    if (found.length !== 1) return null;

    const { offset, length } = found[0];
    const first = runs.find((run) => run.end > offset);
    const last = runs.find((run) => run.end >= offset + length);
    if (!first || !last) return null;

    const range = document.createRange();
    range.setStart(first.node, Math.max(0, offset - first.start));
    range.setEnd(last.node, offset + length - last.start);
    const rects = mergeLineRects(this.rangeRects(range, textLayer)).map(roundRect);
    return rects.length > 0 ? rects : null;
  }

  private rangeRects(range: Range, textLayer: HTMLElement): HighlightRect[] {
    const pageRect = textLayer.getBoundingClientRect();
    return Array.from(range.getClientRects()).map((r) => ({
      left: (r.left - pageRect.left) / pageRect.width,
      top: (r.top - pageRect.top) / pageRect.height,
      width: r.width / pageRect.width,
      height: r.height / pageRect.height,
    }));
  }

  private getClosestTextLayer(node: Node): HTMLElement | null {
    let current: Node | null = node;
    while (current) {
      if (current instanceof Element && current.classList.contains('textLayer')) {
        return current as HTMLElement;
      }
      current = current.parentNode;
    }
    return null;
  }

  private getPageNumberFromLayer(textLayer: HTMLElement): number | null {
    const pageDiv = textLayer.closest('.page');
    if (pageDiv && pageDiv.hasAttribute('data-page-number')) {
      return parseInt(pageDiv.getAttribute('data-page-number')!, 10);
    }
    return null;
  }
}
