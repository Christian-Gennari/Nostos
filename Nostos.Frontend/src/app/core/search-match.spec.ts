import { foldSearchText, matchesSearch, searchRank, sortByRank } from './search-match';

describe('search-match', () => {
  it('folds case, accents and punctuation', () => {
    expect(foldSearchText('Brontë, Catch-22')).toBe('bronte catch 22');
  });

  it('ignores accents on both sides', () => {
    expect(matchesSearch('Kärlek', 'karlek')).toBe(true);
    expect(matchesSearch('Iliada', 'Ilíada')).toBe(true);
  });

  it('forgives a slip only in a word of four letters or more', () => {
    expect(matchesSearch('Pretraining', 'pretrainnig')).toBe(true);
    expect(matchesSearch('Överlåtelse', 'overgivenhet')).toBe(false);
    expect(matchesSearch('ring', 'king')).toBe(false);
    expect(matchesSearch('war', 'wer')).toBe(false);
  });

  it('ranks the whole name, then its start, a word start and the rest', () => {
    const names = ['Bathe', 'Of the sea', 'The Hobbit', 'The'];
    expect(sortByRank(names, (n) => searchRank(n, 'the'))).toEqual(['The', 'The Hobbit', 'Of the sea', 'Bathe']);
  });
});
