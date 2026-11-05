import { Injectable, Injector, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { Book } from '../../core/dtos/book.dtos';
import { BooksService } from '../../core/services/books.service';
import { NotesService } from '../../core/services/notes.service';
import { citationMarkdown, type CitationRef } from '../../ui/markdown-editor/citation.extension';
import type { CitationRenderer } from '../../ui/markdown-editor/live-citations';
import { SourceInsertionSource } from '../source-insertion.helpers';
import { CitationEngine, CitationLocator } from './citation-engine';
import { CitationStyle, bibliographyHeading } from './citation-styles';
import { CslItem, bookToCslItem } from './csl-item';
import { StyledInsertion, styledNote, styledQuote, styledReference } from './styled-insertion';

/** A source the Studio can cite: what it quotes and which book it is from. */
export interface CitableSource extends SourceInsertionSource {
  bookId?: string | null;
}

/** What a source goes in as: its quoted words, the writer's note on it, or a bare reference. */
export type InsertionMode = 'quote' | 'note' | 'reference';

/** A kept source, as far as citations need it: a passage is named in the house format. */
export interface KeptCitationSource {
  id: string;
  bookId?: string | null;
  passageTitle?: string | null;
}

/**
 * Where a source is, when Nostos knows it for certain (issues #1109, #1382):
 * a page of a PDF only when verified, a page of a paper book as the reader
 * typed it, a time in a recording when verified. An EPUB's chapter needs the
 * book's contents, so it comes from `CitationService.locator`.
 */
export function citationLocator(source: CitableSource): CitationLocator | null {
  const kind = source.sourceAnchorKind?.trim().toLowerCase();
  const value = (source.sourceAnchorValue ?? '').trim();
  if (kind === 'pdf_page' || kind === 'physical_page') {
    if (kind === 'pdf_page' && source.anchorVerified !== true) return null;
    const page = Number(value);
    return Number.isInteger(page) && page > 0 ? { page } : null;
  }
  if (kind === 'audio_timestamp' && source.anchorVerified === true) {
    const seconds = Number(value);
    return Number.isFinite(seconds) && seconds >= 0 ? { time: clock(seconds) } : null;
  }
  return null;
}

/** h:mm:ss, or m:ss under an hour: the player's own clock. */
export function clock(seconds: number): string {
  const whole = Math.floor(seconds);
  const h = Math.floor(whole / 3600);
  const m = Math.floor((whole % 3600) / 60);
  const s = whole % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`;
}

/** The section of an EPUB a CFI is in, counted from 1 (its second step is 2, 4, 6…). */
export function epubSectionOf(cfi: string | null | undefined): number | null {
  const match = /^epubcfi\(\/6\/(\d+)/.exec((cfi ?? '').trim());
  const step = match ? Number(match[1]) : 0;
  return step >= 2 ? Math.floor(step / 2) : null;
}

/** A locator as a citation stores it: a page, or `c:<chapter>` / `t:<time>`. */
export function locatorToRef(locator: CitationLocator | null): { page: number | null; loc: string | null } {
  if (!locator) return { page: null, loc: null };
  if ('page' in locator) return { page: locator.page, loc: null };
  return { page: null, loc: 'chapter' in locator ? `c:${locator.chapter}` : `t:${locator.time}` };
}

/** What a citation stored as its place, back as a locator. */
export function locatorOfRef(ref: Pick<CitationRef, 'page' | 'loc'>): CitationLocator | null {
  if (ref.page) return { page: ref.page };
  if (ref.loc?.startsWith('c:')) return { chapter: ref.loc.slice(2) };
  if (ref.loc?.startsWith('t:')) return { time: ref.loc.slice(2) };
  return null;
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
  private readonly injector = inject(Injector);
  private readonly sectionTitles = new Map<string, Promise<readonly (string | null)[]>>();
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
      book = Promise.resolve()
        .then(() => firstValueFrom(this.books.get(bookId)))
        .then(
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

  /** The chapter titles of an EPUB in reading order; none when they cannot be read. */
  private sections(bookId: string): Promise<readonly (string | null)[]> {
    let titles = this.sectionTitles.get(bookId);
    if (!titles) {
      titles = firstValueFrom(this.injector.get(NotesService).sections(bookId)).then(
        (result) => result.titles ?? [],
        () => [],
      );
      this.sectionTitles.set(bookId, titles);
    }
    return titles;
  }

  /**
   * Where the source is (issue #1382): its page or time, or else the chapter
   * the book's table of contents gives an EPUB highlight. Null when Nostos
   * does not know; a raw position is never cited.
   */
  async locator(source: CitableSource): Promise<CitationLocator | null> {
    const known = citationLocator(source);
    if (known || !source.bookId) return known;
    if (source.sourceAnchorKind?.trim().toLowerCase() !== 'epub_cfi') return null;
    const section = epubSectionOf(source.sourceAnchorValue);
    const title = section ? (await this.sections(source.bookId))[section - 1]?.trim() : null;
    return title ? { chapter: title } : null;
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
    const locator = await this.locator(source);
    const ref: CitationRef = { book: book.id, ...locatorToRef(locator), src: null, as: role };
    if (!style.file) return houseText(book, ref, (source.passageTitle ?? '').trim() || null);
    return (await this.engine(style)).cite(bookToCslItem(book), locator, subsequent);
  }

  /**
   * A source as it goes into a writing, a live citation in `style` (issues
   * #1109, #1157): the one shape every insertion takes, from the `@` palette,
   * the Reference rail and a starter draft (issue #1220) alike. Null when the
   * source has no book or the book cannot be cited; the caller then falls
   * back to plain house text. `subsequent` gives a note style's short form;
   * `footnoteNumber` is the number a note style's footnote takes.
   */
  async insertion(
    source: CitableSource & { id?: string | null },
    mode: InsertionMode,
    style: CitationStyle,
    subsequent: boolean,
    footnoteNumber: number,
  ): Promise<StyledInsertion | null> {
    if (!source.bookId) return null;
    let text: string | null = null;
    try {
      text = await this.cite(source, style, subsequent, mode);
    } catch {
      text = null;
    }
    if (!text) return null;
    const citation = citationMarkdown(text, {
      book: source.bookId,
      ...locatorToRef(await this.locator(source)),
      src: source.id ?? null,
      as: mode,
    });
    if (mode === 'reference') return styledReference(citation, style, footnoteNumber);
    if (mode === 'note') {
      return styledNote(source.content, citation, style, footnoteNumber) ?? styledQuote(source.selectedText, citation, style, footnoteNumber);
    }
    return styledQuote(source.selectedText, citation, style, footnoteNumber) ?? styledNote(source.content, citation, style, footnoteNumber);
  }

  /** The reference list for these books, each entry its own Markdown paragraph. */
  async bibliography(bookIds: readonly string[], style: CitationStyle): Promise<string[]> {
    return (await this.bibliographyEntries(bookIds, style)).map((entry) => entry.text);
  }

  /** The reference list with the book each entry is for. */
  async bibliographyEntries(bookIds: readonly string[], style: CitationStyle): Promise<{ book: string; text: string }[]> {
    const unique = [...new Set(bookIds.filter(Boolean))];
    const loaded = (await Promise.all(unique.map((id) => this.book(id)))).filter((book): book is Book => !!book);
    const books = oneEditionOfEachWork(loaded);
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
    const canonical = editionRepresentatives(books.values());
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
      canonicalBook: (id) => canonical.get(id) ?? id,
      inText: (ref) => {
        const book = books.get(ref.book);
        if (!book) return null;
        if (!engine) return houseText(book, ref, passageOf(ref));
        return safely(() => engine.cite(bookToCslItem(book), locatorOfRef(ref)));
      },
      note: (ref, subsequent) => {
        const book = books.get(ref.book);
        if (!book) return null;
        if (!engine) return houseText(book, { ...ref, as: null }, passageOf(ref), false);
        return safely(() => engine.cite(bookToCslItem(book), locatorOfRef(ref), subsequent));
      },
      bibliography: (ids) => {
        const listed = ids.map((id) => books.get(id));
        if (listed.some((book) => !book)) return null;
        const once = oneEditionOfEachWork(listed as Book[]);
        if (!engine) return houseBibliography(once);
        try {
          return engine.bibliographyEntries(once.map(bookToCslItem)).map((entry) => ({ book: entry.id, text: entry.text }));
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

/**
 * One edition stands for a work in a reference list (issue #1382): the book
 * that comes first among those loaded, so the list names a work once however
 * many editions the library holds. Maps each edition to its representative.
 */
export function editionRepresentatives(books: Iterable<Book>): Map<string, string> {
  const byWork = new Map<string, string>();
  const result = new Map<string, string>();
  for (const book of [...books].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))) {
    const work = book.workId?.trim();
    if (!work) continue;
    const first = byWork.get(work) ?? book.id;
    byWork.set(work, first);
    result.set(book.id, first);
  }
  return result;
}

/** The books with each work once, in their order. */
function oneEditionOfEachWork(books: readonly Book[]): Book[] {
  const representative = editionRepresentatives(books);
  return books.filter((book) => (representative.get(book.id) ?? book.id) === book.id);
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
  const place = locatorOfRef(ref);
  const locator = passage
    ? `“${escape(clean(passage))}”`
    : !place
      ? null
      : 'page' in place
        ? `p. ${place.page}`
        : 'chapter' in place
          ? `“${escape(clean(place.chapter))}”`
          : `at ${place.time}`;
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
