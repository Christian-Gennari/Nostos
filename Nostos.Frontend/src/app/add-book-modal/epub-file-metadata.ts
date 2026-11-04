/**
 * Title and author from a chosen EPUB's own package metadata (#1358), so Add a
 * book does not file "Walden" without its author just because the file name
 * only carries the title.
 *
 * epub.js is loaded on demand: the reader already ships it, and this keeps it
 * out of the bundle that opens the library. Only the container and the package
 * document are read; no chapter is rendered.
 */

export interface EpubFileMetadata {
  title: string | null;
  author: string | null;
}

/** Files above this are left to the file-name title rather than read into memory. */
export const MAX_EPUB_METADATA_BYTES = 100 * 1024 * 1024;

/** The form stays usable either way; this only bounds how long a fill may arrive late. */
const METADATA_TIMEOUT_MS = 10_000;

interface OpenedBook {
  opened: Promise<unknown>;
  loaded: { metadata: Promise<{ title?: string; creator?: string } | undefined> };
  destroy(): void;
}

const NOTHING: EpubFileMetadata = { title: null, author: null };

export function isEpubFile(file: File): boolean {
  return file.type === 'application/epub+zip' || file.name.toLowerCase().endsWith('.epub');
}

/** Never throws: a file epub.js cannot open simply has no metadata to offer. */
export async function readEpubFileMetadata(file: Blob): Promise<EpubFileMetadata> {
  if (file.size === 0 || file.size > MAX_EPUB_METADATA_BYTES) return NOTHING;
  // epub.js never settles on bytes that are not a ZIP, so look before loading it.
  if (!(await startsLikeZip(file))) return NOTHING;

  let book: OpenedBook | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    const [{ default: ePub }, bytes] = await Promise.all([import('epubjs'), file.arrayBuffer()]);
    book = ePub(bytes) as unknown as OpenedBook;
    // A ZIP that is not an EPUB may never settle either, so the read is bounded.
    const opened = book.opened.then(() => book!.loaded.metadata);
    const gaveUp = new Promise<never>((_, reject) => {
      timer = setTimeout(() => reject(new Error('EPUB metadata timed out')), METADATA_TIMEOUT_MS);
    });
    const metadata = await Promise.race([opened, gaveUp]);
    return {
      title: clean(metadata?.title),
      author: clean(metadata?.creator),
    };
  } catch {
    return NOTHING;
  } finally {
    clearTimeout(timer);
    try {
      book?.destroy();
    } catch {
      // A half-opened book may not tear down cleanly; there is nothing to keep.
    }
  }
}

async function startsLikeZip(file: Blob): Promise<boolean> {
  try {
    const head = new Uint8Array(await file.slice(0, 4).arrayBuffer());
    return head.length === 4 && head[0] === 0x50 && head[1] === 0x4b && head[2] === 0x03 && head[3] === 0x04;
  } catch {
    return false;
  }
}

function clean(value: string | null | undefined): string | null {
  const text = (value ?? '').replace(/\s+/g, ' ').trim();
  return text ? text : null;
}
