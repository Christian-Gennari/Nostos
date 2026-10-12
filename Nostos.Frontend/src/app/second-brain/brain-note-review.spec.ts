import { signal } from '@angular/core';
import { Subject } from 'rxjs';

import type { NoteSearchHit, NoteSearchPage } from '../core/dtos/note.dtos';
import type { NotesService } from '../core/services/notes.service';
import type { ToastService } from '../core/services/toast.service';
import type { TopicDto } from '../core/services/topics.service';
import { BrainNoteReview } from './brain-note-review';
import type { IndexSort } from './second-brain.helpers';

const hit = (id: string, content = `Note ${id}`) =>
  ({ id, bookId: 'b1', content, selectedText: null, topicNames: [] }) as unknown as NoteSearchHit;
const page = (items: NoteSearchHit[], totalCount = items.length): NoteSearchPage => ({
  items,
  totalCount,
  offset: 0,
  limit: 25,
});

function setup() {
  const requests: { kind: string; args: unknown; page: Subject<NoteSearchPage> }[] = [];
  const updates: { id: string; content: string; done: Subject<void> }[] = [];
  const notes = {
    unlinkedPage: (limit: number, offset: number) => {
      const subject = new Subject<NoteSearchPage>();
      requests.push({ kind: 'unlinked', args: { limit, offset }, page: subject });
      return subject;
    },
    browse: (args: unknown) => {
      const subject = new Subject<NoteSearchPage>();
      requests.push({ kind: 'browse', args, page: subject });
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
    info: (m: string) => toasts.push(`info ${m}`),
  } as unknown as ToastService;

  const reviewing = signal(true);
  let saved = 0;
  let focusChanges = 0;
  const review = new BrainNoteReview(notes, toast, {
    topics: signal<TopicDto[]>([{ id: 't1', name: 'Stoicism', usageCount: 2 }]),
    indexSort: signal<IndexSort>('usage'),
    isReviewing: () => reviewing(),
    noteSaved: () => saved++,
    focusChanged: () => focusChanges++,
  });
  const load = (items: NoteSearchHit[], total = items.length) => {
    review.loadPage();
    requests.at(-1)!.page.next(page(items, total));
  };
  return { review, requests, updates, toasts, load, saved: () => saved, focusChanges: () => focusChanges };
}

describe('BrainNoteReview', () => {
  it('loads the unlinked notes and focuses the first one', () => {
    const { review, requests, load } = setup();
    review.begin({ query: '', bookId: null, oldestFirst: false });

    load([hit('a'), hit('b')], 5);

    expect(requests[0].kind).toBe('unlinked');
    expect(review.note()?.id).toBe('a');
    expect(review.position()).toBe(1);
    expect(review.hasMore()).toBe(true);
  });

  it('walks a filtered review through the notes search instead', () => {
    const { review, requests } = setup();
    review.begin({ query: 'virtue', bookId: 'b9', oldestFirst: true });

    review.loadPage();

    expect(requests[0].kind).toBe('browse');
    expect(requests[0].args).toMatchObject({ query: 'virtue', bookId: 'b9', withoutTopics: true, oldestFirst: true });
  });

  it('skips forward and back without changing a note, and fetches on at the end of a page', () => {
    const { review, requests, load, updates } = setup();
    review.begin({ query: '', bookId: null, oldestFirst: false });
    load([hit('a'), hit('b')], 3);

    review.skip();
    expect(review.note()?.id).toBe('b');
    review.previous();
    expect(review.note()?.id).toBe('a');

    review.skip();
    review.skip();
    expect(requests).toHaveLength(2);
    expect(requests[1].args).toEqual({ limit: 25, offset: 2 });
    requests[1].page.next(page([hit('c')], 3));

    expect(review.note()?.id).toBe('c');
    expect(updates).toEqual([]);
  });

  it('disowns a page that answers after a new review began', () => {
    const { review, requests } = setup();
    review.begin({ query: '', bookId: null, oldestFirst: false });
    review.loadPage();

    review.begin({ query: '', bookId: null, oldestFirst: false });
    requests[0].page.next(page([hit('stale')]));

    expect(review.queue()).toEqual([]);
  });

  it('links the focused note with an explicit reference and takes it off the queue', () => {
    const { review, load, updates, toasts, saved } = setup();
    review.begin({ query: '', bookId: null, oldestFirst: false });
    load([hit('a', 'A thought.'), hit('b')]);
    review.openPicker();
    review.chooseTopic('t1');

    review.confirmLink();
    expect(updates[0]).toMatchObject({ id: 'a', content: 'A thought.\n\n[[Stoicism]]' });
    updates[0].done.next();

    expect(review.queue().map((row) => row.id)).toEqual(['b']);
    expect(review.total()).toBe(1);
    expect(review.note()?.id).toBe('b');
    expect(review.mutated).toBe(true);
    expect(review.pickerOpen()).toBe(false);
    expect(saved()).toBe(1);
    expect(toasts).toEqual(['ok Linked to “Stoicism”']);
  });

  it('keeps an edited note queued unless the edit declares a topic', () => {
    const { review, load, updates } = setup();
    review.begin({ query: '', bookId: null, oldestFirst: false });
    load([hit('a', 'Old.'), hit('b')]);

    review.startEdit();
    review.editContent.set('New, still untagged.');
    review.saveEdit();
    updates[0].done.next();
    expect(review.queue().map((row) => row.content)).toEqual(['New, still untagged.', 'Note b']);

    review.startEdit();
    review.editContent.set('Now about [[Stoicism]].');
    review.saveEdit();
    updates[1].done.next();
    expect(review.queue().map((row) => row.id)).toEqual(['b']);
  });

  it('will not leave a note with an unsaved edit, and says why', () => {
    const { review, load, toasts } = setup();
    review.begin({ query: '', bookId: null, oldestFirst: false });
    load([hit('a', 'Old.'), hit('b')]);
    review.startEdit();
    review.editContent.set('Half written');

    review.skip();

    expect(review.note()?.id).toBe('a');
    expect(review.hasUnsavedEdit()).toBe(true);
    expect(toasts[0]).toBe('error Save or cancel the note edit before leaving it');
  });

  it('brings a resumed review back to its note, or says the note is gone', () => {
    const { review, load, toasts, focusChanges } = setup();
    review.resume({ query: '', bookId: null, oldestFirst: false }, 'b');
    load([hit('a'), hit('b')]);
    expect(review.note()?.id).toBe('b');
    expect(review.restoringFocusId()).toBeNull();

    const other = setup();
    other.review.resume({ query: '', bookId: null, oldestFirst: false }, 'missing');
    other.load([hit('a')]);
    expect(other.review.note()?.id).toBe('a');
    expect(other.toasts).toEqual(['info The previously selected note is no longer in this review queue']);
    expect(toasts).toEqual([]);
    expect(focusChanges()).toBe(0);
  });

  it('forgets the queue when abandoned', () => {
    const { review, load, requests } = setup();
    review.begin({ query: '', bookId: null, oldestFirst: false });
    load([hit('a')], 3);
    review.loadPage();

    review.abandon();
    requests.at(-1)!.page.next(page([hit('late')], 3));

    expect(review.queue()).toEqual([]);
    expect(review.total()).toBe(0);
  });
});
