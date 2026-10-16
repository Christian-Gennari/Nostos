export interface SourceInsertionSource {
  bookTitle?: string | null;
  content?: string | null;
  selectedText?: string | null;
  sourceAnchorKind?: string | null;
  sourceAnchorValue?: string | null;
  anchorVerified?: boolean | null;
  /** A passage's title (issue #1014): the passage is the locator. */
  passageTitle?: string | null;
}

export interface HumanSourceLabel {
  title: string;
  locator: string | null;
}

function normalizeBlockText(value: string | null | undefined): string {
  return (value ?? '').replace(/\r\n?/g, '\n').trim();
}

function normalizeInlineText(value: string | null | undefined): string {
  return normalizeBlockText(value).replace(/\s+/g, ' ');
}

function escapeMarkdownInline(value: string): string {
  return value.replace(/([\\*_\x60])/g, '\\$1');
}

export function sourceHumanLabel(source: SourceInsertionSource): HumanSourceLabel | null {
  const title = normalizeInlineText(source.bookTitle);
  if (!title) return null;

  let locator: string | null = null;
  const passage = normalizeInlineText(source.passageTitle);
  if (passage) {
    // Quoted from a kept passage: name the passage, not where it starts.
    return { title: escapeMarkdownInline(title), locator: `“${escapeMarkdownInline(passage)}”` };
  }
  if (
    source.anchorVerified === true &&
    source.sourceAnchorKind?.trim().toLowerCase() === 'pdf_page'
  ) {
    const rawPage = normalizeInlineText(source.sourceAnchorValue);
    const page = Number(rawPage);
    if (Number.isInteger(page) && page > 0) locator = `p. ${page}`;
  }

  return { title: escapeMarkdownInline(title), locator };
}

export function hasMeaningfulSelectedText(source: SourceInsertionSource): boolean {
  return normalizeBlockText(source.selectedText).length > 0;
}

export function hasMeaningfulNoteContent(source: SourceInsertionSource): boolean {
  return normalizeBlockText(source.content).length > 0;
}

export function buildQuoteMarkdown(source: SourceInsertionSource): string | null {
  const quote = normalizeBlockText(source.selectedText);
  const label = sourceHumanLabel(source);
  if (!quote || !label) return null;

  const quotedLines = quote.split('\n').map((line) => (line ? `> ${line}` : '>'));
  const attribution = `> — *${label.title}*${label.locator ? `, ${label.locator}` : ''}`;
  return [...quotedLines, attribution].join('\n');
}

export function buildNoteMarkdown(source: SourceInsertionSource): string | null {
  const content = normalizeBlockText(source.content);
  const label = sourceHumanLabel(source);
  if (!content || !label) return null;

  const readable = `${label.title}${label.locator ? `, ${label.locator}` : ''}`;
  return `${content}\n\n*Source: ${readable}*`;
}

export function buildReferenceMarkdown(source: SourceInsertionSource): string | null {
  const label = sourceHumanLabel(source);
  if (!label) return null;

  const readable = `${label.title}${label.locator ? `, ${label.locator}` : ''}`;
  return `*See: ${readable}*`;
}
