/**
 * Forgiving name matching for the places that search a short list in memory
 * (the command palette's topics). It follows the server's
 * LibrarySearchMatcher so a name is found the same way a book is: case and
 * accents are ignored ("karlek" finds "Kärlek") and, when nothing contains the
 * words as typed, a slip of one or two letters is forgiven.
 */

/** Shorter words are matched literally; a typo in "war" is another word. */
const MIN_TYPO_LENGTH = 4;
/** From this length a word may carry two typos instead of one. */
const TWO_TYPO_LENGTH = 8;

/** One character, lower-cased and without its accent. Keeps the text's length. */
function foldChar(ch: string): string {
  const base = ch.normalize('NFD').charAt(0);
  return /\p{M}/u.test(base) ? ch.toLowerCase() : base.toLowerCase();
}

/** Lower-cased, accent-stripped text of the same length, for finding a match's position. */
export function foldKeepingLength(text: string): string {
  let out = '';
  for (let i = 0; i < text.length; i++) out += foldChar(text.charAt(i)).charAt(0) || text.charAt(i);
  return out;
}

/** Lower-cased, accent-stripped text with punctuation and whitespace collapsed to single spaces. */
export function foldSearchText(value: string | null | undefined): string {
  if (!value) return '';
  return value
    .normalize('NFKD')
    .replace(/\p{M}/gu, '')
    .toLowerCase()
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim();
}

/** Edit distance where swapping two neighbouring letters is one edit. */
function editDistance(a: string, b: string): number {
  let previous2: number[] = [];
  let previous = Array.from({ length: b.length + 1 }, (_, j) => j);
  for (let i = 1; i <= a.length; i++) {
    const current = [i];
    for (let j = 1; j <= b.length; j++) {
      const cost = a[i - 1] === b[j - 1] ? 0 : 1;
      let best = Math.min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + cost);
      if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) {
        best = Math.min(best, previous2[j - 2] + 1);
      }
      current[j] = best;
    }
    previous2 = previous;
    previous = current;
  }
  return previous[b.length];
}

function matchesWithTypos(token: string, words: string[]): boolean {
  if (token.length < MIN_TYPO_LENGTH) return false;
  const allowed = token.length >= TWO_TYPO_LENGTH ? 2 : 1;
  return words.some(
    (word) =>
      // People rarely mistype the first letter; requiring it keeps "king" from matching "ring".
      word[0] === token[0] &&
      (editDistance(token, word) <= allowed ||
        // The search runs while the user is typing: a longer word's start may match.
        (word.length > token.length && editDistance(token, word.slice(0, token.length)) <= allowed)),
  );
}

/** Whether every word of the query is in the text, as typed or with a small typo. */
export function matchesSearch(text: string, query: string, allowTypos = true): boolean {
  const tokens = foldSearchText(query).split(' ').filter(Boolean);
  if (tokens.length === 0) return false;
  const folded = foldSearchText(text);
  if (tokens.every((t) => folded.includes(t))) return true;
  if (!allowTypos) return false;
  const words = folded.split(' ').filter(Boolean);
  return tokens.every((t) => folded.includes(t) || matchesWithTypos(t, words));
}

/**
 * How well a name fits the query, lower is better: the whole name, its start,
 * the start of one of its words, anywhere in it, then a typo-only match.
 */
export function searchRank(name: string, query: string): number {
  const q = foldSearchText(query);
  const n = foldSearchText(name);
  if (!q) return 5;
  if (n === q) return 0;
  if (n.startsWith(q)) return 1;
  if (n.includes(` ${q}`)) return 2;
  if (n.includes(q)) return 3;
  return 4;
}

/** Items ordered by rank, ties keeping their given order. */
export function sortByRank<T>(items: readonly T[], rank: (item: T) => number): T[] {
  return items
    .map((item, index) => ({ item, index, r: rank(item) }))
    .sort((a, b) => a.r - b.r || a.index - b.index)
    .map((x) => x.item);
}
