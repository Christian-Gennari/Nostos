import { renderExportBody, NOTES_HEADING } from './export-html';
import { chapterTitle } from './export-layout';
import { type ExportBlock, type ExportDocument, type ExportInline, inlineText } from './export-model';

/**
 * Copy as rich text (issue #1165). The clipboard gets HTML for editors that
 * read formatting (Substack, Google Docs, Word) and plain text for those that
 * don't. None of those editors make footnotes from pasted HTML, so the notes
 * are numbered endnotes under "Notes", before the bibliography.
 */
export interface RichTextClip {
  html: string;
  text: string;
}

export function renderExportRichText(doc: ExportDocument): RichTextClip {
  return {
    html: `<meta charset="utf-8"><div>${renderExportBody(doc, { notes: 'document' })}</div>`,
    text: renderExportPlainText(doc),
  };
}

/** The document as plain text: blocks a blank line apart, notes as [1]. */
export function renderExportPlainText(doc: ExportDocument): string {
  const out: string[] = [];
  for (const section of doc.sections) {
    if (section.type === 'group') {
      out.push(section.title);
      continue;
    }
    const title = chapterTitle(doc, section);
    if (title) out.push(title.text);
    out.push(...section.blocks.map((b) => plainBlock(b)));
  }
  if (doc.footnotes.length) {
    out.push(NOTES_HEADING, doc.footnotes.map((note) => `${note.number}. ${plain(note.children)}`).join('\n'));
  }
  if (doc.bibliography) {
    out.push(doc.bibliography.heading, ...doc.bibliography.entries.map((entry) => plain(entry.children)));
  }
  return out.filter((part) => part !== '').join('\n\n') + '\n';
}

function plainBlock(b: ExportBlock, depth = 0): string {
  switch (b.type) {
    case 'heading':
    case 'paragraph':
      return plain(b.children);
    case 'blockquote':
      return b.blocks
        .map((inner) => plainBlock(inner, depth))
        .join('\n\n')
        .split('\n')
        .map((line) => `    ${line}`)
        .join('\n');
    case 'list':
      return b.items
        .map((item, i) => {
          const marker = b.ordered ? `${b.start + i}.` : '•';
          const box = item.checked === null ? '' : item.checked ? '☒ ' : '☐ ';
          const pad = '  '.repeat(depth);
          const [first = '', ...rest] = item.blocks.map((inner) => plainBlock(inner, depth + 1));
          return [`${pad}${marker} ${box}${first}`, ...rest].join('\n');
        })
        .join('\n');
    case 'table':
      return [b.header, ...b.rows].map((row) => row.map(plain).join('\t')).join('\n');
    case 'code':
      return b.text.replace(/\n$/, '');
    case 'rule':
      return '* * *';
  }
}

/** Some inlines as text, each note called as [1]. */
function plain(list: readonly ExportInline[]): string {
  return list
    .map((inline) => {
      switch (inline.type) {
        case 'footnoteRef':
          return `[${inline.number}]`;
        case 'emphasis':
        case 'strong':
        case 'strike':
        case 'link':
          return plain(inline.children);
        default:
          return inlineText([inline]);
      }
    })
    .join('');
}

/**
 * Puts the document on the clipboard. Must be called straight from the click
 * that asked for it: browsers only let a page write the clipboard during a
 * user's gesture. Rejects with a `NotAllowedError` when the browser or the
 * writer has said no.
 */
export async function copyRichText(clip: RichTextClip): Promise<void> {
  const clipboard = typeof navigator !== 'undefined' ? navigator.clipboard : undefined;
  if (clipboard?.write && typeof ClipboardItem !== 'undefined') {
    await clipboard.write([
      new ClipboardItem({
        'text/html': new Blob([clip.html], { type: 'text/html' }),
        'text/plain': new Blob([clip.text], { type: 'text/plain' }),
      }),
    ]);
    return;
  }
  // Browsers without ClipboardItem still let the copy command fill both kinds.
  let written = false;
  const onCopy = (event: ClipboardEvent) => {
    if (!event.clipboardData) return;
    event.clipboardData.setData('text/html', clip.html);
    event.clipboardData.setData('text/plain', clip.text);
    event.preventDefault();
    written = true;
  };
  document.addEventListener('copy', onCopy);
  try {
    document.execCommand('copy');
  } finally {
    document.removeEventListener('copy', onCopy);
  }
  if (!written) throw new DOMException('The browser did not allow copying.', 'NotAllowedError');
}
