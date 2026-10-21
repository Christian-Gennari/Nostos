import TurndownService from 'turndown';
import { gfm } from 'turndown-plugin-gfm';
import { Marked } from 'marked';

import { FOOTNOTE_ATTRIBUTE, footnoteMarkedExtension } from './footnote.extension';
import { citationElementMarkdown, citationMarkedExtension } from './citation.extension';

/**
 * The Studio stores Markdown; the editor edits HTML. These two functions are
 * the only bridge between them (issue #1037), so what the editor offers and
 * what survives a save are decided in one place.
 *
 * Turndown's defaults flatten anything they have no rule for: before this
 * module, `<u>` and `<table>` came back as bare text on the next save. The GFM
 * plugin gives tables (and strikethrough) a Markdown form, and the editor no
 * longer offers underline at all, because Markdown has no way to keep it.
 */

/** The Studio's own Marked, so its footnote and citation syntax do not reach other surfaces. */
const studioMarked = new Marked(footnoteMarkedExtension, citationMarkedExtension);

export function markdownToHtml(markdown: string, options: { breaks?: boolean } = {}): string {
  return studioMarked.parse(markdown ?? '', { async: false, gfm: true, breaks: options.breaks ?? false }) as string;
}

let sharedTurndown: TurndownService | null = null;

export function htmlToMarkdown(html: string): string {
  sharedTurndown ??= createTurndown();
  // TipTap writes a <colgroup> of column widths ahead of the rows. The GFM
  // plugin only treats a table's first row as its header when nothing precedes
  // that row's <tbody>, so with the colgroup present every table was kept as
  // raw HTML. Column widths are presentation, not content.
  return sharedTurndown.turndown((html ?? '').replace(/<colgroup>[\s\S]*?<\/colgroup>/g, ''));
}

function createTurndown(): TurndownService {
  const service = new TurndownService({
    headingStyle: 'atx',
    codeBlockStyle: 'fenced',
    // Bullet and emphasis markers stay at Turndown's defaults, as under
    // TinyMCE, so opening and saving an existing writing does not rewrite
    // every list and italic in it.
  });
  service.use(gfm);

  // TipTap wraps every table cell's text in a paragraph. Turndown's paragraph
  // rule pads it with blank lines, which would break the row across lines and
  // lose the table on the next load. Inside a cell a paragraph is just text,
  // and a literal pipe has to be escaped or it would start a new column.
  service.addRule('tableCellParagraph', {
    filter: (node) =>
      node.nodeName === 'P' && !!node.parentNode && ['TH', 'TD'].includes(node.parentNode.nodeName),
    replacement: (content, node) => {
      const text = content.replace(/\n+/g, ' ').replace(/\|/g, '\\|');
      return node.nextSibling ? `${text} ` : text;
    },
  });

  // TipTap also wraps each list item's text in a paragraph, which Turndown
  // turns into a loose list: a blank, space-only line after every item. The
  // writings saved under TinyMCE are tight lists, so a paragraph inside an item
  // is inline unless another paragraph follows it in the same item.
  service.addRule('listItemParagraph', {
    filter: (node) => node.nodeName === 'P' && node.parentNode?.nodeName === 'LI',
    replacement: (content, node) => {
      const next = (node as HTMLElement).nextElementSibling;
      return next?.nodeName === 'P' ? `${content}\n\n` : content;
    },
  });

  // A footnote marker (issue #1109) goes back to `[^n]`, unescaped.
  service.addRule('footnoteRef', {
    filter: (node) => node.nodeName === 'SUP' && (node as HTMLElement).hasAttribute(FOOTNOTE_ATTRIBUTE),
    replacement: (_content, node) => `[^${(node as HTMLElement).getAttribute(FOOTNOTE_ATTRIBUTE)}]`,
  });

  // A live citation (issue #1157) goes back to its `nostos:cite/` link, with
  // the text the Studio last wrote for it.
  service.addRule('citation', {
    filter: (node) => node.nodeName === 'CITE' && (node as HTMLElement).hasAttribute('data-cite-book'),
    replacement: (content, node) => citationElementMarkdown(node as HTMLElement) ?? content,
  });

  return service;
}
