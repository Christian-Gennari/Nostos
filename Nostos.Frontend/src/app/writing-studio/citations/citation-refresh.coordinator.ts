import { DestroyRef, Injectable, inject } from '@angular/core';
import { WritingSourceDto } from '../../core/dtos/writing.dtos';
import { ToastService } from '../../core/services/toast.service';
import { citationMarkdown } from '../../ui/markdown-editor/citation.extension';
import type { CitationRenderer } from '../../ui/markdown-editor/live-citations';
import { CitableSource, CitationService } from './citation.service';
import { CitationStyle } from './citation-styles';
import { bookToCslItem, missingForStyle, missingPhrase } from './csl-item';
import { StyledInsertion, bibliographyMarkdown } from './styled-insertion';

export interface CitationRefreshEditorAdapter {
  citedBooks: () => string[];
  refreshCitations: (renderer: CitationRenderer) => boolean;
}

export interface CitationInsertionEditorAdapter {
  citedBooks: () => string[];
  nextFootnoteNumber: () => number;
}

/**
 * Coordinates debounced live citation refreshes, CSL styling, bibliography
 * building, and once-per-writing missing-metadata warnings with guaranteed timer
 * cleanup via `DestroyRef`.
 */
@Injectable()
export class CitationRefreshCoordinator {
  private readonly citations = inject(CitationService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  private refreshTimer: ReturnType<typeof setTimeout> | null = null;
  private refreshRun = 0;
  private destroyed = false;
  /** "writing:book" pairs already told about missing details, so each is said once. */
  private readonly toldIncomplete = new Set<string>();

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.clearTimer();
    });
  }

  clearTimer(): void {
    if (this.refreshTimer !== null) {
      clearTimeout(this.refreshTimer);
      this.refreshTimer = null;
    }
  }

  scheduleRefresh(callback: () => void | Promise<unknown>, delay = 250): void {
    this.clearTimer();
    if (this.destroyed) return;
    this.refreshTimer = setTimeout(() => {
      this.refreshTimer = null;
      if (this.destroyed) return;
      void callback();
    }, delay);
  }

  async refreshCitations(params: {
    editor: CitationRefreshEditorAdapter | undefined;
    writingId: string | undefined;
    isCurrent: (writingId: string, editor: CitationRefreshEditorAdapter) => boolean;
    style: CitationStyle;
    keptSources: WritingSourceDto[];
  }): Promise<boolean> {
    const { editor, writingId, isCurrent, style, keptSources } = params;
    if (this.destroyed || !editor || !writingId) return false;
    const books = editor.citedBooks();
    if (!books.length) return false;

    const run = ++this.refreshRun;
    let renderer: CitationRenderer;
    try {
      renderer = await this.citations.renderer(style, books, keptSources);
    } catch {
      return false;
    }

    if (this.destroyed || run !== this.refreshRun || !isCurrent(writingId, editor)) {
      return false;
    }
    return editor.refreshCitations(renderer);
  }

  async styledInsertion(
    editor: CitationInsertionEditorAdapter | undefined,
    source: CitableSource & { id?: string },
    mode: 'quote' | 'note' | 'reference',
    style: CitationStyle,
  ): Promise<StyledInsertion | null> {
    if (!editor || !source.bookId) return null;
    const subsequent = style.kind === 'note' && editor.citedBooks().includes(source.bookId);
    const styled = await this.citations.insertion(source, mode, style, subsequent, editor.nextFootnoteNumber());
    if (!styled && style.kind !== 'house') {
      this.toast.error(`Could not format this source in ${style.label}; it went in as Nostos writes it`);
    }
    return styled;
  }

  async buildBibliographyMarkdown(
    bookIds: string[],
    style: CitationStyle,
  ): Promise<{ markdown: string | null; bookIds: string[] } | null> {
    let entries: { book: string; text: string }[] = [];
    try {
      entries = await this.citations.bibliographyEntries(bookIds, style);
    } catch {
      this.toast.error('Could not build the bibliography');
      return null;
    }
    const markdown = bibliographyMarkdown(
      entries.map((entry) => citationMarkdown(entry.text, { book: entry.book, page: null, src: null, as: 'bib' })),
      style,
    );
    return {
      markdown,
      bookIds: entries.map((entry) => entry.book),
    };
  }

  async warnIfIncomplete(
    bookId: string | null | undefined,
    writingId: string | undefined,
    style: CitationStyle,
    openDetails: (bookId: string) => void,
  ): Promise<void> {
    if (!bookId || !writingId || this.toldIncomplete.has(`${writingId}:${bookId}`)) return;
    const book = await this.citations.book(bookId);
    if (this.destroyed || !book) return;
    const missing = missingForStyle(bookToCslItem(book), style);
    if (!missing.length) return;
    this.toldIncomplete.add(`${writingId}:${bookId}`);
    this.toast.show(
      `“${book.title}” has no ${missingPhrase(missing)} in its details, so ${style.label} cites it without.`,
      'info',
      8000,
      { label: 'Add', run: () => openDetails(bookId) },
    );
  }

  async warnIncompleteBibliography(
    bookIds: readonly string[],
    style: CitationStyle,
    openDetails: (bookId: string) => void,
  ): Promise<void> {
    const books = await Promise.all(bookIds.map((id) => this.citations.book(id)));
    if (this.destroyed) return;
    const incomplete = books.filter((book) => !!book && missingForStyle(bookToCslItem(book), style).length);
    if (!incomplete.length) return;
    const message =
      incomplete.length === 1
        ? `“${incomplete[0]!.title}” has no ${missingPhrase(missingForStyle(bookToCslItem(incomplete[0]!), style))} in its details, so its entry is incomplete.`
        : `${incomplete.length} sources lack details ${style.label} needs, so their entries are incomplete.`;
    this.toast.show(message, 'info', 8000, { label: 'Fix', run: () => openDetails(incomplete[0]!.id) });
  }
}
