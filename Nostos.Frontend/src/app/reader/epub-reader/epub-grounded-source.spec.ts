import {
  epubPassageCandidates,
  nodeAtKoreaderXPointer,
  normalizedResourceOffsetOfNode,
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

describe('epubPassageCandidates', () => {
  const sections = [
    { index: 0, text: 'Contents\nWonder is the feeling of a philosopher.' },
    { index: 2, text: 'Chapter One\nHe paused.\n“Beg your pardon.”\nWonder is the feeling of a philosopher.' },
    { index: 3, text: 'Chapter Two\nA line of verse,broken in two.\nThe arm, bitten off, now ran about on its own.' },
  ];

  it('finds a passage where it occurs once', () => {
    expect(epubPassageCandidates(sections, 'He paused.')).toEqual({
      whole: [{ section: 1, offset: 12, length: 10 }],
      lead: [],
    });
  });

  it('matches across the paragraph break another reader recorded as a line break', () => {
    expect(epubPassageCandidates(sections, 'He paused.\n“Beg your pardon.”').whole).toEqual([
      { section: 1, offset: 12, length: 29 },
    ]);
  });

  it('matches a line break that contributes no character to the book text', () => {
    expect(epubPassageCandidates(sections, 'A line of verse,\nbroken in two.').whole).toEqual([
      { section: 2, offset: 12, length: 30 },
    ]);
  });

  it('narrows a repeated passage to the spine item, not to a list position', () => {
    const passage = 'Wonder is the feeling of a philosopher.';
    expect(epubPassageCandidates(sections, passage).whole.map((m) => m.section)).toEqual([0, 1]);
    expect(epubPassageCandidates(sections, passage, 2).whole.map((m) => m.section)).toEqual([1]);
    // A hint that points nowhere useful must not lose the passage.
    expect(epubPassageCandidates(sections, passage, 7).whole.map((m) => m.section)).toEqual([0, 1]);
  });

  it('offers the opening words only when the whole passage is absent', () => {
    expect(
      epubPassageCandidates(sections, 'The arm, bitten off, now ran about on its own, differently extracted'),
    ).toEqual({ whole: [], lead: [{ section: 2, offset: 43, length: 37 }] });
    const repeated = [
      { index: 0, text: 'It was a bright cold day in April, and the clocks struck.' },
      { index: 1, text: 'It was a bright cold day in April, again.' },
    ];
    // Two places open the same way: the caller must not guess between them.
    expect(
      epubPassageCandidates(repeated, 'It was a bright cold day in April, and nothing else matched').lead,
    ).toHaveLength(2);
  });

  it('is literal about punctuation and finds nothing for text the book lacks', () => {
    const none = { whole: [], lead: [] };
    expect(epubPassageCandidates(sections, 'He paused?')).toEqual(none);
    expect(epubPassageCandidates(sections, 'in CHAPTER ONE')).toEqual(none);
    expect(epubPassageCandidates(sections, '   ')).toEqual(none);
  });
});

describe('KOReader xpointer resolution', () => {
  function bookDocument(markup: string): Document {
    const doc = document.implementation.createHTMLDocument('EPUB');
    doc.body.innerHTML = markup;
    return doc;
  }

  const doc = bookDocument(`
    <h2>One</h2>
    <p>First paragraph.</p>
    <section>
      <p>Inside, <em>stressed</em> and after.</p>
      <p>Second inside.</p>
    </section>
    <p>Third paragraph.</p>
  `);

  it('walks element steps by tag and position among siblings', () => {
    const node = nodeAtKoreaderXPointer(doc, '/body/DocFragment[4]/body/p[2]/text().3');
    expect(node?.textContent).toBe('Third paragraph.');
    expect(nodeAtKoreaderXPointer(doc, '/body/DocFragment[4]/body/section/p[2]/text().0')?.textContent)
      .toBe('Second inside.');
  });

  it('counts only text nodes that hold text, as crengine does', () => {
    const node = nodeAtKoreaderXPointer(doc, '/body/DocFragment[4]/body/section/p[1]/text()[2].1');
    expect(node?.textContent).toBe(' and after.');
  });

  it('accepts a pointer that ends on an element', () => {
    expect((nodeAtKoreaderXPointer(doc, '/body/DocFragment[4]/body/h2.0') as Element).localName).toBe('h2');
  });

  it('keeps the deepest node it reached and rejects a pointer it cannot enter', () => {
    expect(
      (nodeAtKoreaderXPointer(doc, '/body/DocFragment[4]/body/section/p[9]/text().0') as Element).localName,
    ).toBe('section');
    expect(nodeAtKoreaderXPointer(doc, '/body/DocFragment[4]/body/table/tr[1]')).toBeNull();
    expect(nodeAtKoreaderXPointer(doc, 'epubcfi(/6/4!/4/2)')).toBeNull();
  });

  it('maps a node to the start of its block in the normalized text', () => {
    const text = normalizedEpubResourceText(doc);
    const node = nodeAtKoreaderXPointer(doc, '/body/DocFragment[4]/body/section/p[1]/text()[2].1')!;
    expect(normalizedResourceOffsetOfNode(doc, node)).toBe(text.indexOf('Inside,'));
    const section = nodeAtKoreaderXPointer(doc, '/body/DocFragment[4]/body/section/p[9]')!;
    expect(normalizedResourceOffsetOfNode(doc, section)).toBe(text.indexOf('Inside,'));
  });
});
