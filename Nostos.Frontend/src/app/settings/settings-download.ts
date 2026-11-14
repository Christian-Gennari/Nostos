/** How long a downloaded object URL stays alive after the download starts. */
export const DOWNLOAD_URL_LIFETIME_MS = 60_000;

/**
 * Hands a blob to the browser as a download from a link the page clicks itself:
 * no popup for a blocker to stop and no blank tab left behind.
 */
export function saveDownload(blob: Blob, fileName: string): void {
  const objectUrl = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = objectUrl;
  link.download = fileName;
  link.style.display = 'none';
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(objectUrl), DOWNLOAD_URL_LIFETIME_MS);
}

/**
 * The server's suggested filename, or `fallback` when it sends none or one with
 * a different extension than this download produces.
 */
export function fileNameFromDisposition(
  contentDisposition: string | null,
  fallback: string,
  extension: string,
): string {
  const match = contentDisposition?.match(/filename="?([^";]+)"?/iu);
  const fileName = match?.[1]?.trim();
  return fileName?.toLowerCase().endsWith(extension) ? fileName : fallback;
}
