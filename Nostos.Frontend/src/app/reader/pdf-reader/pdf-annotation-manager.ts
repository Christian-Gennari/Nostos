import { Injectable } from '@angular/core';

import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HighlightColour,
  highlightFillRef,
  highlightFillVar,
} from '../highlight-colours';

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
    });
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

    const pageRect = startTextLayer.getBoundingClientRect();
    const rects = Array.from(range.getClientRects()).map((r) => ({
      left: (r.left - pageRect.left) / pageRect.width,
      top: (r.top - pageRect.top) / pageRect.height,
      width: r.width / pageRect.width,
      height: r.height / pageRect.height,
    }));

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
