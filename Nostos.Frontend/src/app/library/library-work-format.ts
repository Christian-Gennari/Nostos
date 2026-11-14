/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { BOOK_FORMAT_LABEL } from '../core/book-format';
import type { Book, EditionSummaryDto } from '../core/dtos/book.dtos';
import { NOSTOS_CONCEPTS } from '../ui/icon/nostos-concepts';
import type { NostosIconName } from '../ui/icon/nostos-icons';

/**
 * The formats a WORK is available in, drawn by the Library's card and row.
 *
 * Why this lives outside `Library`: both views show the same thing — a work with
 * several editions names every format it exists in, as glyphs with one tooltip —
 * and the shelf used to own the mapping, the glyph list and the tooltip sentence
 * inside a 1,400-line component. It is pure data: given a book and its other
 * editions, which formats exist. Nothing here reads a signal or touches the DOM.
 */

/** The four shapes a Nostos book can be, as the badge groups them. */
export type WorkFormatType = 'audio' | 'epub' | 'pdf' | 'physical';

export interface WorkFormatGlyph {
  type: WorkFormatType;
  /** A Nostos glyph NAME, not icon data: the badge asks for an icon, it does not own one. */
  icon: NostosIconName;
  label: string;
}

/**
 * Every glyph a badge can draw, in the order the shelf has always drawn them.
 *
 * Format icons come from the semantic registry, so a format means the same glyph
 * here, in the book row and in the audio reader.
 */
const WORK_FORMAT_GLYPHS: readonly WorkFormatGlyph[] = [
  { type: 'audio', icon: NOSTOS_CONCEPTS.audiobook, label: 'Audiobook' },
  { type: 'epub', icon: NOSTOS_CONCEPTS.ebook, label: BOOK_FORMAT_LABEL.ebook },
  { type: 'pdf', icon: NOSTOS_CONCEPTS.pdf, label: 'PDF' },
  { type: 'physical', icon: NOSTOS_CONCEPTS.physical, label: 'Physical book' },
];

/**
 * Which of the four shapes one edition is. The book's own `type` wins, then the
 * declared format, then the file extension: a PDF is an ebook with a `.pdf` file,
 * and the shelf has always drawn it as a PDF.
 */
export function workFormatType(edition: Book | EditionSummaryDto): WorkFormatType {
  const type = edition.type.toLowerCase();
  const format = 'format' in edition ? edition.format?.trim().toLowerCase() : undefined;
  const extension = edition.fileName?.split('.').pop()?.trim().toLowerCase();

  if (type === 'audiobook' || type === 'audio' || format === 'audiobook' || format === 'audio') {
    return 'audio';
  }
  if (type === 'physical' || format === 'physical') return 'physical';
  if (extension === 'pdf' || format === 'pdf' || type === 'pdf') return 'pdf';
  return 'epub';
}

/** The formats this work is available in, as the badge draws them. */
export function workFormatGlyphs(book: Book): WorkFormatGlyph[] {
  const editions: (Book | EditionSummaryDto)[] = [book, ...(book.otherEditions || [])];
  const present = new Set(editions.map((edition) => workFormatType(edition)));
  return WORK_FORMAT_GLYPHS.filter((glyph) => present.has(glyph.type));
}

/** The badge's tooltip: "Available in Audiobook, Ebook". */
export function workFormatsTooltip(book: Book): string {
  const labels = workFormatGlyphs(book).map((glyph) => glyph.label);
  return `Available in ${labels.join(', ')}`;
}
