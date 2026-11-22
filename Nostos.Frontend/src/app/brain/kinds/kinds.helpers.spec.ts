import { describe, expect, it } from 'vitest';

import type { TopicDto, TopicKindDto } from '../../core/services/topics.service';
import {
  KIND_COLOR_KEYS,
  authorNameIndex,
  authorNames,
  freeKindColorKeys,
  kindCountLabel,
  kindDeleteDescription,
  kindSorterTopics,
  kindSourceLabel,
  kindUsingColor,
  normalizeKindFilter,
  normalizeName,
  normalizePersonName,
  personSuggestionFor,
  rangeSelection,
  topicMatchesAuthor,
} from './kinds.helpers';

const kind = (id: string, name: string, colorKey: string, topicCount = 0): TopicKindDto => ({
  id,
  name,
  colorKey,
  sortOrder: 1,
  createdAt: '2026-01-01T00:00:00Z',
  topicCount,
});

const topic = (id: string, name: string, kindId: string | null = null, source: TopicDto['kindSource'] = null): TopicDto => ({
  id,
  name,
  usageCount: 1,
  kindId,
  kindSource: source,
});

const person = kind('k1', 'Person', 'kind-1', 34);
const idea = kind('k2', 'Idea', 'kind-2');
const kinds = [person, idea];

describe('kinds helpers', () => {
  it('filters by untyped, kind and who set it, then searches by name', () => {
    const topics = [
      topic('t1', 'Ursula'),
      topic('t2', 'Education', 'k2', 'assistant'),
      topic('t3', 'Tolkien', 'k1', 'reader'),
      topic('t4', 'Story', 'k2', 'connected-app'),
    ];

    expect(kindSorterTopics(topics, 'untyped', '').map((t) => t.id)).toEqual(['t1']);
    expect(kindSorterTopics(topics, 'k2', '').map((t) => t.id)).toEqual(['t2', 't4']);
    expect(kindSorterTopics(topics, 'assistant', '').map((t) => t.id)).toEqual(['t2']);
    expect(kindSorterTopics(topics, 'connected-app', '').map((t) => t.id)).toEqual(['t4']);
    expect(kindSorterTopics(topics, 'all', 'tolk').map((t) => t.id)).toEqual(['t3']);
    expect(kindSorterTopics(topics, 'all', 'ürsula').map((t) => t.id)).toEqual(['t1']);
  });

  it('sorts the sorter A–Z, accents and case ignored', () => {
    const topics = [topic('t1', 'zebra'), topic('t2', 'Éclair'), topic('t3', 'apple')];
    expect(kindSorterTopics(topics, 'all', '').map((t) => t.name)).toEqual(['apple', 'Éclair', 'zebra']);
  });

  it('names the source of a kind', () => {
    expect(kindSourceLabel('assistant')).toBe('Set by Ask Nostos');
    expect(kindSourceLabel('connected-app')).toBe('Set by a connected app');
    expect(kindSourceLabel('reader')).toBe('Set by you');
    expect(kindSourceLabel(null)).toBeNull();
  });

  it('offers only colours no other kind uses, keeping the kind its own', () => {
    expect(freeKindColorKeys(kinds)).toEqual(KIND_COLOR_KEYS.slice(2));
    expect(freeKindColorKeys(kinds, 'k1')).toEqual(KIND_COLOR_KEYS.filter((key) => key !== 'kind-2'));
    expect(kindUsingColor(kinds, 'kind-2')?.id).toBe('k2');
    expect(kindUsingColor(kinds, 'kind-2', 'k2')).toBeNull();
  });

  it('selects a shift-click range from the anchor, in either direction', () => {
    const ids = ['a', 'b', 'c', 'd', 'e'];
    expect([...rangeSelection(ids, new Set(), 'b', 'd', true)].sort()).toEqual(['b', 'c', 'd']);
    expect([...rangeSelection(ids, new Set(ids), 'd', 'b', false)].sort()).toEqual(['a', 'e']);
    expect([...rangeSelection(ids, new Set(), null, 'c', true)]).toEqual(['c']);
  });

  it('words the delete confirm with the count and keeps the topics', () => {
    expect(kindDeleteDescription(person)).toBe('34 topics will have no kind. The topics stay.');
    expect(kindDeleteDescription(kind('k', 'One', 'kind-1', 1))).toBe('1 topic will have no kind. The topics stay.');
  });

  it('matches an author name with accents, punctuation and case ignored', () => {
    expect(normalizePersonName('  Úrsula K. Le Guin ')).toBe('ursula k le guin');
    expect(authorNames('Tolkien, J.R.R.')).toContain('Tolkien');
    expect(authorNames('Ursula K. Le Guin & J.R.R. Tolkien')).toEqual(['Ursula K. Le Guin', 'J.R.R. Tolkien']);

    const authors = ['Ursula K. Le Guin', 'J.R.R. Tolkien', 'Jorge Luis Borges'];
    expect(topicMatchesAuthor('Ursula K. Le Guin', authors)).toBe(true);
    expect(topicMatchesAuthor('ursula k le guin', authors)).toBe(true);
    expect(topicMatchesAuthor('Tolkien', authors)).toBe(true);
    expect(topicMatchesAuthor('Le Guin', authors)).toBe(true);
    expect(topicMatchesAuthor('Borges', authors)).toBe(true);
    expect(topicMatchesAuthor('Eco', ['Umberto Eco'])).toBe(true);
    expect(topicMatchesAuthor('Education', authors)).toBe(false);
    // A very short word is not a surname match: no "K" or "Le" suggestions.
    expect(topicMatchesAuthor('K', authors)).toBe(false);
  });

  it('suggests Person only for an untyped topic named after a library author', () => {
    const authors = authorNameIndex(['J.R.R. Tolkien']);
    expect(personSuggestionFor(topic('t1', 'Tolkien'), kinds, authors)?.id).toBe('k1');
    expect(personSuggestionFor(topic('t2', 'Tolkien', 'k2'), kinds, authors)).toBeNull();
    expect(personSuggestionFor(topic('t3', 'Education'), kinds, authors)).toBeNull();
    expect(personSuggestionFor(topic('t4', 'Tolkien'), [idea], authors)).toBeNull();
  });

  it('folds kind names with the invariant mapping, as the server does', () => {
    // The Turkish dotted/dotless i is where the locale mapping and the server's
    // disagree; `I` must fold to `I`, never to `İ`.
    expect(normalizeName('  person ')).toBe('PERSON');
    expect(normalizeName('fıkır')).toBe('FIKIR');
  });

  it('folds the filter from the URL, leaving a kind id alone', () => {
    expect(normalizeKindFilter('Untyped')).toBe('untyped');
    expect(normalizeKindFilter(' ASSISTANT ')).toBe('assistant');
    expect(normalizeKindFilter('Connected-App')).toBe('connected-app');
    expect(normalizeKindFilter('')).toBe('all');
    expect(normalizeKindFilter(null)).toBe('all');
    expect(normalizeKindFilter('0f7b2c1e-0000-4000-8000-000000000000'))
      .toBe('0f7b2c1e-0000-4000-8000-000000000000');
  });

  it('says "13 of 40 topics" only while something is narrowing the list', () => {
    expect(kindCountLabel(13, 40, true)).toBe('13 of 40 topics');
    expect(kindCountLabel(40, 40, false)).toBe('40 topics');
    expect(kindCountLabel(1, 1, false)).toBe('1 topic');
  });
});
