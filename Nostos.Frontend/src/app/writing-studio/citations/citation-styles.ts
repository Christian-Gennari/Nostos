/**
 * The citation styles the Studio offers (issue #1109). "Nostos" is the
 * original house format and stays the default, so nothing changes for anyone
 * who never picks a style. The others are CSL styles from the Citation Style
 * Language project, bundled under `public/csl/` (CC BY-SA 3.0, see NOTICE).
 */
export type CitationStyleId =
  | 'nostos'
  | 'apa'
  | 'harvard'
  | 'chicago-author-date'
  | 'chicago-notes'
  | 'oxford-notes'
  | 'mla';

export interface CitationStyle {
  id: CitationStyleId;
  label: string;
  /** The CSL file under `/csl/styles/`, or null for the house format. */
  file: string | null;
  /** In-text styles cite in brackets; note styles cite in a footnote. */
  kind: 'house' | 'in-text' | 'note';
  locale: 'en-US' | 'en-GB';
}

export const CITATION_STYLES: readonly CitationStyle[] = [
  { id: 'nostos', label: 'Nostos', file: null, kind: 'house', locale: 'en-US' },
  { id: 'apa', label: 'APA 7', file: 'apa.csl', kind: 'in-text', locale: 'en-US' },
  { id: 'harvard', label: 'Harvard', file: 'harvard-cite-them-right.csl', kind: 'in-text', locale: 'en-GB' },
  { id: 'chicago-author-date', label: 'Chicago (author–date)', file: 'chicago-author-date.csl', kind: 'in-text', locale: 'en-US' },
  { id: 'chicago-notes', label: 'Chicago (notes)', file: 'chicago-notes-bibliography.csl', kind: 'note', locale: 'en-US' },
  { id: 'oxford-notes', label: 'Oxford (footnotes)', file: 'oxford-guide-to-style-notes.csl', kind: 'note', locale: 'en-GB' },
  { id: 'mla', label: 'MLA 9', file: 'modern-language-association.csl', kind: 'in-text', locale: 'en-US' },
];

export const DEFAULT_CITATION_STYLE: CitationStyleId = 'nostos';

export function citationStyle(id: string | null | undefined): CitationStyle {
  return CITATION_STYLES.find((style) => style.id === id) ?? CITATION_STYLES[0];
}

export function isCitationStyleId(value: unknown): value is CitationStyleId {
  return CITATION_STYLES.some((style) => style.id === value);
}
