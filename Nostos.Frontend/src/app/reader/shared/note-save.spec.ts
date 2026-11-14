import { Observable, Subject, of, throwError } from 'rxjs';

import { Note } from '../../core/dtos/note.dtos';
import { commitNote, saveNote } from './note-save';

const note = (id: string) => ({ id, bookId: 'book-1', content: '' }) as Note;

/**
 * The optimistic save both readers commit a highlight with (issue #1610):
 * draw, send, settle or roll back, and an answer for a closed book changes
 * nothing.
 */
describe('saveNote', () => {
  it('reports the note the server returned', () => {
    const saved = note('n-1');
    const onSaved = vi.fn();
    const onFailed = vi.fn();

    saveNote({ request: of(saved), onSaved, onFailed });

    expect(onSaved).toHaveBeenCalledWith(saved);
    expect(onFailed).not.toHaveBeenCalled();
  });

  it('reports a refusal without losing the error', () => {
    const error = new Error('offline');
    const onSaved = vi.fn();
    const onFailed = vi.fn();

    saveNote({ request: throwError(() => error), onSaved, onFailed });

    expect(onFailed).toHaveBeenCalledWith(error);
    expect(onSaved).not.toHaveBeenCalled();
  });

  it('drops an answer for a book that is no longer open', () => {
    const onSaved = vi.fn();
    const onFailed = vi.fn();
    const onStale = vi.fn();

    saveNote({
      request: of(note('n-1')),
      isCurrent: () => false,
      onSaved,
      onFailed,
      onStale,
    });

    expect(onStale).toHaveBeenCalledTimes(1);
    expect(onSaved).not.toHaveBeenCalled();
    expect(onFailed).not.toHaveBeenCalled();
  });
});

describe('commitNote', () => {
  it('does nothing but tell the shell when there is nothing on offer', async () => {
    const request = vi.fn(() => of(note('n-1')));
    const onNothing = vi.fn();
    const settle = vi.fn();

    const outcome = await commitNote({
      pending: () => null,
      request,
      settle,
      onFailed: vi.fn(),
      onNothing,
    });

    expect(outcome).toBe('nothing');
    expect(onNothing).toHaveBeenCalledTimes(1);
    expect(request).not.toHaveBeenCalled();
    expect(settle).not.toHaveBeenCalled();
  });

  it('draws the mark before it sends, then settles it with the saved note', async () => {
    const order: string[] = [];
    const saved = note('n-1');
    const settle = vi.fn();

    const outcome = await commitNote({
      pending: () => 'passage',
      request: () => {
        order.push('request');
        return of(saved);
      },
      draw: () => {
        order.push('draw');
      },
      settle: (_pending, created) => {
        order.push('settle');
        settle(created);
      },
      onFailed: vi.fn(),
    });

    expect(outcome).toBe('saved');
    expect(order).toEqual(['draw', 'request', 'settle']);
    expect(settle).toHaveBeenCalledWith(saved);
  });

  it('takes the drawn mark back off the page when the save fails', async () => {
    const order: string[] = [];

    const outcome = await commitNote({
      pending: () => 'passage',
      request: () => throwError(() => new Error('offline')),
      draw: () => () => order.push('rollback'),
      settle: () => order.push('settle'),
      onFailed: () => order.push('failed'),
    });

    expect(outcome).toBe('failed');
    // The mark goes back before the shell is told, so the retry starts clean.
    expect(order).toEqual(['rollback', 'failed']);
  });

  it('keeps a mark whose format draws nothing on failure', async () => {
    const onFailed = vi.fn();

    const outcome = await commitNote({
      pending: () => 'passage',
      request: () => throwError(() => new Error('offline')),
      settle: vi.fn(),
      onFailed,
    });

    expect(outcome).toBe('failed');
    expect(onFailed).toHaveBeenCalledWith('passage');
  });

  it('neither settles nor rolls back an answer for a closed book', async () => {
    const rollback = vi.fn();
    const settle = vi.fn();
    const onFailed = vi.fn();

    const outcome = await commitNote({
      pending: () => 'passage',
      request: () => of(note('n-1')),
      isCurrent: () => false,
      draw: () => rollback,
      settle,
      onFailed,
    });

    expect(outcome).toBe('stale');
    expect(settle).not.toHaveBeenCalled();
    expect(rollback).not.toHaveBeenCalled();
    expect(onFailed).not.toHaveBeenCalled();
  });

  it('waits for the server before settling a save that has not answered yet', async () => {
    const answer = new Subject<Note>();
    const settle = vi.fn();

    const committed = commitNote({
      pending: () => 'passage',
      request: (): Observable<Note> => answer,
      settle,
      onFailed: vi.fn(),
    });

    expect(settle).not.toHaveBeenCalled();
    answer.next(note('n-1'));
    answer.complete();

    await expect(committed).resolves.toBe('saved');
    expect(settle).toHaveBeenCalledTimes(1);
  });
});
