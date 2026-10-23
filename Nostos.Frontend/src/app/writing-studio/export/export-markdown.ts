import { chapterTitle } from './export-layout';
import type { ExportAlign, ExportBlock, ExportDocument, ExportInline } from './export-model';

/**
 * Markdown download (issue #1165): CommonMark with GFM tables and Pandoc
 * `[^1]` footnotes, so it opens in any Markdown editor and goes through
 * Pandoc with real footnotes. Nothing of Nostos is left in it: citations are
 * already their words, and every character a reader could take for Markdown
 * is escaped, so "*Candide*" written as a title stays a title.
 */
export function renderExportMarkdown(doc: ExportDocument): string {
  const out: string[] = [];
  for (const section of doc.sections) {
    if (section.type === 'group') {
      out.push(`${'#'.repeat(Math.min(6, section.depth))} ${escapeLineStart(escapeText(section.title))}`);
      continue;
    }
    const title = chapterTitle(doc, section);
    if (title) out.push(`${'#'.repeat(title.level)} ${escapeLineStart(escapeText(title.text))}`);
    out.push(...section.blocks.map(block).filter((text) => text !== ''));
  }
  if (doc.bibliography) {
    out.push(`## ${escapeText(doc.bibliography.heading)}`);
    out.push(...doc.bibliography.entries.map((entry) => paragraph(entry.children)));
  }
  // Pandoc and GFM read a note's definition anywhere; at the end is where a reader expects them.
  for (const note of doc.footnotes) {
    out.push(`[^${note.number}]: ${indent(paragraph(note.children), '    ').trimStart()}`);
  }
  return out.join('\n\n') + '\n';
}

function block(b: ExportBlock): string {
  switch (b.type) {
    case 'heading': {
      // A heading is one line: a hard break becomes a space.
      const text = inlines(b.children).replace(/\\\n/g, ' ').trim();
      return text ? `${'#'.repeat(b.level)} ${escapeLineStart(text)}` : '';
    }
    case 'paragraph':
      return paragraph(b.children);
    case 'blockquote':
      return b.blocks
        .map(block)
        .filter((text) => text !== '')
        .join('\n\n')
        .split('\n')
        .map((line) => (line ? `> ${line}` : '>'))
        .join('\n');
    case 'list':
      return list(b);
    case 'table':
      return table(b.align, b.header, b.rows);
    case 'code': {
      const fence = '`'.repeat(Math.max(3, longestRun(b.text, '`') + 1));
      return `${fence}${b.language ?? ''}\n${b.text.replace(/\n$/, '')}\n${fence}`;
    }
    case 'rule':
      return '---';
  }
}

function paragraph(children: readonly ExportInline[]): string {
  return escapeLineStart(inlines(children)).replace(/^[ \t]+/gm, (space) => space.replace(/ /g, '&#32;').replace(/\t/g, '&#9;'));
}

function list(b: Extract<ExportBlock, { type: 'list' }>): string {
  return b.items
    .map((item, i) => {
      const marker = b.ordered ? `${b.start + i}.` : '-';
      const box = item.checked === null ? '' : item.checked ? '[x] ' : '[ ] ';
      // A list inside an item follows its line directly, so the list stays tight.
      let body = '';
      item.blocks.forEach((inner, j) => {
        const text = block(inner);
        if (text === '') return;
        body += body ? (inner.type === 'list' && item.blocks[j - 1]?.type === 'paragraph' ? '\n' : '\n\n') + text : text;
      });
      const pad = ' '.repeat(marker.length + 1);
      return `${marker} ${box}${indent(body, pad).slice(pad.length)}`.trimEnd();
    })
    .join('\n');
}

function table(align: readonly ExportAlign[], header: readonly ExportInline[][], rows: readonly ExportInline[][][]): string {
  const width = Math.max(header.length, ...rows.map((row) => row.length), 1);
  const cell = (children: readonly ExportInline[] | undefined) =>
    // A cell is one line, and its pipes are text.
    (children ? inlines(children) : '').replace(/\\\n/g, '<br>').replace(/\n/g, ' ').trim();
  const line = (cells: readonly (readonly ExportInline[])[]) => `| ${Array.from({ length: width }, (_, i) => cell(cells[i])).join(' | ')} |`;
  const rule = `| ${Array.from({ length: width }, (_, i) => {
    switch (align[i]) {
      case 'left':
        return ':---';
      case 'center':
        return ':---:';
      case 'right':
        return '---:';
      default:
        return '---';
    }
  }).join(' | ')} |`;
  return [line(header), rule, ...rows.map(line)].join('\n');
}

function inlines(list: readonly ExportInline[]): string {
  let out = '';
  for (const inline of list) {
    switch (inline.type) {
      case 'text':
        out += escapeText(inline.text);
        break;
      case 'emphasis':
        out += delimit('*', inlines(inline.children));
        break;
      case 'strong':
        out += delimit('**', inlines(inline.children));
        break;
      case 'strike':
        out += delimit('~~', inlines(inline.children));
        break;
      case 'code': {
        const ticks = '`'.repeat(longestRun(inline.text, '`') + 1);
        const pad = /^`|`$|^ .* $/.test(inline.text) ? ' ' : '';
        out += `${ticks}${pad}${inline.text}${pad}${ticks}`;
        break;
      }
      case 'link':
        out += `[${inlines(inline.children)}](${destination(inline.href)})`;
        break;
      case 'image':
        out += `![${escapeText(inline.alt)}](${destination(inline.src)}${inline.title ? ` "${inline.title.replace(/(["\\])/g, '\\$1')}"` : ''})`;
        break;
      case 'break':
        out += '\\\n';
        break;
      case 'footnoteRef':
        out += `[^${inline.number}]`;
        break;
    }
  }
  return out;
}

/**
 * Wraps marked text in its delimiters. Spaces at its edges go outside, since
 * `* word*` is not emphasis in CommonMark; marks with nothing in them vanish.
 */
function delimit(mark: string, text: string): string {
  const match = /^(\s*)([\s\S]*?)(\s*)$/.exec(text)!;
  return match[2] ? `${match[1]}${mark}${match[2]}${mark}${match[3]}` : text;
}

/** Every character CommonMark could read as syntax, escaped wherever it stands. */
export function escapeText(text: string): string {
  return text
    .replace(/[\\`*_[\]<>|~]/g, '\\$&')
    .replace(/&(?=#?[A-Za-z0-9]+;)/g, '\\&')
    .replace(/!(?=\\\[)/g, '\\!');
}

/** What would start a block at the start of a line: a heading, quote, list, rule or fence. */
function escapeLineStart(text: string): string {
  return text
    .split('\n')
    .map((line) => line.replace(/^(\s*)([#>+=-])/, '$1\\$2').replace(/^(\s*\d+)([.)])/, '$1\\$2'))
    .join('\n');
}

/** A link destination CommonMark reads back as the same address. */
function destination(href: string): string {
  return href.replace(/[\s()<>\\]/g, (char) => encodeURIComponent(char));
}

function indent(text: string, pad: string): string {
  return text
    .split('\n')
    .map((line) => (line ? pad + line : line))
    .join('\n');
}

function longestRun(text: string, char: string): number {
  let best = 0;
  let run = 0;
  for (const c of text) {
    run = c === char ? run + 1 : 0;
    best = Math.max(best, run);
  }
  return best;
}
