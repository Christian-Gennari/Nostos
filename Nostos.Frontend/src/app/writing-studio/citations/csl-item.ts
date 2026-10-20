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

/**
 * Names as Nostos stores them: one string, possibly several people. "Marcus
 * Aurelius", "Aurelius, Marcus", "Deleuze & Guattari", "A and B; C" all come
 * in. A single name with a comma is "Family, Given"; otherwise the last word is
 * the family name. Anything unclear is kept whole rather than guessed apart.
 */
export function parseNames(value: string | null | undefined): CslName[] | undefined {
  const text = (value ?? '').replace(/\s+/g, ' ').trim();
  if (!text) return undefined;

  const parts = text.split(/\s*(?:;|&|\band\b)\s*/i).map((p) => p.trim()).filter(Boolean);
  if (parts.length === 1 && /,/.test(parts[0])) {
    const [family, given] = parts[0].split(/\s*,\s*/, 2);
    return family && given ? [{ family, given }] : [{ literal: parts[0] }];
  }
  const people = parts.length === 1 ? parts[0].split(/\s*,\s*/) : parts;
  return people.map((person) => {
    const words = person.split(' ');
    if (words.length === 1) return { literal: person };
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

/** What a style will miss for this book, so the settings can say "APA needs the year". */
export function missingForStyle(item: CslItem): string[] {
  const missing: string[] = [];
  if (!item.author && !item.editor) missing.push('the author');
  if (!item.issued) missing.push('the year');
  if (!item.publisher) missing.push('the publisher');
  return missing;
}
