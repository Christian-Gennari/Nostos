import { languageTagFromCatalogue } from './read-aloud-language';

describe('languageTagFromCatalogue', () => {
  it('accepts a tag and a language name, and refuses to guess', () => {
    expect(languageTagFromCatalogue('en')).toBe('en');
    expect(languageTagFromCatalogue('pt_BR')).toBe('pt-BR');
    expect(languageTagFromCatalogue('English')).toBe('en');
    expect(languageTagFromCatalogue(' Italiano ')).toBe('it');
    expect(languageTagFromCatalogue('Klingon')).toBeNull();
    expect(languageTagFromCatalogue('')).toBeNull();
    expect(languageTagFromCatalogue(null)).toBeNull();
  });
});
