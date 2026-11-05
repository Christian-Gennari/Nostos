/**
 * Reading direction of an EPUB (#1388).
 *
 * epub.js has no idea that Hebrew or Arabic run right to left unless the OPF
 * says so, and many do not (Project Gutenberg sets no
 * `page-progression-direction`). The spine attribute wins when it is present;
 * otherwise the book language decides.
 */

export type EpubDirection = 'ltr' | 'rtl';

/** Primary language subtags written right to left (ISO 639-1 and -2/-3). */
const RTL_LANGUAGES = new Set([
  'ar', 'ara', 'arc', 'ckb', 'dv', 'div', 'fa', 'fas', 'per', 'he', 'heb', 'iw', 'ks', 'ku', 'ps', 'pus',
  'sd', 'snd', 'ug', 'uig', 'ur', 'urd', 'yi', 'yid',
]);

/** Scripts that are right to left whatever the language (BCP 47 script subtag). */
const RTL_SCRIPTS = new Set(['arab', 'hebr', 'thaa', 'syrc', 'nkoo', 'adlm']);

export function isRtlLanguage(language: string | null | undefined): boolean {
  const parts = (language ?? '').trim().toLowerCase().replace(/_/g, '-').split('-');
  const primary = parts[0];
  if (!primary) return false;
  const script = parts.slice(1).find((part) => part.length === 4);
  if (script) return RTL_SCRIPTS.has(script);
  return RTL_LANGUAGES.has(primary);
}

export function epubReadingDirection(input: {
  pageProgressionDirection?: string | null;
  language?: string | null;
}): EpubDirection {
  const declared = input.pageProgressionDirection?.trim().toLowerCase();
  if (declared === 'rtl' || declared === 'ltr') return declared;
  return isRtlLanguage(input.language) ? 'rtl' : 'ltr';
}

/** True when the document, or its body, already states a direction. */
export function documentDeclaresDirection(doc: Document): boolean {
  return !!(doc.documentElement?.getAttribute('dir') || doc.body?.getAttribute('dir'));
}

/** Gives a content document the book's direction unless the publisher set one. */
export function applyDocumentDirection(doc: Document, direction: EpubDirection): void {
  if (direction !== 'rtl' || !doc.documentElement || documentDeclaresDirection(doc)) return;
  doc.documentElement.setAttribute('dir', 'rtl');
}
