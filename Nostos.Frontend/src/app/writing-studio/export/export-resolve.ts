import { BIBLIOGRAPHY_HEADINGS, type CitationRenderer } from '../../ui/markdown-editor/live-citations';
import type { CitationStyle } from '../citations/citation-styles';
import type {
  ExportBlock,
  ExportDocument,
  ExportInline,
  ExportSection,
  ExportWarning,
  ExportWarningKind,
  PageSetup,
} from './export-model';
import { inlineText } from './export-model';
import { type ParseContext, type RawBlock, type RawInline, mergeText, parseInlineMarkdown, parseMarkdown } from './export-parse';
import { Slicer, type SlicerOptions } from './export-slicer';

/**
 * The export's resolve step (issue #1164): one or more writings' Markdown and
 * the chosen page setup in, one finished `ExportDocument` out. Every export
 * format calls this (or `ExportService.resolve()`, which gathers the inputs).
 *
 * What it does, in order, and why it matches the Studio:
 *
 * 1. Reads each chapter with the Studio's Markdown syntax (export-parse.ts).
 * 2. Takes out each reference list the Studio wrote (a run of paragraphs that
 *    are each one `as=bib` citation, and its "Bibliography" / "References" /
 *    "Works Cited" heading). Their books are kept for the one bibliography.
 * 3. Moves citations between the sentence and footnotes for the chosen style,
 *    by the rules `refreshCitations` (live-citations.ts) applies in the
 *    editor: a note style puts every citation in a footnote (an attribution
 *    alone under a quote puts its marker after the quote's last word); an
 *    author–date or house style brings back each footnote that is only one
 *    citation and has one marker, on its own line where the Studio would put
 *    it there. A note the writer wrote stays a note.
 * 4. Pairs footnote markers with their notes. A marker with no note is left
 *    out; a note with no marker is dropped when it was only the Studio's
 *    citation and kept as a plain paragraph when it is the writer's words.
 *    Either way the writer is told.
 * 5. Numbers footnotes once through the whole document, and writes every
 *    citation again, notes in that order, so a book's first note is the full
 *    one and later notes (in any later chapter) the short form.
 * 6. Writes one bibliography, each book once: everything cited, the books
 *    kept with the writings, and books the writer's lists named without a
 *    live citation.
 * 7. Checks each image; one that does not load becomes "[image: alt]".
 *
 * A citation whose book cannot be read keeps the words it was last written
 * with and is listed in `warnings`. Nothing here blocks the export.
 */

export interface ExportChapterInput {
  writingId: string;
  title: string;
  markdown: string;
  /** The folders between the exported folder and this writing, outermost first. */
  groupPath?: readonly { id: string; title: string }[];
}

export interface ResolveExportInput {
  /** The document's title: the writing's or folder's name. */
  title: string;
  /** In export order. Empty writings are skipped. */
  chapters: readonly ExportChapterInput[];
  setup: PageSetup;
  /** The citation style `setup.citationStyle` names. */
  style: CitationStyle;
  /** Writes citations in that style (`CitationService.renderer`). */
  renderer: CitationRenderer;
  /** True when an image loads. Without it every image is taken to load. */
  checkImage?: (src: string) => Promise<boolean>;
  /** Relative links and images are made absolute against this. */
  baseUrl?: string | null;
  /** Time slicing, so a long document does not freeze the page. */
  slicing?: SlicerOptions;
}

/** A writing with nothing in it cannot be exported. */
export function hasExportableContent(markdown: string | null | undefined): boolean {
  return /\S/.test(markdown ?? '');
}

interface Container {
  inlines: RawInline[];
  /** The paragraph or heading holding them; null for a table cell. */
  block: RawBlock | null;
  /** The list the block stands in, so a paragraph can be taken out or added after. */
  siblings: RawBlock[] | null;
  inQuote: boolean;
  chapter: number;
}

interface Note {
  inlines: RawInline[];
  chapter: number;
}

interface ChapterWork {
  input: ExportChapterInput;
  blocks: RawBlock[];
}

const WORD = /[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*/gu;

export async function resolveExport(input: ResolveExportInput): Promise<ExportDocument> {
  const slicer = new Slicer(input.slicing);
  const renderer = input.renderer;
  const warnings = new WarningList();
  const notes: Note[] = [];
  const newNote = (inlines: RawInline[], chapter: number): number => notes.push({ inlines, chapter }) - 1;

  // 1. Read.
  const chapters: ChapterWork[] = [];
  for (const chapter of input.chapters) {
    if (!hasExportableContent(chapter.markdown)) continue;
    const index = chapters.length;
    const context: ParseContext = {
      baseUrl: input.baseUrl ?? null,
      malformedCitation: (words) =>
        warnings.add('citation', index, `${shorten(words)}: citation link couldn’t be read, kept as written`),
    };
    chapters.push({ input: chapter, blocks: await parseMarkdown(chapter.markdown, context, slicer) });
  }
  const chapterTitle = (index: number) => (chapters.length > 1 ? chapters[index]?.input.title ?? null : null);

  // 2. Reference lists the Studio wrote.
  const listed = new Map<string, string>();
  let hadBibliography = false;
  chapters.forEach((chapter) => {
    for (const entry of takeBibliographies(chapter.blocks)) {
      hadBibliography = true;
      if (!listed.has(entry.book)) listed.set(entry.book, entry.text);
    }
  });

  // 3 and 4. Placement and pairing, chapter by chapter.
  for (let index = 0; index < chapters.length; index++) {
    const blocks = chapters[index].blocks;
    if (renderer.placement === 'note') moveCitationsToNotes(blocks, index, newNote);
    else moveNotesIntoText(blocks, index, renderer.placement);
    pairFootnotes(blocks, index, newNote, (message) => warnings.add('footnote', index, message));
    await slicer.tick();
  }
  // A marker inside a note cannot be followed in any format.
  for (const note of notes) stripMarkers(note.inlines);

  // 5. Number the notes in reading order, then write every citation again.
  const numbers = new Map<number, number>();
  const order: number[] = [];
  for (const chapter of chapters) {
    for (const container of containers(chapter.blocks, 0)) {
      walkInlines(container.inlines, (inline) => {
        if (inline.type === 'note' && !numbers.has(inline.id)) {
          numbers.set(inline.id, order.length + 1);
          order.push(inline.id);
        }
      });
    }
    await slicer.tick();
  }

  const written = new Map<RawInline, string>();
  const cited = new Set<string>();
  const citedBooks: string[] = [];
  const noteCite = (cite: Extract<RawInline, { type: 'cite' }>, chapter: number) => {
    const subsequent = cited.has(cite.ref.book);
    const text = renderer.placement === 'note' ? renderer.note(cite.ref, subsequent) : renderer.inText(cite.ref);
    cited.add(cite.ref.book);
    record(cite, text, chapter);
  };
  const record = (cite: Extract<RawInline, { type: 'cite' }>, text: string | null, chapter: number) => {
    if (!citedBooks.includes(cite.ref.book)) citedBooks.push(cite.ref.book);
    if (text === null) {
      warnings.add(
        'citation',
        chapter,
        `${shorten(plainCitation(cite.text))}: book no longer in your library, kept as written`,
      );
      written.set(cite, cite.text);
    } else {
      written.set(cite, text);
    }
  };
  for (const id of order) {
    const note = notes[id];
    walkInlines(note.inlines, (inline) => {
      if (inline.type === 'cite' && inline.ref.as !== 'bib') noteCite(inline, note.chapter);
    });
    await slicer.tick();
  }
  for (let index = 0; index < chapters.length; index++) {
    for (const container of containers(chapters[index].blocks, index)) {
      walkInlines(container.inlines, (inline) => {
        if (inline.type === 'cite' && inline.ref.as !== 'bib') record(inline, renderer.inText(inline.ref), index);
      });
    }
    await slicer.tick();
  }

  // 6. One bibliography.
  let bibliography: ExportDocument['bibliography'] = null;
  if (hadBibliography) {
    const books: string[] = [];
    const add = (book: string) => book && !books.includes(book) && books.push(book);
    citedBooks.forEach(add);
    renderer.bibliographyBooks.forEach(add);
    [...listed.keys()].forEach(add);
    let entries = renderer.bibliography(books);
    if (entries === null) {
      // A book that cannot be read stops the whole list in the Studio; here
      // the readable ones are written and the rest keep their last words.
      const readable = books.filter((book) => renderer.bibliography([book]) !== null);
      entries = readable.length ? (renderer.bibliography(readable) ?? []) : [];
    }
    const seen = new Set<string>();
    const all = entries.filter((entry) => !seen.has(entry.book) && seen.add(entry.book));
    for (const [book, text] of listed) {
      if (seen.has(book)) continue;
      seen.add(book);
      all.push({ book, text });
      warnings.add(
        'citation',
        null,
        `Bibliography entry “${shorten(plainCitation(text))}”: book no longer in your library, kept as written`,
      );
    }
    const parseContext: ParseContext = { baseUrl: input.baseUrl ?? null, malformedCitation: () => undefined };
    bibliography = {
      heading: renderer.bibliographyHeading,
      entries: all.map((entry) => ({ book: entry.book, children: toExportInlines(parseInlineMarkdown(entry.text, parseContext), numbers, written, new Set()) })),
    };
  }

  // 7. Images.
  const failed = await checkImages(chapters, notes, input.checkImage, slicer);

  // The finished document.
  const parseContext: ParseContext = { baseUrl: input.baseUrl ?? null, malformedCitation: () => undefined };
  const resolveCite = (cite: Extract<RawInline, { type: 'cite' }>) => parseInlineMarkdown(written.get(cite) ?? cite.text, parseContext);
  const convert = (inlines: RawInline[], chapter: number) => toExportInlines(inlines, numbers, written, failed, resolveCite, (alt, src) => warnings.add('image', chapter, imageMessage(alt, src)));

  const sections: ExportSection[] = [];
  let previousPath: readonly { id: string; title: string }[] = [];
  let words = 0;
  for (let index = 0; index < chapters.length; index++) {
    const chapter = chapters[index];
    const path = chapter.input.groupPath ?? [];
    let same = 0;
    while (same < path.length && same < previousPath.length && path[same].id === previousPath[same].id) same++;
    for (let depth = same; depth < path.length; depth++) {
      sections.push({ type: 'group', folderId: path[depth].id, title: path[depth].title, depth: depth + 1 });
    }
    previousPath = path;
    const blocks = toExportBlocks(chapter.blocks, (inlines) => convert(inlines, index));
    words += countWords(blocks);
    sections.push({ type: 'chapter', writingId: chapter.input.writingId, title: chapter.input.title, depth: path.length, blocks });
    await slicer.tick();
  }

  const footnotes = order.map((id) => ({ number: numbers.get(id)!, children: convert(notes[id].inlines, notes[id].chapter) }));
  slicer.finish();

  return {
    title: input.title,
    setup: input.setup,
    citationStyle: { id: input.style.id, label: input.style.label, kind: input.style.kind },
    sections,
    footnotes,
    bibliography,
    warnings: warnings.list(chapterTitle),
    words,
  };
}

/* ------------------------------------------------------------------ */
/* Walking                                                             */
/* ------------------------------------------------------------------ */

/** Every run of inlines in reading order, footnote definitions included. */
function containers(blocks: RawBlock[], chapter: number, inQuote = false): Container[] {
  const out: Container[] = [];
  for (const block of blocks) {
    switch (block.type) {
      case 'paragraph':
      case 'heading':
      case 'fndef':
        out.push({ inlines: block.children, block, siblings: blocks, inQuote, chapter });
        break;
      case 'blockquote':
        out.push(...containers(block.blocks, chapter, true));
        break;
      case 'list':
        // Only a paragraph standing directly in a quote counts as in it.
        for (const item of block.items) out.push(...containers(item.blocks, chapter, false));
        break;
      case 'table':
        for (const cell of [...block.header, ...block.rows.flat()]) out.push({ inlines: cell, block: null, siblings: null, inQuote, chapter });
        break;
    }
  }
  return out;
}

function walkInlines(inlines: RawInline[], visit: (inline: RawInline, list: RawInline[]) => void): void {
  for (const inline of [...inlines]) {
    visit(inline, inlines);
    if ('children' in inline) walkInlines(inline.children, visit);
  }
}

type Cite = Extract<RawInline, { type: 'cite' }>;

function isBlank(inline: RawInline): boolean {
  return inline.type === 'text' && !inline.text.trim();
}

/** One live citation and nothing else but spaces (a definition's or a bibliography entry's content). */
function onlyCitation(inlines: readonly RawInline[]): Cite | null {
  let cite: Cite | null = null;
  for (const inline of inlines) {
    if (inline.type === 'cite' && !cite) cite = inline;
    else if (!isBlank(inline)) return null;
  }
  return cite;
}

/** The last character a run of inlines reads as; an atom reads as nothing. */
function lastChar(inline: RawInline | undefined): string {
  if (!inline) return '';
  if (inline.type === 'text' || inline.type === 'code') return inline.text.slice(-1);
  if ('children' in inline) return lastChar(inline.children[inline.children.length - 1]);
  return '';
}

/* ------------------------------------------------------------------ */
/* 2. Bibliographies                                                   */
/* ------------------------------------------------------------------ */

function bibliographyEntry(block: RawBlock): Cite | null {
  if (block.type !== 'paragraph') return null;
  const cite = onlyCitation(block.children);
  return cite?.ref.as === 'bib' ? cite : null;
}

/** Takes each reference list the Studio wrote out of a chapter, and says what it listed. */
function takeBibliographies(blocks: RawBlock[]): { book: string; text: string }[] {
  const listed: { book: string; text: string }[] = [];
  for (let i = 0; i < blocks.length; i++) {
    if (!bibliographyEntry(blocks[i])) continue;
    let end = i;
    while (end < blocks.length && bibliographyEntry(blocks[end])) {
      const cite = bibliographyEntry(blocks[end])!;
      listed.push({ book: cite.ref.book, text: cite.text });
      end++;
    }
    let start = i;
    const before = blocks[i - 1];
    if (before?.type === 'heading' && BIBLIOGRAPHY_HEADINGS.includes(plainInlines(before.children).trim())) start = i - 1;
    blocks.splice(start, end - start);
    i = start - 1;
  }
  return listed;
}

/* ------------------------------------------------------------------ */
/* 3. Placement                                                        */
/* ------------------------------------------------------------------ */

/** A note style: every citation in the writing becomes a footnote. */
function moveCitationsToNotes(blocks: RawBlock[], chapter: number, newNote: (inlines: RawInline[], chapter: number) => number): void {
  for (const container of containers(blocks, chapter)) {
    if (container.block?.type === 'fndef') continue;
    let moved = false;
    const moveIn = (list: RawInline[]) => {
      for (let i = 0; i < list.length; i++) {
        const inline = list[i];
        if (inline.type === 'cite' && inline.ref.as !== 'bib') {
          const id = newNote([inline], chapter);
          list[i] = { type: 'note', id };
          moved = true;
          // "said so (Kant, 1992)." becomes "said so¹." — the space goes with it.
          const before = list[i - 1];
          if (before?.type === 'text' && before.text.endsWith(' ')) {
            const text = before.text.slice(0, -1);
            if (text) list[i - 1] = { type: 'text', text };
            else {
              list.splice(i - 1, 1);
              i--;
            }
          }
        } else if ('children' in inline) {
          moveIn(inline.children);
        }
      }
    };
    const aloneBefore = container.block?.type === 'paragraph' && container.inlines.every((i) => i.type === 'cite' || i.type === 'fnref' || i.type === 'note' || isBlank(i));
    moveIn(container.inlines);
    if (!moved || !aloneBefore || !container.siblings || !container.block) continue;
    // An attribution alone under a quote or a paragraph: its marker goes after
    // that paragraph's last word, and the attribution's own line goes.
    const index = container.siblings.indexOf(container.block);
    const previous = container.siblings[index - 1];
    if (previous?.type === 'paragraph' && previous.children.length > 0) {
      previous.children.push(...container.inlines.filter((i) => !isBlank(i)));
      container.siblings.splice(index, 1);
    }
  }
}

/** An author–date or house style: a footnote that is only the Studio's citation goes back into the sentence. */
function moveNotesIntoText(blocks: RawBlock[], chapter: number, placement: 'in-text' | 'house'): void {
  const all = containers(blocks, chapter);
  const markers = new Map<number, { inline: RawInline; container: Container }[]>();
  for (const container of all) {
    if (container.block?.type === 'fndef') continue;
    walkInlines(container.inlines, (inline, list) => {
      if (inline.type !== 'fnref') return;
      const found = markers.get(inline.number) ?? [];
      // A marker inside emphasis stands in that run, not the container's.
      found.push({ inline, container: list === container.inlines ? container : { ...container, inlines: list, block: null, siblings: null } });
      markers.set(inline.number, found);
    });
  }
  const definitions = blocks.filter((block): block is Extract<RawBlock, { type: 'fndef' }> => block.type === 'fndef');
  for (const definition of definitions) {
    const cite = onlyCitation(definition.children);
    const matching = markers.get(definition.number) ?? [];
    if (!cite || matching.length !== 1) continue;
    const { inline: marker, container } = matching[0];
    const list = container.inlines;
    const at = list.indexOf(marker);
    if (at < 0) continue;
    const atEnd = list.slice(at + 1).every(isBlank);
    const ownParagraph =
      atEnd &&
      container.block?.type === 'paragraph' &&
      ((placement === 'house' && (cite.ref.as === 'quote' || cite.ref.as === 'note')) ||
        (placement === 'in-text' && cite.ref.as === 'quote' && container.inQuote));
    if (ownParagraph && container.siblings && container.block) {
      list.splice(at, 1);
      const index = container.siblings.indexOf(container.block);
      container.siblings.splice(index + 1, 0, { type: 'paragraph', children: [cite] });
    } else {
      const before = lastChar(list[at - 1]);
      list.splice(at, 1, ...(before === '' || /\s/.test(before) ? [cite] : [{ type: 'text', text: ' ' } as RawInline, cite]));
    }
    blocks.splice(blocks.indexOf(definition), 1);
  }
}

/* ------------------------------------------------------------------ */
/* 4. Pairing                                                          */
/* ------------------------------------------------------------------ */

function pairFootnotes(
  blocks: RawBlock[],
  chapter: number,
  newNote: (inlines: RawInline[], chapter: number) => number,
  warn: (message: string) => void,
): void {
  const definitions = blocks.filter((block): block is Extract<RawBlock, { type: 'fndef' }> => block.type === 'fndef');
  const byNumber = new Map<number, Extract<RawBlock, { type: 'fndef' }>>();
  for (const definition of definitions) if (!byNumber.has(definition.number)) byNumber.set(definition.number, definition);
  const used = new Set<RawBlock>();

  for (const container of containers(blocks, chapter)) {
    if (container.block?.type === 'fndef') continue;
    const pair = (list: RawInline[]) => {
      for (let i = 0; i < list.length; i++) {
        const inline = list[i];
        if (inline.type === 'fnref') {
          const definition = byNumber.get(inline.number);
          if (definition) {
            // A note called twice gets its words at each call.
            const words = used.has(definition) ? structuredClone(definition.children) : definition.children;
            used.add(definition);
            list[i] = { type: 'note', id: newNote(words, chapter) };
          } else {
            warn(`Footnote marker ${inline.number}: no note, left out`);
            list.splice(i, 1);
            i--;
          }
        } else if ('children' in inline) {
          pair(inline.children);
        }
      }
    };
    pair(container.inlines);
  }

  for (const definition of definitions) {
    const index = blocks.indexOf(definition);
    if (used.has(definition)) {
      blocks.splice(index, 1);
    } else if (onlyCitation(definition.children)) {
      // The Studio's own note without its marker: the Studio drops it too.
      blocks.splice(index, 1);
    } else {
      warn(`Footnote ${definition.number}: no marker in the text, kept as a paragraph`);
      blocks.splice(index, 1, { type: 'paragraph', children: definition.children });
    }
  }
}

function stripMarkers(inlines: RawInline[]): void {
  for (let i = inlines.length - 1; i >= 0; i--) {
    const inline = inlines[i];
    if (inline.type === 'fnref' || inline.type === 'note') inlines.splice(i, 1);
    else if ('children' in inline) stripMarkers(inline.children);
  }
}

/* ------------------------------------------------------------------ */
/* 7. Images                                                           */
/* ------------------------------------------------------------------ */

async function checkImages(
  chapters: ChapterWork[],
  notes: Note[],
  checkImage: ((src: string) => Promise<boolean>) | undefined,
  slicer: Slicer,
): Promise<Set<string>> {
  const sources = new Set<string>();
  const visit = (inline: RawInline) => {
    if (inline.type === 'image') sources.add(inline.src);
  };
  for (const chapter of chapters) for (const container of containers(chapter.blocks, 0)) walkInlines(container.inlines, visit);
  for (const note of notes) walkInlines(note.inlines, visit);

  const failed = new Set<string>();
  if (sources.has('')) failed.add('');
  if (!checkImage) return failed;
  const queue = [...sources].filter(Boolean);
  const worker = async () => {
    for (let src = queue.shift(); src !== undefined; src = queue.shift()) {
      let ok = false;
      try {
        ok = await checkImage(src);
      } catch {
        ok = false;
      }
      if (!ok) failed.add(src);
      await slicer.tick();
    }
  };
  await Promise.all(Array.from({ length: Math.min(6, queue.length) }, worker));
  return failed;
}

function imageMessage(alt: string, src: string): string {
  const what = alt.trim() ? `“${shorten(alt.trim())}”` : src ? shorten(src, 60) : 'without an address';
  return `Image ${what}: couldn’t be loaded, shown as ${imagePlaceholder(alt)}`;
}

export function imagePlaceholder(alt: string): string {
  return `[image: ${alt.trim() || 'untitled'}]`;
}

/* ------------------------------------------------------------------ */
/* The finished model                                                  */
/* ------------------------------------------------------------------ */

function toExportInlines(
  inlines: RawInline[],
  numbers: ReadonlyMap<number, number>,
  written: ReadonlyMap<RawInline, string>,
  failed: ReadonlySet<string>,
  resolveCite?: (cite: Cite) => RawInline[],
  imageFailed?: (alt: string, src: string) => void,
): ExportInline[] {
  const out: ExportInline[] = [];
  const convert = (list: RawInline[]) => toExportInlines(list, numbers, written, failed, resolveCite, imageFailed);
  for (const inline of mergeText(inlines)) {
    switch (inline.type) {
      case 'text':
      case 'code':
        out.push({ type: inline.type, text: inline.text });
        break;
      case 'break':
        out.push({ type: 'break' });
        break;
      case 'emphasis':
      case 'strong':
      case 'strike':
        out.push({ type: inline.type, children: convert(inline.children) });
        break;
      case 'link':
        out.push({ type: 'link', href: inline.href, children: convert(inline.children) });
        break;
      case 'image':
        if (failed.has(inline.src)) {
          imageFailed?.(inline.alt, inline.src);
          out.push({ type: 'text', text: imagePlaceholder(inline.alt) });
        } else {
          out.push({ type: 'image', src: inline.src, alt: inline.alt, title: inline.title });
        }
        break;
      case 'cite':
        out.push(...convert(resolveCite ? resolveCite(inline) : [{ type: 'text', text: written.get(inline) ?? inline.text }]));
        break;
      case 'note': {
        const number = numbers.get(inline.id);
        if (number) out.push({ type: 'footnoteRef', number });
        break;
      }
      case 'fnref':
        break;
    }
  }
  // Words next to each other read as one run.
  const merged: ExportInline[] = [];
  for (const inline of out) {
    const last = merged[merged.length - 1];
    if (inline.type === 'text' && last?.type === 'text') merged[merged.length - 1] = { type: 'text', text: last.text + inline.text };
    else merged.push(inline);
  }
  return merged;
}

function toExportBlocks(blocks: RawBlock[], convert: (inlines: RawInline[]) => ExportInline[]): ExportBlock[] {
  const out: ExportBlock[] = [];
  for (const block of blocks) {
    switch (block.type) {
      case 'paragraph': {
        const children = convert(block.children);
        // A paragraph that was only a moved citation's line is nothing now.
        if (inlineText(children).trim() !== '' || children.some((c) => c.type === 'image' || c.type === 'footnoteRef')) {
          out.push({ type: 'paragraph', children });
        }
        break;
      }
      case 'heading':
        out.push({ type: 'heading', level: block.level, children: convert(block.children) });
        break;
      case 'blockquote':
        out.push({ type: 'blockquote', blocks: toExportBlocks(block.blocks, convert) });
        break;
      case 'list':
        out.push({
          type: 'list',
          ordered: block.ordered,
          start: block.start,
          items: block.items.map((item) => ({ blocks: toExportBlocks(item.blocks, convert), checked: item.checked })),
        });
        break;
      case 'table':
        out.push({
          type: 'table',
          align: block.align,
          header: block.header.map((cell) => convert(cell)),
          rows: block.rows.map((row) => row.map((cell) => convert(cell))),
        });
        break;
      case 'code':
        out.push({ type: 'code', language: block.language, text: block.text });
        break;
      case 'rule':
        out.push({ type: 'rule' });
        break;
      case 'fndef':
        // Paired or turned into a paragraph before this point.
        break;
    }
  }
  return out;
}

function countWords(blocks: readonly ExportBlock[]): number {
  let words = 0;
  for (const block of blocks) {
    switch (block.type) {
      case 'paragraph':
      case 'heading':
        words += inlineText(block.children).match(WORD)?.length ?? 0;
        break;
      case 'blockquote':
        words += countWords(block.blocks);
        break;
      case 'list':
        for (const item of block.items) words += countWords(item.blocks);
        break;
      case 'table':
        for (const cell of [...block.header, ...block.rows.flat()]) words += inlineText(cell).match(WORD)?.length ?? 0;
        break;
      case 'code':
        words += block.text.match(WORD)?.length ?? 0;
        break;
    }
  }
  return words;
}

/* ------------------------------------------------------------------ */
/* Warnings                                                            */
/* ------------------------------------------------------------------ */

class WarningList {
  private readonly entries = new Map<string, { kind: ExportWarningKind; chapter: number | null; message: string; count: number }>();

  add(kind: ExportWarningKind, chapter: number | null, message: string): void {
    const key = `${kind}|${chapter ?? ''}|${message}`;
    const entry = this.entries.get(key);
    if (entry) entry.count++;
    else this.entries.set(key, { kind, chapter, message, count: 1 });
  }

  list(chapterTitle: (index: number) => string | null): ExportWarning[] {
    return [...this.entries.values()].map((entry) => ({
      kind: entry.kind,
      message: entry.message,
      chapter: entry.chapter === null ? null : chapterTitle(entry.chapter),
      count: entry.count,
    }));
  }
}

function plainInlines(inlines: readonly RawInline[]): string {
  return inlines
    .map((inline) => {
      if (inline.type === 'text' || inline.type === 'code') return inline.text;
      if (inline.type === 'cite') return plainCitation(inline.text);
      if ('children' in inline) return plainInlines(inline.children);
      return '';
    })
    .join('');
}

/** A citation's Markdown words as plain text. */
function plainCitation(markdown: string): string {
  return (markdown ?? '').replace(/\\([\\*_`[\]])|[*_]/g, (_, escaped) => escaped ?? '');
}

function shorten(text: string, max = 80): string {
  const clean = text.replace(/\s+/g, ' ').trim();
  return clean.length <= max ? clean : `${clean.slice(0, max - 1).trimEnd()}…`;
}
