import { NostosDatePipe } from './nostos-date.pipe';
import { DATE_FORMAT, DATE_TIME_FORMAT, formatNostosDate } from '../../core/date-formats';

describe('NostosDatePipe', () => {
  const pipe = new NostosDatePipe();
  // A local time, so the expectations hold in any test time zone.
  const local = new Date(2026, 9, 4, 9, 5);

  it('uses the shared shapes', () => {
    expect(DATE_FORMAT).toBe('d MMM y');
    expect(DATE_TIME_FORMAT).toBe('d MMM y, HH:mm');
  });

  it('formats a date as "d MMM y" by default', () => {
    expect(pipe.transform(local)).toBe('4 Oct 2026');
  });

  it('formats a date and time with a 24-hour clock and no time zone', () => {
    expect(pipe.transform(local, 'datetime')).toBe('4 Oct 2026, 09:05');
    expect(pipe.transform(local.getTime(), 'time')).toBe('09:05');
  });

  it('accepts ISO strings', () => {
    expect(pipe.transform(local.toISOString())).toBe('4 Oct 2026');
  });

  it('returns an empty string for a missing or unreadable value', () => {
    expect(pipe.transform(null)).toBe('');
    expect(pipe.transform(undefined)).toBe('');
    expect(pipe.transform('')).toBe('');
    expect(formatNostosDate('not a date')).toBe('');
  });
});
