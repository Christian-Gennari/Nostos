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

const TOPIC_REFERENCE = /\[\[([^\[\]]*?)\]\]/g;

/**
 * A note's words as they read in an essay: `[[Topic]]` links become the plain
 * topic name (the Brain does the same when a topic is unlinked), and a line
 * that holds only topic links, which is what linking a topic writes, is left
 * out. Without this the editor would escape the brackets and show them.
 */
export function noteTextForWriting(content: string | null | undefined): string {
  const text = (content ?? '').replace(/\r\n?/g, '\n');
  const onlyLinks = (line: string) => {
    const rest = line.replace(TOPIC_REFERENCE, '');
    return rest !== line && /^[\s,;·|]*$/.test(rest);
  };
  return text
    .split('\n')
    .filter((line) => !onlyLinks(line))
    .join('\n')
    .replace(TOPIC_REFERENCE, (_, name: string) => name.trim())
    .replace(/\n{3,}/g, '\n\n')
    .trim();
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
  const kind = source.sourceAnchorKind?.trim().toLowerCase();
  const rawValue = normalizeInlineText(source.sourceAnchorValue);
  const value = rawValue ? Number(rawValue) : NaN;
  // A PDF page only when verified; a paper book's page as the reader typed it.
  if ((kind === 'pdf_page' && source.anchorVerified === true) || kind === 'physical_page') {
    if (Number.isInteger(value) && value > 0) locator = `p. ${value}`;
  } else if (kind === 'audio_timestamp' && source.anchorVerified === true && Number.isFinite(value) && value >= 0) {
    const whole = Math.floor(value);
    const h = Math.floor(whole / 3600);
    const mm = String(Math.floor((whole % 3600) / 60)).padStart(2, '0');
    const ss = String(whole % 60).padStart(2, '0');
    locator = `at ${h > 0 ? `${h}:${mm}:${ss}` : `${Math.floor(whole / 60)}:${ss}`}`;
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
  return noteTextForWriting(source.content).length > 0;
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
  const content = noteTextForWriting(source.content);
  const label = sourceHumanLabel(source);
  if (!content || !label) return null;

  return `${content}\n\n*Source: ${readableSource(label, label.title)}*`;
}

export function buildReferenceMarkdown(source: SourceInsertionSource): string | null {
  const label = sourceHumanLabel(source);
  if (!label) return null;

  return `*See: ${readableSource(label, label.title)}*`;
}
