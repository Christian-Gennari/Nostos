import { TestBed } from '@angular/core/testing';

import type { Note } from '../core/dtos/note.dtos';
import { NotesService } from '../core/services/notes.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService } from '../core/services/topics.service';
import { BrainNoteActionsService } from './brain-note-actions.service';
import { BrainNoteDialogsComponent } from './brain-note-dialogs.component';
import { BrainStateService } from './brain-state.service';

describe('BrainNoteDialogsComponent edit conflict (issue #1404)', () => {
  const theirs = { id: 'n1', content: 'From the other tab', updatedAt: '2026-09-09T00:00:00Z' } as Note;

  function setup() {
    TestBed.configureTestingModule({
      imports: [BrainNoteDialogsComponent],
      providers: [
        BrainNoteActionsService,
        { provide: NotesService, useValue: {} },
        { provide: TopicsService, useValue: {} },
        { provide: ToastService, useValue: {} },
        { provide: BrainStateService, useValue: { refresh: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(BrainNoteDialogsComponent);
    const actions = TestBed.inject(BrainNoteActionsService);
    fixture.detectChanges();
    return { fixture, actions, host: fixture.nativeElement as HTMLElement };
  }

  const buttons = (host: HTMLElement) => Array.from(host.querySelectorAll('button')).map((b) => b.textContent?.trim());

  it('says nothing until a save finds the note changed', () => {
    const { host } = setup();
    expect(host.textContent).not.toContain('changed somewhere else');
  });

  it('offers the same two choices as a writing', () => {
    const { fixture, actions, host } = setup();
    actions.editConflict.set({ edit: { id: 'n1', content: 'mine' }, options: {}, theirs });
    fixture.detectChanges();
    expect(host.textContent).toContain('This note changed somewhere else');
    expect(buttons(host)).toEqual(expect.arrayContaining(['Load the newer version', 'Keep mine (overwrite)']));
  });

  it('routes each button to its action', () => {
    const { fixture, actions, host } = setup();
    const keep = vi.spyOn(actions, 'keepMineAfterConflict').mockImplementation(() => undefined);
    const load = vi.spyOn(actions, 'loadNewerAfterConflict').mockImplementation(() => undefined);
    actions.editConflict.set({ edit: { id: 'n1', content: 'mine' }, options: {}, theirs });
    fixture.detectChanges();
    const click = (label: string) =>
      Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === label)!.click();
    click('Keep mine (overwrite)');
    click('Load the newer version');
    expect(keep).toHaveBeenCalledOnce();
    expect(load).toHaveBeenCalledOnce();
  });
});
