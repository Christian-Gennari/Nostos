import { HttpErrorResponse } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';

import { Note } from '../../core/dtos/note.dtos';
import { readUnlocatedNoteIds } from '../passage/note-locating';
import { NoteLocationCoordinator, newNotesSince } from './note-location-coordinator';

const note = (id: string, overrides: Partial<Note> = {}) =>
  ({ id, bookId: 'book-1', content: '', ...overrides }) as Note;

const located = (id: string) => note(id, { cfiRange: 'epubcfi(/6/4!/4/2/1:0)' });

describe('newNotesSince', () => {
  it('hands over each note once, however often the list is re-read', () => {
    const seen = new Set<string>();

    expect(newNotesSince(seen, [note('a'), note('b')]).map((n) => n.id)).toEqual(['a', 'b']);
    expect(newNotesSince(seen, [note('a'), note('b')])).toEqual([]);

    // A note removed and put back is still the same note.
    expect(newNotesSince(seen, [note('b')])).toEqual([]);
    // A genuinely new one arrives on its own.
    expect(newNotesSince(seen, [note('a'), note('b'), note('c')]).map((n) => n.id)).toEqual(['c']);
  });
});

/**
 * The note-location bookkeeping both readers share (issue #1610): which notes
 * are worth looking for, saving a found location once, and remembering the ones
 * never found or refused.
 */
describe('NoteLocationCoordinator', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => localStorage.clear());

  function coordinator(
    overrides: {
      setLocation?: (noteId: string, location: string) => Observable<Note>;
      notes?: () => Note[] | null;
      isCurrent?: () => boolean;
      onLocated?: (note: Note) => void;
    } = {},
  ) {
    const onLocated = overrides.onLocated ?? vi.fn();
    const locations = new NoteLocationCoordinator({
      isCurrent: overrides.isCurrent ?? (() => true),
      notes: overrides.notes ?? (() => [note('q-1')]),
      setLocation: overrides.setLocation ?? (() => of(located('q-1'))),
      onLocated,
      what: 'a quote on its page',
    });
    return { locations, onLocated };
  }

  it('offers only the notes never found missing and eligible by the format', () => {
    const { locations } = coordinator();

    expect(
      locations.candidates([note('a'), note('b')], (candidate) => candidate.id === 'b'),
    ).toEqual([note('b')]);

    locations.rememberMissing('a');
    expect(readUnlocatedNoteIds().has('a')).toBe(true);
    expect(
      locations.candidates([note('a'), note('b')], () => true).map((candidate) => candidate.id),
    ).toEqual(['b']);
  });

  it('saves one location and draws the note the server returned', async () => {
    const saved = located('q-1');
    const setLocation = vi.fn(() => of(saved));
    const { locations, onLocated } = coordinator({ setLocation, notes: () => [note('q-1')] });

    await expect(locations.save(note('q-1'), '{"pageNumber":4,"rects":[]}')).resolves.toBe('saved');

    expect(setLocation).toHaveBeenCalledWith('q-1', '{"pageNumber":4,"rects":[]}');
    expect(onLocated).toHaveBeenCalledWith(saved);
  });

  it('draws nothing for a note deleted while its location was being saved', async () => {
    const { locations, onLocated } = coordinator({
      setLocation: () => of(located('q-1')),
      notes: () => [],
    });

    await expect(locations.save(note('q-1'), 'loc')).resolves.toBe('deleted');
    expect(onLocated).not.toHaveBeenCalled();
  });

  it('draws nothing when the server answered without a location', async () => {
    const { locations, onLocated } = coordinator({ setLocation: () => of(note('q-1')) });

    await expect(locations.save(note('q-1'), 'loc')).resolves.toBe('deleted');
    expect(onLocated).not.toHaveBeenCalled();
  });

  it('remembers a note the server refused for good, and does not draw it', async () => {
    const { locations, onLocated } = coordinator({
      setLocation: () => throwError(() => new HttpErrorResponse({ status: 400 })),
    });

    await expect(locations.save(note('q-1'), 'loc')).resolves.toBe('refused');

    expect(readUnlocatedNoteIds().has('q-1')).toBe(true);
    expect(onLocated).not.toHaveBeenCalled();
  });

  it('logs an ordinary failure, which the next opening may retry', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    try {
      const { locations, onLocated } = coordinator({
        setLocation: () => throwError(() => new Error('offline')),
      });

      await expect(locations.save(note('q-1'), 'loc')).resolves.toBe('failed');

      expect(readUnlocatedNoteIds().has('q-1')).toBe(false);
      expect(warn).toHaveBeenCalledWith(
        'Could not save the location of a quote on its page:',
        expect.any(Error),
      );
      expect(onLocated).not.toHaveBeenCalled();
    } finally {
      warn.mockRestore();
    }
  });

  it('applies nothing once the reader has moved to another book', async () => {
    const onLocated = vi.fn();
    const { locations } = coordinator({
      isCurrent: () => false,
      setLocation: () => of(located('q-1')),
      onLocated,
    });

    await expect(locations.save(note('q-1'), 'loc')).resolves.toBe('stale');
    expect(onLocated).not.toHaveBeenCalled();
  });
});
