import {
  findEpubPassage,
  normalizedEpubResourceText,
  rangeForNormalizedResourceSpan,
} from './epub-grounded-source';

describe('EPUB normalized resource search text (#761)', () => {
  function bookDocument(markup: string): Document {
    const doc = document.implementation.createHTMLDocument('EPUB');
    doc.body.innerHTML = markup;
    return doc;
  }

  it('keeps inline text searchable while excluding navigation and nested duplicate blocks', () => {
    const doc = bookDocument(`
      <nav>Table of contents</nav>
      <h1>Chapter I</h1>
      <p>Entering Trans<em>ylva</em>nia tonight.</p>
      <blockquote><p>A nested paragraph.</p></blockquote>
    `);

    expect(normalizedEpubResourceText(doc)).toBe(
      'Chapter I\nEntering Transylvania tonight.\nA nested paragraph.',
    );
  });

  it('maps a normalized match spanning inline markup back to the exact DOM range', () => {
    const doc = bookDocument('<p>Entering Trans<em>ylva</em>nia tonight.</p>');
    const text = normalizedEpubResourceText(doc);
    const start = text.indexOf('Transylvania');

    const range = rangeForNormalizedResourceSpan(doc, start, 'Transylvania'.length);

    expect(range).not.toBeNull();
    expect(range!.toString()).toBe('Transylvania');
  });
});

describe('findEpubPassage', () => {
  const sections = [
    { index: 0, text: 'Contents\nWonder is the feeling of a philosopher.' },
    { index: 2, text: 'Chapter One\nHe paused.\n“Beg your pardon.”\nWonder is the feeling of a philosopher.' },
    { index: 3, text: 'Chapter Two\nA line of verse,broken in two.\nThe arm, bitten off, now ran about on its own.' },
  ];

  it('finds a passage where it occurs once', () => {
    expect(findEpubPassage(sections, 'He paused.')).toEqual({ section: 1, offset: 12, length: 10 });
  });

  it('matches across the paragraph break another reader recorded as a line break', () => {
    const match = findEpubPassage(sections, 'He paused.\n“Beg your pardon.”');
    expect(match).toEqual({ section: 1, offset: 12, length: 29 });
  });

  it('matches a line break that contributes no character to the book text', () => {
    expect(findEpubPassage(sections, 'A line of verse,\nbroken in two.')).toEqual({
      section: 2,
      offset: 12,
      length: 30,
    });
  });

  it('settles a repeated passage by the spine item, not by list position', () => {
    const passage = 'Wonder is the feeling of a philosopher.';
    expect(findEpubPassage(sections, passage)?.section).toBe(0);
    expect(findEpubPassage(sections, passage, 2)?.section).toBe(1);
    // A hint that points nowhere useful must not lose the passage.
    expect(findEpubPassage(sections, passage, 7)?.section).toBe(0);
  });

  it('falls back to the opening words only when they name a single place', () => {
    expect(
      findEpubPassage(sections, 'The arm, bitten off, now ran about on its own, differently extracted'),
    ).toEqual({ section: 2, offset: 43, length: 37 });
    expect(
      findEpubPassage(sections, 'Wonder is the feeling of a philosopher. Then words not in the book'),
    ).toBeNull();
  });

  it('is literal about punctuation and returns null for text the book lacks', () => {
    expect(findEpubPassage(sections, 'He paused?')).toBeNull();
    expect(findEpubPassage(sections, 'in CHAPTER ONE')).toBeNull();
    expect(findEpubPassage(sections, '   ')).toBeNull();
  });
});
