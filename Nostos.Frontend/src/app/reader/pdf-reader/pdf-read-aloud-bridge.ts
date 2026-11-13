import { PdfAnnotationManager } from './pdf-annotation-manager';
import { PdfReadAloud, PdfPageContent } from './pdf-read-aloud';
import { type PageRect } from './pdf-read-aloud-text';
import { languageTagFromCatalogue } from '../read-aloud/read-aloud-language';
import { PdfReaderTimers } from './pdf-reader-timers';

export interface PdfReadAloudBridgeDeps {
  language: () => string | null;
  pageCount: () => number;
  currentPage: () => number;
  getPdfDoc: () => any;
  goToPage: (pageNumber: number) => void;
  hostElement: () => HTMLElement;
  highlightService: PdfAnnotationManager;
  timers: PdfReaderTimers;
}

const READ_ALOUD_MOVE_SETTLE_MS = 400;

/**
 * Coordinates `PdfReadAloud` with the PDF reader's host DOM and navigation:
 * extracts page text runs, turns pages while distinguishing read-aloud moves
 * from user turns, and paints/scrolls the active sentence mark inside the
 * component host.
 */
export class PdfReadAloudBridge {
  readonly readAloud: PdfReadAloud;
  private mark: { pageNumber: number; rects: PageRect[] } | null = null;
  /** Counts page turns read-aloud asked for, so `pageChange` can tell them from the person's. */
  private moves = 0;

  constructor(private readonly deps: PdfReadAloudBridgeDeps) {
    this.readAloud = new PdfReadAloud({
      language: () => languageTagFromCatalogue(this.deps.language()),
      pageCount: () => this.deps.pageCount(),
      currentPage: () => this.deps.currentPage(),
      loadPage: (pageNumber) => this.loadPage(pageNumber),
      goToPage: (pageNumber) => this.goToPage(pageNumber),
      showMark: (pageNumber, rects) => this.showMark(pageNumber, rects),
      clearMark: () => this.showMark(0, []),
    });
  }

  isMoveInFlight(): boolean {
    return this.moves > 0;
  }

  notifyUserMoved(): void {
    if (this.moves === 0) {
      this.readAloud.notifyUserMoved();
    }
  }

  /** Re-paint the active read-aloud mark when a text layer renders or re-renders (e.g. zoom). */
  repaintOnTextLayer(pageNumber: number, textLayerDiv: HTMLElement): void {
    if (this.mark?.pageNumber === pageNumber) {
      this.deps.highlightService.paintReadAloudMark(textLayerDiv, this.mark.rects);
      this.keepMarkInView(textLayerDiv);
    }
  }

  reset(): void {
    this.readAloud.end();
    this.mark = null;
    this.moves = 0;
  }

  private async loadPage(pageNumber: number): Promise<PdfPageContent | null> {
    const doc = this.deps.getPdfDoc();
    if (!doc) return null;
    try {
      const page = await doc.getPage(pageNumber);
      const content = await page.getTextContent();
      if (doc !== this.deps.getPdfDoc()) return null;
      const viewport = page.getViewport({ scale: 1 });
      return {
        items: Array.isArray(content?.items) ? content.items : [],
        geometry: {
          width: viewport.width,
          height: viewport.height,
          transform: viewport.transform,
        },
      };
    } catch {
      return null;
    }
  }

  private goToPage(pageNumber: number): void {
    this.moves++;
    this.deps.goToPage(pageNumber);
    this.deps.timers.scheduleTimeout(() => {
      this.moves = Math.max(0, this.moves - 1);
    }, READ_ALOUD_MOVE_SETTLE_MS);
  }

  private showMark(pageNumber: number, rects: PageRect[]): void {
    const previous = this.mark;
    this.mark = rects.length > 0 ? { pageNumber, rects } : null;
    if (previous && previous.pageNumber !== pageNumber) {
      this.paintMark(previous.pageNumber, []);
    }
    if (pageNumber > 0) {
      this.paintMark(pageNumber, rects);
    }
  }

  private paintMark(pageNumber: number, rects: PageRect[]): void {
    const layer = this.deps
      .hostElement()
      .querySelector(`.page[data-page-number="${pageNumber}"] .textLayer`) as HTMLElement | null;
    // A page that has not drawn its text yet is painted when it does.
    if (!layer) return;
    this.deps.highlightService.paintReadAloudMark(layer, rects);
    this.keepMarkInView(layer);
  }

  /**
   * A page is often taller than the window (and wider, zoomed in), so the
   * sentence being read can be off screen on the right page. Scrolling can make
   * the viewer report another page as current; that is read-aloud's doing, not
   * the person's.
   */
  private keepMarkInView(layer: HTMLElement): void {
    const box = layer.querySelector('.read-aloud-mark-box') as HTMLElement | null;
    const container = this.deps.hostElement().querySelector('#viewerContainer') as HTMLElement | null;
    if (!box || !container || typeof box.scrollIntoView !== 'function') return;

    const mark = box.getBoundingClientRect();
    const view = container.getBoundingClientRect();
    if (mark.height === 0 || view.height === 0) return;
    // The transport floats over the bottom of the page, hence the larger margin there.
    const visible =
      mark.top >= view.top + 8
      && mark.bottom <= view.bottom - 96
      && mark.left >= view.left
      && mark.left <= view.right - 40;
    if (visible) return;

    this.moves++;
    box.scrollIntoView({ block: 'center', inline: 'nearest' });
    this.deps.timers.scheduleTimeout(() => {
      this.moves = Math.max(0, this.moves - 1);
    }, READ_ALOUD_MOVE_SETTLE_MS);
  }
}
