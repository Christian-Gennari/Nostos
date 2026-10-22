import { Marked, type Token, type Tokens } from 'marked';

import { CITATION_SCHEME, type CitationRef } from '../../ui/markdown-editor/citation.extension';
import { footnoteMarkedExtension } from '../../ui/markdown-editor/footnote.extension';
import { citationMarkedExtension } from '../../ui/markdown-editor/citation.extension';
import type { ExportAlign } from './export-model';
import type { Slicer } from './export-slicer';

/**
 * A writing's Markdown, read with the Studio's own syntax (footnotes and live
 * citations, issues #1109 and #1157) into blocks the resolver works on. This
 * is the export's one reader of the Studio's Markdown; the citation link
 * grammar itself is `parseCitationHref`, shared with the editor.
 *
 * The blocks here still hold what only Nostos understands (`cite`, footnote
 * markers by their written number, footnote definitions); export-resolve.ts
 * turns them into the finished `ExportDocument`.
 */

export type RawInline =
  | { type: 'text'; text: string }
  | { type: 'emphasis' | 'strong' | 'strike'; children: RawInline[] }
  | { type: 'code'; text: string }
  | { type: 'link'; href: string; children: RawInline[] }
  | { type: 'image'; src: string; alt: string; title: string | null }
  | { type: 'break' }
  /** A live citation and the words it was last written with (Studio Markdown). */
  | { type: 'cite'; ref: CitationRef; text: string }
  /** `[^n]` as written. */
  | { type: 'fnref'; number: number }
  /** A marker the resolver has paired with its note (an index into its notes). */
  | { type: 'note'; id: number };

export interface RawListItem {
  blocks: RawBlock[];
  checked: boolean | null;
}

export type RawBlock =
  | { type: 'heading'; level: 1 | 2 | 3 | 4 | 5 | 6; children: RawInline[] }
  | { type: 'paragraph'; children: RawInline[] }
  | { type: 'blockquote'; blocks: RawBlock[] }
  | { type: 'list'; ordered: boolean; start: number; items: RawListItem[] }
  | { type: 'table'; align: ExportAlign[]; header: RawInline[][]; rows: RawInline[][][] }
  | { type: 'code'; language: string | null; text: string }
  | { type: 'rule' }
  /** `[^n]: …`, a paragraph of its own in the Studio. */
  | { type: 'fndef'; number: number; children: RawInline[] };

export interface ParseContext {
  /** Relative links and images are made absolute against this (the app's origin). */
  baseUrl: string | null;
  /** A `nostos:cite/` link the Studio cannot read: kept as its words, and said. */
  malformedCitation(words: string): void;
}

/** The Studio's Marked, with its footnote and citation syntax. */
const studioMarked = new Marked(footnoteMarkedExtension, citationMarkedExtension);
/** `lexer` takes options in place of the instance's, so they carry the extensions with them. */
const LEX_OPTIONS = { ...studioMarked.defaults, gfm: true, breaks: false };

/** Writings longer than this are read in pieces, so a long one does not hold the page. */
export const PIECE_CHARS = 40_000;

/**
 * Splits Markdown into pieces of about `size` characters, only where a new
 * top-level paragraph starts: after a blank line, outside a code fence, and
 * before a line that does not continue a list, quote or table.
 */
export function splitMarkdown(markdown: string, size = PIECE_CHARS): string[] {
  if (markdown.length <= size) return [markdown];
  const lines = markdown.split('\n');
  const pieces: string[] = [];
  let current: string[] = [];
  let length = 0;
  let fence: string | null = null;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const fenceMatch = /^ {0,3}(`{3,}|~{3,})/.exec(line);
    if (fenceMatch) {
      if (!fence) fence = fenceMatch[1][0];
      else if (fenceMatch[1][0] === fence) fence = null;
    }
    const previousBlank = i > 0 && lines[i - 1].trim() === '';
    if (
      length >= size &&
      !fence &&
      !fenceMatch &&
      previousBlank &&
      line.trim() !== '' &&
      !/^(\s|[-*+]\s|\d+[.)]\s|>|\|)/.test(line)
    ) {
      pieces.push(current.join('\n'));
      current = [];
      length = 0;
    }
    current.push(line);
    length += line.length + 1;
  }
  if (current.length) pieces.push(current.join('\n'));
  return pieces;
}

/** Reads a writing's Markdown into blocks, yielding to the page between pieces. */
export async function parseMarkdown(markdown: string, context: ParseContext, slicer: Slicer): Promise<RawBlock[]> {
  const blocks: RawBlock[] = [];
  for (const piece of splitMarkdown((markdown ?? '').replace(/\r\n?/g, '\n'))) {
    await slicer.tick();
    const tokens = studioMarked.lexer(piece, LEX_OPTIONS);
    for (const token of tokens) {
      blocks.push(...blockFrom(token, context));
      await slicer.tick();
    }
  }
  return blocks;
}

/** A citation's words (a little Markdown: italics, escapes) as inlines. */
export function parseInlineMarkdown(markdown: string, context: ParseContext): RawInline[] {
  const [first] = studioMarked.lexer(markdown ?? '', LEX_OPTIONS);
  if (!first || !('tokens' in first) || !first.tokens) return markdown ? [{ type: 'text', text: markdown }] : [];
  return inlinesFrom(first.tokens, context);
}

function blockFrom(token: Token, context: ParseContext): RawBlock[] {
  switch (token.type) {
    case 'space':
    case 'def':
    case 'checkbox':
      return [];
    case 'heading': {
      const level = Math.min(6, Math.max(1, (token as Tokens.Heading).depth)) as 1 | 2 | 3 | 4 | 5 | 6;
      return [{ type: 'heading', level, children: inlinesFrom(token.tokens ?? [], context) }];
    }
    case 'paragraph':
      return [{ type: 'paragraph', children: inlinesFrom(token.tokens ?? [], context) }];
    case 'text':
      // A tight list item's text.
      return [{ type: 'paragraph', children: token.tokens ? inlinesFrom(token.tokens, context) : textInlines(token.text) }];
    case 'blockquote':
      return [{ type: 'blockquote', blocks: blocksFrom(token.tokens ?? [], context) }];
    case 'list': {
      const list = token as Tokens.List;
      const start = typeof list.start === 'number' ? list.start : 1;
      return [
        {
          type: 'list',
          ordered: list.ordered,
          start,
          items: list.items.map((item) => ({
            blocks: blocksFrom(item.tokens ?? [], context),
            checked: item.task ? !!item.checked : null,
          })),
        },
      ];
    }
    case 'table': {
      const table = token as Tokens.Table;
      return [
        {
          type: 'table',
          align: table.align.map((a) => a ?? null),
          header: table.header.map((cell) => inlinesFrom(cell.tokens, context)),
          rows: table.rows.map((row) => row.map((cell) => inlinesFrom(cell.tokens, context))),
        },
      ];
    }
    case 'code': {
      const code = token as Tokens.Code;
      return [{ type: 'code', language: code.lang?.trim() || null, text: code.text }];
    }
    case 'hr':
      return [{ type: 'rule' }];
    case 'html': {
      // Raw HTML has no format-neutral meaning: its words stay, its tags go.
      const text = decodeEntities(stripTags(token.text ?? '')).trim();
      return text ? [{ type: 'paragraph', children: [{ type: 'text', text }] }] : [];
    }
    case 'footnoteDefinition':
      return [{ type: 'fndef', number: Number(token['number']), children: inlinesFrom(token.tokens ?? [], context) }];
    default: {
      const generic = token as Tokens.Generic;
      if (generic.tokens?.length) return [{ type: 'paragraph', children: inlinesFrom(generic.tokens, context) }];
      return 'text' in token && typeof token.text === 'string' && token.text.trim()
        ? [{ type: 'paragraph', children: textInlines(token.text) }]
        : [];
    }
  }
}

function blocksFrom(tokens: readonly Token[], context: ParseContext): RawBlock[] {
  return tokens.flatMap((token) => blockFrom(token, context));
}

function inlinesFrom(tokens: readonly Token[], context: ParseContext): RawInline[] {
  const out: RawInline[] = [];
  for (const token of tokens) out.push(...inlineFrom(token, context));
  return mergeText(out);
}

function inlineFrom(token: Token, context: ParseContext): RawInline[] {
  switch (token.type) {
    case 'text':
      return token.tokens?.length ? inlinesFrom(token.tokens, context) : textInlines(token.text);
    case 'escape':
      return [{ type: 'text', text: token.text }];
    case 'strong':
      return [{ type: 'strong', children: inlinesFrom(token.tokens ?? [], context) }];
    case 'em':
      return [{ type: 'emphasis', children: inlinesFrom(token.tokens ?? [], context) }];
    case 'del':
      return [{ type: 'strike', children: inlinesFrom(token.tokens ?? [], context) }];
    case 'codespan':
      return [{ type: 'code', text: decodeEntities(token.text) }];
    case 'br':
      return [{ type: 'break' }];
    case 'link': {
      const link = token as Tokens.Link;
      const children = inlinesFrom(link.tokens ?? [], context);
      const href = (link.href ?? '').trim();
      if (href.toLowerCase().startsWith('nostos:')) {
        // A Nostos address outside the Studio means nothing: its words stay.
        if (href.startsWith(CITATION_SCHEME)) context.malformedCitation(plain(children));
        return children;
      }
      const safe = safeUrl(href, context.baseUrl, false);
      return safe ? [{ type: 'link', href: safe, children }] : children;
    }
    case 'image': {
      const image = token as Tokens.Image;
      const src = safeUrl((image.href ?? '').trim(), context.baseUrl, true);
      const alt = decodeEntities(image.text ?? '');
      // An address that cannot load fails the same way a broken one does.
      return [{ type: 'image', src: src ?? '', alt, title: image.title ?? null }];
    }
    case 'html':
      return /^<br\s*\/?>$/i.test((token.text ?? '').trim()) ? [{ type: 'break' }] : [];
    case 'footnoteRef':
      return [{ type: 'fnref', number: Number(token['number']) }];
    case 'citation':
      return [{ type: 'cite', ref: token['ref'] as CitationRef, text: String(token['text'] ?? '') }];
    default: {
      const generic = token as Tokens.Generic;
      if (generic.tokens?.length) return inlinesFrom(generic.tokens, context);
      return typeof generic['text'] === 'string' ? textInlines(generic['text']) : [];
    }
  }
}

function textInlines(text: string): RawInline[] {
  const decoded = decodeEntities(text ?? '');
  return decoded ? [{ type: 'text', text: decoded }] : [];
}

/** Adjacent text runs become one, so the resolver sees words as they read. */
export function mergeText(inlines: RawInline[]): RawInline[] {
  const out: RawInline[] = [];
  for (const inline of inlines) {
    const last = out[out.length - 1];
    if (inline.type === 'text' && last?.type === 'text') out[out.length - 1] = { type: 'text', text: last.text + inline.text };
    else if (inline.type !== 'text' || inline.text) out.push(inline);
  }
  return out;
}

function plain(inlines: readonly RawInline[]): string {
  return inlines
    .map((inline) => {
      if (inline.type === 'text' || inline.type === 'code') return inline.text;
      if (inline.type === 'cite') return inline.text;
      if ('children' in inline) return plain(inline.children);
      return '';
    })
    .join('');
}

function stripTags(html: string): string {
  return html.replace(/<br\s*\/?>/gi, '\n').replace(/<[^>]*>/g, '');
}

const ENTITIES: Record<string, string> = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

/** Marked keeps entities as written; on the page they read as their characters. */
export function decodeEntities(text: string): string {
  return text.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (match, body: string) => {
    if (body[0] === '#') {
      const code = body[1] === 'x' || body[1] === 'X' ? parseInt(body.slice(2), 16) : Number(body.slice(1));
      return Number.isInteger(code) && code > 0 && code <= 0x10ffff ? String.fromCodePoint(code) : match;
    }
    return ENTITIES[body.toLowerCase()] ?? match;
  });
}

/**
 * A link or image address safe to hand to another program: web, mail and
 * (for images) inline image data. A relative address is made absolute against
 * the app, since the exported file lives elsewhere. Anything else is null.
 */
export function safeUrl(href: string, baseUrl: string | null, image: boolean): string | null {
  if (!href) return null;
  if (image && /^data:image\/(png|jpe?g|gif|webp|svg\+xml);/i.test(href)) return href;
  if (!image && href.startsWith('#')) return href;
  const allowed = image ? ['http:', 'https:'] : ['http:', 'https:', 'mailto:'];
  const absolute = /^[a-z][a-z0-9+.-]*:/i.test(href);
  let url: URL;
  try {
    url = absolute || !baseUrl ? new URL(href) : new URL(href, baseUrl);
  } catch {
    return null;
  }
  if (!allowed.includes(url.protocol)) return null;
  // An absolute address stays exactly as the writer wrote it.
  return absolute ? href : url.href;
}
