import { STUDIO_FONTS } from '../page-settings/studio-preferences.service';
import { chapterTitle, footnotesIn } from './export-layout';
import type { ExportBlock, ExportDocument, ExportFootnote, ExportInline } from './export-model';

/**
 * The resolved document as HTML (issue #1164), shared by three formats: the
 * self-contained web page, rich text on the clipboard (#1165) and the print
 * layout a PDF is saved from (#1167).
 */
export interface ExportHtmlOptions {
  /**
   * Where the notes go: all at the end of the document (a web page, pasted
   * rich text), or at the end of the chapter that calls them (the print
   * layout), still numbered once through the whole document.
   */
  notes: 'document' | 'chapter';
  /** Wraps each heading with the block after it, so print never leaves a heading alone at the foot of a page. */
  keepHeadings?: boolean;
}

/** The heading over the notes. */
export const NOTES_HEADING = 'Notes';

/** The document's body: chapters, notes and bibliography, with no page around it. */
export function renderExportBody(doc: ExportDocument, options: ExportHtmlOptions): string {
  const notesByNumber = new Map(doc.footnotes.map((note) => [note.number, note]));
  const body: string[] = [];
  let afterGroup = false;
  for (const section of doc.sections) {
    if (section.type === 'group') {
      const level = Math.min(6, section.depth);
      body.push(`<h${level} class="group${afterGroup ? ' follows-group' : ''}" dir="auto">${escapeHtml(section.title)}</h${level}>`);
      afterGroup = true;
      continue;
    }
    const title = chapterTitle(doc, section);
    const head = title ? [`<h${title.level} dir="auto">${escapeHtml(title.text)}</h${title.level}>`] : [];
    const parts = [...head, ...section.blocks.map(block)];
    if (options.notes === 'chapter') {
      const notes = footnotesIn(section.blocks)
        .map((number) => notesByNumber.get(number))
        .filter((note): note is ExportFootnote => !!note);
      if (notes.length) parts.push(notesList(notes, 'h3'));
    }
    const classes = `chapter${afterGroup ? ' follows-group' : ''}`;
    body.push(`<section class="${classes}" data-writing="${escapeHtml(section.writingId)}">${(options.keepHeadings ? keepWithNext(parts) : parts).join('\n')}</section>`);
    afterGroup = false;
  }
  if (options.notes === 'document' && doc.footnotes.length) body.push(notesList(doc.footnotes, 'h2'));
  if (doc.bibliography) {
    body.push(
      `<section class="bibliography"><h2 dir="auto">${escapeHtml(doc.bibliography.heading)}</h2>${doc.bibliography.entries
        .map((entry) => `<p dir="auto">${inlines(entry.children)}</p>`)
        .join('')}</section>`,
    );
  }
  return body.join('\n');
}

/**
 * The simplest format: one self-contained web page. The page carries the
 * page setup as print CSS, so printing it from a browser gives the chosen
 * paper, margins, font and spacing. Footnotes are numbered notes at the end.
 */
export function renderExportHtml(doc: ExportDocument): string {
  const font = STUDIO_FONTS.find((f) => f.value === doc.setup.font)?.stack ?? 'Georgia, serif';
  const paper = doc.setup.paper === 'letter' ? 'letter' : 'A4';
  return `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(doc.title)}</title>
<style>
@page { size: ${paper}; margin: ${doc.setup.marginMm}mm; }
body { font-family: ${font}; line-height: ${doc.setup.lineSpacing}; max-width: 42rem; margin: 2rem auto; padding: 0 1rem; color: #1a1a1a; background: #fff; }
@media print { body { max-width: none; margin: 0; padding: 0; } .chapter + .chapter, .group { break-before: page; } }
blockquote { margin-inline: 2em 0; }
table { border-collapse: collapse; } td, th { border: 1px solid #999; padding: 0.25em 0.5em; }
img { max-width: 100%; }
.footnotes { font-size: 0.9em; }
.bibliography p { padding-inline-start: 2em; text-indent: -2em; }
</style>
</head>
<body>
${renderExportBody(doc, { notes: 'document' })}
</body>
</html>
`;
}

function notesList(notes: readonly ExportFootnote[], heading: 'h2' | 'h3'): string {
  const items = notes
    .map((note) => `<li id="fn${note.number}" value="${note.number}" dir="auto">${inlines(note.children)} <a href="#fnref${note.number}" aria-label="Back to the text">↩</a></li>`)
    .join('');
  return `<section class="footnotes"><${heading}>${NOTES_HEADING}</${heading}><ol start="${notes[0].number}">${items}</ol></section>`;
}

/**
 * Each run of headings and the paragraph or quote after it, as one group
 * print keeps on one page (`break-after: avoid` alone is not honoured by
 * every browser). A table, list or code block after a heading is left out of
 * the group, since it may be longer than a page.
 */
function keepWithNext(parts: readonly string[]): string[] {
  const heading = /^<h[1-6][ >]/;
  const out: string[] = [];
  for (let i = 0; i < parts.length; i++) {
    if (heading.test(parts[i])) {
      const group = [parts[i]];
      while (i + 1 < parts.length && heading.test(parts[i + 1])) group.push(parts[++i]);
      if (i + 1 < parts.length && /^<(p|blockquote)[ >]/.test(parts[i + 1])) group.push(parts[++i]);
      out.push(`<div class="keep">${group.join('\n')}</div>`);
    } else {
      out.push(parts[i]);
    }
  }
  return out;
}

function blocks(list: readonly ExportBlock[]): string {
  return list.map(block).join('\n');
}

function block(b: ExportBlock): string {
  switch (b.type) {
    case 'heading':
      return `<h${b.level} dir="auto">${inlines(b.children)}</h${b.level}>`;
    case 'paragraph':
      return `<p dir="auto">${inlines(b.children)}</p>`;
    case 'blockquote':
      return `<blockquote>${blocks(b.blocks)}</blockquote>`;
    case 'list': {
      const tag = b.ordered ? 'ol' : 'ul';
      const start = b.ordered && b.start !== 1 ? ` start="${b.start}"` : '';
      const items = b.items
        .map((item) => `<li dir="auto">${item.checked === null ? '' : `${item.checked ? '☒' : '☐'} `}${listItem(item.blocks)}</li>`)
        .join('');
      return `<${tag}${start}>${items}</${tag}>`;
    }
    case 'table': {
      const cell = (tag: string, children: ExportInline[], i: number) =>
        `<${tag} dir="auto"${b.align[i] ? ` style="text-align:${b.align[i]}"` : ''}>${inlines(children)}</${tag}>`;
      return `<table><thead><tr>${b.header.map((c, i) => cell('th', c, i)).join('')}</tr></thead><tbody>${b.rows
        .map((row) => `<tr>${row.map((c, i) => cell('td', c, i)).join('')}</tr>`)
        .join('')}</tbody></table>`;
    }
    case 'code':
      return `<pre><code>${escapeHtml(b.text)}</code></pre>`;
    case 'rule':
      return '<hr>';
  }
}

/** A tight list item's one paragraph is its text, not a paragraph inside it. */
function listItem(list: readonly ExportBlock[]): string {
  if (list[0]?.type === 'paragraph') return [inlines(list[0].children), ...list.slice(1).map(block)].join('\n');
  return blocks(list);
}

function inlines(list: readonly ExportInline[]): string {
  return list
    .map((inline) => {
      switch (inline.type) {
        case 'text':
          return escapeHtml(inline.text);
        case 'emphasis':
          return `<em>${inlines(inline.children)}</em>`;
        case 'strong':
          return `<strong>${inlines(inline.children)}</strong>`;
        case 'strike':
          return `<s>${inlines(inline.children)}</s>`;
        case 'code':
          return `<code>${escapeHtml(inline.text)}</code>`;
        case 'link':
          return `<a href="${escapeHtml(inline.href)}">${inlines(inline.children)}</a>`;
        case 'image':
          return `<img src="${escapeHtml(inline.src)}" alt="${escapeHtml(inline.alt)}"${inline.title ? ` title="${escapeHtml(inline.title)}"` : ''}>`;
        case 'break':
          return '<br>';
        case 'footnoteRef':
          return `<sup id="fnref${inline.number}"><a href="#fn${inline.number}">${inline.number}</a></sup>`;
      }
    })
    .join('');
}

export function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
