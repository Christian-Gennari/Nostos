import { signal } from '@angular/core';
import { ReadAloudSegment, ReaderReadAloud } from '../read-aloud/read-aloud.types';
import {
  SentenceSpan,
  splitReadAloudSentences,
} from '../read-aloud/read-aloud-sentences';
import {
  normalizedEpubResourceProse,
  rangeForNormalizedResourceSpan,
} from './epub-grounded-source';
import { VisibleEpubContent, firstVisibleEpubText } from './epub-visible-excerpt';

/** One rendered spine section, as epub.js hands it out. */
export interface ReadAloudEpubContent extends VisibleEpubContent {
  sectionIndex?: number;
  cfiFromRange(range: Range): string;
}

/**
 * What read-aloud needs from the EPUB reader component. Kept as an interface so
 * the cursor logic can be exercised against plain documents.
 */
export interface EpubReadAloudHost {
  language(): string | null;
  renderedContents(): ReadAloudEpubContent[];
  readingViewRect(): DOMRect | null;
  spineLength(): number | null;
  /** Shows a spine section from its start; false when it cannot be shown. */
  displaySection(index: number): Promise<boolean>;
  /** Turns to the page holding `cfi`. */
  displayCfi(cfi: string): Promise<void>;
  /** True when the range starts on the visible page; null when it cannot be measured. */
  isRangeVisible(range: Range, content: ReadAloudEpubContent): boolean | null;
  showMark(cfi: string): void;
  clearMark(): void;
}

/**
 * The read-aloud cursor of an EPUB: a spine section and a sentence in it.
 *
 * Sentences are addressed by offsets into the section's normalized text, the
 * same model grounded sources and search use, so the Range for a sentence is
 * re-derived from whichever document is rendered now. A font change replaces
 * the document; the offsets stay valid.
 */
export class EpubReadAloud implements ReaderReadAloud {
  private readonly moved = signal(0);
  readonly userMoved = this.moved.asReadonly();

  private sectionIndex: number | null = null;
  private spans: SentenceSpan[] = [];
  private index = -1;
  private running = false;

  constructor(private readonly host: EpubReadAloudHost) {}

  /** True from `begin` until `end`. */
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

    const start = firstVisibleEpubText(this.host.renderedContents(), this.host.readingViewRect());
    const content = (start?.content as ReadAloudEpubContent | undefined) ?? this.host.renderedContents()[0];
    if (!content?.document) return this.fromNextSection(this.currentSectionOf(content) ?? -1);

    this.loadSection(content);
    this.index = start ? this.indexOfSentenceAt(content.document, start.node, start.offset) : 0;

    if (this.index >= this.spans.length) return this.fromNextSection(this.sectionIndex ?? -1);
    // A page often opens mid-sentence. That sentence is read whole, from the
    // page the person is looking at: never turn back to where it began.
    return this.present(false);
  }

  async step(direction: 1 | -1): Promise<ReadAloudSegment | null> {
    if (!this.running || this.sectionIndex === null) return null;

    const target = this.index + direction;
    if (target >= this.spans.length) return this.fromNextSection(this.sectionIndex);
    if (target < 0) return this.fromPreviousSection(this.sectionIndex);

    this.index = target;
    return this.present();
  }

  upcoming(count: number): string[] {
    return this.spans.slice(this.index + 1, this.index + 1 + count).map((span) => span.text);
  }

  end(): void {
    this.running = false;
    this.sectionIndex = null;
    this.spans = [];
    this.index = -1;
    this.host.clearMark();
  }

  /** Marks the cursor's sentence and turns the page to it when it is not shown. */
  private async present(turnPage = true): Promise<ReadAloudSegment | null> {
    const span = this.spans[this.index];
    if (!span) return null;

    try {
      const content = this.contentForSection();
      const range = content
        ? rangeForNormalizedResourceSpan(content.document, span.start, span.length)
        : null;
      const cfi = content && range ? content.cfiFromRange(range) : null;

      if (content && range && typeof cfi === 'string' && cfi.length > 0) {
        if (
          turnPage &&
          !isOutOfFlow(range) &&
          this.host.isRangeVisible(startOf(range), content) === false
        ) {
          await this.host.displayCfi(cfi);
        }
        if (this.running) this.host.showMark(cfi);
      }
    } catch (error) {
      // The sentence is still read; only its mark is missing.
      console.warn('Could not mark the sentence being read:', error);
    }

    return this.running ? { text: span.text } : null;
  }

  private async fromNextSection(after: number): Promise<ReadAloudSegment | null> {
    const length = this.host.spineLength();
    if (length === null) return null;

    for (let next = after + 1; next < length; next++) {
      if (!(await this.host.displaySection(next))) continue;
      if (!this.running) return null;
      const content = this.contentForSection(next);
      if (!content) continue;
      this.loadSection(content, next);
      if (this.spans.length === 0) continue;
      this.index = 0;
      return this.present();
    }

    return null;
  }

  private async fromPreviousSection(before: number): Promise<ReadAloudSegment | null> {
    for (let previous = before - 1; previous >= 0; previous--) {
      if (!(await this.host.displaySection(previous))) continue;
      if (!this.running) return null;
      const content = this.contentForSection(previous);
      if (!content) continue;
      this.loadSection(content, previous);
      if (this.spans.length === 0) continue;
      this.index = this.spans.length - 1;
      return this.present();
    }

    return null;
  }

  private loadSection(content: ReadAloudEpubContent, index?: number): void {
    this.sectionIndex = index ?? this.currentSectionOf(content);
    this.spans = splitReadAloudSentences(
      normalizedEpubResourceProse(content.document),
      this.host.language(),
    );
  }

  private currentSectionOf(content: ReadAloudEpubContent | undefined): number | null {
    return typeof content?.sectionIndex === 'number' ? content.sectionIndex : null;
  }

  private contentForSection(index: number | null = this.sectionIndex): ReadAloudEpubContent | null {
    const rendered = this.host.renderedContents().filter((content) => !!content?.document?.body);
    if (index === null) return rendered[0] ?? null;
    return rendered.find((content) => content.sectionIndex === index) ?? null;
  }

  /**
   * The sentence holding a point in the document. Sentences are in document
   * order, so a binary search over their ranges finds it in a dozen steps
   * however long the chapter is.
   */
  private indexOfSentenceAt(document: Document, node: Node, offset: number): number {
    let low = 0;
    let high = this.spans.length;

    while (low < high) {
      const middle = (low + high) >> 1;
      const span = this.spans[middle];
      const range = rangeForNormalizedResourceSpan(document, span.start, span.length);
      // 1: the point is after this sentence. 0 or -1: in it or before it.
      let side = 0;
      try {
        side = range ? range.comparePoint(node, offset) : 0;
      } catch {
        side = 0;
      }
      if (side > 0) low = middle + 1;
      else high = middle;
    }

    return low;
  }
}

/**
 * True when the range starts inside a float or a positioned box. The browser
 * places such a box where it fits, often pages after the text that follows it
 * in the file: an illustration and its caption pushed past the next paragraphs.
 * It is read in file order, but never turned to, or the page would jump ahead
 * to it and straight back for the text after it.
 */
function isOutOfFlow(range: Range): boolean {
  const start = range.startContainer;
  const view = start.ownerDocument?.defaultView;
  if (!view) return false;

  let element = start.nodeType === 1 ? (start as Element) : start.parentElement;
  while (element && element !== element.ownerDocument.body) {
    const style = view.getComputedStyle(element);
    const float = style.getPropertyValue('float');
    if (float && float !== 'none') return true;
    if (style.position === 'absolute' || style.position === 'fixed') return true;
    element = element.parentElement;
  }
  return false;
}

/**
 * The first character of the range: "is the sentence's first word on this
 * page". One character, not a collapsed range, because a collapsed range has
 * no rectangle to measure in some engines.
 */
function startOf(range: Range): Range {
  const start = range.cloneRange();
  const container = start.startContainer;
  const length = container.nodeType === 3 ? (container.textContent?.length ?? 0) : 0;
  if (start.startOffset < length) start.setEnd(container, start.startOffset + 1);
  else start.collapse(true);
  return start;
}
