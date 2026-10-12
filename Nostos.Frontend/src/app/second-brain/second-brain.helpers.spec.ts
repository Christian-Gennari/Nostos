import type { NoteSearchHit } from '../core/dtos/note.dtos';
import type { TopicDto } from '../core/services/topics.service';
import {
  highlightNameParts,
  newTopicNameFor,
  noteSourceParams,
  rankTopics,
  withTopicReference,
} from './second-brain.helpers';

function topic(id: string, name: string, usageCount = 0): TopicDto {
  return { id, name, usageCount } as TopicDto;
}

function note(fields: Partial<NoteSearchHit>): NoteSearchHit {
  return { id: 'n1', bookId: 'b1', content: '', ...fields } as NoteSearchHit;
}

describe('second-brain helpers', () => {
  const topics = [topic('1', 'Stoicism', 5), topic('2', 'Épistémologie', 9), topic('3', 'Stoa', 1)];

  describe('newTopicNameFor', () => {
    it('offers a trimmed name that no topic has yet', () => {
      expect(newTopicNameFor('  Virtue  ', topics)).toBe('Virtue');
    });

    it('offers nothing for an existing name, whatever its case', () => {
      expect(newTopicNameFor('stoicism', topics)).toBeNull();
    });

    it('offers nothing for an empty, overlong or link-breaking name', () => {
      expect(newTopicNameFor('   ', topics)).toBeNull();
      expect(newTopicNameFor('x'.repeat(101), topics)).toBeNull();
      expect(newTopicNameFor('a]b', topics)).toBeNull();
      expect(newTopicNameFor('a\nb', topics)).toBeNull();
    });
  });

  describe('withTopicReference', () => {
    it('appends the reference after a blank line', () => {
      expect(withTopicReference('A thought.  \n', 'Stoicism')).toBe('A thought.\n\n[[Stoicism]]');
    });

    it('is the whole body when the note was empty', () => {
      expect(withTopicReference('', ' Stoicism ')).toBe('[[Stoicism]]');
    });

    it('leaves a note alone that already declares the topic', () => {
      const content = 'See [[stoicism]] here.';
      expect(withTopicReference(content, 'Stoicism')).toBe(content);
    });
  });

  describe('rankTopics', () => {
    it('orders by usage, then name, when there is no query', () => {
      expect(rankTopics(topics, '', null, 'usage').map((t) => t.name)).toEqual([
        'Épistémologie',
        'Stoicism',
        'Stoa',
      ]);
    });

    it('orders alphabetically in both directions', () => {
      const az = rankTopics(topics, '', null, 'az').map((t) => t.name);
      expect(rankTopics(topics, '', null, 'za').map((t) => t.name)).toEqual([...az].reverse());
    });

    it('puts an exact match before a prefix match, ahead of the sort', () => {
      expect(rankTopics(topics, 'stoa', null, 'usage').map((t) => t.name)).toEqual(['Stoa']);
      expect(rankTopics(topics, 'sto', null, 'usage').map((t) => t.name)).toEqual([
        'Stoicism',
        'Stoa',
      ]);
    });

    it('matches without accents and leaves out the excluded topic', () => {
      expect(rankTopics(topics, 'epist', null, 'usage').map((t) => t.id)).toEqual(['2']);
      expect(rankTopics(topics, '', '2', 'usage').map((t) => t.id)).toEqual(['1', '3']);
    });

    it('does not reorder the list it was given', () => {
      const before = topics.map((t) => t.id);
      rankTopics(topics, '', null, 'az');
      expect(topics.map((t) => t.id)).toEqual(before);
    });
  });

  describe('highlightNameParts', () => {
    it('returns the whole name when there is no query or no match', () => {
      expect(highlightNameParts('Stoicism', ' ')).toEqual([{ text: 'Stoicism', highlight: false }]);
      expect(highlightNameParts('Stoicism', 'zen')).toEqual([{ text: 'Stoicism', highlight: false }]);
    });

    it('highlights the matched part in the middle of a name', () => {
      expect(highlightNameParts('Stoicism', 'OIC')).toEqual([
        { text: 'St', highlight: false },
        { text: 'oic', highlight: true },
        { text: 'ism', highlight: false },
      ]);
    });

    it('keeps the accented characters of the name it highlights', () => {
      expect(highlightNameParts('Épistémologie', 'epistem')).toEqual([
        { text: 'Épistém', highlight: true },
        { text: 'ologie', highlight: false },
      ]);
    });
  });

  describe('noteSourceParams', () => {
    it('reads a verified PDF page, EPUB location and audio time', () => {
      expect(
        noteSourceParams(note({ anchorVerified: true, sourceAnchorKind: 'pdf_page', sourceAnchorValue: '12' })),
      ).toEqual({ sourcePage: 12 });
      expect(
        noteSourceParams(
          note({ anchorVerified: true, sourceAnchorKind: 'epub_cfi', sourceAnchorValue: 'epubcfi(/6/4)' }),
        ),
      ).toEqual({ sourceCfi: 'epubcfi(/6/4)' });
      expect(
        noteSourceParams(
          note({ anchorVerified: true, sourceAnchorKind: 'audio_timestamp', sourceAnchorValue: '90.5' }),
        ),
      ).toEqual({ sourceTime: 90.5 });
    });

    it('ignores an anchor that was never verified', () => {
      expect(
        noteSourceParams(note({ anchorVerified: false, sourceAnchorKind: 'pdf_page', sourceAnchorValue: '12' })),
      ).toBeNull();
    });

    it('rejects a page that is not a positive whole number', () => {
      expect(
        noteSourceParams(note({ anchorVerified: true, sourceAnchorKind: 'pdf_page', sourceAnchorValue: '0' })),
      ).toBeNull();
    });

    it('falls back to the highlight range of an EPUB note', () => {
      expect(noteSourceParams(note({ cfiRange: 'epubcfi(/6/8!/4)' }))).toEqual({
        sourceCfi: 'epubcfi(/6/8!/4)',
      });
    });

    it('has nothing to offer a note with no location', () => {
      expect(noteSourceParams(note({}))).toBeNull();
    });
  });
});
