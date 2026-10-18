import { DestroyRef, Injectable, inject, signal } from '@angular/core';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import { AssistantContextService } from '../ui/assistant/assistant-context.service';
import { AssistantService } from '../ui/assistant/assistant.service';
import type { TopicSuggestionState } from '../ui/topic-picker-popover/topic-picker-popover.component';

/**
 * Optional AI suggestions for one note's topic picker. Asks Ask Nostos for up
 * to three existing topics that fit the note, only when the assistant is
 * available, and only while that note's picker is open. The suggestions are
 * names in the picker; choosing one links it like any other choice.
 *
 * Provided per page: one note at a time asks.
 */
@Injectable()
export class BrainTopicSuggestionsService {
  private readonly assistant = inject(AssistantService);
  private readonly assistantContext = inject(AssistantContextService);

  readonly noteId = signal<string | null>(null);
  readonly state = signal<TopicSuggestionState>('off');
  readonly names = signal<string[]>([]);

  private turnId: string | null = null;
  private unregister: (() => void) | null = null;

  constructor() {
    const finished = this.assistant.turnFinished.subscribe((event) => {
      if (!this.turnId || event.turnId !== this.turnId) return;
      this.turnId = null;
      const noteId = this.noteId();
      if (event.error) {
        this.state.set('error');
        return;
      }
      const names = (event.response?.suggestions ?? [])
        .filter((item) => item.kind === 'topic' && item.noteId === noteId && !!item.label?.trim())
        .map((item) => item.label.trim())
        .slice(0, 3);
      this.names.set(names);
      this.state.set('ready');
    });
    inject(DestroyRef).onDestroy(() => {
      finished.unsubscribe();
      this.clear();
    });
  }

  /** The suggestion state for `note`'s picker; `off` for every other note. */
  stateFor(noteId: string): TopicSuggestionState {
    return this.noteId() === noteId ? this.state() : 'off';
  }

  namesFor(noteId: string): string[] {
    return this.noteId() === noteId ? this.names() : [];
  }

  /** A picker opened on `note`: ask, unless the assistant is off or busy. */
  request(note: NoteSearchHit): void {
    this.clear();
    if (!this.assistant.surfaceAvailable()) return;

    this.noteId.set(note.id);
    // The proposal turn reads which note to suggest for from the context.
    this.unregister = this.assistantContext.register(
      () => ({
        brainReviewNoteId: note.id,
        bookId: note.bookId,
        bookTitle: note.bookTitle ?? undefined,
        selectedText: note.selectedText ?? undefined,
      }),
      { explicit: true },
    );
    const turnId = this.assistant.requestTopicProposals(note.id);
    if (!turnId) {
      // Ask Nostos is in the middle of something else: no suggestions this time.
      this.state.set('off');
      return;
    }
    this.turnId = turnId;
    this.state.set('loading');
  }

  /** The picker closed, or the page went away: stop asking and forget. */
  clear(): void {
    if (this.turnId && this.assistant.activeTurnId() === this.turnId) this.assistant.stopActiveTurn();
    this.turnId = null;
    const noteId = this.noteId();
    if (noteId) this.assistant.dismissSuggestionsForNote(noteId);
    this.unregister?.();
    this.unregister = null;
    this.noteId.set(null);
    this.names.set([]);
    this.state.set('off');
  }
}
