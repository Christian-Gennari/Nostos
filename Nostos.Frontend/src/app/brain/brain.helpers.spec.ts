import type { NoteSearchHit } from '../core/dtos/note.dtos';
import type { TopicDto } from '../core/services/topics.service';
import {
  countLabel,
  defaultOrder,
  isOrder,
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
});
