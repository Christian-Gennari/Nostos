import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { Book } from '../../core/dtos/book.dtos';
import { BooksService } from '../../core/services/books.service';
import { SourceInsertionSource } from '../source-insertion.helpers';
import { CitationEngine, CitationLocator } from './citation-engine';
import { CitationStyle } from './citation-styles';
import { CslItem, bookToCslItem } from './csl-item';

/** A source the Studio can cite: what it quotes and which book it is from. */
export interface CitableSource extends SourceInsertionSource {
  bookId?: string | null;
}

/** A page, only when Nostos verified it; EPUB positions and timestamps are not pages. */
export function citationLocator(source: CitableSource): CitationLocator | null {
  if (source.anchorVerified !== true || source.sourceAnchorKind?.trim().toLowerCase() !== 'pdf_page') return null;
  const page = Number((source.sourceAnchorValue ?? '').trim());
  return Number.isInteger(page) && page > 0 ? { page } : null;
}

/**
 * Citations in the writer's chosen style (issue #1109): the book behind a
 * source, as CSL-JSON, formatted by citeproc-js. Books and engines are loaded
 * once per session.
 */
@Injectable({ providedIn: 'root' })
export class CitationService {
  private readonly books = inject(BooksService);
  private readonly engines = new Map<string, Promise<CitationEngine>>();
  private readonly items = new Map<string, Promise<CslItem | null>>();

  engine(style: CitationStyle): Promise<CitationEngine> {
    let engine = this.engines.get(style.id);
    if (!engine) {
      engine = CitationEngine.create(style);
      engine.catch(() => this.engines.delete(style.id));
      this.engines.set(style.id, engine);
    }
    return engine;
  }

  /** The book as CSL-JSON, or null when it cannot be read. */
  item(bookId: string | null | undefined): Promise<CslItem | null> {
    if (!bookId) return Promise.resolve(null);
    let item = this.items.get(bookId);
    if (!item) {
      item = firstValueFrom(this.books.get(bookId)).then(
        (book: Book) => bookToCslItem(book),
        () => {
          this.items.delete(bookId);
          return null;
        },
      );
      this.items.set(bookId, item);
    }
    return item;
  }

  /** Forget a book whose details may have changed, so its next citation reads them again. */
  forget(bookId: string): void {
    this.items.delete(bookId);
  }

  /** One citation in `style`, as Markdown; null when the book cannot be read. */
  async cite(source: CitableSource, style: CitationStyle, subsequent = false): Promise<string | null> {
    const item = await this.item(source.bookId);
    if (!item) return null;
    return (await this.engine(style)).cite(item, citationLocator(source), subsequent);
  }

  /** The reference list for these books, each entry its own Markdown paragraph. */
  async bibliography(bookIds: readonly string[], style: CitationStyle): Promise<string[]> {
    const unique = [...new Set(bookIds.filter(Boolean))];
    const items = (await Promise.all(unique.map((id) => this.item(id)))).filter((item): item is CslItem => !!item);
    if (!items.length) return [];
    if (!style.file) return houseBibliography(items);
    return (await this.engine(style)).bibliography(items);
  }

  /** What a citation and its reference entry look like in `style`, for the style picker. */
  async example(book: Book, style: CitationStyle): Promise<{ citation: string; reference: string }> {
    const item = bookToCslItem(book);
    if (!style.file) {
      return { citation: houseBibliography([item])[0], reference: houseBibliography([item])[0] };
    }
    const engine = await this.engine(style);
    const citation = engine.cite(item, { page: 42 });
    const [reference] = engine.bibliography([item]);
    return { citation, reference: reference ?? '' };
  }
}

function nameOf(item: CslItem): string {
  const person = item.author?.[0] ?? item.editor?.[0];
  if (!person) return '';
  return person.literal ?? [person.given, person.family].filter(Boolean).join(' ');
}

function escape(text: string): string {
  return text.replace(/([\\*_`[\]])/g, '\\$1');
}

/** The house format's list: "Author, *Title*", sorted by author, then title. */
function houseBibliography(items: readonly CslItem[]): string[] {
  return [...items]
    .sort((a, b) => (sortKey(a) < sortKey(b) ? -1 : sortKey(a) > sortKey(b) ? 1 : 0))
    .map((item) => [nameOf(item) && escape(nameOf(item)), `*${escape(item.title)}*`].filter(Boolean).join(', '));
}

function sortKey(item: CslItem): string {
  const person = item.author?.[0] ?? item.editor?.[0];
  return `${(person?.family ?? person?.literal ?? '').toLowerCase()}|${item.title.toLowerCase()}`;
}
