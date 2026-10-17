import { signal } from '@angular/core';
import { ReadAloudSegment, ReaderReadAloud } from '../read-aloud/read-aloud.types';
import { SentenceSpan, splitReadAloudSentences } from '../read-aloud/read-aloud-sentences';
import {
  MarginLine,
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
/** Margin lines kept, by page: enough for the pages two either side of a few pages. */
const KEEP_MARGINS = 12;
/** Fewer sentences than this left on the page, and the next page is read ahead. */
const READ_AHEAD = 3;

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
  private readonly loading = new Map<number, Promise<LoadedPage | null>>();
  private readonly margins = new Map<number, Promise<MarginLine[]>>();
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
    this.loading.clear();
    this.margins.clear();
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

  /**
   * The sentences after this one, as they will be said, so the voice can
   * fetch them ahead. They carry on over the page break when the next page is
   * already read, and a sentence split by the break is given whole.
   */
  upcoming(count: number): string[] {
    const texts: string[] = [];
    let pageNumber = this.page;
    let spans = this.spans;
    let index = this.index;
    let skipFirst = this.carried;

    while (texts.length < count && pageNumber > 0) {
      index++;
      if (index >= spans.length) {
        // As `fromPage` will go: the next page with something left to read,
        // as far as the pages already read reach.
        const start = pageNumber + 1;
        let found = false;
        for (let number = start; number <= this.host.pageCount(); number++) {
          if (!this.pages.has(number)) break;
          const loaded = this.pages.get(number);
          const skip = number === start && skipFirst ? 1 : 0;
          if (loaded && loaded.spans.length > skip) {
            pageNumber = number;
            spans = loaded.spans;
            index = skip;
            found = true;
            break;
          }
        }
        if (!found) break;
      }
      const spoken = this.spokenAt(spans, index, this.pages.get(pageNumber + 1));
      texts.push(spoken.text);
      skipFirst = spoken.carried;
    }
    return texts;
  }

  end(): void {
    this.running = false;
    this.page = 0;
    this.spans = [];
    this.pageText = null;
    this.index = -1;
    this.carried = false;
    this.pages.clear();
    this.loading.clear();
    this.margins.clear();
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

    const pageNumber = this.page;
    const isLast = this.index === this.spans.length - 1;
    // The page ends mid-sentence: the rest, from the next page, is said with it.
    if (isLast && !endsSentence(span.text) && pageNumber < this.host.pageCount()) {
      await this.load(pageNumber + 1);
      if (!this.running) return null;
    } else if (this.index >= this.spans.length - READ_AHEAD) {
      // Read the next page now, so its first sentences can be fetched ahead
      // and there is no pause when the page turns.
      void this.load(pageNumber + 1).catch(() => undefined);
    }

    const spoken = this.spokenAt(this.spans, this.index, this.pages.get(pageNumber + 1));
    this.carried = spoken.carried;
    return { text: spoken.text };
  }

  /** What is said for the sentence at `index`: with the next page's start when the page breaks it. */
  private spokenAt(
    spans: readonly SentenceSpan[],
    index: number,
    next: LoadedPage | null | undefined,
  ): { text: string; carried: boolean } {
    const text = spans[index].text;
    if (index !== spans.length - 1 || endsSentence(text)) return { text, carried: false };

    const continuation = next?.spans[0]?.text;
    if (continuation && /^[\p{Ll}\p{N}"“‘'(]/u.test(continuation)) {
      return { text: joinAcrossBreak(text, continuation), carried: true };
    }
    return { text, carried: false };
  }

  private load(pageNumber: number): Promise<LoadedPage | null> {
    if (pageNumber < 1 || pageNumber > this.host.pageCount()) return Promise.resolve(null);
    if (this.pages.has(pageNumber)) return Promise.resolve(this.pages.get(pageNumber) ?? null);

    // Reading ahead and reading on may ask for the same page at once.
    const known = this.loading.get(pageNumber);
    if (known) return known;
    const pending: Promise<LoadedPage | null> = this.read(pageNumber).finally(() => {
      if (this.loading.get(pageNumber) === pending) this.loading.delete(pageNumber);
    });
    this.loading.set(pageNumber, pending);
    return pending;
  }

  private async read(pageNumber: number): Promise<LoadedPage | null> {
    const content = await this.host.loadPage(pageNumber);
    let loaded: LoadedPage | null = null;

    if (content) {
      // A margin line that also stands on a page nearby is a running header.
      // Left and right pages often carry different ones, so the pages two
      // away are compared as well as the pages either side.
      const nearby = await Promise.all(
        [pageNumber - 2, pageNumber - 1, pageNumber + 1, pageNumber + 2].map((number) =>
          this.marginsOf(number),
        ),
      );
      const text = buildPdfPageText(content.items, content.geometry, nearby.flat());
      this.margins.set(pageNumber, Promise.resolve(text.marginLines));
      loaded = { text, spans: splitReadAloudSentences(text.text, this.host.language()) };
    }

    this.pages.set(pageNumber, loaded);
    while (this.pages.size > KEEP_PAGES) {
      const oldest = this.pages.keys().next().value as number;
      this.pages.delete(oldest);
    }
    return loaded;
  }

  private marginsOf(pageNumber: number): Promise<MarginLine[]> {
    if (pageNumber < 1 || pageNumber > this.host.pageCount()) return Promise.resolve([]);
    let margins = this.margins.get(pageNumber);
    if (!margins) {
      margins = this.host
        .loadPage(pageNumber)
        .then((content) =>
          content ? buildPdfPageText(content.items, content.geometry).marginLines : [],
        )
        .catch(() => []);
      this.margins.set(pageNumber, margins);
      while (this.margins.size > KEEP_MARGINS) {
        this.margins.delete(this.margins.keys().next().value as number);
      }
    }
    return margins;
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
