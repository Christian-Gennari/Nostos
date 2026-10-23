import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import type { WritingSourceDto } from '../../core/dtos/writing.dtos';
import { WritingsService } from '../../core/services/writings.service';
import { CITATION_SCHEME, parseCitationHref } from '../../ui/markdown-editor/citation.extension';
import { CitationService } from '../citations/citation.service';
import { citationStyle } from '../citations/citation-styles';
import type { ExportFormat } from './export-formats';
import type { ExportDocument, PageSetup } from './export-model';
import { type ExportTreeItem, exportChapters } from './export-order';
import { normalizePageSetup } from './export-page-setup';
import { hasExportableContent, resolveExport } from './export-resolve';

/** One document of an export, loaded. */
export interface ExportSourceChapter {
  id: string;
  title: string;
  markdown: string;
  groupPath: { id: string; title: string }[];
}

/** What an export is made from, loaded once per dialog: re-resolving with new options needs no request. */
export interface ExportSource {
  targetId: string;
  title: string;
  type: 'Folder' | 'Document';
  /** In Studio tree order. */
  chapters: ExportSourceChapter[];
  /** The notes kept with these writings, for the bibliography and passage names. */
  kept: WritingSourceDto[];
}

/** Why an export cannot be made, or null when it can. */
export function exportBlockedReason(source: Pick<ExportSource, 'type' | 'chapters'>): string | null {
  if (source.chapters.some((chapter) => hasExportableContent(chapter.markdown))) return null;
  if (source.type === 'Document') return 'This writing is empty. Write something to export it.';
  return source.chapters.length ? 'Every writing in this folder is empty.' : 'This folder has no writings in it yet.';
}

/** The books a writing's live citations point at. */
export function citedBookIds(markdown: string): string[] {
  const books = new Set<string>();
  for (const match of (markdown ?? '').matchAll(/\]\((nostos:cite\/[^)\s]+)\)/g)) {
    const ref = parseCitationHref(match[1]);
    if (ref) books.add(ref.book);
  }
  return [...books];
}

/**
 * Gathers what an export needs and resolves it (issue #1164): the writings'
 * Markdown, the notes kept with them, the books they cite, and the citation
 * style's engine. Formats take the `ExportDocument` this gives.
 */
@Injectable({ providedIn: 'root' })
export class ExportService {
  private readonly writings = inject(WritingsService);
  private readonly citations = inject(CitationService);

  /**
   * Loads a document or folder. `overrides` holds Markdown newer than the
   * server's (the open writing, before its autosave lands).
   */
  async load(items: readonly ExportTreeItem[], targetId: string, overrides: ReadonlyMap<string, string> = new Map()): Promise<ExportSource> {
    const target = items.find((item) => item.id === targetId);
    if (!target) throw new Error('That writing is no longer in the Studio.');
    const refs = exportChapters(items, targetId);
    const chapters = await mapLimited(refs, 6, async (ref) => ({
      ...ref,
      markdown: overrides.get(ref.id) ?? (await firstValueFrom(this.writings.get(ref.id))).content ?? '',
    }));
    const kept = (
      await mapLimited(
        chapters.filter((chapter) => hasExportableContent(chapter.markdown)),
        6,
        (chapter) => firstValueFrom(this.writings.listSources(chapter.id)).catch(() => [] as WritingSourceDto[]),
      )
    ).flat();
    return { targetId, title: target.name, type: target.type, chapters, kept };
  }

  /** The page setup saved with a writing, or null when it has none. */
  async savedSetup(id: string, fallback: PageSetup): Promise<PageSetup | null> {
    try {
      const saved = await firstValueFrom(this.writings.getPageSetup(id));
      return saved ? normalizePageSetup(saved, fallback) : null;
    } catch {
      return null;
    }
  }

  saveSetup(id: string, setup: PageSetup): Promise<unknown> {
    return firstValueFrom(this.writings.savePageSetup(id, { ...setup }));
  }

  /**
   * The finished document. `order` lists chapter ids for this export only
   * (a folder's reordered chapters); ids it leaves out keep tree order after it.
   */
  async resolve(source: ExportSource, setup: PageSetup, order: readonly string[] | null = null, signal?: AbortSignal): Promise<ExportDocument> {
    const chapters = orderChapters(source.chapters, order);
    const style = citationStyle(setup.citationStyle);
    const books = [...new Set(chapters.flatMap((chapter) => (chapter.markdown.includes(CITATION_SCHEME) ? citedBookIds(chapter.markdown) : [])))];
    const renderer = await this.citations.renderer(style, books, source.kept);
    signal?.throwIfAborted();
    return resolveExport({
      title: source.title,
      chapters: chapters.map((chapter) => ({ writingId: chapter.id, title: chapter.title, markdown: chapter.markdown, groupPath: chapter.groupPath })),
      setup,
      style,
      renderer,
      checkImage: loadsAsImage,
      baseUrl: typeof location !== 'undefined' ? location.origin : null,
      slicing: { signal },
    });
  }

  /** Writes the document in `format` and hands it to the browser as a download. */
  async download(doc: ExportDocument, format: Extract<ExportFormat, { action: 'download' }>, filename: string): Promise<void> {
    const blob = await format.render(doc);
    const url = URL.createObjectURL(blob);
    try {
      const link = document.createElement('a');
      link.href = url;
      link.download = filename;
      link.rel = 'noopener';
      document.body.appendChild(link);
      link.click();
      link.remove();
    } finally {
      // The click has already handed the file to the browser.
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
  }
}

export function orderChapters<T extends { id: string }>(chapters: readonly T[], order: readonly string[] | null): T[] {
  if (!order?.length) return [...chapters];
  const byId = new Map(chapters.map((chapter) => [chapter.id, chapter]));
  const out: T[] = [];
  for (const id of order) {
    const chapter = byId.get(id);
    if (chapter) {
      out.push(chapter);
      byId.delete(id);
    }
  }
  return [...out, ...byId.values()];
}

/** True when the address loads as an image within ten seconds. */
export function loadsAsImage(src: string, timeoutMs = 10_000): Promise<boolean> {
  return new Promise((resolve) => {
    if (typeof Image === 'undefined') {
      resolve(true);
      return;
    }
    const image = new Image();
    const done = (ok: boolean) => {
      clearTimeout(timer);
      image.onload = image.onerror = null;
      resolve(ok);
    };
    const timer = setTimeout(() => done(false), timeoutMs);
    image.onload = () => done(true);
    image.onerror = () => done(false);
    image.referrerPolicy = 'no-referrer';
    image.src = src;
  });
}

async function mapLimited<T, R>(items: readonly T[], limit: number, map: (item: T) => Promise<R>): Promise<R[]> {
  const out: R[] = new Array(items.length);
  let next = 0;
  const worker = async () => {
    while (next < items.length) {
      const index = next++;
      out[index] = await map(items[index]);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return out;
}
