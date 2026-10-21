import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { Book } from '../../core/dtos/book.dtos';
import { BooksService } from '../../core/services/books.service';
import type { CitationRef } from '../../ui/markdown-editor/citation.extension';
import type { CitationRenderer } from '../../ui/markdown-editor/live-citations';
import { SourceInsertionSource } from '../source-insertion.helpers';
import { CitationEngine, CitationLocator } from './citation-engine';
import { CitationStyle, bibliographyHeading } from './citation-styles';
import { CslItem, bookToCslItem } from './csl-item';

/** A source the Studio can cite: what it quotes and which book it is from. */
export interface CitableSource extends SourceInsertionSource {
  bookId?: string | null;
}

/** A kept source, as far as citations need it: a passage is named in the house format. */
export interface KeptCitationSource {
  id: string;
  bookId?: string | null;
  passageTitle?: string | null;
}

/** A page, only when Nostos verified it; EPUB positions and timestamps are not pages. */
export function citationLocator(source: CitableSource): CitationLocator | null {
  if (source.anchorVerified !== true || source.sourceAnchorKind?.trim().toLowerCase() !== 'pdf_page') return null;
  const page = Number((source.sourceAnchorValue ?? '').trim());
  return Number.isInteger(page) && page > 0 ? { page } : null;
}

/**
 * Citations in the writer's chosen style (issues #1109, #1157): the book
 * behind a source, as CSL-JSON, formatted by citeproc-js, or in the house
 * format. Books and engines are loaded once per session; a book whose
 * details are edited here is replaced, and `revision` moves so the open
 * writing's citations are written again.
 */
@Injectable({ providedIn: 'root' })
export class CitationService {
  private readonly books = inject(BooksService);
  private readonly engines = new Map<string, Promise<CitationEngine>>();
  private readonly loaded = new Map<string, Promise<Book | null>>();

  /** Bumped whenever a book's details change here. */
  readonly revision = signal(0);

  engine(style: CitationStyle): Promise<CitationEngine> {
    let engine = this.engines.get(style.id);
    if (!engine) {
      engine = CitationEngine.create(style);
      engine.catch(() => this.engines.delete(style.id));
      this.engines.set(style.id, engine);
    }
    return engine;
  }

  /** The book, or null when it cannot be read. */
  book(bookId: string | null | undefined): Promise<Book | null> {
    if (!bookId) return Promise.resolve(null);
    let book = this.loaded.get(bookId);
    if (!book) {
      book = firstValueFrom(this.books.get(bookId)).then(
        (value: Book) => value,
        () => {
          this.loaded.delete(bookId);
          return null;
        },
      );
      this.loaded.set(bookId, book);
    }
    return book;
  }

  /** The book as CSL-JSON, or null when it cannot be read. */
  async item(bookId: string | null | undefined): Promise<CslItem | null> {
    const book = await this.book(bookId);
    return book ? bookToCslItem(book) : null;
  }

  /** Forget a book whose details may have changed, so its next citation reads them again. */
  forget(bookId: string): void {
    this.loaded.delete(bookId);
    this.revision.update((n) => n + 1);
  }

  /** A book saved with new details: every citation of it follows. */
  updated(book: Book): void {
    this.loaded.set(book.id, Promise.resolve(book));
    this.revision.update((n) => n + 1);
  }

  /** One citation in `style`, as Markdown; null when the book cannot be read. */
  async cite(source: CitableSource, style: CitationStyle, subsequent = false, role: CitationRef['as'] = null): Promise<string | null> {
    const book = await this.book(source.bookId);
    if (!book) return null;
    const ref: CitationRef = { book: book.id, page: citationLocator(source)?.page ?? null, src: null, as: role };
    if (!style.file) return houseText(book, ref, (source.passageTitle ?? '').trim() || null);
    return (await this.engine(style)).cite(bookToCslItem(book), citationLocator(source), subsequent);
  }

  /** The reference list for these books, each entry its own Markdown paragraph. */
  async bibliography(bookIds: readonly string[], style: CitationStyle): Promise<string[]> {
    return (await this.bibliographyEntries(bookIds, style)).map((entry) => entry.text);
  }

  /** The reference list with the book each entry is for. */
  async bibliographyEntries(bookIds: readonly string[], style: CitationStyle): Promise<{ book: string; text: string }[]> {
    const unique = [...new Set(bookIds.filter(Boolean))];
    const books = (await Promise.all(unique.map((id) => this.book(id)))).filter((book): book is Book => !!book);
    if (!books.length) return [];
    if (!style.file) return houseBibliography(books);
    return (await this.engine(style)).bibliographyEntries(books.map(bookToCslItem)).map((entry) => ({ book: entry.id, text: entry.text }));
  }

  /**
   * Writes the writing's live citations in `style` (issue #1157), from the
   * books' current details. A book that cannot be read leaves its citations
   * as they were last written.
   */
  async renderer(style: CitationStyle, bookIds: readonly string[], kept: readonly KeptCitationSource[]): Promise<CitationRenderer> {
    const unique = [...new Set([...bookIds, ...kept.map((source) => source.bookId ?? '')].filter(Boolean))];
    const books = new Map<string, Book>();
    await Promise.all(
      unique.map(async (id) => {
        const book = await this.book(id);
        if (book) books.set(id, book);
      }),
    );
    const engine = style.file ? await this.engine(style) : null;
    const passages = new Map(kept.filter((source) => source.passageTitle?.trim()).map((source) => [source.id, source.passageTitle!.trim()]));
    const passageOf = (ref: CitationRef) => (ref.src ? (passages.get(ref.src) ?? null) : null);
    const safely = (format: () => string): string | null => {
      try {
        return format() || null;
      } catch {
        return null;
      }
    };

    return {
      placement: style.kind === 'note' ? 'note' : style.kind === 'house' ? 'house' : 'in-text',
      bibliographyHeading: bibliographyHeading(style),
      bibliographyBooks: [...new Set(kept.map((source) => source.bookId ?? '').filter(Boolean))],
      inText: (ref) => {
        const book = books.get(ref.book);
        if (!book) return null;
        if (!engine) return houseText(book, ref, passageOf(ref));
        return safely(() => engine.cite(bookToCslItem(book), ref.page ? { page: ref.page } : null));
      },
      note: (ref, subsequent) => {
        const book = books.get(ref.book);
        if (!book) return null;
        if (!engine) return houseText(book, { ...ref, as: null }, passageOf(ref), false);
        return safely(() => engine.cite(bookToCslItem(book), ref.page ? { page: ref.page } : null, subsequent));
      },
      bibliography: (ids) => {
        const listed = ids.map((id) => books.get(id));
        if (listed.some((book) => !book)) return null;
        if (!engine) return houseBibliography(listed as Book[]);
        try {
          return engine.bibliographyEntries((listed as Book[]).map(bookToCslItem)).map((entry) => ({ book: entry.id, text: entry.text }));
        } catch {
          return null;
        }
      },
    };
  }

  /** What a citation and its reference entry look like in `style`, for the style picker. */
  async example(book: Book, style: CitationStyle): Promise<{ citation: string; reference: string }> {
    if (!style.file) {
      const [entry] = houseBibliography([book]);
      return { citation: entry.text, reference: entry.text };
    }
    const item = bookToCslItem(book);
    const engine = await this.engine(style);
    const citation = engine.cite(item, { page: 42 });
    const [reference] = engine.bibliography([item]);
    return { citation, reference: reference ?? '' };
  }
}

function escape(text: string): string {
  return text.replace(/([\\*_`[\]])/g, '\\$1');
}

function clean(text: string | null | undefined): string {
  return (text ?? '').replace(/\s+/g, ' ').trim();
}

/**
 * The house format (the "Nostos" style), from the book's details: "— Author,
 * *Title*, p. 42" under a quote, "*Source: …*" after a note, "*See: …*" in a
 * sentence. A passage is named rather than paged.
 */
export function houseText(book: Book, ref: CitationRef, passage: string | null, framed = true): string {
  const author = clean(book.author) || clean(book.editor);
  const title = escape(clean(book.title));
  const locator = passage ? `“${escape(clean(passage))}”` : ref.page ? `p. ${ref.page}` : null;
  const parts = (shownTitle: string) => [author && escape(author), shownTitle, locator].filter(Boolean).join(', ');
  if (!framed) return parts(`*${title}*`);
  if (ref.as === 'quote') return `— ${parts(`*${title}*`)}`;
  if (ref.as === 'note') return `*Source: ${parts(title)}*`;
  return `*See: ${parts(title)}*`;
}

/** The house format's list: "Author, *Title*", sorted by author, then title. */
function houseBibliography(books: readonly Book[]): { book: string; text: string }[] {
  const key = (book: Book) => {
    const person = bookToCslItem(book).author?.[0] ?? bookToCslItem(book).editor?.[0];
    return `${(person?.family ?? person?.literal ?? '').toLowerCase()}|${clean(book.title).toLowerCase()}`;
  };
  return [...books]
    .sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0))
    .map((book) => {
      const author = clean(book.author) || clean(book.editor);
      return { book: book.id, text: [author && escape(author), `*${escape(clean(book.title))}*`].filter(Boolean).join(', ') };
    });
}
