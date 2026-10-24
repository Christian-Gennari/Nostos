/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

/** A book's format, named as the icon concepts name it (`nostos-concepts.ts`). */
export type BookFormatConcept = 'audiobook' | 'ebook' | 'pdf' | 'physical';

/** Short format names, as a "pick a book" row shows them under the author. */
export const BOOK_FORMAT_LABEL: Readonly<Record<BookFormatConcept, string>> = {
  audiobook: 'Audiobook',
  ebook: 'EPUB',
  pdf: 'PDF',
  physical: 'Physical',
};

/** One book format as the UI names it: the short label and the icon concept. */
export interface BookFormat {
  format: string;
  concept: BookFormatConcept;
}

/**
 * The category covering EPUB and PDF together (a source filter, a file slot that
 * takes either). Say "EPUB" only where it means exactly that format.
 */
export const EBOOK_CATEGORY_LABEL = 'Ebook';
export const EBOOK_CATEGORY_LABEL_PLURAL = 'Ebooks';

/**
 * The one way to name a book's format: Audiobook, EPUB, PDF, Physical. A file
 * extension names any other ebook type ("MOBI"); an ebook with no file says EPUB.
 */
export function formatOf(book: { type: string; format?: string | null; fileName?: string | null }): BookFormat {
  const type = book.type.toLowerCase();
  if (type === 'audiobook' || type === 'audio') return { format: BOOK_FORMAT_LABEL.audiobook, concept: 'audiobook' };
  if (type === 'physical') return { format: BOOK_FORMAT_LABEL.physical, concept: 'physical' };

  const extension = book.fileName?.split('.').pop()?.trim().toLowerCase();
  const declared = book.format?.trim().toLowerCase().replace(/^\./, '');
  const kind = extension || (declared && declared !== 'ebook' ? declared : 'epub');
  if (kind === 'pdf') return { format: BOOK_FORMAT_LABEL.pdf, concept: 'pdf' };
  return { format: kind === 'epub' ? BOOK_FORMAT_LABEL.ebook : kind.toUpperCase(), concept: 'ebook' };
}

/**
 * The format concept for a library book. A PDF is an ebook with a `.pdf` file;
 * the server's library filters draw the same line.
 */
export function bookFormatConcept(type: string | null | undefined, fileName?: string | null): BookFormatConcept {
  const kind = (type ?? '').toLowerCase();
  if (kind === 'audiobook' || kind === 'audio') return 'audiobook';
  if (kind === 'physical') return 'physical';
  return fileName?.toLowerCase().endsWith('.pdf') ? 'pdf' : 'ebook';
}

/** What a "pick a book" row says when a book has no author. */
export const UNKNOWN_AUTHOR = 'Unknown author';

/** What any list says when a book has no title. */
export const UNKNOWN_BOOK = 'Unknown book';
