import { Signal, computed, signal } from '@angular/core';
import {
  FindResultMatchesCount,
  FindState,
  NgxExtendedPdfViewerService,
} from 'ngx-extended-pdf-viewer';
import { ReaderSearchState } from '../reader.interface';
import { PdfReaderTimers } from './pdf-reader-timers';

const UNSPACED_SCRIPT = /[\u3040-\u30ff\u3400-\u9fff\u0e00-\u0eff\u1000-\u109f\u1780-\u17ff]/g;

/**
 * The phrase the reader's search marks after a landing from another edition:
 * the passage's last six words, or, in a script written without spaces between
 * words, its last dozen characters. Null when the passage is too short to find.
 */
export function sourcePassagePhrase(excerpt: string | null | undefined): string | null {
  return sourcePassagePhrases(excerpt)[0] ?? null;
}

/**
 * The phrases to try, best first. After the passage's last six words come its
 * last runs of five or six words that carry no punctuation: a scan's text
 * layer most often differs from the book's text at a quotation mark, a hyphen
 * or a line end, and a run of plain words is still long enough to be one place.
 */
export function sourcePassagePhrases(excerpt: string | null | undefined): string[] {
  const flat = (excerpt ?? '').replace(/\s+/g, ' ').trim();
  if (!flat) return [];

  const unspaced = (flat.match(UNSPACED_SCRIPT)?.length ?? 0) * 2 > flat.length;
  if (unspaced) {
    // A line break in the page text is a space here; the search cannot cross it.
    const tail = flat.split(' ').filter(Boolean).at(-1) ?? '';
    return tail.length >= 4 ? [tail.slice(-12)] : [];
  }

  const words = flat.split(' ').filter(Boolean);
  if (words.length < 3) return [];

  const phrases = [words.slice(-6).join(' ')];
  let run: string[] = [];
  const close = () => {
    if (run.length >= 5) phrases.push(run.slice(-6).join(' '));
    run = [];
  };

  for (let index = words.length - 1; index >= 0 && phrases.length < 3; index--) {
    if (/^[\p{L}\p{N}]+$/u.test(words[index])) run.unshift(words[index]);
    else close();
  }
  if (phrases.length < 3) close();

  return [...new Set(phrases)];
}

export interface PdfSearchControllerDeps {
  pdfSearch: NgxExtendedPdfViewerService;
  textCapability: Signal<'unknown' | 'available' | 'unavailable'>;
  timers: PdfReaderTimers;
  getPdfDoc: () => any;
}

const SOURCE_HIGHLIGHT_TIMER_SLOT = 'sourceHighlight';

/**
 * Adapts `NgxExtendedPdfViewerService`'s find controller into the shared
 * `ReaderSearchState` signal and manages bounded, cancellable grounded
 * source-passage highlighting (`highlightSourcePassage`).
 */
export class PdfSearchController {
  readonly pdfSearchReady = signal(false);
  readonly searchState = signal<ReaderSearchState>({ status: 'idle', current: 0, total: 0 });
  readonly searchAvailable = computed(
    // A text-capable PDF should earn the search affordance. While capability
    // detection is still unknown, keep Search disabled rather than briefly
    // promising it for a scanned/image-only document.
    () => this.pdfSearchReady() && this.deps.textCapability() === 'available',
  );

  private searchGeneration = 0;
  private sourceHighlightGeneration = 0;
  private activeSearchQuery = '';

  constructor(private readonly deps: PdfSearchControllerDeps) {}

  search(query: string): void {
    if (!this.searchAvailable()) {
      this.clear();
      return;
    }

    const normalized = query.trim();
    if (!normalized) {
      // Clear the previous PDF.js find before replacing the tracked query with
      // an empty string, otherwise clear() cannot know a mark exists.
      this.clear();
      return;
    }

    const generation = ++this.searchGeneration;
    this.activeSearchQuery = normalized;
    this.searchState.set({ status: 'searching', current: 0, total: 0 });
    const counts = this.deps.pdfSearch.find(normalized, {
      highlightAll: false,
      matchCase: false,
      dontScrollIntoView: false,
    });
    if (!counts) {
      // ngx-extended-pdf-viewer 25.6.x returns undefined until its find
      // controller is initialized. Fail back to idle instead of leaving the
      // shared panel permanently on “Searching book…”.
      if (generation === this.searchGeneration && this.activeSearchQuery === normalized) {
        this.pdfSearchReady.set(false);
        this.activeSearchQuery = '';
        this.searchState.set({ status: 'idle', current: 0, total: 0 });
      }
      return;
    }

    void Promise.all(counts).then((perPage) => {
      if (generation !== this.searchGeneration || this.activeSearchQuery !== normalized) return;
      const total = perPage.reduce((sum, count) => sum + count, 0);
      if (total === 0) {
        this.searchState.set({ status: 'not-found', current: 0, total: 0 });
        return;
      }
      const current = this.searchState().current || 1;
      this.searchState.set({ status: 'ready', current: Math.min(current, total), total });
    });
  }

  nextResult(): void {
    if (!this.activeSearchQuery || this.searchState().total === 0) return;
    this.deps.pdfSearch.findNext();
  }

  previousResult(): void {
    if (!this.activeSearchQuery || this.searchState().total === 0) return;
    this.deps.pdfSearch.findPrevious();
  }

  clear(): void {
    const hadQuery = this.activeSearchQuery.length > 0;
    this.searchGeneration++;
    this.activeSearchQuery = '';
    this.searchState.set({ status: 'idle', current: 0, total: 0 });
    // Avoid touching the service during early teardown before the viewer has
    // initialized; when a query did run, an empty query is PDF.js's public clear.
    if (hadQuery) {
      this.deps.pdfSearch.find('', { highlightAll: false, dontScrollIntoView: true });
    }
  }

  onMatchesCount(result: FindResultMatchesCount): void {
    if (!this.activeSearchQuery) return;
    if (result.total <= 0) {
      this.searchState.set({ status: 'not-found', current: 0, total: 0 });
      return;
    }
    this.searchState.set({ status: 'ready', current: result.current, total: result.total });
  }

  onFindState(state: FindState): void {
    if (!this.activeSearchQuery) return;
    if (state === FindState.PENDING) {
      const current = this.searchState();
      this.searchState.set({ ...current, status: 'searching' });
    } else if (state === FindState.NOT_FOUND) {
      this.searchState.set({ status: 'not-found', current: 0, total: 0 });
    }
  }

  /**
   * After landing on a page from another edition, mark the passage with the
   * reader's own search. A handful of the passage's last words is enough to
   * find it and short enough to survive a line break in the page text. Not
   * finding it is fine: the page is already the right one.
   */
  highlightSourcePassage(excerpt: string | null | undefined): void {
    // A newer landing, another document or a destroyed reader cancels a
    // pending highlight: it must never search a page it was not meant for.
    const generation = ++this.sourceHighlightGeneration;
    this.deps.timers.clearTimeout(SOURCE_HIGHLIGHT_TIMER_SLOT);

    const phrases = sourcePassagePhrases(excerpt);
    if (phrases.length === 0) return;

    const timerGeneration = this.deps.timers.currentGeneration();
    const doc = this.deps.getPdfDoc();
    const current = () =>
      generation === this.sourceHighlightGeneration
      && this.deps.timers.isCurrent(timerGeneration)
      && (doc === null || doc === this.deps.getPdfDoc());

    // The passage is the book's text as the server read it; the page's own
    // text layer can differ by a word. When a phrase is not on the page, the
    // next one from the same passage is tried.
    const attempt = (index: number, remaining: number) => {
      if (!current()) return;

      if (!this.searchAvailable()) {
        if (remaining > 0) {
          this.deps.timers.replaceTimeout(
            SOURCE_HIGHLIGHT_TIMER_SLOT,
            () => attempt(index, remaining - 1),
            500,
          );
        }
        return;
      }

      const phrase = phrases[index];
      this.search(phrase);

      const settle = (polls: number) => {
        if (!current() || this.activeSearchQuery !== phrase) return;

        const status = this.searchState().status;
        if (status === 'searching' && polls > 0) {
          this.deps.timers.replaceTimeout(
            SOURCE_HIGHLIGHT_TIMER_SLOT,
            () => settle(polls - 1),
            250,
          );
        } else if (status === 'not-found') {
          if (index + 1 < phrases.length) attempt(index + 1, remaining);
          else this.clear();
        }
      };
      settle(40);
    };
    attempt(0, 20);
  }

  /** Cancel pending source-passage retries and reset search state for a book change or teardown. */
  reset(): void {
    ++this.sourceHighlightGeneration;
    this.deps.timers.clearTimeout(SOURCE_HIGHLIGHT_TIMER_SLOT);
    this.clear();
    this.pdfSearchReady.set(false);
  }
}
