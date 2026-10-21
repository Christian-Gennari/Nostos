import type { Node as PMNode, Schema } from '@tiptap/pm/model';
import type { EditorState, Transaction } from '@tiptap/pm/state';

import type { CitationAttrs, CitationRef } from './citation.extension';
import { footnoteDefinitions, footnoteMarkers, isFootnoteDefinition, tidyFootnotesIn } from './footnote.extension';

/**
 * Live citations (issue #1157): the Studio writes every citation again from
 * the book's current details and the chosen style. This module does the
 * editing; what the words are is the Studio's (`CitationRenderer`).
 *
 * A change of style can also change where citations stand: an author–date
 * style cites in the sentence, a note style in a footnote. Citations the
 * Studio put in move between the two; a note someone wrote themselves stays.
 */
export interface CitationRenderer {
  /** `note` cites in footnotes; `in-text` and `house` in the sentence. */
  placement: 'note' | 'in-text' | 'house';
  /** A citation standing in the writing; null leaves it as it is. */
  inText(ref: CitationRef): string | null;
  /** A citation in a footnote; `subsequent` once an earlier note cited the book. */
  note(ref: CitationRef, subsequent: boolean): string | null;
  /** The reference list for these books in the style's order; null leaves it as it is. */
  bibliography(books: readonly string[]): { book: string; text: string }[] | null;
  /** "References", "Works Cited" or "Bibliography". */
  bibliographyHeading: string;
  /** Books the reference list names though no live citation does (kept with the writing). */
  bibliographyBooks: readonly string[];
}

/** The headings a bibliography the Studio wrote may stand under. */
export const BIBLIOGRAPHY_HEADINGS = ['Bibliography', 'References', 'Works Cited'];

interface Found {
  node: PMNode;
  pos: number;
}

function attrsOf(node: PMNode): CitationAttrs {
  return node.attrs as CitationAttrs;
}

function refOf(node: PMNode): CitationRef {
  const { book, page, src, as } = attrsOf(node);
  return { book, page, src, as };
}

/** The top-level block a position is in. */
function topBlock(doc: PMNode, pos: number): PMNode | null {
  const $pos = doc.resolve(pos);
  return $pos.depth >= 1 ? $pos.node(1) : null;
}

function inDefinition(doc: PMNode, pos: number): boolean {
  const block = topBlock(doc, pos);
  return !!block && isFootnoteDefinition(block);
}

/** A paragraph holding one reference-list entry and nothing else. */
function bibliographyEntry(node: PMNode): PMNode | null {
  if (node.type.name !== 'paragraph') return null;
  let entry: PMNode | null = null;
  let other = false;
  node.forEach((child) => {
    if (child.type.name === 'citation' && attrsOf(child).as === 'bib' && !entry) entry = child;
    else if (!(child.isText && !(child.text ?? '').trim())) other = true;
  });
  return other ? null : entry;
}

function isEmptyParagraph(node: PMNode): boolean {
  return node.type.name === 'paragraph' && node.content.size === 0;
}

/** Every book a live citation in the writing points at, in reading order, once each. */
export function citationBooks(doc: PMNode): string[] {
  const books: string[] = [];
  doc.descendants((node) => {
    if (node.type.name === 'citation') {
      const book = attrsOf(node).book;
      if (book && !books.includes(book)) books.push(book);
    }
  });
  return books;
}

/** What a writing's citations are, as one string: it changes when one is added, moved or removed. */
export function citationSignature(doc: PMNode): string {
  const parts: string[] = [];
  doc.descendants((node, pos) => {
    if (node.type.name === 'citation') {
      const { book, page, as } = attrsOf(node);
      parts.push(`${book}:${page ?? ''}:${as ?? ''}:${inDefinition(doc, pos) ? 'n' : 't'}`);
    } else if (node.type.name === 'footnoteRef') {
      parts.push(`^${node.attrs['number']}`);
    }
  });
  return parts.join('|');
}

/**
 * Where a new footnote goes: after the last one, or else ahead of a
 * bibliography at the end of the writing, or else at the very end.
 */
export function footnoteInsertPosition(doc: PMNode): number {
  const definitions = footnoteDefinitions(doc);
  if (definitions.length) {
    const last = definitions[definitions.length - 1];
    return last.pos + last.node.nodeSize;
  }
  return trailingBibliographyStart(doc) ?? doc.content.size;
}

/** Where a bibliography at the end of the writing starts (its heading, when it has one). */
export function trailingBibliographyStart(doc: PMNode): number | null {
  const positions: number[] = [];
  doc.forEach((_node, pos) => positions.push(pos));
  let index = doc.childCount - 1;
  while (index >= 0 && isEmptyParagraph(doc.child(index))) index--;
  let first = -1;
  while (index >= 0 && bibliographyEntry(doc.child(index))) first = index--;
  if (first < 0) return null;
  const before = index >= 0 ? doc.child(index) : null;
  if (before?.type.name === 'heading' && BIBLIOGRAPHY_HEADINGS.includes(before.textContent.trim())) return positions[index];
  return positions[first];
}

function highestFootnote(doc: PMNode): number {
  let highest = 0;
  doc.descendants((node) => {
    if (node.type.name === 'footnoteRef') highest = Math.max(highest, Number(node.attrs['number']) || 0);
  });
  return highest;
}

/** Only spaces besides the citation: a paragraph that is just the attribution. */
function aloneInParagraph(paragraph: PMNode): boolean {
  let alone = true;
  paragraph.forEach((child) => {
    if (child.type.name === 'citation' || child.type.name === 'footnoteRef') return;
    if (!(child.isText && !(child.text ?? '').trim())) alone = false;
  });
  return alone;
}

function firstBodyCitation(doc: PMNode): Found | null {
  let found: Found | null = null;
  doc.descendants((node, pos) => {
    if (found) return false;
    if (node.type.name === 'citation' && attrsOf(node).as !== 'bib' && !inDefinition(doc, pos)) found = { node, pos };
    return !found;
  });
  return found;
}

/** A citation in the sentence becomes a footnote marker, and its words the note. */
function moveToNote(tr: Transaction, found: Found, renderer: CitationRenderer): void {
  const schema: Schema = tr.doc.type.schema;
  const number = highestFootnote(tr.doc) + 1;
  const marker = schema.nodes['footnoteRef'].create({ number });
  const attrs = attrsOf(found.node);
  const text = renderer.note(refOf(found.node), false) ?? attrs.text;
  const definition = schema.nodes['paragraph'].create(null, [
    schema.nodes['footnoteRef'].create({ number }),
    schema.text(': '),
    schema.nodes['citation'].create({ ...attrs, text }),
  ]);

  const $pos = tr.doc.resolve(found.pos);
  const paragraph = $pos.parent;
  const container = $pos.node($pos.depth - 1);
  const index = $pos.index($pos.depth - 1);
  const previous = index > 0 ? container.child(index - 1) : null;
  if (
    aloneInParagraph(paragraph) &&
    previous?.type.name === 'paragraph' &&
    previous.content.size > 0 &&
    !isFootnoteDefinition(previous)
  ) {
    // "> (Voltaire, 1992)" under a quote: the marker goes after the quote's last word.
    const start = $pos.before();
    tr.delete(start, start + paragraph.nodeSize);
    tr.insert(start - 1, marker);
  } else {
    const before = found.pos > $pos.start() ? tr.doc.textBetween(found.pos - 1, found.pos) : '';
    tr.replaceWith(before === ' ' ? found.pos - 1 : found.pos, found.pos + 1, marker);
  }
  tr.insert(footnoteInsertPosition(tr.doc), definition);
}

function firstMovableNote(doc: PMNode): { definition: ReturnType<typeof footnoteDefinitions>[number]; marker: number } | null {
  const markers = footnoteMarkers(doc);
  for (const definition of footnoteDefinitions(doc)) {
    if (!definition.citationOnly) continue;
    const matching = markers.filter((m) => m.number === definition.number);
    if (matching.length === 1) return { definition, marker: matching[0].pos };
  }
  return null;
}

/** A footnote the Studio wrote goes back into the sentence, where its marker was. */
function moveIntoText(tr: Transaction, definition: ReturnType<typeof footnoteDefinitions>[number], markerPos: number, renderer: CitationRenderer): void {
  const schema: Schema = tr.doc.type.schema;
  let note: PMNode | null = null;
  definition.node.forEach((child) => {
    if (child.type.name === 'citation') note = child;
  });
  if (!note) return;
  const attrs = attrsOf(note);
  const text = renderer.inText(refOf(note)) ?? attrs.text;
  const citation = schema.nodes['citation'].create({ ...attrs, text });

  const start = tr.steps.length;
  const map = (pos: number) => tr.mapping.slice(start).map(pos);
  const $marker = tr.doc.resolve(markerPos);
  const paragraph = $marker.parent;
  const atEnd = !paragraph.textBetween($marker.parentOffset + 1, paragraph.content.size, '', '').trim();
  const inQuote = $marker.depth >= 2 && $marker.node($marker.depth - 1).type.name === 'blockquote';
  const ownParagraph =
    atEnd &&
    paragraph.type.name === 'paragraph' &&
    ((renderer.placement === 'house' && (attrs.as === 'quote' || attrs.as === 'note')) ||
      (renderer.placement === 'in-text' && attrs.as === 'quote' && inQuote));

  if (ownParagraph) {
    const after = $marker.after();
    tr.delete(markerPos, markerPos + 1);
    tr.insert(map(after) , schema.nodes['paragraph'].create(null, citation));
  } else {
    const before = markerPos > $marker.start() ? tr.doc.textBetween(markerPos - 1, markerPos) : '';
    const content = before === '' || /\s/.test(before) ? [citation] : [schema.text(' '), citation];
    tr.replaceWith(markerPos, markerPos + 1, content);
  }
  tr.delete(map(definition.pos), map(definition.pos + definition.node.nodeSize));
}

/** Each citation's words, written again; notes in reading order, so a book's first note is the full one. */
function rewriteTexts(tr: Transaction, renderer: CitationRenderer): void {
  const doc = tr.doc;
  const updates: { pos: number; attrs: CitationAttrs }[] = [];
  const definitions = footnoteDefinitions(doc).sort((a, b) => a.number - b.number || a.index - b.index);
  const cited = new Set<string>();
  for (const definition of definitions) {
    definition.node.forEach((child, offset) => {
      if (child.type.name !== 'citation') return;
      const attrs = attrsOf(child);
      // Under an author–date style a note the writer kept cites as the sentence does.
      const text = renderer.placement === 'note' ? renderer.note(refOf(child), cited.has(attrs.book)) : renderer.inText(refOf(child));
      cited.add(attrs.book);
      if (text !== null && text !== attrs.text) updates.push({ pos: definition.pos + 1 + offset, attrs: { ...attrs, text } });
    });
  }
  doc.descendants((node, pos) => {
    if (node.type.name !== 'citation' || attrsOf(node).as === 'bib' || inDefinition(doc, pos)) return;
    const text = renderer.inText(refOf(node));
    if (text !== null && text !== attrsOf(node).text) updates.push({ pos, attrs: { ...attrsOf(node), text } });
  });
  for (const update of updates) tr.setNodeMarkup(update.pos, undefined, update.attrs);
}

/** Each reference list the Studio wrote, listing what the writing cites, in the style's order. */
function rewriteBibliographies(tr: Transaction, renderer: CitationRenderer): void {
  const doc = tr.doc;
  const schema: Schema = doc.type.schema;
  const runs: { from: number; to: number; first: number; entries: CitationAttrs[] }[] = [];
  let run: (typeof runs)[number] | null = null;
  doc.forEach((node, pos, index) => {
    const entry = bibliographyEntry(node);
    if (entry) {
      if (!run) runs.push((run = { from: pos, to: pos, first: index, entries: [] }));
      run.entries.push(attrsOf(entry));
      run.to = pos + node.nodeSize;
    } else {
      run = null;
    }
  });
  if (!runs.length) return;

  const books: string[] = [];
  const add = (book: string) => book && !books.includes(book) && books.push(book);
  doc.descendants((node) => {
    if (node.type.name === 'citation' && attrsOf(node).as !== 'bib') add(attrsOf(node).book);
  });
  renderer.bibliographyBooks.forEach(add);

  for (const each of [...runs].reverse()) {
    const own = [...books];
    for (const entry of each.entries) if (!own.includes(entry.book)) own.push(entry.book);
    const entries = renderer.bibliography(own);
    if (!entries?.length) continue;
    const same =
      entries.length === each.entries.length &&
      entries.every((e, i) => e.book === each.entries[i].book && e.text === each.entries[i].text);
    if (!same) {
      tr.replaceWith(
        each.from,
        each.to,
        entries.map((e) =>
          schema.nodes['paragraph'].create(null, schema.nodes['citation'].create({ book: e.book, page: null, src: null, as: 'bib', text: e.text })),
        ),
      );
    }
    const heading = each.first > 0 ? doc.child(each.first - 1) : null;
    if (heading?.type.name === 'heading' && BIBLIOGRAPHY_HEADINGS.includes(heading.textContent.trim()) && heading.textContent.trim() !== renderer.bibliographyHeading) {
      const headingPos = each.from - heading.nodeSize;
      tr.replaceWith(headingPos + 1, headingPos + 1 + heading.content.size, schema.text(renderer.bibliographyHeading));
    }
  }
}

/**
 * Every live citation in the writing written again for `renderer`: moved
 * between sentence and footnote when the style asks, its words rewritten, the
 * footnotes renumbered and the reference list rebuilt. Null when nothing
 * changes, so an up-to-date writing is not saved again.
 */
export function refreshCitations(state: EditorState, renderer: CitationRenderer): Transaction | null {
  const tr = state.tr;
  // Each pass takes one citation and ends when none is left to move; the
  // bound only guards against a document the passes cannot settle.
  for (let guard = 0; guard < 2000; guard++) {
    if (renderer.placement === 'note') {
      const found = firstBodyCitation(tr.doc);
      if (!found) break;
      moveToNote(tr, found, renderer);
    } else {
      const found = firstMovableNote(tr.doc);
      if (!found) break;
      moveIntoText(tr, found.definition, found.marker, renderer);
    }
  }
  tidyFootnotesIn(tr);
  rewriteTexts(tr, renderer);
  rewriteBibliographies(tr, renderer);
  return tr.docChanged ? tr : null;
}
