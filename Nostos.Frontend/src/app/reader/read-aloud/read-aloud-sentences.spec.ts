import { sentenceIndexAtOffset, splitReadAloudSentences } from './read-aloud-sentences';

/**
 * Read-aloud speaks one span at a time and marks it on the page, so two things
 * must hold: every span's offset points at its own text, and a span is a unit a
 * listener would call a sentence.
 */
describe('splitReadAloudSentences', () => {
  const texts = (value: string, language?: string) =>
    splitReadAloudSentences(value, language).map((span) => span.text);

  it('keeps each span addressable by its offset', () => {
    const source = 'It was a bright cold day in April. The clocks were striking thirteen, all of them.\nChapter Two';
    for (const span of splitReadAloudSentences(source, 'en')) {
      expect(source.slice(span.start, span.start + span.length)).toBe(span.text);
    }
  });

  it('does not say note numbers or line references, and splits around them', () => {
    const source =
      'For instance when someone is asleep or insane or drunk.185 So as a result there are [1147A] two ways of having premises.';
    const spans = splitReadAloudSentences(source, 'en');

    expect(spans.map((span) => span.text)).toEqual([
      'For instance when someone is asleep or insane or drunk.',
      'So as a result there are two ways of having premises.',
    ]);
    // The mark still covers the printed words, reference included.
    expect(source.slice(spans[1].start, spans[1].start + spans[1].length)).toBe(
      'So as a result there are [1147A] two ways of having premises.',
    );
  });

  it('keeps numbers that are part of the sentence', () => {
    expect(
      texts('The year was 1984, as far as he could tell. Pi is 3.14 and the train left at 5:30 sharp.', 'en'),
    ).toEqual([
      'The year was 1984, as far as he could tell.',
      'Pi is 3.14 and the train left at 5:30 sharp.',
    ]);
  });

  it('splits sentences inside a block and treats a newline as a hard break', () => {
    expect(
      texts('It was a bright cold day in April. The clocks were striking thirteen, all of them.\nChapter Two', 'en'),
    ).toEqual([
      'It was a bright cold day in April.',
      'The clocks were striking thirteen, all of them.',
      'Chapter Two',
    ]);
  });

  it('does not end a sentence at a title or an initial', () => {
    expect(
      texts('“My dear Mr. Bennet,” said his lady to him one day, “have you heard the news?” Mr. Bennet replied that he had not.', 'en'),
    ).toEqual([
      '“My dear Mr. Bennet,” said his lady to him one day, “have you heard the news?”',
      'Mr. Bennet replied that he had not.',
    ]);
    expect(texts('The book was written by J. R. R. Tolkien in the nineteen thirties.', 'en')).toHaveLength(1);
  });

  it('joins a short fragment to its neighbour', () => {
    expect(texts('“No.” He closed the door behind him and went out.', 'en')).toEqual([
      '“No.” He closed the door behind him and went out.',
    ]);
    expect(texts('He closed the door behind him and went out. “No.”', 'en')).toEqual([
      'He closed the door behind him and went out. “No.”',
    ]);
  });

  it('cuts a very long sentence at a pause, losing no words', () => {
    const clause = 'and the road went ever on and on, down from the door where it began';
    const long = `${Array.from({ length: 12 }, () => clause).join(', ')}.`;
    const spans = splitReadAloudSentences(long, 'en');

    expect(spans.length).toBeGreaterThan(1);
    expect(Math.max(...spans.map((span) => span.length))).toBeLessThanOrEqual(420);
    expect(spans.map((span) => span.text).join(' ').replace(/\s+/g, ' ')).toBe(long);
  });

  it('drops blocks with nothing to say', () => {
    expect(texts('* * *\nThe next morning it rained without stopping.\n—', 'en')).toEqual([
      'The next morning it rained without stopping.',
    ]);
  });

  it('splits other languages too', () => {
    expect(
      texts('Longtemps, je me suis couché de bonne heure. Parfois, à peine ma bougie éteinte, mes yeux se fermaient.', 'fr'),
    ).toHaveLength(2);
    expect(texts('吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。', 'ja').length).toBeGreaterThanOrEqual(1);
  });
});

describe('sentenceIndexAtOffset', () => {
  const spans = splitReadAloudSentences(
    'It was a bright cold day in April. The clocks were striking thirteen, all of them.',
    'en',
  );

  it('finds the sentence an offset falls in', () => {
    expect(sentenceIndexAtOffset(spans, 0)).toBe(0);
    expect(sentenceIndexAtOffset(spans, 40)).toBe(1);
  });

  it('answers past the end with the length', () => {
    expect(sentenceIndexAtOffset(spans, 10_000)).toBe(spans.length);
  });
});
