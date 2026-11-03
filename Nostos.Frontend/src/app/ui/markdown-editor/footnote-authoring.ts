import { Extension, InputRule } from '@tiptap/core';
import type { Editor } from '@tiptap/core';
import { Plugin, PluginKey, TextSelection } from '@tiptap/pm/state';
import type { Transaction } from '@tiptap/pm/state';
import { Decoration, DecorationSet } from '@tiptap/pm/view';
import type { Node as PMNode } from '@tiptap/pm/model';

import { footnoteDefinitions, footnoteMarkers, isFootnoteDefinition, tidyFootnotesIn } from './footnote.extension';
import { footnoteInsertPosition } from './live-citations';

/**
 * Footnotes someone writes themselves (issue #1342). Until now a footnote only
 * came in with a source under a note citation style; this adds the command
 * (bubble menu, `/footnote`, Mod-Alt-F as in Word), turns a typed `[^]` into a
 * real marker, shows the notes as notes, and lets a click move between a
 * marker and its note.
 */

/** How long a note may run in a marker's tooltip before it is cut. */
const PREVIEW_LENGTH = 160;

/**
 * Put a footnote marker over `from`–`to` inside `tr`. With `existing` naming a
 * note that is already there, the marker points at it and the caret stays
 * after the marker. Otherwise a new, empty note goes in with the others and
 * the caret moves into it, ready to be written. Numbers are tidied within the
 * same transaction, so the new note lands in reading order. False, with `tr`
 * untouched, where a footnote cannot go: in code, outside text, or inside a
 * note itself.
 */
export function insertFootnoteIn(tr: Transaction, from: number, to: number, existing: number | null = null): boolean {
  const doc = tr.doc;
  const schema = doc.type.schema;
  const ref = schema.nodes['footnoteRef'];
  const paragraph = schema.nodes['paragraph'];
  if (!ref || !paragraph || from < 0 || to > doc.content.size || from > to) return false;
  const $from = doc.resolve(from);
  if (!$from.parent.inlineContent || $from.parent.type.spec.code || $from.depth < 1) return false;
  if (doc.resolve(to).parent !== $from.parent) return false;
  if (isFootnoteDefinition(doc.child($from.index(0)))) return false;

  if (existing !== null && footnoteDefinitions(doc).some((d) => d.number === existing)) {
    tr.replaceWith(from, to, ref.create({ number: existing }));
    tr.setSelection(TextSelection.create(tr.doc, from + 1));
    return true;
  }

  let highest = 0;
  doc.descendants((node) => {
    if (node.type === ref) highest = Math.max(highest, Number(node.attrs['number']) || 0);
  });
  const number = highest + 1;
  tr.replaceWith(from, to, ref.create({ number }));
  tr.insert(footnoteInsertPosition(tr.doc), paragraph.create(null, [ref.create({ number }), schema.text(': ')]));

  const start = tr.steps.length;
  tidyFootnotesIn(tr);
  const markerPos = tr.mapping.slice(start).map(from, 1);
  const renumbered = Number(tr.doc.nodeAt(markerPos)?.attrs['number']) || number;
  const note = footnoteDefinitions(tr.doc).find((d) => d.number === renumbered);
  if (note) tr.setSelection(TextSelection.create(tr.doc, note.pos + note.node.nodeSize - 1));
  return true;
}

/** A new footnote after the selection, its note opened for writing. */
export function insertFootnoteAtSelection(editor: Editor): boolean {
  if (editor.isDestroyed || !editor.isEditable) return false;
  const { state, view } = editor;
  const tr = state.tr;
  const at = state.selection.to;
  if (!insertFootnoteIn(tr, at, at)) return false;
  view.dispatch(tr.scrollIntoView());
  view.focus();
  return true;
}

/**
 * Where a click on a footnote number goes: from a marker to the end of its
 * note, or from a note's own number back to just after the first marker that
 * points at it. Null when there is nowhere to go.
 */
export function footnoteJumpTarget(doc: PMNode, pos: number): number | null {
  const node = doc.nodeAt(pos);
  if (node?.type.name !== 'footnoteRef') return null;
  const definitions = footnoteDefinitions(doc);
  const own = definitions.find((d) => d.pos + 1 === pos);
  if (own) {
    const marker = footnoteMarkers(doc).find((m) => m.number === own.number);
    return marker ? marker.pos + 1 : null;
  }
  const number = Number(node.attrs['number']) || 0;
  const note = definitions.find((d) => d.number === number);
  return note ? note.pos + note.node.nodeSize - 1 : null;
}

/** A note's words, without its number and colon, for a marker's tooltip. */
export function footnotePreview(definition: PMNode): string {
  let text = '';
  definition.forEach((child, _offset, index) => {
    if (index === 0) return;
    text += child.isText ? (child.text ?? '') : child.textContent;
  });
  const words = text.replace(/^:/, '').replace(/\s+/g, ' ').trim();
  return words.length > PREVIEW_LENGTH ? `${words.slice(0, PREVIEW_LENGTH - 1).trimEnd()}…` : words;
}

/**
 * The notes read as notes: each definition paragraph is marked, the first one
 * opens the section, a marker carries its note as a tooltip, and a note's own
 * number says it leads back. Decorations only: nothing here reaches Markdown.
 */
export function footnoteDecorations(doc: PMNode): DecorationSet {
  const definitions = footnoteDefinitions(doc);
  if (!definitions.length) return DecorationSet.empty;
  const decorations: Decoration[] = [];
  const previews = new Map<number, string>();
  definitions.forEach((d, i) => {
    decorations.push(
      Decoration.node(d.pos, d.pos + d.node.nodeSize, {
        class: i === 0 ? 'footnote-note footnote-notes-start' : 'footnote-note',
      }),
      Decoration.node(d.pos + 1, d.pos + 2, { class: 'footnote-backref', title: 'Back to the text' }),
    );
    if (!previews.has(d.number)) previews.set(d.number, footnotePreview(d.node));
  });
  for (const marker of footnoteMarkers(doc)) {
    const preview = previews.get(marker.number);
    decorations.push(Decoration.node(marker.pos, marker.pos + 1, { title: preview || 'Empty note' }));
  }
  return DecorationSet.create(doc, decorations);
}

export const FOOTNOTE_AUTHORING_KEY = new PluginKey<DecorationSet>('footnoteAuthoring');

export const FootnoteAuthoring = Extension.create({
  name: 'footnoteAuthoring',

  addKeyboardShortcuts() {
    return { 'Mod-Alt-f': () => insertFootnoteAtSelection(this.editor) };
  },

  addInputRules() {
    return [
      new InputRule({
        // `[^]` makes a new note; `[^3]` points at note 3 when there is one.
        find: /\[\^(\d{0,4})\]$/,
        handler: ({ state, range, match }) => {
          const existing = match[1] ? Number(match[1]) : null;
          // Null leaves the typed text as it is.
          return insertFootnoteIn(state.tr, range.from, range.to, existing) ? undefined : null;
        },
      }),
    ];
  },

  addProseMirrorPlugins() {
    return [
      new Plugin<DecorationSet>({
        key: FOOTNOTE_AUTHORING_KEY,
        state: {
          init: (_config, state) => footnoteDecorations(state.doc),
          apply: (tr, old) => (tr.docChanged ? footnoteDecorations(tr.doc) : old),
        },
        props: {
          decorations: (state) => FOOTNOTE_AUTHORING_KEY.getState(state),
          handleClickOn: (view, _pos, node, nodePos, event, direct) => {
            if (!direct || node.type.name !== 'footnoteRef' || event.shiftKey) return false;
            const target = footnoteJumpTarget(view.state.doc, nodePos);
            if (target === null) return false;
            view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, target)).scrollIntoView());
            view.focus();
            return true;
          },
        },
      }),
    ];
  },
});
