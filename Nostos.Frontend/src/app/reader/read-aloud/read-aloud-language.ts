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
 * A language tag from a book's catalogue language, which may be a tag ("en",
 * "pt-BR") or a name ("English"). Null when it is neither: guessing would have
 * a voice for the wrong language read the book.
 */
export function languageTagFromCatalogue(language: string | null | undefined): string | null {
  const value = (language ?? '').trim();
  if (!value) return null;
  if (/^[a-z]{2,3}([-_][a-z0-9]{2,8})*$/i.test(value)) return value.replace(/_/g, '-');
  return LANGUAGE_NAMES[value.toLowerCase()] ?? null;
}
