/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

/** A book's format, named as the icon concepts name it (`nostos-concepts.ts`). */
export type BookFormatConcept = 'audiobook' | 'ebook' | 'pdf' | 'physical';

/** Short format names, as a "pick a book" row shows them under the author. */
export const BOOK_FORMAT_LABEL: Readonly<Record<BookFormatConcept, string>> = {
  audiobook: 'Audiobook',
  ebook: 'eBook',
  pdf: 'PDF',
  physical: 'Physical',
};

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
