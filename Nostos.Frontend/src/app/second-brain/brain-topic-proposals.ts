import { signal } from '@angular/core';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import type { TopicDetailDto, TopicsService } from '../core/services/topics.service';
import type {
  AssistantService,
  AssistantSuggestionDto,
  AssistantTurnResponse,
} from '../ui/assistant/assistant.service';
import type { BrainTopicProposalState } from './topic-proposals/brain-topic-proposals.component';

/** What the suggestions need from the Brain, and how they report a link. */
export interface BrainTopicProposalsHost {
  /** The note being browsed or reviewed, if any. */
  focusedNote(): NoteSearchHit | null;
  /** Changes whenever the focused note, its text or its topics change. */
  focusedNoteKey(): string | null;
  isEditingNote(): boolean;
  /** The assistant linked the focused note to `accepted`. */
  linked(accepted: AssistantSuggestionDto): void;
  /** Put keyboard focus back on the control that asked for suggestions. */
  restoreFocus(): void;
}

/** What the assistant reports when one of its turns ends. */
export interface AssistantTurnOutcome {
  turnId: string;
  response: AssistantTurnResponse | null;
  error: string | null;
}

/**
 * The assistant's suggested topics for the focused note: asking for them,
 * showing which came back, and accepting one.
 *
 * Suggestions belong to one exact state of one note. They are bound to the
 * note's key when requested, and every later step checks that key, so an
 * answer that arrives after the user moved on, or edited the note, is dropped
 * instead of being shown beside the wrong text.
 */
export class BrainTopicProposals {
  readonly state = signal<BrainTopicProposalState>('idle');
  readonly candidates = signal<AssistantSuggestionDto[]>([]);
  readonly message = signal<string | null>(null);
  readonly detail = signal<TopicDetailDto | null>(null);
  readonly inspectingId = signal<string | null>(null);

  private noteKey: string | null = null;
  private noteId: string | null = null;
  private turnId: string | null = null;
  private linkTurnId: string | null = null;
  private accepted: AssistantSuggestionDto | null = null;
  private readonly ignoredTurns = new Set<string>();

  constructor(
    private readonly assistant: AssistantService,
    private readonly topicsService: TopicsService,
    private readonly host: BrainTopicProposalsHost,
  ) {}

  /** Explicitly request the assistant's validated proposal artifact beside this note. */
  ask(): void {
    const note = this.host.focusedNote();
    if (!note || this.host.isEditingNote()) return;
    this.clear();
    this.noteKey = this.host.focusedNoteKey();
    this.noteId = note.id;
    const turnId = this.assistant.requestTopicProposals(note.id);
    if (!turnId) {
      this.state.set('unavailable');
      this.message.set('Ask Nostos is busy or unavailable. You can still link a topic manually.');
      return;
    }
    this.turnId = turnId;
    this.state.set('loading');
  }

  cancel(): void {
    this.clear();
    this.host.restoreFocus();
  }

  clear(): void {
    if (this.turnId) {
      this.ignoredTurns.add(this.turnId);
      if (this.assistant.activeTurnId() === this.turnId) this.assistant.stopActiveTurn();
    }
    this.turnId = null;
    this.linkTurnId = null;
    this.accepted = null;
    if (this.noteId) this.assistant.dismissSuggestionsForNote(this.noteId);
    this.noteId = null;
    this.noteKey = null;
    this.candidates.set([]);
    this.detail.set(null);
    this.inspectingId.set(null);
    this.message.set(null);
    this.state.set('idle');
  }

  /** The focused note changed: suggestions made for another note or text go. */
  noteFocusChanged(key: string | null): void {
    if (this.noteKey && this.noteKey !== key) this.clear();
  }

  inspect(candidate: AssistantSuggestionDto): void {
    if (!candidate.value || this.noteKey !== this.host.focusedNoteKey()) return;
    if (this.inspectingId() === candidate.value) {
      this.inspectingId.set(null);
      this.detail.set(null);
      return;
    }
    this.message.set(null);
    this.detail.set(null);
    this.inspectingId.set(candidate.value);
    this.topicsService.get(candidate.value).subscribe({
      next: (detail) => {
        if (this.inspectingId() === detail.id && this.noteKey === this.host.focusedNoteKey())
          this.detail.set(detail);
      },
      error: () => {
        if (this.inspectingId() === candidate.value) {
          this.inspectingId.set(null);
          this.message.set('Could not load this topic. Try again or choose another.');
        }
      },
    });
  }

  accept(candidate: AssistantSuggestionDto): void {
    const note = this.host.focusedNote();
    if (!note || this.state() !== 'ready' || this.noteKey !== this.host.focusedNoteKey()
      || candidate.noteId !== note.id || !candidate.value || this.assistant.sending()) return;
    // The server validates canonical IDs; also reject any link that became visible
    // locally while the user was considering this proposal.
    if (note.topicNames.some((name) => name.trim().toLocaleLowerCase() === candidate.label.trim().toLocaleLowerCase())) {
      this.candidates.update((items) => items.filter((item) => item.value !== candidate.value));
      if (!this.candidates().length) this.state.set('empty');
      return;
    }
    this.accepted = candidate;
    const turnId = this.assistant.applySuggestion(candidate, true);
    if (!turnId) {
      this.accepted = null;
      this.state.set('error');
      this.message.set('Could not start the link. You can link manually or try again.');
      return;
    }
    this.linkTurnId = turnId;
    this.state.set('linking');
  }

  canRetryLink(): boolean {
    return !!this.accepted && this.noteKey === this.host.focusedNoteKey();
  }

  retryLink(): void {
    if (!this.accepted || !this.canRetryLink()) return;
    this.state.set('ready');
    this.accept(this.accepted);
  }

  /** Every assistant turn ends here; only the two this class started matter. */
  handleTurnFinished(event: AssistantTurnOutcome): void {
    if (this.ignoredTurns.delete(event.turnId)) {
      this.assistant.dismissSuggestions(event.turnId);
      return;
    }
    if (event.turnId === this.turnId) {
      this.turnId = null;
      if (!this.noteKey || this.noteKey !== this.host.focusedNoteKey()) return;
      if (event.error) {
        this.state.set(/offline|allowance|disabled|unavailable|configured/i.test(event.error)
          ? 'unavailable' : 'error');
        this.message.set(event.error);
        return;
      }
      const note = this.host.focusedNote();
      const linkedNames = new Set((note?.topicNames ?? []).map((name) => name.trim().toLocaleLowerCase()));
      const candidates = (event.response?.suggestions ?? []).filter((item) =>
        item.kind === 'topic' && item.noteId === note?.id && item.value && item.reason?.trim()
        && !linkedNames.has(item.label.trim().toLocaleLowerCase())).slice(0, 3);
      this.candidates.set(candidates);
      this.state.set(candidates.length ? 'ready' : 'empty');
      return;
    }
    if (event.turnId === this.linkTurnId) {
      this.linkTurnId = null;
      if (this.noteKey !== this.host.focusedNoteKey()) return;
      if (event.response?.executedCapabilities?.includes('notes_link_existing_topic')) {
        if (this.accepted) this.host.linked(this.accepted);
        this.clear();
      } else {
        this.state.set('error');
        this.message.set(event.error ?? 'No link was made. You can retry or link manually.');
      }
    }
  }
}
