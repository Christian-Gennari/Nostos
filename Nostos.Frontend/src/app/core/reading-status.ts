/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

/** Where a reader is with a book, as the Library's status filters name it. */
export type ReadingStatus = 'notstarted' | 'reading' | 'finished';

/**
 * The one wording for each reading status (issue #1206): the Library sidebar,
 * its filter chip, Book detail and the edition switcher all say these.
 */
export const READING_STATUS_LABEL: Readonly<Record<ReadingStatus, string>> = {
  notstarted: 'Not started',
  reading: 'Reading',
  finished: 'Finished',
};

/** The reading list: books the reader wants but has not started or owned yet. */
export const TO_READ_LABEL = 'To read';

/**
 * A book's status from its progress. Finished wins; any saved progress or
 * location counts as Reading; otherwise it has not been started.
 */
export function readingStatusOf(
  book: { finishedAt?: string | null; progressPercent?: number | null; lastLocation?: string | null } | null | undefined,
): ReadingStatus {
  if (!book) return 'notstarted';
  if (book.finishedAt) return 'finished';
  if ((book.progressPercent ?? 0) > 0 || book.lastLocation) return 'reading';
  return 'notstarted';
}
