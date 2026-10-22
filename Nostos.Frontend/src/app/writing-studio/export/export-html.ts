import { STUDIO_FONTS } from '../page-settings/studio-preferences.service';
import type { ExportBlock, ExportDocument, ExportInline } from './export-model';

/**
 * The simplest format: one self-contained web page (issue #1164). It is what
 * the export dialog can download until the real formats land (#1165–#1167),
 * and it shows a format how to walk the model. The page carries the page
 * setup as print CSS, so printing it from a browser gives the chosen paper,
 * margins, font and spacing. Footnotes are numbered notes at the end.
 */
export function renderExportHtml(doc: ExportDocument): string {
  const font = STUDIO_FONTS.find((f) => f.value === doc.setup.font)?.stack ?? 'Georgia, serif';
  const paper = doc.setup.paper === 'letter' ? 'letter' : 'A4';
  const body: string[] = [];
  for (const section of doc.sections) {
    if (section.type === 'group') {
      const level = Math.min(6, section.depth);
      body.push(`<h${level} class="group">${escapeHtml(section.title)}</h${level}>`);
    } else {
      body.push(`<section class="chapter" data-writing="${escapeHtml(section.writingId)}">${blocks(section.blocks)}</section>`);
    }
  }
  if (doc.footnotes.length) {
    body.push(
      `<section class="footnotes"><hr><ol>${doc.footnotes
        .map((note) => `<li id="fn${note.number}" value="${note.number}">${inlines(note.children)} <a href="#fnref${note.number}" aria-label="Back to the text">↩</a></li>`)
        .join('')}</ol></section>`,
    );
  }
  if (doc.bibliography) {
    body.push(
      `<section class="bibliography"><h2>${escapeHtml(doc.bibliography.heading)}</h2>${doc.bibliography.entries
        .map((entry) => `<p>${inlines(entry.children)}</p>`)
        .join('')}</section>`,
    );
  }
  return `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(doc.title)}</title>
<style>
@page { size: ${paper}; margin: ${doc.setup.marginMm}mm; }
body { font-family: ${font}; line-height: ${doc.setup.lineSpacing}; max-width: 42rem; margin: 2rem auto; padding: 0 1rem; color: #1a1a1a; background: #fff; unicode-bidi: plaintext; }
@media print { body { max-width: none; margin: 0; padding: 0; } .chapter + .chapter, .group { break-before: page; } }
p, li, h1, h2, h3, h4, h5, h6, blockquote, td, th { unicode-bidi: plaintext; }
blockquote { margin-inline: 2em 0; }
table { border-collapse: collapse; } td, th { border: 1px solid #999; padding: 0.25em 0.5em; }
img { max-width: 100%; }
.footnotes { font-size: 0.9em; }
.bibliography p { padding-inline-start: 2em; text-indent: -2em; }
</style>
</head>
<body>
${body.join('\n')}
</body>
</html>
`;
}

function blocks(list: readonly ExportBlock[]): string {
  return list.map(block).join('\n');
}

function block(b: ExportBlock): string {
  switch (b.type) {
    case 'heading':
      return `<h${b.level}>${inlines(b.children)}</h${b.level}>`;
    case 'paragraph':
      return `<p>${inlines(b.children)}</p>`;
    case 'blockquote':
      return `<blockquote>${blocks(b.blocks)}</blockquote>`;
    case 'list': {
      const tag = b.ordered ? 'ol' : 'ul';
      const start = b.ordered && b.start !== 1 ? ` start="${b.start}"` : '';
      const items = b.items
        .map((item) => `<li>${item.checked === null ? '' : `<input type="checkbox" disabled${item.checked ? ' checked' : ''}> `}${blocks(item.blocks)}</li>`)
        .join('');
      return `<${tag}${start}>${items}</${tag}>`;
    }
    case 'table': {
      const cell = (tag: string, children: ExportInline[], i: number) =>
        `<${tag}${b.align[i] ? ` style="text-align:${b.align[i]}"` : ''}>${inlines(children)}</${tag}>`;
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

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
