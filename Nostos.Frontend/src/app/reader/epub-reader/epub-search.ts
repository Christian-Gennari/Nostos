import { signal } from '@angular/core';
import type { Book, Contents } from 'epubjs';

import type { ReaderSearchState, ReaderSourceTarget } from '../reader.interface';
import {
  normalizeEpubSourceText,
  normalizedEpubResourceText,
  rangeForNormalizedResourceSpan,
} from './epub-grounded-source';

export interface EpubSearchSection {
  href: string;
  index: number;
  text: string;
}

export interface EpubSearchMatch {
  href: string;
  index: number;
  offset: number;
  length: number;
}

export interface EpubSearchHost {
  book(): Book | null;
  renderedContents(): Contents[];
  contentMatchesHref(content: Contents, href: string | null): boolean;
  goToSource(target: ReaderSourceTarget): Promise<void>;
  showSearchHighlight(cfi: string): void;
  clearSearchHighlight(): void;
}

/**
 * Finds all literal case-insensitive matches for a normalized query across
 * the normalized section corpus.
 */
export function findEpubSearchMatches(
  corpus: readonly EpubSearchSection[],
  normalizedQuery: string,
): EpubSearchMatch[] {
  if (!normalizedQuery) return [];

  const escaped = normalizedQuery.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const matcher = new RegExp(escaped, 'giu');
  const matches: EpubSearchMatch[] = [];

  for (const section of corpus) {
    matcher.lastIndex = 0;
    let match: RegExpExecArray | null;
    while ((match = matcher.exec(section.text)) !== null) {
      matches.push({
        href: section.href,
        index: section.index,
        offset: match.index,
        length: match[0].length,
      });
      // Literal queries are never empty after normalization, but keep this
      // guard so a future matcher change cannot spin forever.
      if (match[0].length === 0) matcher.lastIndex += 1;
    }
  }

  return matches;
}

/**
 * Owns EPUB full-book search corpus loading, regex match execution,
 * result cycling, and search highlight painting.
 */
export class EpubSearchController {
  readonly state = signal<ReaderSearchState>({ status: 'idle', current: 0, total: 0 });
  readonly available = signal(false);

  private corpus: EpubSearchSection[] | null = null;
  private corpusPromise: Promise<EpubSearchSection[]> | null = null;
  private matches: EpubSearchMatch[] = [];
  private activeIndex = -1;
  private generation = 0;

  constructor(private readonly host: EpubSearchHost) {}

  setAvailable(available: boolean): void {
    this.available.set(available);
  }

  currentGeneration(): number {
    return this.generation;
  }

  async search(query: string): Promise<void> {
    if (!this.available()) {
      this.clear();
      return;
    }

    const normalizedQuery = normalizeEpubSourceText(query);
    const generation = ++this.generation;

    this.host.clearSearchHighlight();
    this.matches = [];
    this.activeIndex = -1;

    if (!normalizedQuery) {
      this.state.set({ status: 'idle', current: 0, total: 0 });
      return;
    }

    this.state.set({ status: 'searching', current: 0, total: 0 });

    try {
      const corpus = await this.ensureCorpus();
      if (generation !== this.generation) return;

      const matches = findEpubSearchMatches(corpus, normalizedQuery);
      if (generation !== this.generation) return;

      this.matches = matches;

      if (matches.length === 0) {
        this.state.set({ status: 'not-found', current: 0, total: 0 });
        return;
      }

      this.activeIndex = 0;
      this.state.set({ status: 'ready', current: 1, total: matches.length });
      await this.activateResult(generation);
    } catch (error) {
      if (generation !== this.generation) return;
      console.warn('EPUB search failed:', error);
      this.state.set({ status: 'not-found', current: 0, total: 0 });
    }
  }

  async next(): Promise<void> {
    if (this.matches.length === 0) return;
    const generation = this.generation;
    this.activeIndex = (this.activeIndex + 1) % this.matches.length;
    this.state.set({
      status: 'ready',
      current: this.activeIndex + 1,
      total: this.matches.length,
    });
    await this.activateResult(generation);
  }

  async previous(): Promise<void> {
    if (this.matches.length === 0) return;
    const generation = this.generation;
    this.activeIndex = (this.activeIndex - 1 + this.matches.length) % this.matches.length;
    this.state.set({
      status: 'ready',
      current: this.activeIndex + 1,
      total: this.matches.length,
    });
    await this.activateResult(generation);
  }

  clear(): number {
    this.generation++;
    this.matches = [];
    this.activeIndex = -1;
    this.state.set({ status: 'idle', current: 0, total: 0 });
    this.host.clearSearchHighlight();
    return this.generation;
  }

  reset(): void {
    this.available.set(false);
    this.clear();
    this.corpus = null;
    this.corpusPromise = null;
  }

  async ensureCorpus(): Promise<EpubSearchSection[]> {
    if (this.corpus) return this.corpus;
    if (this.corpusPromise) return this.corpusPromise;

    const book = this.host.book();
    if (!book) return [];

    const promise = (async () => {
      await book.ready;
      if (this.host.book() !== book) return [];

      const spine = book.spine as unknown as {
        spineItems?: Array<{
          href?: unknown;
          index?: unknown;
          linear?: unknown;
          load?: (request: (path: string) => Promise<unknown>) => Promise<Element>;
          unload?: () => void;
        }>;
      };

      const corpus: EpubSearchSection[] = [];
      for (const [fallbackIndex, section] of (spine.spineItems ?? []).entries()) {
        if (section.linear === false || typeof section.load !== 'function') continue;

        const href = typeof section.href === 'string' ? section.href : '';
        if (!href) continue;
        const index = typeof section.index === 'number' ? section.index : fallbackIndex;

        try {
          const contents = await section.load(
            book.load.bind(book) as (path: string) => Promise<unknown>,
          );
          if (this.host.book() !== book) return [];
          const document = contents?.ownerDocument;
          if (!document) continue;
          const text = normalizedEpubResourceText(document);
          if (text) corpus.push({ href, index, text });
        } finally {
          section.unload?.();
        }
      }

      if (this.host.book() === book) this.corpus = corpus;
      return corpus;
    })();

    this.corpusPromise = promise;
    try {
      return await promise;
    } finally {
      if (this.corpusPromise === promise) this.corpusPromise = null;
    }
  }

  private async activateResult(generation: number): Promise<void> {
    const match = this.matches[this.activeIndex];
    if (!match || generation !== this.generation) return;

    await this.revealTextSpan(match, generation);
  }

  private async revealTextSpan(match: EpubSearchMatch, generation: number): Promise<void> {
    await this.host.goToSource({
      type: 'epub',
      epubResourceHref: match.href,
      epubSpineIndex: match.index,
      epubTextOffset: match.offset,
    });
    if (generation !== this.generation) return;

    try {
      const rendered = this.host.renderedContents();
      const contents =
        rendered.find((candidate) => this.host.contentMatchesHref(candidate, match.href)) ??
        rendered[0];
      if (!contents?.document) return;

      const range = rangeForNormalizedResourceSpan(contents.document, match.offset, match.length);
      const cfi = range ? contents.cfiFromRange(range) : null;
      if (typeof cfi === 'string' && cfi.length > 0) {
        this.host.showSearchHighlight(cfi);
      }
    } catch (error) {
      // Navigation is still useful if a malformed publisher DOM prevents the
      // optional temporary paint.
      console.warn('Could not paint EPUB search result:', error);
    }
  }
}
