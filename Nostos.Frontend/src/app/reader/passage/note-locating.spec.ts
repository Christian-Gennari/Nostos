import {
  passageOccurrences,
  readUnlocatedNoteIds,
  rememberUnlocatedNoteId,
} from './note-locating';

describe('passageOccurrences', () => {
  it('finds exact words and reports where they stand in the text searched', () => {
    expect(passageOccurrences('one two three two three', 'two three')).toEqual([
      { offset: 4, length: 9 },
      { offset: 14, length: 9 },
    ]);
  });

  it('reads through line breaks in the source and in the quote', () => {
    expect(passageOccurrences('the river\nran  on', 'river ran on')).toHaveLength(1);
    expect(passageOccurrences('river ran on', 'river\nran on')).toHaveLength(1);
  });

  it('takes curly and straight quotation marks for each other', () => {
    expect(passageOccurrences('I don’t know', "I don't know")).toHaveLength(1);
    expect(passageOccurrences('called “paprika hendl,” here', 'called "paprika hendl," here')).toHaveLength(1);
  });

  it('counts soft hyphens in the length of the match, so the offsets stay true', () => {
    const text = 'a philo­sophy here';
    const [match] = passageOccurrences(text, 'philosophy');
    expect(text.slice(match.offset, match.offset + match.length)).toBe('philo­sophy');
    expect(passageOccurrences('a philosophy here', 'philo­sophy')).toHaveLength(1);
  });

  it('reads a ligature as its letters, in either text', () => {
    expect(passageOccurrences('too ﬁercely and oﬀ', 'too fiercely and off')).toHaveLength(1);
    expect(passageOccurrences('too fiercely', 'too ﬁercely')).toHaveLength(1);
  });

  it('wants every word: a different word is no match', () => {
    expect(passageOccurrences('the heart has its reasons, which', 'the heart has its reasons of which')).toEqual([]);
  });

  it('takes brackets and the like literally', () => {
    expect(passageOccurrences('a [note] (here)', '[note] (here)')).toHaveLength(1);
    expect(passageOccurrences('a note', '[note]')).toEqual([]);
  });
});

describe('the memory of notes not found', () => {
  beforeEach(() => localStorage.clear());

  it('keeps an id, and copes with what is stored being unreadable', () => {
    rememberUnlocatedNoteId('n-1');
    expect(readUnlocatedNoteIds().has('n-1')).toBe(true);

    localStorage.setItem('nostos.reader.unlocatedImportedNotes', '{not json');
    expect(readUnlocatedNoteIds().size).toBe(0);
  });
});
