import { pdfDefaultOptions, pdfjsVersion } from 'ngx-extended-pdf-viewer';

/** The slice of pdf.js the passage viewer uses. */
export interface PdfJsLib {
  getDocument(source: { url: string; withCredentials?: boolean; worker?: unknown }): PdfLoadingTask;
  PDFWorker: new (options: { port: Worker }) => { destroy(): void };
}

/**
 * A pdf.js worker of the viewer's own, handed to each document it opens. The
 * reader's copy of pdf.js keeps its worker settings (relative to its own
 * script, and routed through its CSP service), so the viewer never relies on
 * them, nor changes them.
 */
export function createPdfWorker(lib: PdfJsLib): { worker: unknown; terminate(): void } {
  const port = new Worker(assetUrls().worker, { type: 'module' });
  const worker = new lib.PDFWorker({ port });
  return {
    worker,
    terminate: () => {
      worker.destroy();
      port.terminate();
    },
  };
}

export interface PdfLoadingTask {
  promise: Promise<PdfDocument>;
  destroy(): Promise<void>;
}

/** No `destroy()`: pdf.js 6 closes a document only through its loading task. */
export interface PdfDocument {
  numPages: number;
  getPage(pageNumber: number): Promise<PdfPage>;
}

export interface PdfPage {
  getViewport(options: { scale: number }): { width: number; height: number };
  render(options: {
    canvasContext: CanvasRenderingContext2D;
    viewport: { width: number; height: number };
  }): { promise: Promise<void>; cancel(): void };
}

const VIEWER_LOADED_EVENT = 'ngxViewerFileHasBeenLoaded';

let isolated: Promise<PdfJsLib> | null = null;

/**
 * pdf.js for the passage viewer (issue #1018). Nostos ships pdf.js only inside
 * ngx-extended-pdf-viewer's viewer bundle.
 *
 * While the PDF reader is open its copy is on `globalThis.pdfjsLib`, and that
 * is used. Otherwise a private copy of the same bundle is imported under a URL
 * of its own: a module URL runs once per page, so the reader's own URL must
 * stay untouched for it to load later. The bundle sets `globalThis.pdfjsLib`
 * and announces itself with an event the reader's loader listens for; the
 * global is put back as it was, and the announcement is kept from the reader.
 */
export function loadPdfJs(): Promise<PdfJsLib> {
  const shared = (globalThis as { pdfjsLib?: PdfJsLib }).pdfjsLib;
  if (shared?.getDocument) return Promise.resolve(shared);
  isolated ??= importIsolated().catch((error) => {
    isolated = null;
    throw error;
  });
  return isolated;
}

function assetUrls(): { viewer: string; worker: string } {
  const assets = new URL(`${pdfDefaultOptions.assetsFolder}/`, document.baseURI);
  const suffix = pdfDefaultOptions._internalFilenameSuffix ?? '.min';
  return {
    viewer: new URL(`viewer-${pdfjsVersion}${suffix}.mjs?passage-viewer`, assets).href,
    worker: new URL(`pdf.worker-${pdfjsVersion}${suffix}.mjs`, assets).href,
  };
}

async function importIsolated(): Promise<PdfJsLib> {
  const { viewer: viewerUrl } = assetUrls();

  const scope = globalThis as { pdfjsLib?: PdfJsLib };
  const before = scope.pdfjsLib;
  const held: CustomEvent[] = [];
  // Window capture runs before the reader's listener on document.
  const hold = (event: Event) => {
    held.push(event as CustomEvent);
    event.stopImmediatePropagation();
  };
  window.addEventListener(VIEWER_LOADED_EVENT, hold, true);

  let module: { PDFViewerApplication?: unknown };
  let lib: PdfJsLib | undefined;
  try {
    module = await import(/* @vite-ignore */ viewerUrl);
    lib = scope.pdfjsLib;
  } finally {
    window.removeEventListener(VIEWER_LOADED_EVENT, hold, true);
    if (before) scope.pdfjsLib = before;
    else delete scope.pdfjsLib;
  }

  // An announcement from the reader's own copy, made meanwhile, goes on.
  for (const event of held) {
    if (event.detail?.PDFViewerApplication !== module.PDFViewerApplication) {
      document.dispatchEvent(new CustomEvent(VIEWER_LOADED_EVENT, { detail: event.detail }));
    }
  }

  if (!lib?.getDocument) throw new Error('pdf.js did not load');
  return lib;
}
