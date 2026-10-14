import { readAloudVoiceLabel } from './read-aloud-bar.component';

describe('readAloudVoiceLabel', () => {
  const heart = { id: 'af_heart', name: 'Heart', language: 'en-US' };
  const emma = { id: 'bf_emma', name: 'Emma', language: 'en-GB' };
  const sara = { id: 'if_sara', name: 'Sara', language: 'it' };

  it('names the voice alone when every voice has the same accent', () => {
    expect(readAloudVoiceLabel(heart, [heart])).toBe('Heart');
  });

  it('adds the accent when one language has several', () => {
    expect(readAloudVoiceLabel(emma, [heart, emma])).toBe('Emma · British');
  });

  it('names the language when the voices span several', () => {
    expect(readAloudVoiceLabel(sara, [heart, emma, sara])).toBe('Sara · Italian');
    expect(readAloudVoiceLabel(emma, [heart, emma, sara])).toBe('Emma · British English');
  });
});
