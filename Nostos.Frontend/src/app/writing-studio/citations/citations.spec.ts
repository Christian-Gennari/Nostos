import apaCsl from '../../../../public/csl/styles/apa.csl' with { loader: 'text' };
import harvardCsl from '../../../../public/csl/styles/harvard-cite-them-right.csl' with { loader: 'text' };
import chicagoAuthorDateCsl from '../../../../public/csl/styles/chicago-author-date.csl' with { loader: 'text' };
import chicagoNotesCsl from '../../../../public/csl/styles/chicago-notes-bibliography.csl' with { loader: 'text' };
import oxfordNotesCsl from '../../../../public/csl/styles/oxford-guide-to-style-notes.csl' with { loader: 'text' };
import mlaCsl from '../../../../public/csl/styles/modern-language-association.csl' with { loader: 'text' };
import enUs from '../../../../public/csl/locales/locales-en-US.xml' with { loader: 'text' };
import enGb from '../../../../public/csl/locales/locales-en-GB.xml' with { loader: 'text' };

import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import { Book } from '../../core/dtos/book.dtos';
import { BooksService } from '../../core/services/books.service';
import { MarkdownEditorComponent } from '../../ui/markdown-editor/markdown-editor.component';
import { CitationService, houseText } from './citation.service';
import { htmlToMarkdown, markdownToHtml } from '../../ui/markdown-editor/markdown-conversion';
import { CitationEngine, cslHtmlToMarkdown } from './citation-engine';
import { CITATION_STYLES, citationStyle } from './citation-styles';
import { bookToCslItem, missingForStyle, missingPhrase, parseNames, publishedYear } from './csl-item';
import { bibliographyMarkdown, styledNote, styledQuote, styledReference } from './styled-insertion';

function book(overrides: Partial<Book> = {}): Book {
  return {
    id: 'b1',
    title: 'The World as Will and Representation',
    subtitle: null,
    author: 'Arthur Schopenhauer',
    editor: null,
    translator: 'E. F. J. Payne',
    publisher: 'Dover',
    placeOfPublication: 'New York',
    publishedDate: '1851',
    edition: null,
    ...overrides,
  } as Book;
}

describe('citation styles (issue #1109): book metadata as CSL', () => {
  it('reads names as Nostos stores them', () => {
    expect(parseNames('Arthur Schopenhauer')).toEqual([{ family: 'Schopenhauer', given: 'Arthur' }]);
    expect(parseNames('Schopenhauer, Arthur')).toEqual([{ family: 'Schopenhauer', given: 'Arthur' }]);
    expect(parseNames('Gilles Deleuze & Félix Guattari')).toEqual([
      { family: 'Deleuze', given: 'Gilles' },
      { family: 'Guattari', given: 'Félix' },
    ]);
    expect(parseNames('Homer')).toEqual([{ literal: 'Homer' }]);
    expect(parseNames('  ')).toBeUndefined();
  });

  it('takes the year from any published date', () => {
    expect(publishedYear('1851')).toBe(1851);
    expect(publishedYear('2006-03-01')).toBe(2006);
    expect(publishedYear('March 2006')).toBe(2006);
    expect(publishedYear(null)).toBeNull();
  });

  it('maps a book, and says what a style will miss', () => {
    const item = bookToCslItem(book({ subtitle: 'Volume I' }));
    expect(item).toMatchObject({
      id: 'b1',
      type: 'book',
      title: 'The World as Will and Representation: Volume I',
      issued: { 'date-parts': [[1851]] },
      publisher: 'Dover',
      'publisher-place': 'New York',
    });
    expect(missingForStyle(item)).toEqual([]);
    expect(missingForStyle(bookToCslItem(book({ publishedDate: null, publisher: null })))).toEqual(['year', 'publisher']);
  });
});

describe('citation styles (issue #1109): Markdown it produces', () => {
  const apa = citationStyle('apa');
  const chicagoNotes = citationStyle('chicago-notes');

  it('keeps CSL italics and escapes everything else', () => {
    expect(cslHtmlToMarkdown('Schopenhauer, A. (1851). <i>The World</i> (E. F. J. Payne, Trans.). Dover.')).toBe(
      'Schopenhauer, A. (1851). *The World* (E. F. J. Payne, Trans.). Dover.',
    );
    expect(cslHtmlToMarkdown('<div class="csl-entry">A_b &amp; [c]</div>')).toBe('A\\_b & \\[c\\]');
  });

  it('closes a quote with an in-text citation, or a footnote marker in a note style', () => {
    expect(styledQuote('Every act.\nOf will.', '(Schopenhauer, 1851, p. 42)', apa, 1)).toEqual({
      markdown: '> Every act.\n> Of will.\n>\n> (Schopenhauer, 1851, p. 42)',
      footnote: null,
    });
    expect(styledQuote('Every act.', 'Arthur Schopenhauer, *The World*, 42.', chicagoNotes, 3)).toEqual({
      markdown: '> Every act.[^3]',
      footnote: { number: 3, text: 'Arthur Schopenhauer, *The World*, 42.' },
    });
    expect(styledQuote('  ', '(x)', apa, 1)).toBeNull();
  });

  it('cites a note after its text and a reference in the sentence', () => {
    expect(styledNote('My thought.', '(Schopenhauer, 1851)', apa, 1)?.markdown).toBe('My thought. (Schopenhauer, 1851)');
    expect(styledNote('My thought.', 'Note.', chicagoNotes, 2)?.markdown).toBe('My thought.[^2]');
    expect(styledReference('(Schopenhauer, 1851)', apa, 1)).toEqual({ markdown: '(Schopenhauer, 1851)', footnote: null });
    expect(styledReference('Note.', chicagoNotes, 4).markdown).toBe('[^4]');
  });

  it('heads a bibliography the way each style names it', () => {
    expect(bibliographyMarkdown(['A.', 'B.'], citationStyle('mla'))).toBe('## Works Cited\n\nA.\n\nB.');
    expect(bibliographyMarkdown(['A.'], apa)).toBe('## References\n\nA.');
    expect(bibliographyMarkdown(['A.'], chicagoNotes)).toBe('## Bibliography\n\nA.');
    expect(bibliographyMarkdown([], apa)).toBeNull();
  });

  it('keeps footnote markers and definitions through the editor and back', () => {
    const markdown = 'A claim.[^1] Another.[^2]\n\n[^1]: Ibid.\n\n[^2]: Arthur Schopenhauer, _The World_, 42.';
    const html = markdownToHtml(markdown);
    expect(html).toContain('<sup data-footnote="1">1</sup>');
    expect(html).toContain('<p><sup data-footnote="1">1</sup>: Ibid.</p>');
    // Turndown writes italics as underscores, as for every italic in the Studio.
    expect(htmlToMarkdown(html)).toBe(markdown.replace('*The World*', '_The World_'));
  });

  it('leaves text that only looks like a footnote alone', () => {
    expect(htmlToMarkdown(markdownToHtml('Arrays like a[^x] stay text.'))).toBe('Arrays like a\\[^x\\] stay text.');
  });
});

describe('citation styles (issue #1109): the bundled CSL styles format a real book', () => {
  // The files the app serves under /csl/, bundled into the spec as text.
  const files: Record<string, string> = {
    '/csl/styles/apa.csl': apaCsl,
    '/csl/styles/harvard-cite-them-right.csl': harvardCsl,
    '/csl/styles/chicago-author-date.csl': chicagoAuthorDateCsl,
    '/csl/styles/chicago-notes-bibliography.csl': chicagoNotesCsl,
    '/csl/styles/oxford-guide-to-style-notes.csl': oxfordNotesCsl,
    '/csl/styles/modern-language-association.csl': mlaCsl,
    '/csl/locales/locales-en-US.xml': enUs,
    '/csl/locales/locales-en-GB.xml': enGb,
  };
  let realFetch: typeof fetch;

  beforeAll(() => {
    realFetch = globalThis.fetch;
    globalThis.fetch = (async (url: string) => {
      const text = files[String(url)];
      return { ok: text !== undefined, text: async () => text } as Response;
    }) as typeof fetch;
  });

  afterAll(() => {
    globalThis.fetch = realFetch;
  });

  const item = bookToCslItem(book());
  const expected: Record<string, { first: string; later?: string; entry: string }> = {
    apa: { first: '(Schopenhauer, 1851, p. 42)', entry: 'Schopenhauer, A. (1851). *The World as Will and Representation* (E. F. J. Payne, Trans.). Dover.' },
    harvard: { first: '(Schopenhauer, 1851, p. 42)', entry: 'Schopenhauer, A. (1851) *The World as Will and Representation*. Translated by E.F.J. Payne. New York: Dover.' },
    'chicago-author-date': { first: '(Schopenhauer 1851, 42)', entry: 'Schopenhauer, Arthur. 1851. *The World as Will and Representation*. Translated by E. F. J. Payne. Dover.' },
    'chicago-notes': {
      first: 'Arthur Schopenhauer, *The World as Will and Representation*, trans. E. F. J. Payne (Dover, 1851), 42.',
      later: 'Schopenhauer, *The World as Will and Representation*, 42.',
      entry: 'Schopenhauer, Arthur. *The World as Will and Representation*. Translated by E. F. J. Payne. Dover, 1851.',
    },
    'oxford-notes': {
      first: 'Arthur Schopenhauer, *The World as Will and Representation*, trans. E. F. J. Payne (New York, 1851), 42.',
      later: 'Schopenhauer, *The World as Will and Representation*, 42.',
      entry: 'Schopenhauer, A., *The World as Will and Representation*, trans. E. F. J. Payne (New York, 1851).',
    },
    mla: { first: '(Schopenhauer 42)', entry: 'Schopenhauer, Arthur. *The World as Will and Representation*. Translated by E. F. J. Payne, Dover, 1851.' },
  };

  for (const style of CITATION_STYLES.filter((s) => s.file)) {
    it(`${style.label}: citation, later citation and reference entry`, async () => {
      const engine = await CitationEngine.create(style);
      const want = expected[style.id];
      expect(engine.cite(item, { page: 42 })).toBe(want.first);
      if (want.later) expect(engine.cite(item, { page: 42 }, true)).toBe(want.later);
      expect(engine.bibliography([item])).toEqual([want.entry]);
    });
  }

  it('never invents a page: no locator, no page', async () => {
    const engine = await CitationEngine.create(citationStyle('apa'));
    expect(engine.cite(item, null)).toBe('(Schopenhauer, 1851)');
  });

  it('says which book each reference entry is for, in the style’s order (#1157)', async () => {
    const engine = await CitationEngine.create(citationStyle('apa'));
    const weil = bookToCslItem(book({ id: 'b2', author: 'Simone Weil', title: 'Gravity and Grace', publishedDate: '1947' }));
    expect(engine.bibliographyEntries([weil, item]).map((entry) => entry.id)).toEqual(['b1', 'b2']);
  });

  describe('live citations through the real styles (#1157)', () => {
    const candide = { id: 'c1', title: 'Candide', subtitle: null, author: 'Voltaire', editor: null, translator: null, publisher: 'Cramer', placeOfPublication: null, publishedDate: null, edition: null } as unknown as Book;
    let service: CitationService;

    beforeEach(() => {
      TestBed.configureTestingModule({
        imports: [MarkdownEditorComponent],
        providers: [{ provide: BooksService, useValue: { get: vi.fn((id: string) => (id === 'c1' ? of(candide) : throwError(() => new Error('gone')))) } }],
      });
      service = TestBed.inject(CitationService);
    });

    it('cites a book with no year as the style does, and with its year once it has one', async () => {
      const ref = { book: 'c1', page: 12, src: null, as: 'quote' as const };
      expect((await service.renderer(citationStyle('apa'), ['c1'], [])).inText(ref)).toBe('(Voltaire, n.d., p. 12)');

      service.updated({ ...candide, publishedDate: '1759' });
      const renderer = await service.renderer(citationStyle('apa'), ['c1'], []);
      expect(renderer.inText(ref)).toBe('(Voltaire, 1759, p. 12)');
      expect(renderer.bibliography(['c1'])).toEqual([{ book: 'c1', text: 'Voltaire. (1759). *Candide*. Cramer.' }]);
      // A book that cannot be read leaves the list as it is.
      expect(renderer.bibliography(['c1', 'gone'])).toBeNull();
      expect(renderer.inText({ ...ref, book: 'gone' })).toBeNull();
    });

    it('moves an APA citation into a Chicago footnote in a real writing, and back', async () => {
      service.updated({ ...candide, publishedDate: '1759' });
      const fixture = TestBed.createComponent(MarkdownEditorComponent);
      const start = 'Optimism [(Voltaire, n.d.)](nostos:cite/c1?as=reference) is mocked.\n\n## References\n\n[Voltaire. (n.d.). *Candide*. Cramer.](nostos:cite/c1?as=bib)';
      fixture.componentRef.setInput('initialContent', start);
      fixture.detectChanges();
      await fixture.whenStable();
      const editor = fixture.componentInstance;

      editor.refreshCitations(await service.renderer(citationStyle('apa'), editor.citedBooks(), []));
      expect(editor.currentMarkdown().trim()).toBe(
        'Optimism [(Voltaire, 1759)](nostos:cite/c1?as=reference) is mocked.\n\n## References\n\n[Voltaire. (1759). *Candide*. Cramer.](nostos:cite/c1?as=bib)',
      );

      editor.refreshCitations(await service.renderer(citationStyle('chicago-notes'), editor.citedBooks(), []));
      expect(editor.currentMarkdown().trim()).toBe(
        [
          'Optimism[^1] is mocked.',
          '[^1]: [Voltaire, *Candide* (Cramer, 1759).](nostos:cite/c1?as=reference)',
          '## Bibliography',
          '[Voltaire. *Candide*. Cramer, 1759.](nostos:cite/c1?as=bib)',
        ].join('\n\n'),
      );

      editor.refreshCitations(await service.renderer(citationStyle('apa'), editor.citedBooks(), []));
      expect(editor.currentMarkdown().trim()).toContain('Optimism [(Voltaire, 1759)](nostos:cite/c1?as=reference) is mocked.');
      fixture.destroy();
    });
  });
});

describe('the house format and missing details (#1157)', () => {
  const house = citationStyle('nostos');
  const voltaire = { id: 'b1', title: 'Candide', author: 'Voltaire' } as Book;

  it('writes the house format from the book, framed by where it stands', () => {
    expect(houseText(voltaire, { book: 'b1', page: 4, src: null, as: 'quote' }, null)).toBe('— Voltaire, *Candide*, p. 4');
    expect(houseText(voltaire, { book: 'b1', page: null, src: null, as: 'note' }, null)).toBe('*Source: Voltaire, Candide*');
    expect(houseText(voltaire, { book: 'b1', page: 4, src: 'p1', as: 'reference' }, 'On Hope')).toBe('*See: Voltaire, Candide, “On Hope”*');
    expect(houseText({ ...voltaire, author: null, title: 'A [b]' } as Book, { book: 'b1', page: null, src: null, as: 'quote' }, null)).toBe('— *A \\[b\\]*');
  });

  it('asks the house format only for an author, and names what is missing', () => {
    const bare = bookToCslItem(book({ author: null, publishedDate: null, publisher: null }));
    expect(missingForStyle(bare, house)).toEqual(['author']);
    expect(missingForStyle(bare, citationStyle('mla'))).toEqual(['author', 'year', 'publisher']);
    expect(missingPhrase(['year'])).toBe('year');
    expect(missingPhrase(['year', 'publisher'])).toBe('year and publisher');
    expect(missingPhrase(['author', 'year', 'publisher'])).toBe('author, year and publisher');
  });
});
