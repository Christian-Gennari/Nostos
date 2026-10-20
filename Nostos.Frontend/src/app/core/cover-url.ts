/**
 * The URL of a book's cover image, or of a resized thumbnail when `width` is
 * given. Null when the book has no cover.
 *
 * The book's `coverVersion` rides along as `v`: it changes whenever the cover's
 * bytes change, so the server lets the browser cache a versioned URL for good,
 * and a replaced cover arrives under a new URL instead of reverting to the
 * cached one. Without a version the URL is revalidated on every use.
 */
export function coverImageUrl(
  book: { coverUrl: string | null; coverVersion?: string | null } | null | undefined,
  width?: number,
): string | null {
  const base = book?.coverUrl;
  if (!base) return null;

  const params = new URLSearchParams();
  if (width) params.set('width', String(width));
  if (book.coverVersion) params.set('v', book.coverVersion);

  const path = width ? `${base}/thumbnail` : base;
  const query = params.toString();
  return query ? `${path}?${query}` : path;
}
