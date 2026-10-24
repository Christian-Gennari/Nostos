/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

import { formatDate } from '@angular/common';

/**
 * The app's date shapes (issue #1205). Every date a reader sees uses one of
 * these, so a note, a book's "Added" line and a backup read the same way.
 * Times are shown in the browser's own time zone, without naming it.
 */
export const DATE_FORMAT = 'd MMM y'; // 24 Oct 2026
export const DATE_TIME_FORMAT = 'd MMM y, HH:mm'; // 24 Oct 2026, 14:05
export const TIME_FORMAT = 'HH:mm'; // 14:05

export type NostosDateMode = 'date' | 'datetime' | 'time';

const FORMATS: Readonly<Record<NostosDateMode, string>> = {
  date: DATE_FORMAT,
  datetime: DATE_TIME_FORMAT,
  time: TIME_FORMAT,
};

/** The app's locale for dates. The UI is English; the shapes above are fixed. */
const DATE_LOCALE = 'en-US';

/**
 * Format a date the app's way. Returns '' for a missing or unreadable value,
 * so a template never shows "Invalid Date".
 */
export function formatNostosDate(
  value: string | number | Date | null | undefined,
  mode: NostosDateMode = 'date',
): string {
  if (value === null || value === undefined || value === '') return '';
  const time = value instanceof Date ? value.getTime() : typeof value === 'number' ? value : Date.parse(value);
  if (Number.isNaN(time)) return '';
  return formatDate(time, FORMATS[mode], DATE_LOCALE);
}
