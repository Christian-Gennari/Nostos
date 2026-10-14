import {
  epubPassageCandidates,
  nodeAtKoreaderXPointer,
  normalizedResourceOffsetOfNode,
  normalizedEpubResourceProse,
  normalizedEpubResourceText,
  rangeForNormalizedResourceSpan,
  sentenceSpanAt,
  sentenceSpanInResource,
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

  it('turns the file’s own line wraps into spaces for prose, keeping every offset', () => {
    const doc = bookDocument('<h1>Chapter I</h1><p>It is a truth universally\nacknowledged, that a single man.</p>');

    const text = normalizedEpubResourceText(doc);
    const prose = normalizedEpubResourceProse(doc);

    expect(text).toBe('Chapter I\nIt is a truth universally\nacknowledged, that a single man.');
    expect(prose).toBe('Chapter I\nIt is a truth universally acknowledged, that a single man.');
    expect(prose.length).toBe(text.length);
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

describe('sentenceSpanAt (#844)', () => {
  function sentence(text: string, endOfWord: string): string | null {
    const span = sentenceSpanAt(text, text.indexOf(endOfWord) + endOfWord.length);
    return span ? text.slice(span.start, span.start + span.length) : null;
  }

  const text =
    'Marley was dead: to begin with. There is no doubt whatever about that. ' +
    '“Bah!” said Scrooge. “Humbug!” Mr. Utterson the lawyer was a man of a rugged countenance.';

  it('marks the whole sentence a position falls inside', () => {
    expect(sentence(text, 'no doubt')).toBe('There is no doubt whatever about that.');
  });

  it('starts at the block when the sentence opens it', () => {
    expect(sentence(text, 'Marley was')).toBe('Marley was dead: to begin with.');
  });

  it('keeps closing quotation marks with their sentence', () => {
    expect(sentence(text, 'said')).toBe('“Bah!” said Scrooge.');
    expect(sentence(text, 'Humbug')).toBe('“Humbug!”');
  });

  it('does not end a sentence at a title or an initial', () => {
    expect(sentence(text, 'lawyer')).toBe(
      'Mr. Utterson the lawyer was a man of a rugged countenance.',
    );
    expect(sentence('He met J. K. Jerome there. Then left.', 'Jerome')).toBe(
      'He met J. K. Jerome there.',
    );
  });

  it('reads through the line breaks a publisher left inside a paragraph', () => {
    // Project Gutenberg wraps its source at seventy characters.
    const wrapped =
      'This must be distinctly understood. If we were not perfectly convinced\n' +
      'that Hamlet’s Father died before the play began, there would be nothing\n' +
      'more remarkable in his taking a stroll at night, upon his own ramparts,\n' +
      'than there would be in any other gentleman. Scrooge never painted it out.';

    expect(sentence(wrapped, 'ramparts')).toBe(
      'If we were not perfectly convinced\nthat Hamlet’s Father died before the play began, ' +
        'there would be nothing\nmore remarkable in his taking a stroll at night, upon his own ' +
        'ramparts,\nthan there would be in any other gentleman.',
    );
  });

  it('ends a sentence at the mark in text written without spaces', () => {
    expect(sentence('我是猫。还没有名字。在哪里出生的也不清楚。', '名字')).toBe('还没有名字。');
  });

  it('cuts an unbroken run to whole words around the position', () => {
    const run = 'word '.repeat(600).trim();
    const span = sentenceSpanAt(run, 1500)!;
    const marked = run.slice(span.start, span.start + span.length);

    expect(span.length).toBeLessThanOrEqual(1200);
    expect(span.start).toBeLessThan(1500);
    expect(span.start + span.length).toBeGreaterThan(1500);
    expect(marked.startsWith('word')).toBe(true);
    expect(marked.endsWith('word')).toBe(true);
  });

  it('returns nothing for an empty text', () => {
    expect(sentenceSpanAt('', 3)).toBeNull();
    expect(sentenceSpanAt('   ', 2)).toBeNull();
  });
});

describe('sentenceSpanInResource (#844)', () => {
  function bookDocument(markup: string): Document {
    const doc = document.implementation.createHTMLDocument('EPUB');
    doc.body.innerHTML = markup;
    return doc;
  }

  const doc = bookDocument(`
    <h2>Stave One</h2>
    <p>Marley was dead: to begin with. There is no doubt
       whatever about <em>that</em>. The register was signed.</p>
    <p>Scrooge knew he was dead? Of course he did.</p>
  `);
  const text = normalizedEpubResourceText(doc);

  function marked(endOfWord: string): string | null {
    const span = sentenceSpanInResource(doc, text.indexOf(endOfWord) + endOfWord.length);
    if (!span) return null;
    return rangeForNormalizedResourceSpan(doc, span.start, span.length)?.toString() ?? null;
  }

  it('maps the sentence back to the page, through inline markup and wrapped lines', () => {
    expect(marked('no doubt')?.replace(/\s+/g, ' ')).toBe('There is no doubt whatever about that.');
  });

  it('keeps a heading out of the sentence below it', () => {
    expect(marked('Stave')).toBe('Stave One');
    expect(marked('Marley')).toBe('Marley was dead: to begin with.');
  });

  it('keeps one paragraph out of the next', () => {
    expect(marked('signed')).toBe('The register was signed.');
    expect(marked('Scrooge knew')).toBe('Scrooge knew he was dead?');
  });

  it('returns nothing past the end of the resource', () => {
    expect(sentenceSpanInResource(doc, text.length + 50)).toBeNull();
  });
});
