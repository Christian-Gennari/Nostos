import type { NoteSearchHit } from '../core/dtos/note.dtos';
import type { TopicDto } from '../core/services/topics.service';
import {
  bookKey,
  countLabel,
  defaultOrder,
  facetKey,
  isListedTopic,
  isOrder,
  noteDateGroups,
  placeTitle,
  sidebarTopics,
  stillBelongs,
  topicsMatching,
} from './brain.helpers';

const topic = (name: string, usageCount: number): TopicDto => ({ id: name, name, usageCount });
const hit = (topicNames: string[]): NoteSearchHit => ({
  id: 'n', bookId: 'b', bookTitle: 'Book', content: '', selectedText: null, snippet: null, topicNames, createdAt: '',
});

describe('brain helpers', () => {
  it('names each place and counts in its own words', () => {
    expect(placeTitle('all', null)).toBe('All notes');
    expect(placeTitle('topic', topic('virtue', 4))).toBe('virtue');
    expect(countLabel('unsorted', 43)).toBe('43 notes without a topic');
    expect(countLabel('passages', 1)).toBe('1 passage');
    expect(countLabel('all', 1)).toBe('1 note');
  });

  it('reads a topic by book and everything else newest first by default', () => {
    expect(defaultOrder('topic')).toBe('book');
    expect(defaultOrder('unsorted')).toBe('newest');
    expect(isOrder('oldest')).toBe(true);
    expect(isOrder('sideways')).toBe(false);
  });

  it('lists sidebar topics most used first or A–Z, hiding topics no note carries', () => {
    const topics = [topic('beta', 1), topic('Alpha', 3), topic('gone', 0), topic('alpha two', 3)];
    expect(sidebarTopics(topics, 'usage').map((t) => t.name)).toEqual(['Alpha', 'alpha two', 'beta']);
    expect(sidebarTopics(topics, 'az').map((t) => t.name)).toEqual(['Alpha', 'alpha two', 'beta']);
  });

  it('keeps a topic the reader described even when no note carries it', () => {
    const kept = { ...topic('mine', 0), description: 'In my words.' };
    const blank = { ...topic('blank', 0), description: '   ' };
    expect(isListedTopic(kept)).toBe(true);
    expect(isListedTopic(blank)).toBe(false);
    expect(sidebarTopics([kept, blank, topic('used', 1)], 'az').map((t) => t.name)).toEqual(['mine', 'used']);
  });

  it('finds topics by name for a search, ignoring accents, most used first', () => {
    const topics = [topic('Café society', 1), topic('cafeteria', 5), topic('tea', 9)];
    expect(topicsMatching(topics, 'cafe').map((t) => t.name)).toEqual(['cafeteria', 'Café society']);
    expect(topicsMatching(topics, '  ')).toEqual([]);
  });

  it('drops a note from a list it no longer belongs to', () => {
    const virtue = topic('virtue', 2);
    const wisdom = topic('wisdom', 2);
    expect(stillBelongs(hit(['virtue']), 'unsorted', null, null)).toBe(false);
    expect(stillBelongs(hit([]), 'unsorted', null, null)).toBe(true);
    expect(stillBelongs(hit(['other']), 'topic', virtue, null)).toBe(false);
    expect(stillBelongs(hit(['Virtue']), 'topic', virtue, null)).toBe(true);
    expect(stillBelongs(hit(['virtue']), 'topic', virtue, wisdom)).toBe(false);
    expect(stillBelongs(hit([]), 'all', null, null)).toBe(true);
  });

  it('keys a book by its work, so its editions are one book (#1302)', () => {
    expect(bookKey({ bookId: 'epub', workId: 'w' })).toBe('w');
    expect(bookKey({ bookId: 'audio', workId: 'w' })).toBe(bookKey({ bookId: 'epub', workId: 'w' }));
    expect(bookKey({ bookId: 'orphan', workId: null })).toBe('orphan');
    expect(facetKey({ bookId: 'epub', workId: 'w' })).toBe('w');
  });
});

describe('date groups (Newest first / Oldest first)', () => {
  /** A local time, so the tests read the same calendar day in any zone. */
  const at = (y: number, m: number, d: number, h = 12, min = 0) => new Date(y, m, d, h, min);
  const iso = (y: number, m: number, d: number, h = 12, min = 0) => at(y, m, d, h, min).toISOString();
  const dated = (createdAt: string): NoteSearchHit =>
    ({ id: createdAt, bookId: 'b', bookTitle: 'Book', content: '', selectedText: null, snippet: null, topicNames: [], createdAt });
  const groups = (dates: string[], now: Date) => noteDateGroups(dates.map(dated), now);

  it('files a note by the reader’s own day, not by a 24-hour span', () => {
    const now = at(2026, 10, 10, 0, 5); // 00:05 on 10 November 2026
    const [today, yesterday] = groups([iso(2026, 10, 10, 0, 0), iso(2026, 10, 9, 23, 59)], now);

    expect(today?.label).toBe('Today');
    expect(yesterday?.label).toBe('Yesterday');
  });

  it('starts the week on Monday', () => {
    const now = at(2026, 10, 11); // Wednesday 11 November 2026
    const [monday, sunday] = groups([iso(2026, 10, 9), iso(2026, 10, 8)], now);

    expect(monday?.label).toBe('This week');
    expect(sunday?.label).toBe('Earlier this month');
  });

  it('opens a month header at the month change and a year header at twelve months', () => {
    const now = at(2026, 10, 10); // 10 November 2026
    const [thisMonth, lastMonth, elevenMonthsAgo, twelveMonthsAgo] = groups(
      [iso(2026, 10, 1), iso(2026, 9, 31), iso(2025, 11, 20), iso(2025, 10, 20)],
      now,
    );

    expect(thisMonth?.label).toBe('Earlier this month');
    expect(lastMonth?.label).toBe('October 2026');
    expect(elevenMonthsAgo?.label).toBe('December 2025');
    expect(twelveMonthsAgo?.label).toBe('2025');
  });

  it('does not repeat a header when the next page continues the group', () => {
    const now = at(2026, 10, 10); // Tuesday 10 November 2026
    const pageOne = [iso(2026, 10, 10, 8), iso(2026, 10, 9, 20)];
    const pageTwo = [iso(2026, 10, 9, 8), iso(2026, 10, 8, 18)];
    const all = groups([...pageOne, ...pageTwo], now);

    expect(all.map((group) => group?.label)).toEqual(['Today', 'Yesterday', 'Yesterday', 'Earlier this month']);
    expect(all.map((group) => group?.starts)).toEqual([true, true, false, true]);
    expect(all[1]?.count).toBe(2);
  });

  it('counts the notes each header stands over', () => {
    const now = at(2026, 10, 10);
    const all = groups([iso(2026, 10, 10), iso(2026, 10, 9), iso(2026, 9, 2), iso(2026, 9, 3)], now);

    expect(all.map((group) => group?.count)).toEqual([1, 1, 2, 2]);
  });

  it('leaves an unreadable date without a header', () => {
    const all = noteDateGroups([dated('not a date')], at(2026, 10, 10));

    expect(all).toEqual([null]);
  });
});
