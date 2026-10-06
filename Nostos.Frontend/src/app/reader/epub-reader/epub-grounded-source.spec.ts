import {
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
