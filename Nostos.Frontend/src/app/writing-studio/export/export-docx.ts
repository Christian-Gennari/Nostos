import {
  AlignmentType,
  BorderStyle,
  Document,
  ExternalHyperlink,
  Footer,
  FootnoteReferenceRun,
  HeadingLevel,
  ImageRun,
  LevelFormat,
  LineRuleType,
  Packer,
  PageNumber,
  Paragraph,
  type ParagraphChild,
  SectionType,
  ShadingType,
  Table,
  TableCell,
  TableRow,
  TextRun,
  WidthType,
  type ISectionOptions,
  type INumberingOptions,
} from 'docx';

import { STUDIO_FONTS } from '../page-settings/studio-preferences.service';
import { type EmbeddedImage, type EmbeddedImages } from './export-images';
import { chapterTitle, isRightToLeft } from './export-layout';
import type { ExportAlign, ExportBlock, ExportDocument, ExportInline } from './export-model';
import { imagePlaceholder } from './export-resolve';

/**
 * DOCX (issue #1166), written in the browser with the `docx` package. This
 * module is only loaded when a writer exports a DOCX, so it adds nothing to
 * the Studio's first load.
 *
 * Footnotes are real Word footnotes. Headings use Word's own Heading 1–6
 * styles (so the navigation pane and a table of contents work), set in the
 * writing's font. The page follows the writing's page setup, with page
 * numbers in the footer. Each chapter of a folder starts on a new page.
 */

/** Twentieths of a point per millimetre, and the page sizes Word uses. */
const TWIPS_PER_MM = 1440 / 25.4;
const PAGE = { a4: { width: 11906, height: 16838 }, letter: { width: 12240, height: 15840 } } as const;
const BODY_PT = 12;
const NOTE_PT = 10;
const HEADING_PT = [20, 16, 14, 12, 12, 12] as const;
const MONO = 'Courier New';
/** Indents, in twips: block quotes and a list level. */
const INDENT = 720;

const HEADINGS = [HeadingLevel.HEADING_1, HeadingLevel.HEADING_2, HeadingLevel.HEADING_3, HeadingLevel.HEADING_4, HeadingLevel.HEADING_5, HeadingLevel.HEADING_6] as const;

/** The name Word looks the font up by. A Studio font the computer lacks falls back as Word chooses. */
export function docxFontName(font: string): string {
  if (font === 'mono') return MONO;
  return STUDIO_FONTS.find((f) => f.value === font)?.label ?? 'Times New Roman';
}

/** Lays out the document. `images` holds every embedded image; one missing from it is its placeholder. */
export async function renderExportDocx(doc: ExportDocument, embedded: EmbeddedImages): Promise<Blob> {
  return Packer.toBlob(buildDocx(doc, embedded));
}

export function buildDocx(doc: ExportDocument, embedded: Pick<EmbeddedImages, 'images'>): Document {
  const font = docxFontName(doc.setup.font);
  const page = PAGE[doc.setup.paper] ?? PAGE.a4;
  const margin = Math.round(doc.setup.marginMm * TWIPS_PER_MM);
  const textWidthPx = ((page.width - 2 * margin) / 1440) * 96;
  const textHeightPx = ((page.height - 2 * margin) / 1440) * 96;
  const line = Math.round(240 * doc.setup.lineSpacing);

  const writer = new DocxWriter(embedded.images, { width: textWidthPx, height: textHeightPx * 0.9 });

  // One Word section per chapter, so each starts on a new page; a folder's
  // group headings open the section of the chapter after them.
  const bodies: Paragraph[][] = [];
  let pending: (Paragraph | Table)[] = [];
  for (const section of doc.sections) {
    if (section.type === 'group') {
      pending.push(writer.heading(Math.min(6, section.depth), [{ type: 'text', text: section.title }]));
      continue;
    }
    const title = chapterTitle(doc, section);
    if (title) pending.push(writer.heading(title.level, [{ type: 'text', text: title.text }]));
    pending.push(...writer.blocks(section.blocks, 0));
    bodies.push(pending as Paragraph[]);
    pending = [];
  }
  const back: (Paragraph | Table)[] = [...pending];
  if (doc.bibliography) {
    back.push(writer.heading(1, [{ type: 'text', text: doc.bibliography.heading }]));
    for (const entry of doc.bibliography.entries) {
      back.push(writer.paragraph(entry.children, { indent: { left: INDENT, hanging: INDENT } }));
    }
  }
  if (back.length) {
    if (bodies.length) bodies[bodies.length - 1].push(...(back as Paragraph[]));
    else bodies.push(back as Paragraph[]);
  }

  const footer = () =>
    new Footer({
      children: [new Paragraph({ alignment: AlignmentType.CENTER, children: [new TextRun({ children: [PageNumber.CURRENT], size: NOTE_PT * 2 })] })],
    });
  const sections: ISectionOptions[] = bodies.map((children, i) => ({
    properties: {
      type: i === 0 ? SectionType.CONTINUOUS : SectionType.NEXT_PAGE,
      page: {
        size: { width: page.width, height: page.height },
        margin: { top: margin, bottom: margin, left: margin, right: margin, header: Math.round(margin / 2), footer: Math.round(margin / 2) },
      },
    },
    footers: { default: footer() },
    children,
  }));

  const footnotes: Record<string, { children: Paragraph[] }> = {};
  for (const note of doc.footnotes) {
    footnotes[String(note.number)] = { children: [writer.paragraph(note.children, { size: NOTE_PT, spacing: { after: 0, line: 240 } })] };
  }

  return new Document({
    title: xmlText(doc.title),
    creator: 'Nostos',
    features: { updateFields: false },
    styles: {
      default: {
        document: { run: { font, size: BODY_PT * 2 }, paragraph: { spacing: { line, lineRule: LineRuleType.AUTO, after: 120 } } },
        ...Object.fromEntries(
          HEADING_PT.map((pt, i) => [
            `heading${i + 1}`,
            {
              run: { font, size: pt * 2, bold: true, italics: i >= 4, color: '000000' },
              paragraph: { keepNext: true, keepLines: true, spacing: { before: i === 0 ? 360 : 240, after: 120, line: 240, lineRule: LineRuleType.AUTO } },
            },
          ]),
        ),
      },
    },
    numbering: writer.numbering(),
    footnotes,
    sections,
  });
}

interface ParagraphOptions {
  indent?: { left?: number; hanging?: number; right?: number };
  size?: number;
  spacing?: { after?: number; line?: number; before?: number };
  numbering?: { reference: string; level: number; instance?: number };
  heading?: (typeof HEADINGS)[number];
  alignment?: (typeof AlignmentType)[keyof typeof AlignmentType];
  italics?: boolean;
}

interface RunStyle {
  italics?: boolean;
  bold?: boolean;
  strike?: boolean;
  size?: number;
  link?: boolean;
}

/** Walks the blocks of the model into Word paragraphs, tables and lists. */
class DocxWriter {
  /** One numbering per ordered list start, plus the bullets. */
  private readonly orderedStarts = new Set<number>();
  private listInstance = 0;

  constructor(
    private readonly images: ReadonlyMap<string, EmbeddedImage>,
    private box: { width: number; height: number },
  ) {}

  numbering(): INumberingOptions {
    const levels = (format: (typeof LevelFormat)[keyof typeof LevelFormat], text: (level: number) => string, start = 1) =>
      Array.from({ length: 9 }, (_, level) => ({
        level,
        format,
        text: text(level),
        start,
        alignment: AlignmentType.LEFT,
        style: { paragraph: { indent: { left: INDENT * (level + 1), hanging: 360 } } },
      }));
    const bullets = ['•', '◦', '▪'];
    return {
      config: [
        { reference: 'bullets', levels: levels(LevelFormat.BULLET, (level) => bullets[level % bullets.length]) },
        ...[...this.orderedStarts].map((start) => ({ reference: `ordered-${start}`, levels: levels(LevelFormat.DECIMAL, (level) => `%${level + 1}.`, start) })),
      ],
    };
  }

  heading(level: number, children: ExportInline[]): Paragraph {
    return this.paragraph(children, { heading: HEADINGS[Math.max(1, Math.min(6, level)) - 1] });
  }

  blocks(list: readonly ExportBlock[], quote: number): (Paragraph | Table)[] {
    return list.flatMap((b) => this.block(b, quote));
  }

  private block(b: ExportBlock, quote: number): (Paragraph | Table)[] {
    const indent = quote ? { left: INDENT * quote, right: INDENT } : undefined;
    switch (b.type) {
      case 'heading':
        return [this.heading(b.level, b.children)];
      case 'paragraph':
        return [this.paragraph(b.children, { indent })];
      case 'blockquote':
        return this.blocks(b.blocks, quote + 1);
      case 'list':
        return this.list(b, 0, quote);
      case 'table':
        return [this.table(b.align, b.header, b.rows)];
      case 'code':
        return [
          new Paragraph({
            indent,
            shading: { type: ShadingType.CLEAR, color: 'auto', fill: 'F2F2F2' },
            spacing: { line: 240, lineRule: LineRuleType.AUTO },
            children: b.text
              .replace(/\n$/, '')
              .split('\n')
              .map((text, i) => new TextRun({ text: xmlText(text), font: MONO, size: NOTE_PT * 2, break: i ? 1 : undefined })),
          }),
        ];
      case 'rule':
        return [new Paragraph({ alignment: AlignmentType.CENTER, children: [new TextRun('* * *')] })];
    }
  }

  private list(b: Extract<ExportBlock, { type: 'list' }>, level: number, quote: number): Paragraph[] {
    let reference = 'bullets';
    if (b.ordered) {
      this.orderedStarts.add(b.start);
      reference = `ordered-${b.start}`;
    }
    // Each list is its own instance, so a second list counts from its own start.
    const instance = ++this.listInstance;
    const out: Paragraph[] = [];
    for (const item of b.items) {
      const [first, ...rest] = item.blocks;
      const box: ExportInline[] = item.checked === null ? [] : [{ type: 'text', text: item.checked ? '☒ ' : '☐ ' }];
      const lead = first?.type === 'paragraph' || first?.type === 'heading' ? first.children : [];
      out.push(this.paragraph([...box, ...lead], { numbering: { reference, level: Math.min(8, level), instance }, spacing: { after: 60 } }));
      for (const inner of lead.length ? rest : item.blocks) {
        if (inner.type === 'list') out.push(...this.list(inner, level + 1, quote));
        else out.push(...(this.block(inner, quote) as Paragraph[]).map((p) => p));
      }
    }
    return out;
  }

  private table(align: readonly ExportAlign[], header: readonly ExportInline[][], rows: readonly ExportInline[][][]): Table {
    const width = Math.max(header.length, ...rows.map((row) => row.length), 1);
    const row = (cells: readonly ExportInline[][], head: boolean) =>
      new TableRow({
        tableHeader: head,
        cantSplit: true,
        children: Array.from(
          { length: width },
          (_, i) =>
            new TableCell({
              width: { size: Math.floor(5000 / width), type: WidthType.PERCENTAGE },
              margins: { top: 40, bottom: 40, left: 80, right: 80 },
              children: [this.inCell(width, () => this.paragraph(cells[i] ?? [], { alignment: cellAlignment(align[i]), spacing: { after: 0, line: 240 } }, head ? { bold: true } : {}))],
            }),
        ),
      });
    const border = { style: BorderStyle.SINGLE, size: 4, color: '999999' };
    return new Table({
      width: { size: 5000, type: WidthType.PERCENTAGE },
      borders: { top: border, bottom: border, left: border, right: border, insideHorizontal: border, insideVertical: border },
      rows: [row(header, true), ...rows.map((cells) => row(cells, false))],
    });
  }

  /** Lays out a table cell's content, its images no wider than the cell (columns share the text width evenly). */
  private inCell<T>(columns: number, write: () => T): T {
    const page = this.box;
    this.box = { width: Math.max(24, page.width / columns - 16), height: page.height };
    try {
      return write();
    } finally {
      this.box = page;
    }
  }

  paragraph(children: readonly ExportInline[], options: ParagraphOptions = {}, style: RunStyle = {}): Paragraph {
    const rtl = isRightToLeft(children);
    return new Paragraph({
      heading: options.heading,
      alignment: options.alignment,
      indent: options.indent,
      numbering: options.numbering,
      // LibreOffice reads a line height without its rule as exact, which clips images and overlaps wrapped lines.
      spacing: options.spacing?.line ? { ...options.spacing, lineRule: LineRuleType.AUTO } : options.spacing,
      bidirectional: rtl || undefined,
      children: this.runs(children, { ...style, size: options.size }, rtl),
    });
  }

  private runs(list: readonly ExportInline[], style: RunStyle, rtl: boolean): ParagraphChild[] {
    const out: ParagraphChild[] = [];
    for (const inline of list) {
      switch (inline.type) {
        case 'text':
          out.push(this.text(inline.text, style, rtl));
          break;
        case 'emphasis':
          out.push(...this.runs(inline.children, { ...style, italics: true }, rtl));
          break;
        case 'strong':
          out.push(...this.runs(inline.children, { ...style, bold: true }, rtl));
          break;
        case 'strike':
          out.push(...this.runs(inline.children, { ...style, strike: true }, rtl));
          break;
        case 'code':
          out.push(new TextRun({ text: xmlText(inline.text), font: MONO, size: style.size ? style.size * 2 : undefined }));
          break;
        case 'link':
          // An in-page link ("#part-two") points at an anchor the file doesn't have: its words stay, plain.
          if (inline.href.startsWith('#')) {
            out.push(...this.runs(inline.children, style, rtl));
            break;
          }
          out.push(new ExternalHyperlink({ link: inline.href, children: this.runs(inline.children, { ...style, link: true }, rtl) as TextRun[] }));
          break;
        case 'image':
          out.push(this.image(inline.src, inline.alt, style, rtl));
          break;
        case 'break':
          out.push(new TextRun({ break: 1 }));
          break;
        case 'footnoteRef':
          out.push(new FootnoteReferenceRun(inline.number));
          break;
      }
    }
    return out;
  }

  private text(text: string, style: RunStyle, rtl: boolean): TextRun {
    return new TextRun({
      text: xmlText(text),
      italics: style.italics,
      bold: style.bold,
      strike: style.strike,
      size: style.size ? style.size * 2 : undefined,
      rightToLeft: rtl || undefined,
      ...(style.link ? { style: 'Hyperlink', color: '0563C1', underline: {} } : {}),
    });
  }

  private image(src: string, alt: string, style: RunStyle, rtl: boolean): ParagraphChild {
    const image = this.images.get(src);
    if (!image) return this.text(imagePlaceholder(alt), style, rtl);
    // Never wider than the text, nor taller than the page.
    const scale = Math.min(1, this.box.width / image.width, this.box.height / image.height);
    return new ImageRun({
      type: 'png',
      data: image.data,
      // Rounded down, so a full-width image never pokes past the margin.
      transformation: { width: Math.max(1, Math.floor(image.width * scale)), height: Math.max(1, Math.floor(image.height * scale)) },
      altText: { name: xmlText(alt) || 'image', description: xmlText(alt), title: xmlText(alt) },
    });
  }
}

/**
 * Text as XML 1.0 allows it. Pasted text can carry control characters
 * (a form feed, a vertical tab) and half a surrogate pair; any one of them
 * makes Word ask to repair the file. Breaks that look like space become a
 * space, a broken pair the replacement character, the rest nothing.
 */
export function xmlText(text: string): string {
  return text
    .replace(/[\u000B\u000C\u0085]/g, ' ')
    .replace(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/g, '\uFFFD')
    .replace(/[\u0000-\u0008\u000E-\u001F\u007F\uFFFE\uFFFF]/g, '');
}

function cellAlignment(align: ExportAlign): (typeof AlignmentType)[keyof typeof AlignmentType] | undefined {
  switch (align) {
    case 'center':
      return AlignmentType.CENTER;
    case 'right':
      return AlignmentType.RIGHT;
    case 'left':
      return AlignmentType.LEFT;
    default:
      return undefined;
  }
}
