/**
 * What the page readers share to find a note's words in a book: the matcher,
 * and the memory of the notes that were looked for and not found.
 */

const UNLOCATED_NOTES_KEY = 'nostos.reader.unlocatedImportedNotes';

export function readUnlocatedNoteIds(): Set<string> {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(UNLOCATED_NOTES_KEY) ?? '[]');
    return new Set(Array.isArray(stored) ? stored.filter((id) => typeof id === 'string') : []);
  } catch {
    return new Set();
  }
}

export function rememberUnlocatedNoteId(noteId: string): void {
  try {
    const ids = readUnlocatedNoteIds().add(noteId);
    // Bounded: this is a cache of misses, and forgetting one only costs a retry.
    localStorage.setItem(UNLOCATED_NOTES_KEY, JSON.stringify([...ids].slice(-500)));
  } catch {
    // Storage can be unavailable; the lookup is then simply repeated.
  }
}

const SOFT_HYPHEN = '­';
const APOSTROPHES = "'‘’ʼ";
const QUOTATION_MARKS = '"“”„';

function characterPattern(character: string): string {
  if (APOSTROPHES.includes(character)) return `[${APOSTROPHES}]`;
  if (QUOTATION_MARKS.includes(character)) return `[${QUOTATION_MARKS}]`;
  return character.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

const LIGATURES: Record<string, string> = {
  ffi: '\uFB03',
  ffl: '\uFB04',
  ff: '\uFB00',
  fi: '\uFB01',
  fl: '\uFB02',
};
const LIGATURE_LETTERS = /ffi|ffl|ff|fi|fl|[^]/gu;

function wordPattern(word: string): string {
  // A book's type may join letters a copy of its words keeps apart.
  const pieces = word.replace(/[\uFB00-\uFB04]/g, (ligature) => ligature.normalize('NFKC'));
  return (pieces.match(LIGATURE_LETTERS) ?? [])
    .map((piece) =>
      LIGATURES[piece]
        ? `(?:${Array.from(piece).join(`${SOFT_HYPHEN}*`)}|${LIGATURES[piece]})`
        : characterPattern(piece),
    )
    // A soft hyphen is invisible in the book and absent from a copy of its words.
    .join(`${SOFT_HYPHEN}*`);
}

/**
 * The expression for a passage in `normalizeEpubSourceText` form. Offsets of a
 * match are offsets into the text it ran on, so what it tolerates (how words
 * are spaced and wrapped, which quotation marks, soft hyphens) never moves the
 * place it reports. The words themselves must all be there, in order.
 */
export function passageMatcher(normalizedPassage: string): RegExp {
  const pattern = normalizedPassage
    .replaceAll(SOFT_HYPHEN, '')
    .split('\n')
    .map((line) => line.split(' ').map(wordPattern).join('\\s+'))
    // Another reading app marks every line break in what it copies, including
    // a <br> that contributes no character at all to this text.
    .join('\\s*');
  return new RegExp(pattern, 'gu');
}

export interface PassageOccurrence {
  offset: number;
  length: number;
}

/** Every place a normalized passage occurs in `text`, in order. */
export function passageOccurrences(text: string, normalizedPassage: string): PassageOccurrence[] {
  const matcher = passageMatcher(normalizedPassage);
  const found: PassageOccurrence[] = [];
  let match: RegExpExecArray | null;
  while ((match = matcher.exec(text)) !== null) {
    found.push({ offset: match.index, length: match[0].length });
    if (match[0].length === 0) matcher.lastIndex += 1;
  }
  return found;
}
