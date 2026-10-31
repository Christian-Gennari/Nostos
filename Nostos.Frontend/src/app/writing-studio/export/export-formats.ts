import type { ExportDocument, ExportWarning } from './export-model';
import { renderExportHtml } from './export-html';
import { embedImages } from './export-images';
import { renderExportMarkdown } from './export-markdown';
import { type RichTextClip, renderExportRichText } from './export-rich-text';

/**
 * The formats the export dialog offers (issues #1164–#1167). A format turns
 * the resolved document into a file to download, rich text to copy, or a
 * print layout to save as PDF.
 *
 * `paged` says the format lays text out on pages (DOCX, PDF). Only then does
 * the dialog offer paper, margins, font and spacing; Markdown, rich text and a
 * web page have no pages, so they show only the citation style.
 */
interface FormatBase {
  id: string;
  label: string;
  /** One quiet line under the format, when there is something to know. */
  hint: string | null;
  paged: boolean;
  /** For the file name; the print dialog names a PDF from it too. */
  extension: string;
  /** What the format alone would change, found before the writer commits (an image a DOCX can't carry). */
  check?(doc: ExportDocument, signal: AbortSignal): Promise<ExportWarning[]>;
}

export type ExportFormat =
  | (FormatBase & { action: 'download'; render(doc: ExportDocument): Promise<Blob> | Blob })
  | (FormatBase & { action: 'copy'; clip(doc: ExportDocument): RichTextClip })
  | (FormatBase & { action: 'print'; print(doc: ExportDocument, title: string): Promise<void> });

export type ExportAction = ExportFormat['action'];

export const EXPORT_FORMATS: readonly ExportFormat[] = [
  {
    id: 'docx',
    label: 'Word document (.docx)',
    hint: null,
    paged: true,
    extension: 'docx',
    action: 'download',
    check: async (doc, signal) => (await embedImages(doc, undefined, signal)).warnings,
    render: async (doc) => {
      // The DOCX writer and its library load only when someone exports one.
      const [{ renderExportDocx }, embedded] = await Promise.all([import('./export-docx'), embedImages(doc)]);
      return renderExportDocx(doc, embedded);
    },
  },
  {
    id: 'pdf',
    label: 'PDF',
    hint: 'Your browser’s print dialog opens: choose “Save as PDF” there. Notes come at the end of each chapter.',
    paged: true,
    extension: 'pdf',
    action: 'print',
    print: async (doc, title) => (await import('./export-pdf')).printExport(doc, title),
  },
  {
    id: 'markdown',
    label: 'Markdown (.md)',
    hint: null,
    paged: false,
    extension: 'md',
    action: 'download',
    render: (doc) => new Blob([renderExportMarkdown(doc)], { type: 'text/markdown;charset=utf-8' }),
  },
  {
    id: 'rich-text',
    label: 'Copy as rich text',
    hint: 'For Substack, Google Docs or Word. Notes come as endnotes.',
    paged: false,
    extension: 'html',
    action: 'copy',
    clip: renderExportRichText,
  },
  {
    id: 'html',
    label: 'Web page (.html)',
    hint: null,
    paged: false,
    extension: 'html',
    action: 'download',
    render: (doc) => new Blob([renderExportHtml(doc)], { type: 'text/html;charset=utf-8' }),
  },
];

const LAST_FORMAT_KEY = 'nostos.export.format';

/** The format the writer used last, so the next export starts there. */
export function lastExportFormat(): ExportFormat {
  try {
    const id = localStorage.getItem(LAST_FORMAT_KEY);
    return EXPORT_FORMATS.find((format) => format.id === id) ?? EXPORT_FORMATS[0];
  } catch {
    return EXPORT_FORMATS[0];
  }
}

export function rememberExportFormat(id: string): void {
  try {
    localStorage.setItem(LAST_FORMAT_KEY, id);
  } catch {
    // Private mode: the next export starts from the default.
  }
}
