const LANGUAGE_NAMES: Record<string, string> = {
  english: 'en',
  italian: 'it',
  italiano: 'it',
  spanish: 'es',
  español: 'es',
  french: 'fr',
  français: 'fr',
  german: 'de',
  deutsch: 'de',
  portuguese: 'pt',
  português: 'pt',
  swedish: 'sv',
  svenska: 'sv',
  dutch: 'nl',
  russian: 'ru',
  polish: 'pl',
  japanese: 'ja',
  chinese: 'zh',
  hindi: 'hi',
  danish: 'da',
  norwegian: 'no',
  finnish: 'fi',
  greek: 'el',
  latin: 'la',
};

/**
 * Three-letter ISO 639-2 codes, bibliographic and terminology forms, for the
 * languages above. Some EPUBs and catalogues write "eng" where the voices are
 * tagged "en".
 */
const THREE_LETTER_CODES: Record<string, string> = {
  eng: 'en',
  ita: 'it',
  spa: 'es',
  fra: 'fr',
  fre: 'fr',
  deu: 'de',
  ger: 'de',
  por: 'pt',
  swe: 'sv',
  nld: 'nl',
  dut: 'nl',
  rus: 'ru',
  pol: 'pl',
  jpn: 'ja',
  zho: 'zh',
  chi: 'zh',
  hin: 'hi',
  dan: 'da',
  nor: 'no',
  fin: 'fi',
  ell: 'el',
  gre: 'el',
  lat: 'la',
};

/**
 * A language tag from a book's declared language, which may be a tag ("en",
 * "pt-BR", "eng") or a name ("English"). Null when it is none of these, or is
 * "und" (undetermined): guessing would have a voice for the wrong language
 * read the book.
 */
export function languageTagFromCatalogue(language: string | null | undefined): string | null {
  const value = (language ?? '').trim();
  if (!value) return null;
  if (/^[a-z]{2,3}([-_][a-z0-9]{2,8})*$/i.test(value)) {
    const [primary, ...rest] = value.split(/[-_]/);
    const lower = primary.toLowerCase();
    if (lower === 'und') return null;
    return [THREE_LETTER_CODES[lower] ?? primary, ...rest].join('-');
  }
  return LANGUAGE_NAMES[value.toLowerCase()] ?? null;
}
