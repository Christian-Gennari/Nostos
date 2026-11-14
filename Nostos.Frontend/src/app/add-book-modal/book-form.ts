/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

import type { BookType } from '../core/dtos/book.dtos';
import type { DropdownOption } from '../ui/dropdown/dropdown.component';

/**
 * The Add book / Edit book draft, shared by the modal shell and the metadata
 * form it renders.
 *
 * The shell owns the object (it is what submit() sends and what the source
 * import diffs against); the form component edits it in place. Keeping the
 * shape in one module means neither side can drift from the other.
 */
export interface BookForm {
  type: BookType;
  title: string;
  subtitle: string | null;

  author: string | null;
  editor: string | null;
  translator: string | null;
  narrator: string | null;

  description: string | null;

  isbn: string | null;
  asin: string | null;

  publisher: string | null;
  placeOfPublication: string | null;
  publishedDate: string | null;

  edition: string | null;
  pageCount: number | null;
  duration: string | null;
  language: string;
  categories: string | null;
  series: string | null;
  volumeNumber: string | null;
  collectionIds: string[];

  personalReview: string | null;
}

/** Which acquisition door opened the sheet. Edit Book bypasses all of them. */
export type AddBookIntentKind = 'upload' | 'source' | 'physical' | 'manual';

export const BOOK_TYPE_OPTIONS = [
  { value: 'physical', label: 'Physical book' },
  { value: 'ebook', label: 'E-book (EPUB, PDF)' },
  { value: 'audiobook', label: 'Audiobook' },
] satisfies readonly DropdownOption[];

/** Larger files are not sent just to read their title; the file name stays. */
export const MAX_METADATA_READ_BYTES = 50 * 1024 * 1024;
export const DEFAULT_LANGUAGE = 'en';

/** Helper to format dates into YYYY or YYYY-MM-DD. */
export function sanitizeDate(dateStr: string | null): string | null {
  if (!dateStr) return null;
  const clean = dateStr.trim();

  // 1. If it's already just a year (e.g. "1999"), keep it.
  if (/^\d{4}$/.test(clean)) return clean;

  // 2. Try parsing as a standard date (handles "Jan 1, 2020", "2020/01/01")
  const timestamp = Date.parse(clean);
  if (!isNaN(timestamp)) {
    const date = new Date(timestamp);
    return date.toISOString().split('T')[0]; // Returns YYYY-MM-DD
  }

  // 3. Fallback: Try to just find a 4-digit year in the string
  const yearMatch = clean.match(/\d{4}/);
  return yearMatch ? yearMatch[0] : clean;
}

/** Helper to clean ISBNs (remove dashes, spaces, keep only digits and X). */
export function sanitizeIsbn(isbn: string | null): string | null {
  if (!isbn) return null;
  return isbn.replace(/[^0-9X]/gi, '').toUpperCase();
}

/**
 * A language code becomes its full name ("fr" -> "French"); anything longer
 * than a code is already a name and is kept as typed.
 */
export function languageName(input: string | null): string | null {
  if (!input) return null;
  const clean = input.trim();
  if (clean.length > 3) return clean;
  try {
    return new Intl.DisplayNames(['en'], { type: 'language' }).of(clean) || clean;
  } catch {
    return clean;
  }
}
