import type { ExportDocument } from './export-model';
import { renderExportHtml } from './export-html';

/**
 * The formats the export dialog offers (issue #1164). A format is a name, a
 * file extension and one function from the resolved document to a file.
 * Rich text and Markdown (#1165), DOCX (#1166) and PDF (#1167) each add an
 * entry here; until then the dialog offers only the web page.
 *
 * `paged` says the format lays text out on pages (DOCX, PDF). Only then does
 * the dialog offer paper, margins, font and spacing; a web page or Markdown
 * has no pages, so it shows only the citation style.
 */
export interface ExportFormat {
  id: string;
  label: string;
  extension: string;
  paged: boolean;
  render(doc: ExportDocument): Promise<Blob> | Blob;
}

export const EXPORT_FORMATS: readonly ExportFormat[] = [
  {
    id: 'html',
    label: 'Web page (.html)',
    extension: 'html',
    paged: false,
    render: (doc) => new Blob([renderExportHtml(doc)], { type: 'text/html;charset=utf-8' }),
  },
];
