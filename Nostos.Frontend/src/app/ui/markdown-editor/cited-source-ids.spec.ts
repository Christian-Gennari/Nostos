import { citedSourceIds } from './citation.extension';

// Issue #1162: the Studio reads which kept sources a writing cites from the
// editor's current document, by the same rules as the server's index.
describe('citedSourceIds', () => {
  const book = '11111111-1111-1111-1111-111111111111';
  const a = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
  const b = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
  const cite = (text: string, query: string) => `[${text}](nostos:cite/${book}${query})`;

  it('reads each cited note once, in the order it first appears', () => {
    const markdown = [
      cite('(Leibniz)', `?as=quote&src=${b}`),
      cite('(Voltaire, p. 1)', `?p=1&src=${a}`),
      cite('(Voltaire, p. 2)', `?p=2&as=note&src=${a.toUpperCase()}`),
    ].join(' ');

    expect(citedSourceIds(markdown)).toEqual([b, a]);
  });

  it('ignores malformed links, invalid ids and sources from no library', () => {
    const markdown = [
      `Half deleted [(Voltaire](nostos:cite/${book}?src=${a}`,
      `nostos:cite/${book}?src=${a}`,
      cite('(Odd)', '?src=not-a-guid'),
      cite('(Empty)', '?src=00000000-0000-0000-0000-000000000000'),
      '[(Broken)](nostos:cite/??src=x)',
    ].join('\n\n');

    expect(citedSourceIds(markdown)).toEqual([]);
  });

  it('never counts a book-only citation or a bibliography entry', () => {
    const markdown = `${cite('(Voltaire, 1759)', '?p=3')}\n\n[^1]: ${cite('Voltaire, *Candide*.', `?as=bib&src=${a}`)}`;

    expect(citedSourceIds(markdown)).toEqual([]);
  });

  it('skips citations shown as code', () => {
    const markdown = ['```', cite('(Fenced)', `?src=${a}`), '```', `\`${cite('(Inline)', `?src=${a}`)}\``, cite('(Real)', `?src=${b}`)].join('\n');

    expect(citedSourceIds(markdown)).toEqual([b]);
  });

  it('cites nothing in empty or citation-free text', () => {
    expect(citedSourceIds('')).toEqual([]);
    expect(citedSourceIds(null)).toEqual([]);
    expect(citedSourceIds('Only [[Optimism]] and [a link](https://example.com).')).toEqual([]);
  });
});
