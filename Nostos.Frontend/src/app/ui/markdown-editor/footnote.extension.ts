import { Node, mergeAttributes } from '@tiptap/core';
import type { MarkedExtension } from 'marked';

/**
 * Markdown footnotes for note citation styles (issue #1109): `[^3]` in the
 * text and a `[^3]: …` paragraph at the end. The editor keeps each marker as
 * one small atom, so it can neither be half-deleted nor escaped to `\[^3\]` on
 * the next save, which is what plain text did. A definition is an ordinary
 * paragraph that starts with its marker.
 */
export const FOOTNOTE_ATTRIBUTE = 'data-footnote';

export const FootnoteRef = Node.create({
  name: 'footnoteRef',
  group: 'inline',
  inline: true,
  atom: true,
  selectable: true,

  addAttributes() {
    return {
      number: {
        default: 1,
        parseHTML: (element) => Number(element.getAttribute(FOOTNOTE_ATTRIBUTE)) || 1,
        renderHTML: (attributes) => ({ [FOOTNOTE_ATTRIBUTE]: String(attributes['number']) }),
      },
    };
  },

  parseHTML() {
    return [{ tag: `sup[${FOOTNOTE_ATTRIBUTE}]` }];
  },

  renderHTML({ node, HTMLAttributes }) {
    return ['sup', mergeAttributes(HTMLAttributes, { class: 'footnote-ref' }), String(node.attrs['number'])];
  },

  renderText({ node }) {
    return `[^${node.attrs['number']}]`;
  },
});

/**
 * `[^n]` in Markdown becomes the marker's HTML, wherever it stands in a line.
 * A `[^n]: …` line is claimed first as a definition paragraph: left to Marked
 * it reads as a link reference definition ("[^2]: Ibid." would vanish).
 */
export const footnoteMarkedExtension: MarkedExtension = {
  extensions: [
    {
      name: 'footnoteDefinition',
      level: 'block',
      start(src: string) {
        const match = /^\[\^\d{1,4}\]:/m.exec(src);
        return match ? match.index : undefined;
      },
      tokenizer(src: string) {
        const match = /^\[\^(\d{1,4})\]:[ \t]*([^\n]*)(?:\n|$)/.exec(src);
        if (!match) return undefined;
        return {
          type: 'footnoteDefinition',
          raw: match[0],
          number: Number(match[1]),
          tokens: this.lexer.inlineTokens(match[2]),
        };
      },
      renderer(token) {
        const number = Number(token['number']);
        return `<p><sup ${FOOTNOTE_ATTRIBUTE}="${number}">${number}</sup>: ${this.parser.parseInline(token.tokens ?? [])}</p>\n`;
      },
    },
    {
      name: 'footnoteRef',
      level: 'inline',
      start(src: string) {
        const index = src.indexOf('[^');
        return index < 0 ? undefined : index;
      },
      tokenizer(src: string) {
        const match = /^\[\^(\d{1,4})\]/.exec(src);
        return match ? { type: 'footnoteRef', raw: match[0], number: Number(match[1]) } : undefined;
      },
      renderer(token) {
        const number = Number(token['number']);
        return `<sup ${FOOTNOTE_ATTRIBUTE}="${number}">${number}</sup>`;
      },
    },
  ],
};

/** The numbers of the footnote markers in a Markdown document. */
export function footnoteNumbers(markdown: string): number[] {
  return [...(markdown ?? '').matchAll(/\[\^(\d{1,4})\]/g)].map((match) => Number(match[1]));
}
