import { Passage } from '../../core/dtos/note.dtos';
import { noteWikilinksToText } from '../pipes/note-format.pipe';

/**
 * The passage as markup for the viewer: its saved reading markup when there is
 * some (EPUB), otherwise its plain text set as paragraphs. The markup was
 * sanitised to reading tags when it was saved, and Angular sanitises it again
 * on binding.
 */
export function passageBodyHtml(passage: Pick<Passage, 'html' | 'text' | 'content'>): string {
  const body = passageTextHtml(passage);
  if (!body) return '';
  // The reader's note opens the first page and turns away with it, its topic
  // links read as the words they show in a card.
  const note = noteWikilinksToText(passage.content ?? '').trim();
  return note ? `<aside class="pv-note">${escapeHtml(note)}</aside>${body}` : body;
}

function passageTextHtml(passage: Pick<Passage, 'html' | 'text'>): string {
  const html = passage.html?.trim();
  if (html) return html;
  const text = passage.text?.trim();
  if (!text) return '';
  return text
    .split(/\n\s*\n/)
    .map((paragraph) => paragraph.replace(/\s+/g, ' ').trim())
    .filter(Boolean)
    .map((paragraph) => `<p>${escapeHtml(paragraph)}</p>`)
    .join('');
}

/** Reader route query params that open the book at a passage's start. */
export function passageSourceParams(
  passage: Pick<Passage, 'anchorKind' | 'start'>,
): Record<string, string | number> | null {
  const start = passage.start?.trim();
  if (!start) return null;
  switch (passage.anchorKind) {
    case 'epub_cfi':
      return { sourceCfi: start };
    case 'pdf_page': {
      const page = Number(start);
      return Number.isInteger(page) && page > 0 ? { sourcePage: page } : null;
    }
    case 'audio_timestamp': {
      const time = Number(start);
      return Number.isFinite(time) && time >= 0 ? { sourceTime: time } : null;
    }
    default:
      return null;
  }
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}
