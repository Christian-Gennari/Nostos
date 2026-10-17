import { NoteSearchHit } from '../../core/dtos/note.dtos';
import { WritingSourceDto } from '../../core/dtos/writing.dtos';
import { SourceInsertionSource } from '../source-insertion.helpers';

/**
 * One thing the `@` palette can put on the page (issue #1039): a kept note,
 * a highlight with its quotation, or a passage. Built from either a search
 * hit or a source already kept with the writing, so the palette does not care
 * where a row came from.
 */
export interface PaletteSource extends SourceInsertionSource {
  id: string;
  bookId: string;
  kind: 'note' | 'passage';
  /** The words the row leads with: the quotation, or the note when there is none. */
  lead: string;
  /** The person's own note, shown under a quotation when there is one. */
  note: string | null;
  /** Whether this source is already kept with the open writing. */
  kept: boolean;
}

export type PaletteInsertMode = 'quote' | 'note' | 'reference';

export interface PaletteChoice {
  source: PaletteSource;
  mode: PaletteInsertMode;
}

function clean(value: string | null | undefined): string {
  return (value ?? '').replace(/\s+/g, ' ').trim();
}

export function fromSearchHit(hit: NoteSearchHit, keptIds: ReadonlySet<string>): PaletteSource {
  return toSource({ ...hit, selectedText: hit.selectedText ?? null }, keptIds.has(hit.id));
}

export function fromKeptSource(source: WritingSourceDto): PaletteSource {
  return toSource(source, true);
}

function toSource(
  value: {
    id: string;
    bookId: string;
    bookTitle?: string | null;
    bookAuthor?: string | null;
    content: string;
    selectedText?: string | null;
    sourceAnchorKind?: string;
    sourceAnchorValue?: string | null;
    anchorVerified?: boolean;
    kind?: string;
    passageTitle?: string | null;
  },
  kept: boolean,
): PaletteSource {
  const quote = clean(value.selectedText);
  const note = clean(value.content);
  return {
    id: value.id,
    bookId: value.bookId,
    kind: value.kind === 'passage' ? 'passage' : 'note',
    bookTitle: value.bookTitle ?? null,
    bookAuthor: value.bookAuthor ?? null,
    selectedText: value.selectedText ?? null,
    content: value.content,
    sourceAnchorKind: value.sourceAnchorKind ?? null,
    sourceAnchorValue: value.sourceAnchorValue ?? null,
    anchorVerified: value.anchorVerified ?? null,
    passageTitle: value.passageTitle ?? null,
    lead: quote || note,
    note: quote && note ? note : null,
    kept,
  };
}

/** "Author · Book", from whatever is known. */
export function sourceByline(source: PaletteSource): string {
  return [clean(source.bookAuthor), clean(source.bookTitle)].filter(Boolean).join(' · ');
}

/** The words of a query, lower-cased, at least two letters each. */
export function queryWords(query: string): string[] {
  return (query.toLowerCase().match(/[\p{L}\p{N}][\p{L}\p{N}'’-]*/gu) ?? []).filter((w) => w.length >= 2);
}

export interface TextSegment {
  text: string;
  match: boolean;
}

/** Splits `text` so the words of the query can be emphasised where they occur. */
export function highlightSegments(text: string, words: readonly string[]): TextSegment[] {
  if (!text || words.length === 0) return [{ text, match: false }];
  const escaped = [...words].sort((a, b) => b.length - a.length).map(escapeRegExp);
  const re = new RegExp(`(${escaped.join('|')})`, 'giu');
  const segments: TextSegment[] = [];
  let last = 0;
  for (const m of text.matchAll(re)) {
    const at = m.index ?? 0;
    if (at > last) segments.push({ text: text.slice(last, at), match: false });
    segments.push({ text: m[0], match: true });
    last = at + m[0].length;
  }
  if (last < text.length) segments.push({ text: text.slice(last), match: false });
  return segments;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Re-ranks server hits so a row that holds every word of the query comes
 * first, then rows by how many words they hold, in the server's order
 * otherwise. Words in the quotation or note count over words only in the
 * byline, a source already kept with the writing wins a tie, and a direct
 * quotation wins one over a passage.
 */
export function rankSources(sources: readonly PaletteSource[], words: readonly string[]): PaletteSource[] {
  if (words.length === 0) return [...sources];
  const score = (s: PaletteSource): number => {
    const own = `${s.lead} ${s.note ?? ''} ${s.passageTitle ?? ''}`.toLowerCase();
    const byline = sourceByline(s).toLowerCase();
    let hits = 0;
    let ownHits = 0;
    for (const w of words) {
      const inOwn = own.includes(w);
      if (inOwn || byline.includes(w)) hits++;
      if (inOwn) ownHits++;
    }
    // A quotation goes straight onto the page; a passage first opens to have
    // its sentences chosen. When both answer the query as well, the direct one
    // leads, so Enter does what was asked.
    const passagePenalty = s.kind === 'passage' ? 2 : 0;
    return (hits === words.length ? 1000 : 0) + hits * 10 + ownHits * 2 + (s.kept ? 1 : 0) - passagePenalty;
  };
  return sources
    .map((s, i) => ({ s, i, score: score(s) }))
    .sort((a, b) => b.score - a.score || a.i - b.i)
    .map((x) => x.s);
}

/**
 * The query with each word that matches nothing replaced by the closest word
 * from `vocabulary` (author names and titles), when one is within an edit or
 * two. "schopenhaur" becomes "schopenhauer"; a word already in the vocabulary,
 * or one with no close neighbour, is left alone.
 */
export function correctQuery(query: string, vocabulary: ReadonlySet<string>): string {
  const words = queryWords(query);
  if (words.length === 0 || vocabulary.size === 0) return query;
  let changed = false;
  const corrected = words.map((word) => {
    if (word.length < 4 || vocabulary.has(word)) return word;
    const budget = word.length >= 8 ? 2 : 1;
    let best: string | null = null;
    let bestDistance = budget + 1;
    for (const candidate of vocabulary) {
      if (Math.abs(candidate.length - word.length) > budget) continue;
      const d = editDistance(word, candidate, budget);
      if (d < bestDistance) {
        best = candidate;
        bestDistance = d;
      }
    }
    if (best && bestDistance <= budget) {
      changed = true;
      return best;
    }
    return word;
  });
  return changed ? corrected.join(' ') : query;
}

/** Optimal-string-alignment distance, stopping once it exceeds `limit`. */
export function editDistance(a: string, b: string, limit = Infinity): number {
  const rows = a.length + 1;
  const cols = b.length + 1;
  let prevPrev: number[] = [];
  let prev = Array.from({ length: cols }, (_, j) => j);
  for (let i = 1; i < rows; i++) {
    const row = [i];
    let rowMin = i;
    for (let j = 1; j < cols; j++) {
      const cost = a[i - 1] === b[j - 1] ? 0 : 1;
      let v = Math.min(prev[j] + 1, row[j - 1] + 1, prev[j - 1] + cost);
      if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) {
        v = Math.min(v, prevPrev[j - 2] + 1);
      }
      row.push(v);
      rowMin = Math.min(rowMin, v);
    }
    if (rowMin > limit) return rowMin;
    prevPrev = prev;
    prev = row;
  }
  return prev[cols - 1];
}

/** The words of authors and titles, as the vocabulary `correctQuery` corrects towards. */
export function vocabularyOf(texts: readonly (string | null | undefined)[]): Set<string> {
  const out = new Set<string>();
  for (const t of texts) for (const w of queryWords(t ?? '')) if (w.length >= 4) out.add(w);
  return out;
}

const RECENT_KEY = 'nostos.studio.recentSources';
const RECENT_MAX = 8;

/**
 * Sources recently put on a page, newest first; per browser, a convenience.
 * Kept per account (`scope`), so on a shared browser one person's quotations
 * never appear in another's palette.
 */
export function readRecentSources(scope = 'local'): PaletteSource[] {
  try {
    const raw = localStorage.getItem(`${RECENT_KEY}:${scope}`);
    const parsed = raw ? (JSON.parse(raw) as unknown) : [];
    return Array.isArray(parsed)
      ? (parsed.filter((s) => s && typeof s.id === 'string' && typeof s.lead === 'string') as PaletteSource[])
      : [];
  } catch {
    return [];
  }
}

export function rememberRecentSource(source: PaletteSource, scope = 'local'): void {
  try {
    const rest = readRecentSources(scope).filter((s) => s.id !== source.id);
    localStorage.setItem(
      `${RECENT_KEY}:${scope}`,
      JSON.stringify([{ ...source, kept: false }, ...rest].slice(0, RECENT_MAX)),
    );
  } catch {
    // Recents are a convenience; a full or blocked storage just skips them.
  }
}
