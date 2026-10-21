import { Node, mergeAttributes } from '@tiptap/core';
import { Plugin, PluginKey } from '@tiptap/pm/state';
import type { EditorState, Transaction } from '@tiptap/pm/state';
import type { Node as PMNode } from '@tiptap/pm/model';
import type { MarkedExtension } from 'marked';

/**
 * Markdown footnotes for note citation styles (issue #1109): `[^3]` in the
 * text and a `[^3]: …` paragraph at the end. The editor keeps each marker as
 * one small atom, so it can neither be half-deleted nor escaped to `\[^3\]` on
 * the next save, which is what plain text did. A definition is an ordinary
 * paragraph that starts with its marker.
 */
export const FOOTNOTE_ATTRIBUTE = 'data-footnote';

export interface FootnoteRefOptions {
  /** True while an insertion puts a marker and its note in, so the two are tidied together. */
  paused: () => boolean;
}

export const FootnoteRef = Node.create<FootnoteRefOptions>({
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

  addOptions() {
    return { paused: () => false };
  },

  addProseMirrorPlugins() {
    const paused = () => this.options.paused();
    return [
      new Plugin({
        key: new PluginKey('footnoteHousekeeping'),
        appendTransaction: (transactions, _old, state) => {
          if (paused() || !transactions.some((tr) => tr.docChanged)) return null;
          const tr = tidyFootnotes(state);
          // A change kept out of the undo history keeps its tidying out too.
          if (tr && transactions.every((t) => t.getMeta('addToHistory') === false)) tr.setMeta('addToHistory', false);
          return tr;
        },
      }),
    ];
  },
});

/** A `[^n]: …` paragraph at the top level of the writing. */
export interface FootnoteDefinition {
  node: PMNode;
  pos: number;
  index: number;
  number: number;
  /** Nothing in it but one live citation (issue #1157): the Studio's own note. */
  citationOnly: boolean;
}

export function isFootnoteDefinition(node: PMNode): boolean {
  if (node.type.name !== 'paragraph' || node.firstChild?.type.name !== 'footnoteRef' || node.childCount < 2) return false;
  const second = node.child(1);
  return second.isText && (second.text ?? '').startsWith(':');
}

function isCitationOnly(node: PMNode): boolean {
  let citations = 0;
  let other = false;
  node.forEach((child, _offset, index) => {
    if (index === 0) return;
    if (child.type.name === 'citation') citations++;
    else if (!child.isText) other = true;
    else if ((index === 1 ? (child.text ?? '').replace(/^:/, '') : (child.text ?? '')).trim()) other = true;
  });
  return citations === 1 && !other;
}

export function footnoteDefinitions(doc: PMNode): FootnoteDefinition[] {
  const definitions: FootnoteDefinition[] = [];
  doc.forEach((node, pos, index) => {
    if (!isFootnoteDefinition(node)) return;
    definitions.push({ node, pos, index, number: Number(node.firstChild!.attrs['number']) || 0, citationOnly: isCitationOnly(node) });
  });
  return definitions;
}

/** Every footnote marker in the text, in reading order; a definition's own marker is not one. */
export function footnoteMarkers(doc: PMNode): { pos: number; number: number }[] {
  const definitionNodes = new Set(footnoteDefinitions(doc).map((d) => d.node));
  const markers: { pos: number; number: number }[] = [];
  doc.descendants((node, pos, parent, index) => {
    if (node.type.name !== 'footnoteRef') return;
    if (index === 0 && parent && definitionNodes.has(parent)) return;
    markers.push({ pos, number: Number(node.attrs['number']) || 0 });
  });
  return markers;
}

/**
 * Footnotes kept in order (issue #1157): numbered as they are read, their
 * notes together in that order, and the Studio's own note gone with its
 * marker. A note someone wrote themselves is never deleted, only renumbered.
 */
export function tidyFootnotes(state: EditorState): Transaction | null {
  const tr = state.tr;
  return tidyFootnotesIn(tr) ? tr : null;
}

/** Tidies the footnotes of `tr`'s document within `tr`; false when they were already tidy. */
export function tidyFootnotesIn(tr: Transaction): boolean {
  const { doc } = tr;
  const schema = doc.type.schema;
  const definitions = footnoteDefinitions(doc);
  const markers = footnoteMarkers(doc);
  if (!definitions.length && !markers.length) return false;

  const marked = new Set(markers.map((m) => m.number));
  const dropped = definitions.filter((d) => d.citationOnly && !marked.has(d.number));
  const kept = definitions.filter((d) => !dropped.includes(d));

  const renumber = new Map<number, number>();
  for (const marker of markers) if (!renumber.has(marker.number)) renumber.set(marker.number, renumber.size + 1);
  for (const definition of kept) if (!renumber.has(definition.number)) renumber.set(definition.number, renumber.size + 1);
  const next = (n: number) => renumber.get(n) ?? n;

  const sorted = [...kept].sort((a, b) => next(a.number) - next(b.number) || a.index - b.index);
  const together = kept.every((d, i) => i === 0 || d.index === kept[i - 1].index + 1);
  const inOrder = sorted.every((d, i) => d === kept[i]);
  const regroup = !together || !inOrder;
  const changesNumbers = markers.some((m) => next(m.number) !== m.number) || kept.some((d) => next(d.number) !== d.number);
  if (!dropped.length && !regroup && !changesNumbers) return false;

  const ref = schema.nodes['footnoteRef'];
  const start = tr.steps.length;
  const map = (pos: number, assoc = 1) => tr.mapping.slice(start).map(pos, assoc);
  for (const marker of markers) {
    if (next(marker.number) !== marker.number) tr.setNodeMarkup(map(marker.pos), undefined, { number: next(marker.number) });
  }
  const renumbered = (d: FootnoteDefinition) =>
    d.node.copy(d.node.content.replaceChild(0, ref.create({ number: next(d.number) })));

  if (regroup) {
    const at = kept[0].pos;
    for (const definition of [...definitions].reverse()) {
      tr.delete(map(definition.pos), map(definition.pos + definition.node.nodeSize));
    }
    tr.insert(map(at, -1), sorted.map(renumbered));
  } else {
    for (const definition of kept) {
      if (next(definition.number) !== definition.number) tr.setNodeMarkup(map(definition.pos) + 1, undefined, { number: next(definition.number) });
    }
    for (const definition of [...dropped].reverse()) {
      tr.delete(map(definition.pos), map(definition.pos + definition.node.nodeSize));
    }
  }
  // Deleting the last paragraph would leave the writing with no block at all.
  if (tr.doc.childCount === 0) tr.insert(0, schema.nodes['paragraph'].create());
  return true;
}

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
