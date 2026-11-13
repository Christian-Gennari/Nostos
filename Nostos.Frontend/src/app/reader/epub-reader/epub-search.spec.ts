import type { Book, Contents } from 'epubjs';

import { EpubSearchController, findEpubSearchMatches } from './epub-search';

describe('findEpubSearchMatches', () => {
  it('escapes regex metacharacters and finds case-insensitive matches across sections', () => {
    const corpus = [
      { href: 'ch1.xhtml', index: 0, text: 'A (test) [1]? Yes, (TEST) [1]!' },
      { href: 'ch2.xhtml', index: 1, text: 'Another (Test) [1] here.' },
    ];

    const matches = findEpubSearchMatches(corpus, '(test) [1]');
    expect(matches).toEqual([
      { href: 'ch1.xhtml', index: 0, offset: 2, length: 10 },
      { href: 'ch1.xhtml', index: 0, offset: 19, length: 10 },
      { href: 'ch2.xhtml', index: 1, offset: 8, length: 10 },
    ]);
  });

  it('returns an empty list for an empty normalized query', () => {
    expect(findEpubSearchMatches([{ href: 'ch1.xhtml', index: 0, text: 'Hello' }], '')).toEqual([]);
  });
});

describe('EpubSearchController', () => {
  function makeDocument(html: string) {
    const doc = document.implementation.createHTMLDocument('Section');
    doc.body.innerHTML = html;
    return doc;
  }

  function createHarness() {
    const doc1 = makeDocument('<p>First <em>alpha</em> word. Alpha again.</p>');
    const doc2 = makeDocument('<p>No match here.</p>');
    const unload1 = vi.fn();
    const unload2 = vi.fn();

    let currentBook: Book | null = {
      ready: Promise.resolve(),
      load: vi.fn(),
      spine: {
        spineItems: [
          {
            href: 'ch1.xhtml',
            index: 0,
            linear: true,
            load: vi.fn(() => Promise.resolve(doc1.documentElement)),
            unload: unload1,
          },
          {
            href: 'non-linear.xhtml',
            index: 1,
            linear: false,
            load: vi.fn(() => Promise.resolve(doc2.documentElement)),
            unload: unload2,
          },
          {
            href: 'ch2.xhtml',
            index: 2,
            linear: true,
            load: vi.fn(() => Promise.resolve(doc2.documentElement)),
            unload: unload2,
          },
        ],
      },
    } as unknown as Book;

    const rendered = {
      document: doc1,
      section: { href: 'ch1.xhtml' },
      cfiFromRange: vi.fn(() => 'epubcfi(/6/2!/4/2:6,/4/2:11)'),
    } as unknown as Contents;

    const goToSource = vi.fn(() => Promise.resolve());
    const showSearchHighlight = vi.fn();
    const clearSearchHighlight = vi.fn();

    const controller = new EpubSearchController({
      book: () => currentBook,
      renderedContents: () => [rendered],
      contentMatchesHref: (c, href) => (c as unknown as { section?: { href?: string } }).section?.href === href,
      goToSource,
      showSearchHighlight,
      clearSearchHighlight,
    });

    return {
      controller,
      unload1,
      unload2,
      goToSource,
      showSearchHighlight,
      clearSearchHighlight,
      setBook: (book: Book | null) => {
        currentBook = book;
      },
    };
  }

  it('builds corpus from linear spine items, unloads sections, and cycles results', async () => {
    const h = createHarness();
    h.controller.setAvailable(true);

    await h.controller.search('alpha');

    expect(h.controller.state()).toEqual({ status: 'ready', current: 1, total: 2 });
    expect(h.unload1).toHaveBeenCalledTimes(1);
    expect(h.unload2).toHaveBeenCalledTimes(1);
    expect(h.goToSource).toHaveBeenLastCalledWith({
      type: 'epub',
      epubResourceHref: 'ch1.xhtml',
      epubSpineIndex: 0,
      epubTextOffset: 6,
    });
    expect(h.showSearchHighlight).toHaveBeenCalledWith('epubcfi(/6/2!/4/2:6,/4/2:11)');

    await h.controller.next();
    expect(h.controller.state()).toEqual({ status: 'ready', current: 2, total: 2 });

    await h.controller.next();
    expect(h.controller.state()).toEqual({ status: 'ready', current: 1, total: 2 });

    await h.controller.previous();
    expect(h.controller.state()).toEqual({ status: 'ready', current: 2, total: 2 });

    h.controller.clear();
    expect(h.controller.state()).toEqual({ status: 'idle', current: 0, total: 0 });
    expect(h.clearSearchHighlight).toHaveBeenCalled();
  });

  it('reports not-found when no matches exist and stays idle when search is unavailable', async () => {
    const h = createHarness();
    await h.controller.search('alpha');
    expect(h.controller.state()).toEqual({ status: 'idle', current: 0, total: 0 });

    h.controller.setAvailable(true);
    await h.controller.search('absent');
    expect(h.controller.state()).toEqual({ status: 'not-found', current: 0, total: 0 });
  });

  it('drops stale corpus loads if the book switches mid-read', async () => {
    const h = createHarness();
    let resolveReady!: () => void;
    const delayedBook = {
      ready: new Promise<void>((resolve) => {
        resolveReady = resolve;
      }),
      spine: { spineItems: [] },
    } as unknown as Book;
    h.setBook(delayedBook);
    h.controller.setAvailable(true);

    const searchPromise = h.controller.search('alpha');
    h.setBook(null);
    resolveReady();
    await searchPromise;

    expect(h.controller.state()).toEqual({ status: 'not-found', current: 0, total: 0 });
  });
});
