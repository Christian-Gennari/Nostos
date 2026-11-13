import { Pipe, PipeTransform } from '@angular/core';
import { marked } from 'marked';
import { UNKNOWN_BOOK } from '../core/book-format';
import { Note } from '../core/dtos/note.dtos';
import { noteWikilinksToText } from '../ui/pipes/note-format.pipe';
import { hasMeaningfulSelectedText, sourceHumanLabel } from './source-insertion.helpers';

const MAX_PREVIEW_CACHE_ENTRIES = 500;

interface CachedNotePreview {
  raw: string | null | undefined;
  preview: string;
}

const previewByRawValue = new Map<string, string>();
const previewByNoteRef = new WeakMap<object, CachedNotePreview>();

function rawPreviewSource(note: Pick<Note, 'selectedText' | 'content'>): string | null | undefined {
  return hasMeaningfulSelectedText(note) ? note.selectedText : note.content;
}

function renderPreviewText(rawValue: string | null | undefined): string {
  const markdown = noteWikilinksToText(rawValue ?? '');
  const html = marked.parse(markdown, { async: false });
  const text = new DOMParser().parseFromString(html, 'text/html').body.textContent ?? '';
  return text.replace(/\s+/g, ' ').trim();
}

/**
 * Memoized plain-text preview for a source note or highlight. Avoids invoking
 * `marked.parse` + `DOMParser` on every Angular change-detection pass.
 */
export function computeReferencePreview(note: Pick<Note, 'selectedText' | 'content'>): string {
  const raw = rawPreviewSource(note);
  const noteHit = previewByNoteRef.get(note);
  if (noteHit && noteHit.raw === raw) {
    return noteHit.preview;
  }

  const cacheKey = raw ?? '';
  let preview = previewByRawValue.get(cacheKey);
  if (preview === undefined) {
    preview = renderPreviewText(cacheKey);
    if (previewByRawValue.size >= MAX_PREVIEW_CACHE_ENTRIES) {
      const oldest = previewByRawValue.keys().next().value;
      if (oldest !== undefined) previewByRawValue.delete(oldest);
    }
    previewByRawValue.set(cacheKey, preview);
  } else {
    // Refresh recency in insertion-ordered map.
    previewByRawValue.delete(cacheKey);
    previewByRawValue.set(cacheKey, preview);
  }

  previewByNoteRef.set(note, { raw, preview });
  return preview;
}

/** Clears the memoized preview cache (for deterministic unit tests). */
export function clearReferencePreviewCache(): void {
  previewByRawValue.clear();
}

export function formatSourceProvenance(note: Note): string {
  const title = note.bookTitle?.trim() || UNKNOWN_BOOK;
  const locator = sourceHumanLabel(note)?.locator ?? null;
  return locator ? `${title} · ${locator}` : title;
}

export function formatSourceRowLabel(note: Note): string {
  return `Inspect source from ${formatSourceProvenance(note)}: ${computeReferencePreview(note)}`;
}

export interface PrecomputedReferenceRowMeta {
  preview: string;
  provenance: string;
  rowLabel: string;
  hasQuotePreview: boolean;
}

/** Precomputes row display metadata once per note list update. */
export function buildReferenceRowMetaMap(notes: readonly Note[]): ReadonlyMap<string, PrecomputedReferenceRowMeta> {
  const map = new Map<string, PrecomputedReferenceRowMeta>();
  for (const note of notes) {
    const preview = computeReferencePreview(note);
    const provenance = formatSourceProvenance(note);
    map.set(note.id, {
      preview,
      provenance,
      rowLabel: `Inspect source from ${provenance}: ${preview}`,
      hasQuotePreview: hasMeaningfulSelectedText(note),
    });
  }
  return map;
}

@Pipe({
  name: 'referencePreview',
  standalone: true,
  pure: true,
})
export class ReferencePreviewPipe implements PipeTransform {
  transform(note: Pick<Note, 'selectedText' | 'content'> | null | undefined): string {
    return note ? computeReferencePreview(note) : '';
  }
}
