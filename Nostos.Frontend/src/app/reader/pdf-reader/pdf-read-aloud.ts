import { signal } from '@angular/core';
import { ReadAloudSegment, ReaderReadAloud } from '../read-aloud/read-aloud.types';
import { SentenceSpan, splitReadAloudSentences } from '../read-aloud/read-aloud-sentences';
import {
  PageRect,
  PdfPageGeometry,
  PdfPageText,
  PdfTextItem,
  buildPdfPageText,
  rectsForPdfSpan,
} from './pdf-read-aloud-text';

export interface PdfPageContent {
  items: PdfTextItem[];
  geometry: PdfPageGeometry;
}

/**
 * What read-aloud needs from the PDF reader component. An interface, so the
 * cursor can be exercised with plain page data.
 */
export interface PdfReadAloudHost {
  language(): string | null;
  pageCount(): number;
  currentPage(): number;
  /** The text runs of a page, or null when the page cannot be read. */
  loadPage(pageNumber: number): Promise<PdfPageContent | null>;
  goToPage(pageNumber: number): void;
  showMark(pageNumber: number, rects: PageRect[]): void;
  clearMark(): void;
}

interface LoadedPage {
  text: PdfPageText;
  spans: SentenceSpan[];
}

/** Pages kept in memory: the one being read and its neighbours. */
const KEEP_PAGES = 4;

/**
 * The read-aloud cursor of a PDF: a page and a sentence on it.
 *
 * A sentence that runs over the page break is spoken whole. Its first half is
 * marked on the page it starts on, and reading resumes on the next page after
 * its second half.
 */
export class PdfReadAloud implements ReaderReadAloud {
  private readonly moved = signal(0);
  readonly userMoved = this.moved.asReadonly();

  private readonly pages = new Map<number, LoadedPage | null>();
  private page = 0;
  private spans: SentenceSpan[] = [];
  private pageText: PdfPageText | null = null;
  private index = -1;
  /** The next page's first sentence was already spoken with this page's last. */
  private carried = false;
  private running = false;

  constructor(private readonly host: PdfReadAloudHost) {}

  get active(): boolean {
    return this.running;
  }

  /** Called by the reader when the page changed and read-aloud did not ask for it. */
  notifyUserMoved(): void {
    if (this.running) this.moved.update((count) => count + 1);
  }

  language(): string | null {
    return this.host.language();
  }

  async begin(): Promise<ReadAloudSegment | null> {
    this.running = true;
    this.carried = false;
    this.pages.clear();
    return this.fromPage(this.host.currentPage(), 1, 0);
  }

  async step(direction: 1 | -1): Promise<ReadAloudSegment | null> {
    if (!this.running || this.page === 0) return null;

    const target = this.index + direction;
    if (target >= 0 && target < this.spans.length) {
      this.index = target;
      this.carried = false;
      return this.present();
    }

    if (direction === 1) {
      const skip = this.carried ? 1 : 0;
      this.carried = false;
      return this.fromPage(this.page + 1, 1, skip);
    }
    this.carried = false;
    return this.fromPage(this.page - 1, -1, 0);
  }

  upcoming(count: number): string[] {
    return this.spans.slice(this.index + 1, this.index + 1 + count).map((span) => span.text);
  }

  end(): void {
    this.running = false;
    this.page = 0;
    this.spans = [];
    this.pageText = null;
    this.index = -1;
    this.carried = false;
    this.pages.clear();
    this.host.clearMark();
  }

  /**
   * Places the cursor on the first page at or after `start` (or before it,
   * going backward) that has something to read. `skip` leaves out leading
   * sentences that were already spoken.
   */
  private async fromPage(
    start: number,
    direction: 1 | -1,
    skip: number,
  ): Promise<ReadAloudSegment | null> {
    const last = this.host.pageCount();

    for (let number = start; number >= 1 && number <= last; number += direction) {
      const loaded = await this.load(number);
      if (!this.running) return null;

      const available = loaded ? loaded.spans.length - (number === start ? skip : 0) : 0;
      if (!loaded || available <= 0) continue;

      this.page = number;
      this.spans = loaded.spans;
      this.pageText = loaded.text;
      this.index = direction === 1 ? (number === start ? skip : 0) : loaded.spans.length - 1;
      if (number !== this.host.currentPage()) this.host.goToPage(number);
      return this.present();
    }

    return null;
  }

  private async present(): Promise<ReadAloudSegment | null> {
    const span = this.spans[this.index];
    if (!span || !this.pageText) return null;

    this.host.showMark(this.page, rectsForPdfSpan(this.pageText, span.start, span.length));

    let text = span.text;
    this.carried = false;

    // The page ends mid-sentence: say the rest, from the next page, with it.
    const isLast = this.index === this.spans.length - 1;
    if (isLast && !endsSentence(text) && this.page < this.host.pageCount()) {
      const next = await this.load(this.page + 1);
      if (!this.running) return null;
      const continuation = next?.spans[0]?.text;
      if (continuation && /^[\p{Ll}\p{N}"“‘'(]/u.test(continuation)) {
        text = joinAcrossBreak(text, continuation);
        this.carried = true;
      }
    }

    return { text };
  }

  private async load(pageNumber: number): Promise<LoadedPage | null> {
    if (pageNumber < 1 || pageNumber > this.host.pageCount()) return null;
    if (this.pages.has(pageNumber)) return this.pages.get(pageNumber) ?? null;

    const content = await this.host.loadPage(pageNumber);
    let loaded: LoadedPage | null = null;

    if (content) {
      // A margin line that also stands on the page before (or after) is a
      // running header: it is found by comparing the two.
      const neighbourNumber = pageNumber > 1 ? pageNumber - 1 : pageNumber + 1;
      const neighbour =
        neighbourNumber <= this.host.pageCount() ? await this.host.loadPage(neighbourNumber) : null;
      const neighbourMargins = neighbour
        ? buildPdfPageText(neighbour.items, neighbour.geometry).marginLines
        : [];

      const text = buildPdfPageText(content.items, content.geometry, neighbourMargins);
      loaded = { text, spans: splitReadAloudSentences(text.text, this.host.language()) };
    }

    this.pages.set(pageNumber, loaded);
    while (this.pages.size > KEEP_PAGES) {
      const oldest = this.pages.keys().next().value as number;
      this.pages.delete(oldest);
    }
    return loaded;
  }
}

function endsSentence(text: string): boolean {
  return /[.!?…。！？]["'”’)»\]]*$/u.test(text.trim());
}

/** Joins the two halves of a sentence split by a page break, undoing a hyphenation. */
function joinAcrossBreak(head: string, tail: string): string {
  if (/\p{L}[-‐]$/u.test(head) && /^\p{Ll}/u.test(tail)) return head.slice(0, -1) + tail;
  return `${head} ${tail}`;
}
