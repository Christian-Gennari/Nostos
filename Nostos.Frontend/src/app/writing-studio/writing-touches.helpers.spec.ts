import type { NoteSearchHit } from '../core/dtos/note.dtos';
import { removedCitedIds, touchedTopics } from './writing-touches.helpers';

function hit(id: string, topicNames: string[]): NoteSearchHit {
  return {
    id,
    bookId: 'book',
    bookTitle: 'Candide',
    content: '',
    selectedText: null,
    snippet: null,
    topicNames,
    createdAt: '2026-10-01T00:00:00Z',
  };
}

// Issue #1162: what a writing touches comes from what it cites, read at the
// moment; nothing is stored on the writing.
describe('writing touches', () => {
  const lookup = new Map([
    ['a', hit('a', ['Theodicy', 'Optimism'])],
    ['b', hit('b', ['Optimism'])],
    ['c', hit('c', [])],
    ['kept', hit('kept', ['Gardening'])],
  ]);

  it('lists each cited topic once, the most shared first, then by name', () => {
    expect(touchedTopics(['a', 'b'], lookup)).toEqual(['Optimism', 'Theodicy']);
  });

  it('counts only cited sources, never one that is kept and not cited', () => {
    expect(touchedTopics(['b'], lookup)).toEqual(['Optimism']);
    expect(touchedTopics([], lookup)).toEqual([]);
  });

  it('touches nothing when the cited notes carry no topics', () => {
    expect(touchedTopics(['c'], lookup)).toEqual([]);
  });

  it('drops a removed source from the count', () => {
    expect(touchedTopics(['a', 'gone'], lookup)).toEqual(['Optimism', 'Theodicy']);
    expect(removedCitedIds(['a', 'gone'], lookup, new Set(['a', 'gone']))).toEqual(['gone']);
  });

  it('does not call a source removed before the server was asked about it', () => {
    expect(removedCitedIds(['new'], lookup, new Set(['a']))).toEqual([]);
  });

  it('matches ids in any case', () => {
    expect(touchedTopics(['A'], lookup)).toEqual(['Optimism', 'Theodicy']);
  });
});
