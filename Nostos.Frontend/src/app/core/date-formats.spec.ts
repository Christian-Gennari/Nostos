import { describe, expect, it } from 'vitest';

import {
  DATE_FORMAT,
  DATE_TIME_FORMAT,
  MONTH_YEAR_FORMAT,
  TIME_FORMAT,
  YEAR_FORMAT,
  formatNostosDate,
} from './date-formats';

describe('formatNostosDate', () => {
  // Local noon, so no time zone can move the day.
  const date = new Date(2026, 9, 24, 14, 5);

  it('exposes the app’s shapes', () => {
    expect(DATE_FORMAT).toBe('d MMM y');
    expect(DATE_TIME_FORMAT).toBe('d MMM y, HH:mm');
    expect(TIME_FORMAT).toBe('HH:mm');
    expect(MONTH_YEAR_FORMAT).toBe('MMMM y');
    expect(YEAR_FORMAT).toBe('y');
  });

  it.each([
    ['date', '24 Oct 2026'],
    ['datetime', '24 Oct 2026, 14:05'],
    ['time', '14:05'],
    ['month', 'October 2026'],
    ['year', '2026'],
  ] as const)('formats a Date as %s', (mode, expected) => {
    expect(formatNostosDate(date, mode)).toBe(expected);
  });

  it('defaults to the date shape', () => {
    expect(formatNostosDate(date)).toBe('24 Oct 2026');
  });

  it('reads an ISO string as the same moment', () => {
    const iso = date.toISOString();

    expect(formatNostosDate(iso, 'datetime')).toBe('24 Oct 2026, 14:05');
  });

  it('reads a timestamp', () => {
    expect(formatNostosDate(date.getTime(), 'time')).toBe('14:05');
  });

  it.each([null, undefined, ''])('shows nothing for %s rather than "Invalid Date"', (value) => {
    expect(formatNostosDate(value)).toBe('');
    expect(formatNostosDate(value, 'datetime')).toBe('');
  });

  it('shows nothing for text that is not a date', () => {
    expect(formatNostosDate('yesterday')).toBe('');
  });

  it('shows nothing for a number that is not a time', () => {
    expect(formatNostosDate(Number.NaN)).toBe('');
  });
});
