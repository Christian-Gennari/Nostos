/**
 * The resolved export document (issue #1164): what every export format
 * (#1165 rich text and Markdown, #1166 DOCX, #1167 PDF) is written from.
 *
 * It is format-neutral and finished. Live citations are already their words in
 * the chosen style, footnotes are numbered once for the whole document, the
 * bibliography is written, and nothing that exists only in Nostos is left: no
 * `nostos:` address, no footnote marker without its note. A format walks
 * `sections` in order, then `footnotes` (or places each where its marker is),
 * then `bibliography`, and lays it all out with `setup`.
 *
 * Get one from `resolveExport()` (export-resolve.ts), or, inside the app, from
 * `ExportService.resolve()`, which loads the writings and books first.
 */

import type { CitationStyleId } from '../citations/citation-styles';

/** Text inside a block. Marks nest: `strong` may hold `emphasis` and so on. */
export type ExportInline =
  | { type: 'text'; text: string }
  | { type: 'emphasis'; children: ExportInline[] }
  | { type: 'strong'; children: ExportInline[] }
  | { type: 'strike'; children: ExportInline[] }
  | { type: 'code'; text: string }
  | { type: 'link'; href: string; children: ExportInline[] }
  /** Linked by URL, never embedded; an image that failed to load is already the text "[image: alt]". */
  | { type: 'image'; src: string; alt: string; title: string | null }
  | { type: 'break' }
  /** Where footnote `number` is called; its words are `ExportDocument.footnotes`. */
  | { type: 'footnoteRef'; number: number };

export type ExportAlign = 'left' | 'center' | 'right' | null;

export interface ExportListItem {
  blocks: ExportBlock[];
  /** A task list item's box; null for an ordinary item. */
  checked: boolean | null;
}

export type ExportBlock =
  | { type: 'heading'; level: 1 | 2 | 3 | 4 | 5 | 6; children: ExportInline[] }
  | { type: 'paragraph'; children: ExportInline[] }
  | { type: 'blockquote'; blocks: ExportBlock[] }
  | { type: 'list'; ordered: boolean; start: number; items: ExportListItem[] }
  | { type: 'table'; align: ExportAlign[]; header: ExportInline[][]; rows: ExportInline[][][] }
  | { type: 'code'; language: string | null; text: string }
  | { type: 'rule' };

/**
 * One part of the document, in reading order. A single writing is one
 * chapter. A folder gives one chapter per document, with a `group` heading
 * before the first chapter of each subfolder (depth 1 for a folder directly in
 * the exported one, 2 for a folder in that, and so on). A chapter's `title` is
 * its writing's name; its own first heading usually says the same, so a format
 * decides whether to print it.
 */
export type ExportSection =
  | { type: 'group'; folderId: string; title: string; depth: number }
  | { type: 'chapter'; writingId: string; title: string; depth: number; blocks: ExportBlock[] };

export interface ExportFootnote {
  /** 1, 2, 3… continuously through the whole document. */
  number: number;
  children: ExportInline[];
}

export interface ExportBibliography {
  /** "References", "Works Cited" or "Bibliography", as the style names it. */
  heading: string;
  /** In the style's order, each book once. */
  entries: { book: string; children: ExportInline[] }[];
}

export type ExportWarningKind = 'citation' | 'image' | 'footnote';

/** Something the writer should know before downloading. It never blocks the export. */
export interface ExportWarning {
  kind: ExportWarningKind;
  /** A short item, ready to show: what it is, then what happened ("(Voltaire, 1759): book no longer in your library, kept as written"). */
  message: string;
  /** The chapter it is in, for a folder. */
  chapter: string | null;
  /** How many times the same thing happens in that chapter. */
  count: number;
}

export type PaperSize = 'a4' | 'letter';

/** How the document is laid out (saved per writing on the server). */
export interface PageSetup {
  paper: PaperSize;
  marginMm: number;
  /** A Studio font value (`STUDIO_FONTS`). */
  font: string;
  lineSpacing: number;
  citationStyle: CitationStyleId;
}

export interface ExportDocument {
  title: string;
  setup: PageSetup;
  citationStyle: { id: CitationStyleId; label: string; kind: 'house' | 'in-text' | 'note' };
  sections: ExportSection[];
  footnotes: ExportFootnote[];
  bibliography: ExportBibliography | null;
  warnings: ExportWarning[];
  /** Words in the sections (not notes or bibliography), for the dialog. */
  words: number;
}

/** The plain text of some inlines: marks off, a footnote marker as nothing. */
export function inlineText(inlines: readonly ExportInline[]): string {
  let out = '';
  for (const inline of inlines) {
    switch (inline.type) {
      case 'text':
      case 'code':
        out += inline.text;
        break;
      case 'break':
        out += '\n';
        break;
      case 'image':
        out += inline.alt;
        break;
      case 'footnoteRef':
        break;
      default:
        out += inlineText(inline.children);
    }
  }
  return out;
}
