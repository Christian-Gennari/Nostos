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

/** What a book's page says about where the reader is: the status and its figure. */
export interface BookReadingStatus {
  status: ReadingStatus;
  /**
   * How far in, only while the status is Reading: the edition on screen when it
   * is the one being read, else the edition read most recently.
   */
  percent: number | null;
  /** The edition that figure comes from. */
  percentEditionId: string | null;
}

/** An edition as the book-level status reads it. */
interface StatusEdition {
  id?: string;
  finishedAt?: string | null;
  lastReadAt?: string | null;
  progressPercent?: number | null;
  lastLocation?: string | null;
}

/**
 * A book's status from all its editions. Reading status is never stored; the
 * Library's filters and counts work it out the same way.
 *
 * - Finished: any edition is finished.
 * - Reading: an unfinished edition has progress and either nothing is finished
 *   or it was read after the latest finish (a reread). A book can be Reading
 *   and Finished at once.
 * - Not started: no edition has progress or a finish.
 *
 * The page's pill shows Reading when it holds, else Finished, else Not started.
 * A single edition gives the same answer as {@link readingStatusOf}.
 */
export function bookReadingStatusOf(
  book: (StatusEdition & { id: string }) | null | undefined,
  otherEditions: readonly StatusEdition[] | null | undefined = [],
): BookReadingStatus {
  if (!book) return { status: 'notstarted', percent: null, percentEditionId: null };

  const editions: StatusEdition[] = [book, ...(otherEditions ?? []).filter((edition) => edition.id !== book.id)];
  // The edition on screen also counts a saved place as progress (a paper
  // book's page, before it has a percentage).
  const hasProgress = (edition: StatusEdition) =>
    (edition.progressPercent ?? 0) > 0 || (edition === book && !!edition.lastLocation);

  const readAt = (edition: StatusEdition) => Date.parse(edition.lastReadAt ?? '') || 0;
  const times = editions.flatMap((edition) => (edition.finishedAt ? [Date.parse(edition.finishedAt)] : []));
  const anyFinished = editions.some((edition) => !!edition.finishedAt);
  const latestFinish = times.length ? Math.max(...times) : null;

  const inProgress = editions.filter((edition) => {
    if (edition.finishedAt || !hasProgress(edition)) return false;
    if (!anyFinished) return true;
    const read = edition.lastReadAt ? Date.parse(edition.lastReadAt) : NaN;
    return !Number.isNaN(read) && latestFinish !== null && read > latestFinish;
  });

  if (inProgress.length > 0) {
    const shown = inProgress.includes(book)
      ? book
      : inProgress.reduce((latest, edition) => (readAt(edition) > readAt(latest) ? edition : latest));
    const percent = Math.round(shown.progressPercent ?? 0);
    return { status: 'reading', percent: percent > 0 ? percent : null, percentEditionId: shown.id ?? null };
  }
  if (anyFinished) return { status: 'finished', percent: null, percentEditionId: null };
  return { status: 'notstarted', percent: null, percentEditionId: null };
}
