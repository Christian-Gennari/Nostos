import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import type { NoteSearchHit } from '../../core/dtos/note.dtos';
import { HighlightImportService } from '../../core/services/highlight-import.service';
import { WritingsService } from '../../core/services/writings.service';
import { CitationService } from '../citations/citation.service';
import { StudioPreferencesService } from '../page-settings/studio-preferences.service';
import { StarterPassage, composeStarterDraft, starterDraftTitle } from './starter-draft';

/**
 * Starts a draft from the reader's imported highlights (issue #1220): picks
 * the passages, writes them in as the `@` palette would, keeps them with the
 * writing, and tells the Studio to open it with the caret ready. The same on
 * every deployment: it uses only the Studio's own writing and citation calls.
 */
@Injectable({ providedIn: 'root' })
export class StarterDraftService {
  private readonly imports = inject(HighlightImportService);
  private readonly writings = inject(WritingsService);
  private readonly citations = inject(CitationService);
  private readonly prefs = inject(StudioPreferencesService);

  /** The writing whose caret goes after its text once the Studio opens it. */
  private caretFor: string | null = null;

  /** The passages to start from: of these imports, or of the library's latest. */
  passages(batchIds: readonly string[] = []): Promise<NoteSearchHit[]> {
    return firstValueFrom(this.imports.starterPassages(batchIds));
  }

  /** Creates the draft and returns its id, or null when there is nothing to quote. */
  async create(hits: readonly NoteSearchHit[]): Promise<string | null> {
    const passages = hits.filter((hit) => (hit.selectedText ?? '').trim()).map(toPassage);
    if (!passages.length) return null;

    const style = this.prefs.citationStyle();
    const content = await composeStarterDraft(passages, style, (passage, subsequent, footnote) =>
      this.citations.insertion(passage, 'quote', style, subsequent, footnote),
    );
    const name = starterDraftTitle(passages);

    const created = await firstValueFrom(this.writings.create({ name, type: 'Document', parentId: null }));
    await firstValueFrom(this.writings.update(created.id, { name, content }));
    // Kept with the writing, as a quote from the palette is: the Reference
    // rail lists them under "For this writing" and the bibliography finds them.
    for (const passage of passages) {
      try {
        await firstValueFrom(this.writings.addSource(created.id, passage.id));
      } catch {
        // The quote and its citation are in the writing either way.
      }
    }
    this.caretFor = created.id;
    return created.id;
  }

  /** True once, for the draft just created: the Studio puts the caret after its text. */
  takeCaret(writingId: string): boolean {
    if (this.caretFor !== writingId) return false;
    this.caretFor = null;
    return true;
  }
}

function toPassage(hit: NoteSearchHit): StarterPassage {
  return {
    id: hit.id,
    bookId: hit.bookId,
    bookTitle: hit.bookTitle,
    bookAuthor: hit.bookAuthor ?? null,
    content: hit.content,
    selectedText: hit.selectedText,
    sourceAnchorKind: hit.sourceAnchorKind ?? null,
    sourceAnchorValue: hit.sourceAnchorValue ?? null,
    anchorVerified: hit.anchorVerified ?? null,
    passageTitle: hit.passageTitle ?? null,
  };
}
