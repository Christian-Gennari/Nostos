import { READING_STATUS_LABEL, TO_READ_LABEL, bookReadingStatusOf, readingStatusOf } from './reading-status';

describe('book reading status across editions', () => {
  const epub = { id: 'epub', progressPercent: 0, finishedAt: null, lastReadAt: null };
  const audio = { id: 'audio', progressPercent: 0, finishedAt: null, lastReadAt: null };

  it('is Not started when no edition has been touched', () => {
    expect(bookReadingStatusOf(epub, [audio])).toEqual({ status: 'notstarted', percent: null, percentEditionId: null });
    expect(bookReadingStatusOf(null)).toEqual({ status: 'notstarted', percent: null, percentEditionId: null });
  });

  it('answers as one edition does for a book with a single edition', () => {
    for (const book of [
      { id: 'a' },
      { id: 'a', progressPercent: 12, lastReadAt: '2026-10-01T00:00:00Z' },
      { id: 'a', progressPercent: 0, lastLocation: 'page:3' },
      { id: 'a', progressPercent: 100, finishedAt: '2026-10-01T00:00:00Z' },
    ]) {
      expect(bookReadingStatusOf(book).status).toBe(readingStatusOf(book));
    }
    expect(bookReadingStatusOf({ id: 'a', progressPercent: 12.4 }).percent).toBe(12);
  });

  it('is Reading with the figure of the edition read, when only one has progress', () => {
    const result = bookReadingStatusOf(epub, [{ ...audio, progressPercent: 34, lastReadAt: '2026-10-02T00:00:00Z' }]);
    expect(result).toEqual({ status: 'reading', percent: 34, percentEditionId: 'audio' });
  });

  it('prefers the edition on screen for the figure when it is in progress', () => {
    const onScreen = { ...epub, progressPercent: 37, lastReadAt: '2026-10-01T00:00:00Z' };
    const other = { ...audio, progressPercent: 34, lastReadAt: '2026-10-03T00:00:00Z' };
    expect(bookReadingStatusOf(onScreen, [other])).toMatchObject({ status: 'reading', percent: 37, percentEditionId: 'epub' });
  });

  it('takes the figure from the edition read most recently when the one on screen is not in progress', () => {
    const older = { id: 'older', progressPercent: 20, lastReadAt: '2026-10-01T00:00:00Z' };
    const newer = { id: 'newer', progressPercent: 60, lastReadAt: '2026-10-05T00:00:00Z' };
    expect(bookReadingStatusOf(epub, [older, newer])).toMatchObject({ percent: 60, percentEditionId: 'newer' });
  });

  it('is Finished when one edition is finished, even if the others were never opened', () => {
    const finished = { ...epub, progressPercent: 100, finishedAt: '2026-10-01T00:00:00Z' };
    expect(bookReadingStatusOf(audio, [finished]).status).toBe('finished');
    expect(bookReadingStatusOf(finished, [audio])).toEqual({ status: 'finished', percent: null, percentEditionId: null });
  });

  it('is Finished, not Reading, when an old partial listen came before the finish', () => {
    const listened = { ...audio, progressPercent: 60, lastReadAt: '2026-09-01T00:00:00Z' };
    const finished = { ...epub, progressPercent: 100, finishedAt: '2026-10-01T00:00:00Z', lastReadAt: '2026-10-01T00:00:00Z' };
    expect(bookReadingStatusOf(listened, [finished]).status).toBe('finished');
  });

  it('is Reading on a reread after a finish, and shows that edition', () => {
    const finished = { ...epub, progressPercent: 100, finishedAt: '2026-01-01T00:00:00Z', lastReadAt: '2026-01-01T00:00:00Z' };
    const reread = { ...audio, progressPercent: 34, lastReadAt: '2026-10-01T00:00:00Z' };
    expect(bookReadingStatusOf(finished, [reread])).toEqual({ status: 'reading', percent: 34, percentEditionId: 'audio' });
    expect(bookReadingStatusOf(reread, [finished])).toMatchObject({ status: 'reading', percent: 34 });
  });

  it('does not count an unfinished edition with no read date as a reread', () => {
    const finished = { ...epub, finishedAt: '2026-01-01T00:00:00Z' };
    expect(bookReadingStatusOf({ ...audio, progressPercent: 30 }, [finished]).status).toBe('finished');
  });
});

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
