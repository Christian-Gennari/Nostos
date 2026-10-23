import { type ExportBlock, type ExportDocument, type ExportInline, type ExportSection, inlineText } from './export-model';

/**
 * Layout choices every format makes the same way (issues #1165–#1167), so a
 * writing reads alike as Markdown, DOCX, PDF and pasted rich text.
 */

type Chapter = Extract<ExportSection, { type: 'chapter' }>;

/**
 * The heading a chapter of a combined folder gets, or null. A chapter's own
 * first heading usually is its title already; a chapter that starts straight
 * into prose would otherwise run on from the one before, so it gets its
 * writing's name, one level below the folder it sits in. A single writing is
 * printed exactly as written.
 */
export function chapterTitle(doc: Pick<ExportDocument, 'sections'>, chapter: Chapter): { level: 1 | 2 | 3 | 4 | 5 | 6; text: string } | null {
  const chapters = doc.sections.filter((section) => section.type === 'chapter').length;
  const grouped = doc.sections.some((section) => section.type === 'group');
  if (chapters < 2 && !grouped) return null;
  const first = chapter.blocks[0];
  if (first?.type === 'heading') return null;
  const text = chapter.title.trim();
  if (!text) return null;
  return { level: Math.min(6, chapter.depth + 1) as 1 | 2 | 3 | 4 | 5 | 6, text };
}

/** Calls `visit` on every run of inlines in some blocks, in reading order. */
export function eachInlines(blocks: readonly ExportBlock[], visit: (inlines: readonly ExportInline[]) => void): void {
  for (const block of blocks) {
    switch (block.type) {
      case 'heading':
      case 'paragraph':
        visit(block.children);
        break;
      case 'blockquote':
        eachInlines(block.blocks, visit);
        break;
      case 'list':
        for (const item of block.items) eachInlines(item.blocks, visit);
        break;
      case 'table':
        for (const row of [block.header, ...block.rows]) for (const cell of row) visit(cell);
        break;
    }
  }
}

/** Calls `visit` on every inline, nested marks included. */
function eachInline(inlines: readonly ExportInline[], visit: (inline: ExportInline) => void): void {
  for (const inline of inlines) {
    visit(inline);
    if ('children' in inline) eachInline(inline.children, visit);
  }
}

/** The footnote numbers called in some blocks, in order, each once. */
export function footnotesIn(blocks: readonly ExportBlock[]): number[] {
  const seen = new Set<number>();
  eachInlines(blocks, (inlines) => eachInline(inlines, (inline) => inline.type === 'footnoteRef' && seen.add(inline.number)));
  return [...seen];
}

const RTL = /[\u0590-\u08FF\uFB1D-\uFDFF\uFE70-\uFEFF]/;
const STRONG = /[A-Za-z\u00C0-\u024F\u0370-\u03FF\u0400-\u04FF\u0590-\u08FF\uFB1D-\uFDFF\uFE70-\uFEFF]/;

/** True when the first strong letter of the text is right-to-left (Hebrew, Arabic…), as `dir="auto"` decides. */
export function isRightToLeft(inlines: readonly ExportInline[]): boolean {
  const first = STRONG.exec(inlineText(inlines))?.[0];
  return !!first && RTL.test(first);
}

/** Every image a document shows, by address, with its alt text, once each in reading order. */
export function imagesIn(doc: Pick<ExportDocument, 'sections' | 'footnotes'>): Map<string, string> {
  const found = new Map<string, string>();
  const collect = (inlines: readonly ExportInline[]) =>
    eachInline(inlines, (inline) => inline.type === 'image' && !found.has(inline.src) && found.set(inline.src, inline.alt));
  for (const section of doc.sections) if (section.type === 'chapter') eachInlines(section.blocks, collect);
  for (const note of doc.footnotes) collect(note.children);
  return found;
}
