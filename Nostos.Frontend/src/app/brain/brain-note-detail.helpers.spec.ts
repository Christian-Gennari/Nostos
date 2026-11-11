import type { NoteContextDto, TopicWritingUsageDto } from '../core/services/topics.service';
import { relatedNotesFrom, writingsCitingNote } from './brain-note-detail.helpers';

const note = (id: string, content: string, createdAt = '2026-10-01T00:00:00Z'): NoteContextDto => ({
  noteId: id,
  content,
  bookId: 'book-1',
  bookTitle: 'Book',
  createdAt,
});

describe('brain note detail helpers', () => {
  it('orders related notes by shared topics, then newest, without the note itself', () => {
    const related = relatedNotesFrom([
      { topicName: 'Attention', notes: [note('open', 'Me'), note('two', 'Two', '2026-10-01T00:00:00Z')] },
      { topicName: 'Solitude', notes: [note('two', 'Two', '2026-10-01T00:00:00Z'), note('one', 'One', '2026-11-01T00:00:00Z')] },
    ], 'open');

    expect(related.map((item) => item.id)).toEqual(['two', 'one']);
    expect(related[0].sharedTopics).toEqual(['Attention', 'Solitude']);
    expect(related[1].sharedTopics).toEqual(['Solitude']);
  });

  it('shows a related note by its quote, or its words with the topic references left out', () => {
    const related = relatedNotesFrom([
      {
        topicName: 'Attention',
        notes: [
          { ...note('quoted', 'words'), selectedText: 'A quoted line.' },
          note('words', 'Only the words.\n\n[[Attention]]', '2026-10-02T00:00:00Z'),
        ],
      },
    ], 'open');

    expect(related.map((item) => item.excerpt)).toEqual(['Only the words.', 'A quoted line.']);
  });

  it('caps the related notes', () => {
    const related = relatedNotesFrom([
      { topicName: 'Attention', notes: Array.from({ length: 8 }, (_, i) => note(`n${i}`, `Note ${i}`)) },
    ], 'open', 3);
    expect(related).toHaveLength(3);
  });

  it('names the writings that cite the note, once, and drops ids the reply did not name', () => {
    const usage = (writingIds: string[], writings: { id: string; name: string }[]): TopicWritingUsageDto => ({
      total: writings.length,
      writings: writings.map((writing) => ({ ...writing, updatedAt: '2026-11-01T00:00:00Z' })),
      noteCitations: [{ noteId: 'note-1', writings: writingIds.length, writingIds }],
    });

    const cited = writingsCitingNote([
      { usage: usage(['w1', 'unknown'], [{ id: 'w1', name: 'On owning less' }]) },
      { usage: usage(['w1'], [{ id: 'w1', name: 'On owning less' }]) },
    ], 'note-1');

    expect(cited).toEqual([{ id: 'w1', name: 'On owning less' }]);
  });

  it('treats a failed topic read as no citations', () => {
    expect(writingsCitingNote([{ usage: null }], 'note-1')).toEqual([]);
  });
});
