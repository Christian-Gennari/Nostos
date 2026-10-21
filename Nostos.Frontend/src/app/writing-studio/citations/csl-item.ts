import { Book } from '../../core/dtos/book.dtos';

/** The CSL-JSON a style formats; only the fields Nostos has. */
export interface CslName {
  family?: string;
  given?: string;
  literal?: string;
}

export interface CslItem {
  id: string;
  type: 'book';
  title: string;
  author?: CslName[];
  editor?: CslName[];
  translator?: CslName[];
  issued?: { 'date-parts': number[][] };
  publisher?: string;
  'publisher-place'?: string;
  edition?: string;
}

const PLACEHOLDER_NAMES = /^(multiple authors|various( authors)?|unknown( author)?)$/i;
const EPITHET = /\bthe\b|^(saint|st\.?) /i;

/**
 * Names as Nostos stores them: one string, possibly several people. "Marcus
 * Aurelius", "Aurelius, Marcus", "Deleuze & Guattari", "A and B; C" all come
 * in. A single name with a comma is "Family, Given"; otherwise the last word is
 * the family name. Anything unclear is kept whole rather than guessed apart.
 */
export function parseNames(value: string | null | undefined): CslName[] | undefined {
  const text = (value ?? '').replace(/\s+/g, ' ').trim();
  // A library's placeholder names nobody: the style cites by title (issue #1157).
  if (!text || PLACEHOLDER_NAMES.test(text)) return undefined;

  const parts = text.split(/\s*(?:;|&|\band\b)\s*/i).map((p) => p.trim()).filter(Boolean);
  if (parts.length === 1 && /,/.test(parts[0])) {
    const [family, given] = parts[0].split(/\s*,\s*/, 2);
    return family && given ? [{ family, given }] : [{ literal: parts[0] }];
  }
  const people = parts.length === 1 ? parts[0].split(/\s*,\s*/) : parts;
  return people.map((person) => {
    const words = person.split(' ');
    // "Seneca the Younger", "Saint Augustine": an epithet, not a family name.
    if (words.length === 1 || EPITHET.test(person)) return { literal: person };
    return { family: words[words.length - 1], given: words.slice(0, -1).join(' ') };
  });
}

/** The year from a published date as Nostos stores it ("1851", "2006-03-01", "March 2006"). */
export function publishedYear(value: string | null | undefined): number | null {
  const match = /\b(\d{4})\b/.exec(value ?? '');
  return match ? Number(match[1]) : null;
}

export function bookToCslItem(book: Book): CslItem {
  const title = [book.title?.trim(), book.subtitle?.trim()].filter(Boolean).join(': ');
  const year = publishedYear(book.publishedDate);
  const item: CslItem = { id: book.id, type: 'book', title };
  const author = parseNames(book.author);
  const editor = parseNames(book.editor);
  const translator = parseNames(book.translator);
  if (author) item.author = author;
  if (editor) item.editor = editor;
  if (translator) item.translator = translator;
  if (year) item.issued = { 'date-parts': [[year]] };
  if (book.publisher?.trim()) item.publisher = book.publisher.trim();
  if (book.placeOfPublication?.trim()) item['publisher-place'] = book.placeOfPublication.trim();
  if (book.edition?.trim()) item.edition = book.edition.trim();
  return item;
}

/** A detail a citation can lack, as Book details names it. */
export type CitationDetail = 'author' | 'year' | 'publisher';

/**
 * The details citations will lack for this book. The house format names only
 * the author and title; every CSL style also wants the year and publisher.
 */
export function missingForStyle(item: CslItem, style: { kind: string } | null = null): CitationDetail[] {
  const missing: CitationDetail[] = [];
  if (!item.author && !item.editor) missing.push('author');
  if (style?.kind === 'house') return missing;
  if (!item.issued) missing.push('year');
  if (!item.publisher) missing.push('publisher');
  return missing;
}

/** "the year", "the year and publisher", "the author, year and publisher". */
export function missingPhrase(missing: readonly CitationDetail[]): string {
  if (missing.length <= 1) return missing.join('');
  return `${missing.slice(0, -1).join(', ')} and ${missing[missing.length - 1]}`;
}
