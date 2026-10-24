import chicagoNotesCsl from '../../../../public/csl/styles/chicago-notes-bibliography.csl' with { loader: 'text' };
import apaCsl from '../../../../public/csl/styles/apa.csl' with { loader: 'text' };
import enUs from '../../../../public/csl/locales/locales-en-US.xml' with { loader: 'text' };
import enGb from '../../../../public/csl/locales/locales-en-GB.xml' with { loader: 'text' };

import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import { Book } from '../../core/dtos/book.dtos';
import { BooksService } from '../../core/services/books.service';
import { MarkdownEditorComponent } from '../../ui/markdown-editor/markdown-editor.component';
import { CitationService } from '../citations/citation.service';
import { CitationStyle, citationStyle } from '../citations/citation-styles';
import { StyledInsertion } from '../citations/styled-insertion';
import {
  STARTER_DRAFT_TITLE,
  StarterPassage,
  answerStarterOffer,
  composeStarterDraft,
  starterDraftPrompt,
  starterDraftTitle,
  starterOfferAnswered,
} from './starter-draft';

const walden = { id: 'b1', title: 'Walden', subtitle: null, author: 'Henry David Thoreau', editor: null, translator: null, publisher: 'Ticknor and Fields', placeOfPublication: 'Boston', publishedDate: '1854', edition: null } as unknown as Book;
const emma = { id: 'b2', title: 'Emma', subtitle: null, author: 'Jane Austen', editor: null, translator: null, publisher: 'John Murray', placeOfPublication: 'London', publishedDate: '1815', edition: null } as unknown as Book;

const passage = (overrides: Partial<StarterPassage>): StarterPassage => ({
  id: 'n1',
  bookId: 'b1',
  bookTitle: 'Walden',
  bookAuthor: 'Henry David Thoreau',
  selectedText: 'I went to the woods because I wished to live deliberately.',
  content: '',
  sourceAnchorKind: 'kindle_location',
  sourceAnchorValue: '120-121',
  anchorVerified: false,
  ...overrides,
});

const woods = passage({ id: 'n1', content: 'Why I keep coming back to this.' });
const marrow = passage({ id: 'n2', selectedText: 'I wanted to live deep and suck out all the marrow of life.', sourceAnchorKind: 'pdf_page', sourceAnchorValue: '91', anchorVerified: true });
const handsome = passage({ id: 'n3', bookId: 'b2', bookTitle: 'Emma', bookAuthor: 'Jane Austen', selectedText: 'Emma Woodhouse, handsome, clever, and rich,\nwith a comfortable home and happy disposition.', content: 'The first sentence\ndoes all the work.' });

describe('starter draft (issue #1220): its name and its one line', () => {
  it('is named for the book when every passage is from one, and "First draft" otherwise', () => {
    expect(starterDraftTitle([woods, marrow])).toBe('Walden');
    expect(starterDraftTitle([woods, handsome])).toBe(STARTER_DRAFT_TITLE);
    expect(starterDraftTitle([passage({ bookTitle: '  ' })])).toBe(STARTER_DRAFT_TITLE);
  });

  it('says what is below and where to write, and nothing more', () => {
    expect(starterDraftPrompt(3)).toBe('Three passages you highlighted, with their sources. Start writing below them.');
    expect(starterDraftPrompt(2)).toBe('Two passages you highlighted, with their sources. Start writing below them.');
    expect(starterDraftPrompt(1)).toBe('A passage you highlighted, with its source. Start writing below it.');
  });

  it('remembers that the empty Studio\'s offer was answered', () => {
    localStorage.removeItem('nostos.studio.starterDraftOffer');
    expect(starterOfferAnswered()).toBe(false);
    answerStarterOffer('declined');
    expect(starterOfferAnswered()).toBe(true);
    localStorage.removeItem('nostos.studio.starterDraftOffer');
  });
});

describe('starter draft (issue #1220): composing it', () => {
  const house = citationStyle('nostos');
  const quoteAs = (cite: (p: StarterPassage) => string | null) =>
    vi.fn(async (p: StarterPassage): Promise<StyledInsertion | null> => {
      const text = cite(p);
      return text ? { markdown: `> ${p.selectedText}\n>\n> ${text}`, footnote: null } : null;
    });

  it('puts the prompt first, each passage as a quote, and the reader\'s note under it', async () => {
    const quote = quoteAs((p) => `[— ${p.bookTitle}](nostos:cite/${p.bookId}?as=quote&src=${p.id})`);
    const markdown = await composeStarterDraft([woods, handsome], house, quote);

    expect(markdown).toBe(
      [
        'Two passages you highlighted, with their sources. Start writing below them.',
        '> I went to the woods because I wished to live deliberately.\n>\n> [— Walden](nostos:cite/b1?as=quote&src=n1)',
        'Why I keep coming back to this.',
        '> Emma Woodhouse, handsome, clever, and rich,\nwith a comfortable home and happy disposition.\n>\n> [— Emma](nostos:cite/b2?as=quote&src=n3)',
        'The first sentence\ndoes all the work.',
      ].join('\n\n'),
    );
  });

  it('falls back to the house text the palette uses when a book cannot be cited', async () => {
    const markdown = await composeStarterDraft([woods], house, quoteAs(() => null));
    expect(markdown).toContain('> I went to the woods because I wished to live deliberately.\n>\n> — Henry David Thoreau, *Walden*');
  });

  it('numbers a note style\'s footnotes in order and cites a book short the second time', async () => {
    const chicago = citationStyle('chicago-notes');
    const quote = vi.fn(async (p: StarterPassage, subsequent: boolean, footnote: number): Promise<StyledInsertion> => ({
      markdown: `> ${p.selectedText}[^${footnote}]`,
      footnote: { number: footnote, text: `${subsequent ? 'Short' : 'Full'} ${p.bookTitle}.` },
    }));

    const markdown = await composeStarterDraft([woods, marrow, handsome], chicago, quote);

    expect(quote.mock.calls.map(([p, subsequent, footnote]) => [p.id, subsequent, footnote])).toEqual([
      ['n1', false, 1],
      ['n2', true, 2],
      ['n3', false, 3],
    ]);
    expect(markdown.endsWith('[^1]: Full Walden.\n\n[^2]: Short Walden.\n\n[^3]: Full Emma.')).toBe(true);
  });
});

/**
 * The draft must be what the `@` palette would have made had the reader
 * quoted the same three passages themselves (issue #1220): the same live
 * citations, footnotes and Markdown, so bibliography, style changes and
 * export treat it like any writing.
 */
describe('starter draft (issue #1220): the same writing a real @-quote makes', () => {
  const files: Record<string, string> = {
    '/csl/styles/chicago-notes-bibliography.csl': chicagoNotesCsl,
    '/csl/styles/apa.csl': apaCsl,
    '/csl/locales/locales-en-US.xml': enUs,
    '/csl/locales/locales-en-GB.xml': enGb,
  };
  let realFetch: typeof fetch;
  let service: CitationService;

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

  beforeEach(() => {
    const books: Record<string, Book> = { b1: walden, b2: emma };
    TestBed.configureTestingModule({
      imports: [MarkdownEditorComponent],
      providers: [
        { provide: BooksService, useValue: { get: vi.fn((id: string) => (books[id] ? of(books[id]) : throwError(() => new Error('gone')))) } },
      ],
    });
    service = TestBed.inject(CitationService);
  });

  async function editorWith(markdown: string) {
    const fixture = TestBed.createComponent(MarkdownEditorComponent);
    fixture.componentRef.setInput('initialContent', markdown);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  /** What the Studio does for each passage chosen from the `@` palette, then the note typed under it. */
  async function quotedByHand(passages: StarterPassage[], style: CitationStyle): Promise<string> {
    const fixture = await editorWith(starterDraftPrompt(passages.length));
    const editor = fixture.componentInstance;
    await editor.restoreTransientState({ at: 'body-end' }, starterDraftPrompt(passages.length));
    for (const p of passages) {
      const subsequent = style.kind === 'note' && editor.citedBooks().includes(p.bookId);
      const styled = await service.insertion(p, 'quote', style, subsequent, editor.nextFootnoteNumber());
      expect(styled).not.toBeNull();
      const footnote = styled!.footnote ? { number: styled!.footnote.number, markdown: styled!.footnote.text } : null;
      expect(editor.insertBlockMarkdown(styled!.markdown, footnote)).toBe(true);
      if (p.content?.trim()) expect(editor.insertBlockMarkdown(p.content)).toBe(true);
    }
    const markdown = editor.currentMarkdown().trim();
    fixture.destroy();
    return markdown;
  }

  async function opened(markdown: string): Promise<string> {
    const fixture = await editorWith(markdown);
    const out = fixture.componentInstance.currentMarkdown().trim();
    fixture.destroy();
    return out;
  }

  for (const id of ['nostos', 'apa', 'chicago-notes'] as const) {
    it(`${id}: the composed draft is the writing the palette makes`, async () => {
      const style = citationStyle(id);
      const passages = [woods, marrow, handsome];
      const composed = await composeStarterDraft(passages, style, (p, subsequent, footnote) =>
        service.insertion(p, 'quote', style, subsequent, footnote),
      );

      // Every passage went in as a live citation, none as the fallback text.
      expect(composed.match(/\(nostos:cite\//g)?.length).toBe(3);
      const byHand = await quotedByHand(passages, style);
      expect(await opened(composed)).toBe(byHand);
      // Each passage is a live citation of its own note, with its page only when Nostos verified one.
      expect(composed).toContain('nostos:cite/b1?as=quote&src=n1');
      expect(composed).toContain('nostos:cite/b1?p=91&as=quote&src=n2');
      expect(composed).toContain('nostos:cite/b2?as=quote&src=n3');
      expect(composed).not.toContain('120');
    });
  }
});
