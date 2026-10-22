/**
 * A file name for an export that every common system accepts (issue #1164),
 * from the writing's title. Letters in any script, right-to-left text and
 * emoji stay as written; only what a file system refuses or misreads changes.
 *
 * - Windows refuses `< > : " / \ | ? *` and control characters, a name that
 *   ends in a dot or a space, and the device names (CON, PRN, AUX, NUL,
 *   COM1–9, LPT1–9) with or without an extension.
 * - macOS and Linux refuse `/` and NUL; a leading dot hides the file.
 * - All three cap a name at 255 units (bytes on Linux, UTF-16 units on
 *   macOS, characters on Windows). The name is cut, at a character, to fit
 *   the strictest of these with room to spare.
 * - Invisible bidirectional controls (U+202A–202E, U+2066–2069, U+200E/F) are
 *   removed from the name: in a file name they can make "report‮fdp.exe" read
 *   as a PDF. The writing's text itself is never touched.
 */

/** Room under the 255-unit limits, for a browser's " (1)" and the like. */
export const MAX_FILENAME_BYTES = 200;

const RESERVED = /^(con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(\..*)?$/i;

export function exportFilename(title: string | null | undefined, extension: string): string {
  const ext = extension.replace(/^\.+/, '').replace(/[^A-Za-z0-9]/g, '').toLowerCase();
  let base = (title ?? '')
    .replace(/[‪-‮⁦-⁩‎‏]/g, '')
    .replace(/[\u0000-\u001f\u007f-\u009f]/g, ' ')
    .replace(/[<>:"/\\|?*]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/^[.\s]+/, '')
    .replace(/[.\s]+$/, '');
  if (!base) base = 'Untitled';
  if (RESERVED.test(base)) base = `${base}_`;
  const suffix = ext ? `.${ext}` : '';
  base = cutToBytes(base, MAX_FILENAME_BYTES - utf8Length(suffix)).replace(/[.\s]+$/, '') || 'Untitled';
  return `${base}${suffix}`;
}

function utf8Length(text: string): number {
  return new TextEncoder().encode(text).length;
}

/** Cuts at a whole character (a grapheme where the platform can tell), never inside one. */
function cutToBytes(text: string, max: number): string {
  if (utf8Length(text) <= max) return text;
  const segments: string[] =
    typeof Intl !== 'undefined' && 'Segmenter' in Intl
      ? Array.from(new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(text), (s) => s.segment)
      : Array.from(text);
  let out = '';
  let bytes = 0;
  for (const segment of segments) {
    const size = utf8Length(segment);
    if (bytes + size > max) break;
    out += segment;
    bytes += size;
  }
  return out;
}
