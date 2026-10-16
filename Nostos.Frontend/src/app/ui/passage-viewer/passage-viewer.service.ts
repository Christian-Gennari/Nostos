import { Injectable, signal } from '@angular/core';
import { Passage } from '../../core/dtos/note.dtos';

/** What opening the passage viewer asks for. */
export interface PassageViewerRequest {
  noteId: string;
  /**
   * Replaces the default "Open in book" (the reader route at the passage's
   * start). The reader passes its own, which turns its open book to the
   * passage instead of reloading it.
   */
  openInBook?: (passage: Passage) => void;
  /**
   * Offered as "Quote in writing" for text selected in the viewer (Studio,
   * issue #1021). Absent everywhere else.
   */
  quoteSelection?: (text: string, passage: Passage) => void;
}

/**
 * Opens the passage viewer (issue #1014) from anywhere: a note card in the
 * reader, on a book's page, in Brain or in Studio. One viewer is mounted at the
 * app root, so no page has to host its own.
 */
@Injectable({ providedIn: 'root' })
export class PassageViewerService {
  readonly request = signal<PassageViewerRequest | null>(null);

  open(noteId: string, options: Omit<PassageViewerRequest, 'noteId'> = {}): void {
    this.request.set({ noteId, ...options });
  }

  close(): void {
    this.request.set(null);
  }
}
