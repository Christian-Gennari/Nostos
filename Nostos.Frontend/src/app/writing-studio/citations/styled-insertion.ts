import { CitationStyle } from './citation-styles';

/** What goes in at the caret, and the footnote that goes at the end of the writing. */
export interface StyledInsertion {
  markdown: string;
  footnote: { number: number; text: string } | null;
}

function block(text: string | null | undefined): string {
  return (text ?? '').replace(/\r\n?/g, '\n').trim();
}

/**
 * A quotation in a citation style (issue #1109). In-text styles close the
 * quotation with "(Schopenhauer, 1851, p. 42)"; note styles put a footnote
 * marker after its last word and the note at the end.
 */
export function styledQuote(
  quote: string | null | undefined,
  citation: string,
  style: CitationStyle,
  footnoteNumber: number,
): StyledInsertion | null {
  const lines = block(quote).split('\n');
  if (!lines.join('').trim()) return null;
  const quoted = lines.map((line) => (line ? `> ${line}` : '>'));
  if (style.kind === 'note') {
    quoted[quoted.length - 1] += `[^${footnoteNumber}]`;
    return { markdown: quoted.join('\n'), footnote: { number: footnoteNumber, text: citation } };
  }
  return { markdown: [...quoted, '>', `> ${citation}`].join('\n'), footnote: null };
}

/** The writer's own note on a source, cited after its last sentence. */
export function styledNote(
  content: string | null | undefined,
  citation: string,
  style: CitationStyle,
  footnoteNumber: number,
): StyledInsertion | null {
  const text = block(content);
  if (!text) return null;
  if (style.kind === 'note') {
    return { markdown: `${text}[^${footnoteNumber}]`, footnote: { number: footnoteNumber, text: citation } };
  }
  return { markdown: `${text} ${citation}`, footnote: null };
}

/** A bare reference in the sentence: the in-text citation, or a footnote marker. */
export function styledReference(citation: string, style: CitationStyle, footnoteNumber: number): StyledInsertion {
  if (style.kind === 'note') {
    return { markdown: `[^${footnoteNumber}]`, footnote: { number: footnoteNumber, text: citation } };
  }
  return { markdown: citation, footnote: null };
}

/** A bibliography block: a heading and one paragraph per entry. */
export function bibliographyMarkdown(entries: readonly string[], style: CitationStyle): string | null {
  if (!entries.length) return null;
  const heading = style.id === 'mla' ? 'Works Cited' : style.id === 'apa' || style.id === 'harvard' ? 'References' : 'Bibliography';
  return [`## ${heading}`, ...entries].join('\n\n');
}
