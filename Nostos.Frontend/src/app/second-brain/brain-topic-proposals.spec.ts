import { signal } from '@angular/core';
import { Subject } from 'rxjs';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import type { TopicDetailDto, TopicsService } from '../core/services/topics.service';
import type {
  AssistantService,
  AssistantSuggestionDto,
  AssistantTurnResponse,
} from '../ui/assistant/assistant.service';
import { BrainTopicProposals } from './brain-topic-proposals';

const note = { id: 'n1', bookId: 'b1', content: 'A thought.', topicNames: [] } as unknown as NoteSearchHit;
const suggestion = (value: string, label: string, noteId = 'n1') =>
  ({ kind: 'topic', noteId, value, label, reason: 'It fits.' }) as AssistantSuggestionDto;
const answer = (...suggestions: AssistantSuggestionDto[]) =>
  ({ suggestions }) as unknown as AssistantTurnResponse;

function setup() {
  let nextTurn = 0;
  let available = true;
  const calls: string[] = [];
  const sending = signal(false);
  const activeTurnId = signal<string | null>(null);
  const assistant = {
    sending,
    activeTurnId,
    requestTopicProposals: (id: string) => (available ? (calls.push(`ask ${id}`), `turn-${++nextTurn}`) : null),
    applySuggestion: (item: AssistantSuggestionDto) => (calls.push(`link ${item.value}`), `turn-${++nextTurn}`),
    dismissSuggestions: (turnId: string) => calls.push(`dismiss ${turnId}`),
    dismissSuggestionsForNote: (id: string) => calls.push(`dismiss-note ${id}`),
    stopActiveTurn: () => calls.push('stop'),
  } as unknown as AssistantService;

  const details = new Subject<TopicDetailDto>();
  const topics = { get: () => details } as unknown as TopicsService;

  const focused = signal<NoteSearchHit | null>(note);
  const editing = signal(false);
  const linked: AssistantSuggestionDto[] = [];
  let focusRestored = 0;
  const proposals = new BrainTopicProposals(assistant, topics, {
    focusedNote: () => focused(),
    focusedNoteKey: () => {
      const current = focused();
      return current ? JSON.stringify([current.id, current.content, current.topicNames]) : null;
    },
    isEditingNote: () => editing(),
    linked: (accepted) => linked.push(accepted),
    restoreFocus: () => focusRestored++,
  });

  return {
    proposals, calls, focused, editing, linked, details,
    setAvailable: (value: boolean) => (available = value),
    focusRestored: () => focusRestored,
  };
}

describe('BrainTopicProposals', () => {
  it('asks for the focused note and shows what came back, at most three', () => {
    const { proposals, calls } = setup();

    proposals.ask();
    expect(proposals.state()).toBe('loading');
    expect(calls).toEqual(['ask n1']);

    proposals.handleTurnFinished({
      turnId: 'turn-1',
      error: null,
      response: answer(suggestion('t1', 'One'), suggestion('t2', 'Two'), suggestion('t3', 'Three'), suggestion('t4', 'Four')),
    });

    expect(proposals.state()).toBe('ready');
    expect(proposals.candidates().map((item) => item.value)).toEqual(['t1', 't2', 't3']);
  });

  it('does not ask while the note is being edited, and says so when the assistant is busy', () => {
    const { proposals, calls, editing, setAvailable } = setup();

    editing.set(true);
    proposals.ask();
    expect(calls).toEqual([]);

    editing.set(false);
    setAvailable(false);
    proposals.ask();
    expect(proposals.state()).toBe('unavailable');
  });

  it('leaves out suggestions for another note or for a topic already linked', () => {
    const { proposals, focused } = setup();
    focused.set({ ...note, topicNames: ['Linked'] } as NoteSearchHit);
    proposals.ask();

    proposals.handleTurnFinished({
      turnId: 'turn-1',
      error: null,
      response: answer(suggestion('t1', 'linked'), suggestion('t2', 'Other', 'n2')),
    });

    expect(proposals.state()).toBe('empty');
  });

  it('drops an answer that arrives after the note changed', () => {
    const { proposals, focused } = setup();
    proposals.ask();

    focused.set({ ...note, content: 'Edited meanwhile.' } as NoteSearchHit);
    proposals.handleTurnFinished({ turnId: 'turn-1', error: null, response: answer(suggestion('t1', 'One')) });

    expect(proposals.candidates()).toEqual([]);
    expect(proposals.state()).toBe('loading');
  });

  it('clears itself when the focus moves to another note', () => {
    const { proposals, calls } = setup();
    proposals.ask();

    proposals.noteFocusChanged('another-note');

    expect(proposals.state()).toBe('idle');
    expect(calls).toContain('dismiss-note n1');
  });

  it('ignores the answer to a request the user cancelled, and hands focus back', () => {
    const { proposals, calls, focusRestored } = setup();
    proposals.ask();

    proposals.cancel();
    proposals.handleTurnFinished({ turnId: 'turn-1', error: null, response: answer(suggestion('t1', 'One')) });

    expect(proposals.state()).toBe('idle');
    expect(calls).toContain('dismiss turn-1');
    expect(focusRestored()).toBe(1);
  });

  it('separates an unavailable assistant from an ordinary failure', () => {
    const { proposals } = setup();

    proposals.ask();
    proposals.handleTurnFinished({ turnId: 'turn-1', error: 'Your AI allowance is used up.', response: null });
    expect(proposals.state()).toBe('unavailable');

    proposals.ask();
    proposals.handleTurnFinished({ turnId: 'turn-2', error: 'Something broke.', response: null });
    expect(proposals.state()).toBe('error');
  });

  it('links an accepted suggestion and tells the host once the assistant confirms it', () => {
    const { proposals, calls, linked } = setup();
    const candidate = suggestion('t1', 'One');
    proposals.ask();
    proposals.handleTurnFinished({ turnId: 'turn-1', error: null, response: answer(candidate) });

    proposals.accept(candidate);
    expect(proposals.state()).toBe('linking');
    expect(calls).toContain('link t1');

    proposals.handleTurnFinished({
      turnId: 'turn-2',
      error: null,
      response: { executedCapabilities: ['notes_link_existing_topic'] } as unknown as AssistantTurnResponse,
    });

    expect(linked).toEqual([candidate]);
    expect(proposals.state()).toBe('idle');
  });

  it('keeps the accepted suggestion for a retry when no link was made', () => {
    const { proposals, calls, linked } = setup();
    const candidate = suggestion('t1', 'One');
    proposals.ask();
    proposals.handleTurnFinished({ turnId: 'turn-1', error: null, response: answer(candidate) });
    proposals.accept(candidate);

    proposals.handleTurnFinished({ turnId: 'turn-2', error: null, response: answer() });
    expect(proposals.state()).toBe('error');
    expect(proposals.canRetryLink()).toBe(true);
    expect(linked).toEqual([]);

    proposals.retryLink();
    expect(proposals.state()).toBe('linking');
    expect(calls.filter((call) => call === 'link t1')).toHaveLength(2);
  });

  it('shows a suggestion’s existing evidence, and hides it on a second ask', () => {
    const { proposals, details } = setup();
    const candidate = suggestion('t1', 'One');
    proposals.ask();
    proposals.handleTurnFinished({ turnId: 'turn-1', error: null, response: answer(candidate) });

    proposals.inspect(candidate);
    expect(proposals.inspectingId()).toBe('t1');
    details.next({ id: 't1', name: 'One', notes: [] });
    expect(proposals.detail()?.name).toBe('One');

    proposals.inspect(candidate);
    expect(proposals.inspectingId()).toBeNull();
    expect(proposals.detail()).toBeNull();
  });
});
