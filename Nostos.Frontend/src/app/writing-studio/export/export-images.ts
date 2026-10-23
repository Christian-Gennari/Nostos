import { imagePlaceholder } from './export-resolve';
import { imagesIn } from './export-layout';
import type { ExportDocument, ExportWarning } from './export-model';

/**
 * Images a file carries inside it (DOCX, issue #1166). A web page or PDF
 * shows an image by its address; a Word file has to hold its bytes, so each
 * image is fetched and redrawn as a PNG in the browser (which also turns WebP,
 * AVIF and SVG into something every word processor opens). An image the
 * browser may show but not read, because its site doesn't allow it (CORS),
 * becomes the "[image: alt]" placeholder and a warning; it never fails the
 * export.
 */
export interface EmbeddedImage {
  data: Uint8Array;
  /** Pixels as drawn. */
  width: number;
  height: number;
}

export interface EmbeddedImages {
  images: ReadonlyMap<string, EmbeddedImage>;
  warnings: ExportWarning[];
}

/** The longest side, in pixels, an embedded image is drawn at. */
export const MAX_EDGE = 1600;

export type ImageFetcher = (src: string, signal?: AbortSignal) => Promise<EmbeddedImage | null>;

/** Kept per resolved document (its sections array), so checking before the download and writing the file fetch once. */
const prepared = new WeakMap<object, Promise<EmbeddedImages>>();

/**
 * Fetches every image of the document once. The warnings name each image
 * that will be a placeholder, in the dialog's own words.
 */
export function embedImages(doc: Pick<ExportDocument, 'sections' | 'footnotes'>, fetchImage: ImageFetcher = fetchAsPng, signal?: AbortSignal): Promise<EmbeddedImages> {
  const known = prepared.get(doc.sections);
  if (known) return known;
  const work = (async () => {
    const images = new Map<string, EmbeddedImage>();
    const warnings: ExportWarning[] = [];
    const found = imagesIn(doc);
    const fetched = await Promise.all([...found.keys()].map((src) => fetchImage(src, signal).catch(() => null)));
    [...found].forEach(([src, alt], i) => {
      const image = fetched[i];
      if (image) {
        images.set(src, image);
      } else {
        warnings.push({
          kind: 'image',
          chapter: null,
          count: 1,
          message: `Image “${alt.trim() || 'untitled'}”: couldn’t be copied into the file, shown as ${imagePlaceholder(alt)}`,
        });
      }
    });
    signal?.throwIfAborted();
    return { images, warnings };
  })();
  prepared.set(doc.sections, work);
  // A cancelled or failed check is not remembered: the next one tries again.
  work.catch(() => prepared.delete(doc.sections));
  return work;
}

/** The image at `src` as PNG bytes, or null when it can't be read. */
export async function fetchAsPng(src: string, signal?: AbortSignal): Promise<EmbeddedImage | null> {
  if (typeof fetch === 'undefined' || typeof document === 'undefined') return null;
  try {
    // A site that never answers is a placeholder after 15 seconds, not a dialog that waits forever.
    const timeout = AbortSignal.timeout(15_000);
    const response = await fetch(src, { signal: signal ? AbortSignal.any([signal, timeout]) : timeout, mode: 'cors', credentials: sameOrigin(src) ? 'same-origin' : 'omit', referrerPolicy: 'no-referrer' });
    if (!response.ok) return null;
    const blob = await response.blob();
    const bitmap = await decode(blob);
    if (!bitmap) return null;
    const canvas = document.createElement('canvas');
    // Drawn at most MAX_EDGE pixels on its longest side: twice a page's text
    // width, sharp in print, and a 6000-pixel photo doesn't make a 50 MB file
    // or run the browser out of canvas memory.
    const scale = Math.min(1, MAX_EDGE / Math.max(bitmap.width, bitmap.height, 1));
    canvas.width = Math.max(1, Math.round(bitmap.width * scale));
    canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    const context = canvas.getContext('2d');
    if (!context || !canvas.width || !canvas.height) return null;
    context.drawImage(bitmap.source, 0, 0, canvas.width, canvas.height);
    bitmap.close();
    const png = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/png'));
    if (!png) return null;
    return { data: new Uint8Array(await png.arrayBuffer()), width: canvas.width, height: canvas.height };
  } catch {
    return null;
  }
}

async function decode(blob: Blob): Promise<{ source: CanvasImageSource; width: number; height: number; close(): void } | null> {
  // SVG has no pixels of its own for createImageBitmap in every browser; an <img> draws it.
  if (typeof createImageBitmap !== 'undefined' && !blob.type.includes('svg')) {
    try {
      const bitmap = await createImageBitmap(blob);
      return { source: bitmap, width: bitmap.width, height: bitmap.height, close: () => bitmap.close() };
    } catch {
      // Fall through to <img>.
    }
  }
  const url = URL.createObjectURL(blob);
  try {
    const image = new Image();
    image.src = url;
    await image.decode();
    const width = image.naturalWidth || 800;
    const height = image.naturalHeight || 600;
    return { source: image, width, height, close: () => URL.revokeObjectURL(url) };
  } catch {
    URL.revokeObjectURL(url);
    return null;
  }
}

function sameOrigin(src: string): boolean {
  try {
    return typeof location !== 'undefined' && new URL(src, location.href).origin === location.origin;
  } catch {
    return false;
  }
}
