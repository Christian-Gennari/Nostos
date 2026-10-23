import type { CitationRef } from '../../ui/markdown-editor/citation.extension';
import type { CitationRenderer } from '../../ui/markdown-editor/live-citations';
import { citationStyle } from '../citations/citation-styles';
import type { ExportDocument, PageSetup } from './export-model';
import { type ExportChapterInput, resolveExport } from './export-resolve';

/**
 * Test fixtures for the export specs (issues #1164–#1167): a stand-in
 * citation renderer, and writings that use every citation role.
 */

export const NAMES: Record<string, string> = { b1: 'Voltaire', b2: 'Kant', b3: 'Weil' };

/**
 * The live-citations spec's stand-in renderer: predictable words for each
 * place a citation stands. A book not in NAMES is one that was deleted.
 */
export function renderer(placement: CitationRenderer['placement'], options: { year?: string; kept?: string[] } = {}): CitationRenderer {
  const year = options.year ?? '1992';
  const page = (ref: CitationRef, prefix: string) => (ref.page ? `${prefix}${ref.page}` : '');
  return {
    placement,
    bibliographyHeading: placement === 'note' ? 'Bibliography' : 'References',
    bibliographyBooks: options.kept ?? [],
    inText: (ref) => {
      if (!NAMES[ref.book]) return null;
      return placement === 'house' ? `*See: ${NAMES[ref.book]}${page(ref, ', p. ')}*` : `(${NAMES[ref.book]}, ${year}${page(ref, ', p. ')})`;
    },
    note: (ref, subsequent) => {
      if (!NAMES[ref.book]) return null;
      return subsequent ? `${NAMES[ref.book]}, *Short*${page(ref, ', ')}.` : `${NAMES[ref.book]}, *Full* (Knopf, ${year})${page(ref, ', ')}.`;
    },
    bibliography: (books) => {
      if (books.some((book) => !NAMES[book])) return null;
      return [...books].sort((a, b) => NAMES[a].localeCompare(NAMES[b])).map((book) => ({ book, text: `${NAMES[book]}. *Book*. ${year}.` }));
    },
  };
}

export const SETUP: PageSetup = { paper: 'a4', marginMm: 25.4, font: 'newsreader', lineSpacing: 1.5, citationStyle: 'apa' };

export function chapter(markdown: string, title = 'Essay', groupPath: ExportChapterInput['groupPath'] = []): ExportChapterInput {
  return { writingId: title.toLowerCase().replace(/\W+/g, '-'), title, markdown, groupPath };
}

export async function resolve(
  chapters: ExportChapterInput[] | string,
  placement: CitationRenderer['placement'] = 'in-text',
  extra: Partial<Parameters<typeof resolveExport>[0]> = {},
): Promise<ExportDocument> {
  const list = typeof chapters === 'string' ? [chapter(chapters)] : chapters;
  const styleId = placement === 'note' ? 'chicago-notes' : placement === 'house' ? 'nostos' : 'apa';
  return resolveExport({
    title: list[0]?.title ?? 'Untitled',
    chapters: list,
    setup: { ...SETUP, citationStyle: styleId },
    style: citationStyle(styleId),
    renderer: renderer(placement),
    slicing: { yieldToPage: () => Promise.resolve() },
    ...extra,
  });
}


/** What the Studio's own live-citations spec writes (issue #1157). */
export const IN_TEXT = [
  'Opening line.',
  '> To be is to do.\n> \n> [(Voltaire, 1992, p. 42)](nostos:cite/b1?p=42&as=quote&src=n1)',
  'My own thought [(Kant, 1992)](nostos:cite/b2?as=note)',
  'As argued [(Voltaire, 1992)](nostos:cite/b1?as=reference) here.',
  '## References',
  '[Kant. *Book*. 1992.](nostos:cite/b2?as=bib)',
  '[Voltaire. *Book*. 1992.](nostos:cite/b1?as=bib)',
].join('\n\n');

export const NOTES = [
  'Opening line.',
  '> To be is to do.[^1]',
  'My own thought[^2]',
  'As argued[^3] here.',
  '[^1]: [Voltaire, *Full* (Knopf, 1992), 42.](nostos:cite/b1?p=42&as=quote&src=n1)',
  '[^2]: [Kant, *Full* (Knopf, 1992).](nostos:cite/b2?as=note)',
  '[^3]: [Voltaire, *Short*.](nostos:cite/b1?as=reference)',
  '## Bibliography',
  '[Kant. *Book*. 1992.](nostos:cite/b2?as=bib)',
  '[Voltaire. *Book*. 1992.](nostos:cite/b1?as=bib)',
].join('\n\n');
