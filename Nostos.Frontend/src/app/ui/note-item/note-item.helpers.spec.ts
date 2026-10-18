import {
  hasTopic,
  isReferenceLine,
  newTopicName,
  noteLocationLabel,
  rankTopicChoices,
  thoughtForDisplay,
} from './note-item.helpers';
import type { TopicDto } from '../../core/services/topics.service';

const topic = (name: string, usageCount: number): TopicDto => ({ id: name, name, usageCount });

describe('thoughtForDisplay', () => {
  it('leaves out lines that only hold topic references, which the chips show', () => {
    expect(thoughtForDisplay('A thought.\n\n[[virtue]]\n\n[[mean]] [[choice]]')).toBe('A thought.');
  });

  it('keeps a reference written inside a sentence', () => {
    expect(thoughtForDisplay('On [[virtue]] as a mean.')).toBe('On [[virtue]] as a mean.');
  });

  it('keeps a labelled list, whose label is words', () => {
    expect(thoughtForDisplay('Concepts: [[a]], [[b]]')).toBe('Concepts: [[a]], [[b]]');
  });

  it('collapses the blank lines a removed line leaves', () => {
    expect(thoughtForDisplay('One.\n\n[[a]]\n\nTwo.')).toBe('One.\n\nTwo.');
  });

  it('is empty for a note made only of references', () => {
    expect(thoughtForDisplay('[[a]]')).toBe('');
    expect(thoughtForDisplay(null)).toBe('');
  });
});

describe('isReferenceLine', () => {
  it('accepts references with separators and rejects prose', () => {
    expect(isReferenceLine('[[a]], [[b]] · [[c]]')).toBe(true);
    expect(isReferenceLine('  [[a]]  ')).toBe(true);
    expect(isReferenceLine('See [[a]]')).toBe(false);
    expect(isReferenceLine('')).toBe(false);
  });
});

describe('noteLocationLabel', () => {
  it('names a PDF page and an audio time, and nothing for an EPUB location', () => {
    expect(noteLocationLabel({ sourceAnchorKind: 'pdf_page', sourceAnchorValue: '53' })).toBe('p. 53');
    expect(noteLocationLabel({ sourceAnchorKind: 'audio_timestamp', sourceAnchorValue: '3725' })).toBe('1:02:05');
    expect(noteLocationLabel({ sourceAnchorKind: 'epub_cfi', sourceAnchorValue: 'epubcfi(/6/2)' })).toBeNull();
    expect(noteLocationLabel({ sourceAnchorKind: 'pdf_page', sourceAnchorValue: 'x' })).toBeNull();
  });
});

describe('rankTopicChoices', () => {
  const topics = [topic('virtue ethics', 1), topic('virtue', 4), topic('civic virtue', 2), topic('freedom', 9)];

  it('puts the exact name first, then names starting with the query, then the rest that contain it', () => {
    expect(rankTopicChoices(topics, 'virtue', []).map((t) => t.name))
      .toEqual(['virtue', 'virtue ethics', 'civic virtue']);
  });

  it('lists the most used first with no query, leaving out names the note has', () => {
    expect(rankTopicChoices(topics, '', ['Freedom']).map((t) => t.name))
      .toEqual(['virtue', 'civic virtue', 'virtue ethics']);
  });
});

describe('newTopicName', () => {
  const topics = [topic('Virtue', 1)];

  it('offers a new name and refuses an existing one, ignoring case', () => {
    expect(newTopicName(topics, '  practical   wisdom ')).toBe('practical wisdom');
    expect(newTopicName(topics, 'virtue')).toBeNull();
  });

  it('refuses names that cannot stand inside [[ ]]', () => {
    expect(newTopicName(topics, '')).toBeNull();
    expect(newTopicName(topics, 'a ]] b')).toBeNull();
    expect(newTopicName(topics, 'foo]')).toBeNull();
    expect(newTopicName(topics, '[foo')).toBeNull();
    expect(newTopicName(topics, 'a|b')).toBeNull();
    expect(newTopicName(topics, 'Kant [1781] notes')).toBe('Kant [1781] notes');
  });
});

describe('hasTopic', () => {
  it('compares trimmed and case-insensitively', () => {
    expect(hasTopic([' Virtue '], 'virtue')).toBe(true);
    expect(hasTopic(['virtue'], 'vice')).toBe(false);
  });
});
