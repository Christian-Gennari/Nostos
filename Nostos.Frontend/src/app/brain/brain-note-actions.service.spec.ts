import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import type { Note, NoteSearchHit } from '../core/dtos/note.dtos';
import { NotesService } from '../core/services/notes.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService } from '../core/services/topics.service';
import { BrainNoteActionsService } from './brain-note-actions.service';
import { BrainStateService } from './brain-state.service';

describe('BrainNoteActionsService edit', () => {
  const saved = { id: 'n1', content: 'New words' } as Note;
  const hit = { id: 'n1', content: 'New words' } as NoteSearchHit;
  let service: BrainNoteActionsService;
  let update: ReturnType<typeof vi.fn>;
  let toast: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
  const host = { replace: vi.fn(), remove: vi.fn(), edited: vi.fn() };

  beforeEach(() => {
    host.replace.mockReset();
    host.edited.mockReset();
    update = vi.fn().mockReturnValue(of(saved));
    toast = { success: vi.fn(), error: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        BrainNoteActionsService,
        { provide: NotesService, useValue: { update, get: vi.fn().mockReturnValue(of(hit)) } },
        { provide: TopicsService, useValue: {} },
        { provide: ToastService, useValue: toast },
        { provide: BrainStateService, useValue: { refresh: vi.fn() } },
      ],
    });
    service = TestBed.inject(BrainNoteActionsService);
    service.attach(host);
  });

  it('tells the host what was saved, then shows the note as the server has it', () => {
    service.edit({ id: 'n1', content: 'New words' });
    expect(host.edited).toHaveBeenCalledWith(saved, { id: 'n1', content: 'New words' });
    expect(host.replace).toHaveBeenCalledWith(hit);
    expect(toast.success).toHaveBeenCalledWith('Note saved');
    expect(service.busy().has('n1')).toBe(false);
  });

  it('stays quiet on success when asked, as a pen change does', () => {
    service.edit({ id: 'n1', content: 'New words' }, { quiet: true });
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('toasts a failed save, replaces nothing and lets the note settle', () => {
    update.mockReturnValue(throwError(() => new Error('offline')));
    service.edit({ id: 'n1', content: 'New words' });
    expect(toast.error).toHaveBeenCalledWith('Could not save the note. Try again.');
    expect(host.replace).not.toHaveBeenCalled();
    expect(host.edited).not.toHaveBeenCalled();
    expect(service.busy().has('n1')).toBe(false);
  });
});

describe('BrainNoteActionsService delete', () => {
  let service: BrainNoteActionsService;
  let del: ReturnType<typeof vi.fn>;
  let toast: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
  const host = { replace: vi.fn(), remove: vi.fn() };

  beforeEach(() => {
    host.remove.mockReset();
    del = vi.fn().mockReturnValue(of(undefined));
    toast = { success: vi.fn(), error: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        BrainNoteActionsService,
        { provide: NotesService, useValue: { delete: del } },
        { provide: TopicsService, useValue: {} },
        { provide: ToastService, useValue: toast },
        { provide: BrainStateService, useValue: { refresh: vi.fn() } },
      ],
    });
    service = TestBed.inject(BrainNoteActionsService);
    service.attach(host);
  });

  it('words a note delete as a note', () => {
    service.askDelete(['n1']);
    expect(service.deleteHeading()).toBe('Delete this note?');
    expect(service.deleteConfirmLabel()).toBe('Delete note');
    service.confirmDelete();
    expect(host.remove).toHaveBeenCalledWith('n1');
    expect(toast.success).toHaveBeenCalledWith('Note deleted');
    expect(service.deleteIds()).toEqual([]);
  });

  it('words a highlight delete as a highlight, through the same path', () => {
    service.askDelete(['h1'], 'highlight');
    expect(service.deleteHeading()).toBe('Delete this highlight?');
    expect(service.deleteConfirmLabel()).toBe('Delete highlight');
    expect(service.deleteDescription()).toContain('highlight');
    service.confirmDelete();
    expect(del).toHaveBeenCalledWith('h1');
    expect(host.remove).toHaveBeenCalledWith('h1');
    expect(toast.success).toHaveBeenCalledWith('Highlight deleted');
  });

  it('stays open, unbusy, with a Could not toast when nothing was deleted', () => {
    del.mockReturnValue(throwError(() => new Error('offline')));
    service.askDelete(['h1'], 'highlight');
    service.confirmDelete();
    expect(service.deleting()).toBe(false);
    expect(service.deleteIds()).toEqual(['h1']);
    expect(host.remove).not.toHaveBeenCalled();
    expect(toast.error).toHaveBeenCalledWith('Could not delete the highlight');
  });
});
