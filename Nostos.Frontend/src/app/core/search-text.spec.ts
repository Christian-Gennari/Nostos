import { describe, expect, it } from 'vitest';

import { normalizeSearchText } from './search-text';

describe('normalizeSearchText', () => {
  it('folds accents away so "Café" finds "cafe"', () => {
    expect(normalizeSearchText('Café')).toBe('cafe');
    expect(normalizeSearchText('CAFE')).toBe('cafe');
    expect(normalizeSearchText('naïve façade')).toBe('naive facade');
  });

  it('folds a decomposed accent the same way as a composed one', () => {
    expect(normalizeSearchText('Cafe\u0301')).toBe(normalizeSearchText('Café'));
  });

  it('lower cases without touching letters outside the Latin accents', () => {
    expect(normalizeSearchText('ÆON')).toBe('æon');
    expect(normalizeSearchText('Ω')).toBe('ω');
  });

  it('leaves an empty string alone', () => {
    expect(normalizeSearchText('')).toBe('');
  });

  it('keeps the punctuation it was given, so it is the caller’s to trim', () => {
    expect(normalizeSearchText('Virtue, or: “duty”?')).toBe('virtue, or: “duty”?');
  });
});
