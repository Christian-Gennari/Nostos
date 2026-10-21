import { Node } from '@tiptap/core';
import type { DOMOutputSpec } from '@tiptap/pm/model';
import type { MarkedExtension } from 'marked';

/**
 * A live citation (issue #1157): the words a citation style gives a source,
 * kept together with the book (and page) they came from, so the Studio can
 * write them again when the book's details or the style change.
 *
 * In Markdown it is an ordinary link to a `nostos:cite/` address, so the text
 * reads as written anywhere else:
 *
 *   [(Voltaire, 1992, p. 42)](nostos:cite/<book id>?p=42)
 *   [^1]: [Voltaire, *Candide* (Knopf, 1992), 42.](nostos:cite/<book id>?p=42)
 *
 * In the editor it is one atom, like a footnote marker: it can be deleted or
 * moved whole, never half-edited into something the Studio would overwrite.
 */

/** What a citation points at. `as` is the shape it was inserted in. */
export type CitationRole = 'quote' | 'note' | 'reference' | 'bib';

export interface CitationRef {
  book: string;
  /** A page Nostos verified; never invented. */
  page: number | null;
  /** The kept source (note or passage) it was inserted from. */
  src: string | null;
  as: CitationRole | null;
}

export interface CitationAttrs extends CitationRef {
  /** The text as last written, in the Studio's Markdown. */
  text: string;
}

export const CITATION_SCHEME = 'nostos:cite/';
const ROLES: readonly CitationRole[] = ['quote', 'note', 'reference', 'bib'];

function cleanId(value: string | null | undefined): string | null {
  const id = (value ?? '').trim();
  return /^[A-Za-z0-9_-]{1,64}$/.test(id) ? id : null;
}

/** `nostos:cite/<book>?p=42&as=quote&src=<id>` → its parts, or null for any other address. */
export function parseCitationHref(href: string | null | undefined): CitationRef | null {
  const value = (href ?? '').trim();
  if (!value.startsWith(CITATION_SCHEME)) return null;
  const [path, query = ''] = value.slice(CITATION_SCHEME.length).split('?', 2);
  const book = cleanId(path);
  if (!book) return null;
  const params = new URLSearchParams(query);
  const page = Number(params.get('p'));
  const as = params.get('as') as CitationRole | null;
  return {
    book,
    page: Number.isInteger(page) && page > 0 ? page : null,
    src: cleanId(params.get('src')),
    as: as && ROLES.includes(as) ? as : null,
  };
}

export function citationHref(ref: CitationRef): string {
  const params: string[] = [];
  if (ref.page) params.push(`p=${ref.page}`);
  if (ref.as) params.push(`as=${ref.as}`);
  if (ref.src) params.push(`src=${ref.src}`);
  return `${CITATION_SCHEME}${ref.book}${params.length ? `?${params.join('&')}` : ''}`;
}

/** The Markdown for a live citation whose text is already Markdown. */
export function citationMarkdown(text: string, ref: CitationRef): string {
  return `[${text.trim()}](${citationHref(ref)})`;
}

/** The citation's Markdown text as plain words: emphasis and escapes off. */
export function citationPlainText(markdown: string): string {
  return (markdown ?? '').replace(/\\([\\*_`[\]])|[*_]/g, (_, escaped) => escaped ?? '');
}

/**
 * The little Markdown a citation holds (italic, bold, escapes) as DOM: the
 * editor draws the atom from its text attribute, not from parsed HTML.
 */
export function citationTextSpec(markdown: string): (DOMOutputSpec | string)[] {
  const out: (DOMOutputSpec | string)[] = [];
  const stack: { tag: string; children: (DOMOutputSpec | string)[] }[] = [];
  const target = () => (stack.length ? stack[stack.length - 1].children : out);
  let text = '';
  const flush = () => {
    if (text) target().push(text);
    text = '';
  };
  const source = markdown ?? '';
  for (let i = 0; i < source.length; i++) {
    const char = source[i];
    if (char === '\\' && i + 1 < source.length && /[\\*_`[\]]/.test(source[i + 1])) {
      text += source[++i];
      continue;
    }
    if (char === '*') {
      const tag = source[i + 1] === '*' ? 'strong' : 'em';
      if (tag === 'strong') i++;
      flush();
      if (stack.length && stack[stack.length - 1].tag === tag) {
        const done = stack.pop()!;
        target().push([done.tag, {}, ...done.children] as DOMOutputSpec);
      } else {
        stack.push({ tag, children: [] });
      }
      continue;
    }
    text += char;
  }
  flush();
  // An unclosed marker stays as the text it was.
  while (stack.length) {
    const open = stack.pop()!;
    target().push(open.tag === 'strong' ? '**' : '*', ...open.children);
  }
  return out;
}

const ATTR = {
  book: 'data-cite-book',
  page: 'data-cite-page',
  src: 'data-cite-src',
  as: 'data-cite-as',
  text: 'data-cite-text',
} as const;

export function escapeAttribute(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** The citation's HTML attributes, shared by the editor and the Markdown parser. */
function citationAttributes(attrs: CitationAttrs): Record<string, string> {
  const out: Record<string, string> = { [ATTR.book]: attrs.book, [ATTR.text]: attrs.text, class: 'citation' };
  if (attrs.page) out[ATTR.page] = String(attrs.page);
  if (attrs.src) out[ATTR.src] = attrs.src;
  if (attrs.as) out[ATTR.as] = attrs.as;
  return out;
}

/** Reads a `<cite>` the editor or the Markdown parser wrote back into its attributes. */
export function citationFromElement(element: HTMLElement): CitationAttrs | null {
  const ref = parseCitationHref(
    `${CITATION_SCHEME}${element.getAttribute(ATTR.book) ?? ''}?${new URLSearchParams({
      p: element.getAttribute(ATTR.page) ?? '',
      as: element.getAttribute(ATTR.as) ?? '',
      src: element.getAttribute(ATTR.src) ?? '',
    }).toString()}`,
  );
  if (!ref) return null;
  return { ...ref, text: element.getAttribute(ATTR.text) ?? element.textContent ?? '' };
}

export const Citation = Node.create({
  name: 'citation',
  group: 'inline',
  inline: true,
  atom: true,
  selectable: true,
  draggable: false,

  addAttributes() {
    const read = (element: HTMLElement) => citationFromElement(element);
    return {
      book: { default: '', parseHTML: (element) => read(element)?.book ?? '', renderHTML: () => ({}) },
      page: { default: null, parseHTML: (element) => read(element)?.page ?? null, renderHTML: () => ({}) },
      src: { default: null, parseHTML: (element) => read(element)?.src ?? null, renderHTML: () => ({}) },
      as: { default: null, parseHTML: (element) => read(element)?.as ?? null, renderHTML: () => ({}) },
      text: { default: '', parseHTML: (element) => read(element)?.text ?? '', renderHTML: () => ({}) },
    };
  },

  parseHTML() {
    return [{ tag: `cite[${ATTR.book}]`, getAttrs: (element) => (citationFromElement(element as HTMLElement) ? null : false) }];
  },

  renderHTML({ node }) {
    const attrs = node.attrs as CitationAttrs;
    return [
      'cite',
      { ...citationAttributes(attrs), title: 'Follows the book’s details and your citation style' },
      ...citationTextSpec(attrs.text),
    ] as DOMOutputSpec;
  },

  renderText({ node }) {
    return citationPlainText((node.attrs as CitationAttrs).text);
  },
});

const CITATION_LINK = /^\[((?:\\.|[^\\\]])*)\]\((nostos:cite\/[^)\s]+)\)/;

/** `[text](nostos:cite/…)` in Markdown becomes the citation's `<cite>`, not a link. */
export const citationMarkedExtension: MarkedExtension = {
  extensions: [
    {
      name: 'citation',
      level: 'inline',
      start(src: string) {
        const match = /\[(?:\\.|[^\\\]])*\]\(nostos:cite\//.exec(src);
        return match ? match.index : undefined;
      },
      tokenizer(src: string) {
        const match = CITATION_LINK.exec(src);
        if (!match) return undefined;
        const ref = parseCitationHref(match[2]);
        if (!ref) return undefined;
        return { type: 'citation', raw: match[0], ref, text: match[1], tokens: this.lexer.inlineTokens(match[1]) };
      },
      renderer(token) {
        const attrs = { ...(token['ref'] as CitationRef), text: String(token['text']) };
        const html = Object.entries(citationAttributes(attrs))
          .map(([name, value]) => `${name}="${escapeAttribute(value)}"`)
          .join(' ');
        return `<cite ${html}>${this.parser.parseInline(token.tokens ?? [])}</cite>`;
      },
    },
  ],
};

/** The Markdown a `<cite>` the editor wrote goes back to, or null for any other `<cite>`. */
export function citationElementMarkdown(element: HTMLElement): string | null {
  const attrs = citationFromElement(element);
  return attrs ? citationMarkdown(attrs.text, attrs) : null;
}
