export interface SourceInsertionSource {
  bookTitle?: string | null;
  /** The book's author (issue #1044): when a thinker is quoted, the name is what readers look for. */
  bookAuthor?: string | null;
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
  author: string | null;
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

  const authorText = normalizeInlineText(source.bookAuthor);
  const author = authorText ? escapeMarkdownInline(authorText) : null;
  let locator: string | null = null;
  const passage = normalizeInlineText(source.passageTitle);
  if (passage) {
    // Quoted from a kept passage: name the passage, not where it starts.
    return { title: escapeMarkdownInline(title), author, locator: `“${escapeMarkdownInline(passage)}”` };
  }
  if (
    source.anchorVerified === true &&
    source.sourceAnchorKind?.trim().toLowerCase() === 'pdf_page'
  ) {
    const rawPage = normalizeInlineText(source.sourceAnchorValue);
    const page = Number(rawPage);
    if (Number.isInteger(page) && page > 0) locator = `p. ${page}`;
  }

  return { title: escapeMarkdownInline(title), author, locator };
}

/** "Author, Title, locator" with whichever parts are known; `title` is pre-formatted. */
function readableSource(label: HumanSourceLabel, title: string): string {
  return [label.author, title, label.locator].filter(Boolean).join(', ');
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
  // The attribution is its own paragraph inside the quote: as a second line of
  // the same paragraph it ran on after the quoted words once rendered.
  const attribution = `> — ${readableSource(label, `*${label.title}*`)}`;
  return [...quotedLines, '>', attribution].join('\n');
}

export function buildNoteMarkdown(source: SourceInsertionSource): string | null {
  const content = normalizeBlockText(source.content);
  const label = sourceHumanLabel(source);
  if (!content || !label) return null;

  return `${content}\n\n*Source: ${readableSource(label, label.title)}*`;
}

export function buildReferenceMarkdown(source: SourceInsertionSource): string | null {
  const label = sourceHumanLabel(source);
  if (!label) return null;

  return `*See: ${readableSource(label, label.title)}*`;
}
