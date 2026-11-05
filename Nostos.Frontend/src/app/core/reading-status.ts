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
  /** The latest finish across the editions, when the status is Finished. */
  finishedAt?: string | null;
}

/** An edition as the book-level status reads it. */
interface StatusEdition {
  id?: string;
  finishedAt?: string | null;
  lastReadAt?: string | null;
  progressPercent?: number | null;
  lastLocation?: string | null;
  /** A saved place, for an edition whose location is not sent (an edition summary). */
  hasPlace?: boolean;
  /** A paper book without a page count has a page, not a percentage. */
  type?: string;
  pageCount?: number | null;
}

/**
 * A book's status from all its editions. Reading status is never stored; the
 * Library's filters and counts work it out the same way.
 *
 * A book has exactly one status, the same one the server gives it:
 * - Reading: an unfinished edition has progress (a percentage or a saved
 *   place) and either nothing is finished or it was read after the latest
 *   finish (a reread).
 * - Finished: any edition is finished and none was read after it.
 * - Not started: no edition has progress or a finish.
 *
 * A single edition gives the same answer as {@link readingStatusOf}. A book's
 * own page asks about its edition alone (no other editions).
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
    (edition.progressPercent ?? 0) > 0 || !!edition.lastLocation || !!edition.hasPlace;

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
    // A paper book without a page count has no honest figure (older saves
    // stored 1%), only its page.
    const noFigure = shown.type === 'physical' && !shown.pageCount;
    const percent = noFigure ? 0 : Math.round(shown.progressPercent ?? 0);
    return { status: 'reading', percent: percent > 0 ? percent : null, percentEditionId: shown.id ?? null };
  }
  if (anyFinished) {
    const finishedAt = editions.find((edition) => edition.finishedAt && Date.parse(edition.finishedAt) === latestFinish)?.finishedAt ?? null;
    return { status: 'finished', percent: null, percentEditionId: null, finishedAt };
  }
  return { status: 'notstarted', percent: null, percentEditionId: null };
}
