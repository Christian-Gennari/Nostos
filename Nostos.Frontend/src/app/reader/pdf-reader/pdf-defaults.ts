import { pdfDefaultOptions } from 'ngx-extended-pdf-viewer';

/**
 * pdf.js options for the reader, set once when the PDF reader's chunk loads and
 * so before any viewer instance starts loading a document. They live here, not
 * in main.ts, because importing ngx-extended-pdf-viewer at boot put the whole
 * viewer (~640 kB) into the initial bundle of every page.
 *
 * Range loading: fetch only the 1 MiB chunks a page needs, never the whole file
 * in the background.
 */
pdfDefaultOptions.disableAutoFetch = true;
pdfDefaultOptions.rangeChunkSize = 1024 * 1024;
