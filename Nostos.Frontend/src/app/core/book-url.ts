/**
 * The URLs of a book's own files, minted in one place so a component never
 * spells out a backend route (see `cover-url.ts` for covers, which carry a
 * cache-busting version instead).
 *
 * They are plain URLs rather than requests: audio players, pdf.js and the file
 * cache hand them to the browser, so the response is streamed and range-
 * requested from the origin instead of passing through `HttpClient`.
 */

function bookApiUrl(bookId: string): string {
  return `/api/books/${encodeURIComponent(bookId)}`;
}

/** The book's file: the EPUB/PDF read in place, or a single-file audiobook. */
export function bookFileUrl(bookId: string): string {
  return `${bookApiUrl(bookId)}/file`;
}

/** The book's file as a download; a multi-track audiobook arrives as one ZIP. */
export function bookFileDownloadUrl(bookId: string): string {
  return `${bookApiUrl(bookId)}/file/download`;
}

/** One track of a multi-track audiobook, by its 1-based track number. */
export function bookTrackUrl(bookId: string, trackNumber: number): string {
  return `${bookApiUrl(bookId)}/tracks/${trackNumber}`;
}
