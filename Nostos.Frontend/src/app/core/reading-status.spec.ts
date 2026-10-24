import { READING_STATUS_LABEL, TO_READ_LABEL, readingStatusOf } from './reading-status';

describe('reading status', () => {
  it('has one sentence-case wording per status', () => {
    expect(READING_STATUS_LABEL).toEqual({ notstarted: 'Not started', reading: 'Reading', finished: 'Finished' });
    expect(TO_READ_LABEL).toBe('To read');
  });

  it('lets finished win, then any progress or location, else not started', () => {
    expect(readingStatusOf(null)).toBe('notstarted');
    expect(readingStatusOf({ progressPercent: 0, lastLocation: null })).toBe('notstarted');
    expect(readingStatusOf({ progressPercent: 12 })).toBe('reading');
    expect(readingStatusOf({ progressPercent: 0, lastLocation: 'page:3' })).toBe('reading');
    expect(readingStatusOf({ progressPercent: 40, finishedAt: '2026-10-01T00:00:00Z' })).toBe('finished');
  });
});
