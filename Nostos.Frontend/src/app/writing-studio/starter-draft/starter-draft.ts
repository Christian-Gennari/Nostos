import type { CitationStyle } from '../citations/citation-styles';
import type { CitableSource } from '../citations/citation.service';
import type { StyledInsertion } from '../citations/styled-insertion';
import { buildNoteMarkdown, buildQuoteMarkdown } from '../source-insertion.helpers';

/**
 * A starter draft (issue #1220): a first writing that already holds three of
 * the reader's own imported passages, quoted with their sources, so the first
 * session in the Studio reaches the moment Nostos is for. Nothing in it is
 * invented beyond one short, neutral line; nothing is written by AI.
 */

/** An imported highlight the draft quotes: the note, with its book. */
export interface StarterPassage extends CitableSource {
  id: string;
  bookId: string;
  bookTitle?: string | null;
}

/** The name a starter draft gets when its passages come from more than one book. */
export const STARTER_DRAFT_TITLE = 'First draft';

/** The writing's name: the book's title when every passage is from one book. */
export function starterDraftTitle(passages: readonly StarterPassage[]): string {
  const books = new Set(passages.map((passage) => passage.bookId));
  const title = (passages[0]?.bookTitle ?? '').replace(/\s+/g, ' ').trim();
  return books.size === 1 && title ? title : STARTER_DRAFT_TITLE;
}

/** The draft's one line of its own: what is below, and where to write. */
export function starterDraftPrompt(count: number): string {
  if (count === 1) return 'A passage you highlighted, with its source. Start writing below it.';
  const number = count === 2 ? 'Two' : count === 3 ? 'Three' : String(count);
  return `${number} passages you highlighted, with their sources. Start writing below them.`;
}

/**
 * How one passage goes in as a quote, exactly as the `@` palette puts it in:
 * `CitationService.insertion` in the writer's style, null when the book
 * cannot be cited.
 */
export type StarterQuote = (
  passage: StarterPassage,
  subsequent: boolean,
  footnoteNumber: number,
) => Promise<StyledInsertion | null>;

/** The reader's own note on a passage, as a paragraph under its quote. */
function noteParagraph(passage: StarterPassage): string | null {
  const note = (passage.content ?? '').replace(/\r\n?/g, '\n').trim();
  return note || null;
}

/**
 * The draft's Markdown: the prompt, then each passage quoted as the `@`
 * palette quotes it (a live citation in the chosen style, or the house text
 * when the book cannot be read), the reader's note under it, and a note
 * style's footnotes at the end where the editor keeps them.
 */
export async function composeStarterDraft(
  passages: readonly StarterPassage[],
  style: CitationStyle,
  quote: StarterQuote,
): Promise<string> {
  const blocks: string[] = [starterDraftPrompt(passages.length)];
  const footnotes: string[] = [];
  const cited = new Set<string>();

  for (const passage of passages) {
    const subsequent = style.kind === 'note' && cited.has(passage.bookId);
    const styled = await quote(passage, subsequent, footnotes.length + 1);
    // The palette's own fallback when a book cannot be cited.
    const markdown = styled?.markdown ?? buildQuoteMarkdown(passage) ?? buildNoteMarkdown(passage);
    if (!markdown) continue;
    blocks.push(markdown.trim());
    if (styled?.footnote) footnotes.push(`[^${styled.footnote.number}]: ${styled.footnote.text.trim()}`);
    if (styled) cited.add(passage.bookId);
    // A passage with no quotable words went in as the note itself.
    const note = markdown.trimStart().startsWith('>') ? noteParagraph(passage) : null;
    if (note) blocks.push(note);
  }

  return [...blocks, ...footnotes].join('\n\n');
}

/** Whether the empty Studio's offer of a starter draft has been answered on this device. */
const STARTER_OFFER_KEY = 'nostos.studio.starterDraftOffer';

export function starterOfferAnswered(): boolean {
  try {
    return localStorage.getItem(STARTER_OFFER_KEY) !== null;
  } catch {
    return false;
  }
}

/** Taken or declined: either way it is not offered again. */
export function answerStarterOffer(answer: 'taken' | 'declined'): void {
  try {
    localStorage.setItem(STARTER_OFFER_KEY, answer);
  } catch {
    // A convenience only: without storage the offer may come back.
  }
}
