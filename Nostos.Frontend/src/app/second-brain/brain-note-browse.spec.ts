import { signal } from '@angular/core';
import { Subject } from 'rxjs';

import type { NoteSearchHit, NoteSearchPage } from '../core/dtos/note.dtos';
import type { NotesService } from '../core/services/notes.service';
import type { ToastService } from '../core/services/toast.service';
import type { TopicDto } from '../core/services/topics.service';
import { BrainNoteBrowse } from './brain-note-browse';
import { NOTE_SEARCH_DEBOUNCE_MS, type IndexSort } from './second-brain.helpers';

const hit = (id: string, content = `Note ${id}`, topicNames: string[] = []) =>
  ({ id, bookId: 'b1', content, selectedText: null, topicNames }) as unknown as NoteSearchHit;
const page = (items: NoteSearchHit[], totalCount = items.length): NoteSearchPage => ({
  items,
  totalCount,
  offset: 0,
  limit: 25,
});

function setup() {
  const requests: { args: Record<string, unknown>; page: Subject<NoteSearchPage> }[] = [];
  const updates: { id: string; content: string; done: Subject<void> }[] = [];
  const notes = {
    browse: (args: Record<string, unknown>) => {
      const subject = new Subject<NoteSearchPage>();
      requests.push({ args, page: subject });
      return subject;
    },
    update: (id: string, body: { content: string }) => {
      const done = new Subject<void>();
      updates.push({ id, content: body.content, done });
      return done;
    },
  } as unknown as NotesService;
  const toasts: string[] = [];
  const toast = {
    success: (m: string) => toasts.push(`ok ${m}`),
    error: (m: string) => toasts.push(`error ${m}`),
  } as unknown as ToastService;

  const panelNote = signal<NoteSearchHit | null>(null);
  const browsing = signal(true);
  let saved = 0;
  const browse = new BrainNoteBrowse(notes, toast, {
    topics: signal<TopicDto[]>([{ id: 't1', name: 'Stoicism', usageCount: 2 }]),
    indexSort: signal<IndexSort>('usage'),
    panelNote,
    isBrowsingNotes: () => browsing(),
    noteSaved: () => saved++,
  });
  const load = (items: NoteSearchHit[], total = items.length) => {
    browse.loadPage();
    requests.at(-1)!.page.next(page(items, total));
  };
  return { browse, requests, updates, toasts, panelNote, load, saved: () => saved };
}

describe('BrainNoteBrowse', () => {
  afterEach(() => vi.useRealTimers());

  it('loads a page with the current filters and appends the next without repeats', () => {
    const { browse, requests, load } = setup();

    load([hit('a'), hit('b')], 3);
    expect(requests[0].args).toMatchObject({ query: '', withoutTopics: false, oldestFirst: false, offset: 0 });

    browse.loadPage();
    expect(requests[1].args).toMatchObject({ offset: 2 });
    requests[1].page.next(page([hit('b'), hit('c')], 3));

    expect(browse.notes().map((note) => note.id)).toEqual(['a', 'b', 'c']);
    expect(browse.total()).toBe(3);
    expect(browse.loaded()).toBe(true);
  });

  it('starts over, closing the open note, when a filter changes', () => {
    const { browse, requests, load, panelNote } = setup();
    load([hit('a')]);
    panelNote.set(hit('a'));

    browse.setWithoutTopics(true);

    expect(browse.notes()).toEqual([]);
    expect(panelNote()).toBeNull();
    expect(requests.at(-1)!.args).toMatchObject({ withoutTopics: true, offset: 0 });
  });

  it('waits for typing to pause before searching, and disowns the answer to an older query', () => {
    vi.useFakeTimers();
    const { browse, requests } = setup();
    browse.loadPage();

    browse.setQuery('vir');
    browse.setQuery('virtue');
    expect(requests).toHaveLength(1);
    requests[0].page.next(page([hit('stale')]));
    expect(browse.notes()).toEqual([]);

    vi.advanceTimersByTime(NOTE_SEARCH_DEBOUNCE_MS);
    expect(requests).toHaveLength(2);
    expect(requests[1].args).toMatchObject({ query: 'virtue' });
  });

  it('will not change a filter over an unsaved edit, and says why', () => {
    const { browse, load, panelNote, toasts, requests } = setup();
    load([hit('a', 'Old.')]);
    panelNote.set(browse.notes()[0]);
    browse.startEdit();
    browse.editContent.set('Half written');

    browse.setOldestFirst(true);

    expect(browse.oldestFirst()).toBe(false);
    expect(requests).toHaveLength(1);
    expect(toasts).toEqual(['error Save or cancel the note edit before leaving it']);
  });

  it('saves an edit into the list and the open note', () => {
    const { browse, load, panelNote, updates, saved, toasts } = setup();
    load([hit('a', 'Old.'), hit('b')]);
    panelNote.set(browse.notes()[0]);
    browse.startEdit();
    browse.editContent.set('New text about [[Stoicism]].');

    browse.saveEdit();
    expect(browse.saving()).toBe(true);
    updates[0].done.next();

    expect(browse.notes()[0]).toMatchObject({ content: 'New text about [[Stoicism]].', topicNames: ['Stoicism'] });
    expect(panelNote()?.content).toBe('New text about [[Stoicism]].');
    expect(browse.editing()).toBe(false);
    expect(saved()).toBe(1);
    expect(toasts).toEqual(['ok Note saved']);
  });

  it('links the open note to a picked topic, and drops it from a "without topics" list', () => {
    const { browse, load, panelNote, updates } = setup();
    browse.withoutTopics.set(true);
    load([hit('a', 'A thought.'), hit('b')], 2);
    panelNote.set(browse.notes()[0]);
    browse.openPicker();
    browse.pickerId.set('t1');

    browse.linkTopic();
    expect(updates[0].content).toBe('A thought.\n\n[[Stoicism]]');
    updates[0].done.next();

    expect(browse.notes().map((note) => note.id)).toEqual(['b']);
    expect(browse.total()).toBe(1);
    expect(browse.pickerOpen()).toBe(false);
  });

  it('offers a new topic only for a name that is free, and creates it by linking', () => {
    const { browse, load, panelNote, updates } = setup();
    load([hit('a', 'A thought.')]);
    panelNote.set(browse.notes()[0]);
    browse.openPicker();

    browse.pickerQuery.set('stoicism');
    expect(browse.newTopicName()).toBeNull();

    browse.pickerQuery.set('Virtue');
    browse.createTopic();

    expect(updates[0].content).toBe('A thought.\n\n[[Virtue]]');
  });

  it('keeps the edit open and reports a failed save', () => {
    const { browse, load, panelNote, updates, toasts } = setup();
    load([hit('a', 'Old.')]);
    panelNote.set(browse.notes()[0]);
    browse.startEdit();
    browse.editContent.set('New.');
    browse.saveEdit();

    updates[0].done.error(new Error('offline'));

    expect(browse.saving()).toBe(false);
    expect(browse.editing()).toBe(true);
    expect(toasts).toEqual(['error Could not save the note']);
  });
});
