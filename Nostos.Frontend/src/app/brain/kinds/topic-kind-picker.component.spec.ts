import { ComponentFixture, TestBed } from '@angular/core/testing';
import { OverlayModule } from '@angular/cdk/overlay';
import { provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';

import { NotesService } from '../../core/services/notes.service';
import { TopicsService, type TopicDto, type TopicKindDto } from '../../core/services/topics.service';
import { AssistantService } from '../../ui/assistant/assistant.service';
import { BrainStateService } from '../brain-state.service';
import { TopicKindPickerComponent } from './topic-kind-picker.component';

const kind = (id: string, name: string, colorKey: string): TopicKindDto => ({
  id,
  name,
  colorKey,
  sortOrder: 1,
  createdAt: '2026-01-01T00:00:00Z',
  topicCount: 0,
});

const person = kind('k1', 'Person', 'kind-1');
const idea = kind('k2', 'Idea', 'kind-2');

/** Lets the popover's queued focus move, the panel's rAF and its microtasks run. */
const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 40));

describe('TopicKindPickerComponent', () => {
  let fixture: ComponentFixture<TopicKindPickerComponent>;
  let kinds: TopicKindDto[];
  let setTopicsKind: ReturnType<typeof vi.fn>;
  let createKind: ReturnType<typeof vi.fn>;
  let kindsFail: boolean;

  const topic: TopicDto = { id: 't1', name: 'Tolkien', usageCount: 3, kindId: null, kindSource: null };

  async function mount(): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [TopicKindPickerComponent, OverlayModule],
      providers: [
        provideRouter([]),
        {
          provide: TopicsService,
          useValue: {
            list: () => of([topic]),
            listKinds: () => (kindsFail ? throwError(() => new Error('kinds unavailable')) : of(kinds)),
            setTopicsKind,
            createKind,
          },
        },
        { provide: NotesService, useValue: { summary: () => of({ total: 0 }) } },
        { provide: AssistantService, useValue: { actionExecuted: new Subject() } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(TopicKindPickerComponent);
    fixture.componentRef.setInput('topic', topic);
    fixture.detectChanges();
  }

  beforeEach(async () => {
    kinds = [person, idea];
    kindsFail = false;
    setTopicsKind = vi.fn((ids: string[], kindId: string | null) =>
      of({ updatedTopicIds: ids, kindId, kindSource: kindId ? 'reader' : null }));
    createKind = vi.fn(() => of(kind('k3', 'Field', 'kind-3')));

    await mount();
  });

  afterEach(() => {
    fixture.destroy();
    document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
  });

  it('shows a quiet Add a kind line when the topic has none', () => {
    const trigger = fixture.nativeElement.querySelector('.kind-trigger') as HTMLElement;
    expect(trigger.textContent?.trim()).toBe('Add a kind');
    expect(trigger.classList.contains('is-add')).toBe(true);
  });

  it('shows the current kind as a pill', () => {
    fixture.componentRef.setInput('topic', { ...topic, kindId: 'k1', kindSource: 'reader' });
    fixture.detectChanges();
    const trigger = fixture.nativeElement.querySelector('.kind-trigger') as HTMLElement;
    expect(trigger.textContent?.trim()).toContain('Person');
    expect(trigger.classList.contains('is-add')).toBe(false);
  });

  it('opens with every kind, No kind, New kind… and Manage kinds', () => {
    fixture.componentInstance.toggle();
    fixture.detectChanges();

    const choices = Array.from(document.querySelectorAll<HTMLButtonElement>('.kind-choice'));
    expect(choices.map((choice) => choice.textContent?.trim())).toEqual(
      expect.arrayContaining(['No kind', 'Person', 'Idea']),
    );
    expect(document.body.textContent).toContain('New kind…');
    expect(document.body.textContent).toContain('Manage kinds');
  });

  it('puts focus on the current kind when it opens', async () => {
    fixture.componentRef.setInput('topic', { ...topic, kindId: 'k1', kindSource: 'reader' });
    fixture.detectChanges();
    fixture.componentInstance.toggle();
    fixture.detectChanges();
    await tick();

    const focused = document.activeElement as HTMLElement;
    expect(focused.classList.contains('kind-choice')).toBe(true);
    expect(focused.textContent).toContain('Person');
  });

  it('puts focus on No kind when the topic has none', async () => {
    fixture.componentInstance.toggle();
    fixture.detectChanges();
    await tick();

    expect((document.activeElement as HTMLElement).textContent).toContain('No kind');
  });

  it('gives focus back to the trigger after a choice, not to the page body', async () => {
    fixture.componentRef.setInput('topic', { ...topic, kindId: 'k1', kindSource: 'reader' });
    fixture.detectChanges();
    fixture.componentInstance.toggle();
    fixture.detectChanges();
    await tick();

    fixture.componentInstance.choose('k2');
    fixture.detectChanges();
    await tick();

    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('.kind-trigger'));
  });

  it('sets a kind and stores the reader as its source', () => {
    const replace = vi.spyOn(TestBed.inject(BrainStateService), 'replaceTopic');
    fixture.componentInstance.choose('k2');
    fixture.detectChanges();

    expect(setTopicsKind).toHaveBeenCalledWith(['t1'], 'k2');
    expect(replace).toHaveBeenCalledWith(expect.objectContaining({ id: 't1', kindId: 'k2', kindSource: 'reader' }));
  });

  it('clears the kind with No kind', () => {
    fixture.componentInstance.choose(null);
    expect(setTopicsKind).toHaveBeenCalledWith(['t1'], null);
  });

  it('adds a new kind with the next free colour, then sets it', () => {
    const reload = vi.spyOn(TestBed.inject(BrainStateService), 'reloadKinds');
    fixture.componentInstance.startCreating();
    fixture.componentInstance.draftName.set('Field');
    fixture.componentInstance.createKind();
    fixture.detectChanges();

    expect(createKind).toHaveBeenCalledWith({ name: 'Field', colorKey: 'kind-3' });
    expect(setTopicsKind).toHaveBeenCalledWith(['t1'], 'k3');
    expect(reload).toHaveBeenCalled();
  });

  it('keeps the new kind when it could not be set, and says which half failed', () => {
    setTopicsKind.mockReturnValue(throwError(() => new Error('network')));
    const reload = vi.spyOn(TestBed.inject(BrainStateService), 'reloadKinds');
    fixture.componentInstance.toggle();
    fixture.componentInstance.startCreating();
    fixture.componentInstance.draftName.set('Field');
    fixture.componentInstance.createKind();
    fixture.detectChanges();

    expect(createKind).toHaveBeenCalled();
    // The kind is real: the list is read again, and the panel stays open to
    // choose it, with a message that is not "Could not add the kind".
    expect(reload).toHaveBeenCalled();
    expect(fixture.componentInstance.error()).toBe(
      'Added “Field”, but it could not be set on this topic. Choose it above.');
    expect(fixture.componentInstance.isOpen()).toBe(true);
  });

  it('does not offer a new kind at twelve, and says why', () => {
    kinds = Array.from({ length: 12 }, (_, i) => kind(`k${i}`, `Kind ${i}`, `kind-${i + 1}`));
    TestBed.inject(BrainStateService).reloadKinds();
    fixture.componentInstance.toggle();
    fixture.detectChanges();

    expect(fixture.componentInstance.atKindLimit()).toBe(true);
    expect(document.body.textContent).toContain('All 12 kinds are in use. Delete one to add another.');
    expect(document.body.textContent).not.toContain('New kind…');
  });


  it('says the kinds could not be loaded, offers Try again, and offers no new kind', async () => {
    fixture.destroy();
    TestBed.resetTestingModule();
    kindsFail = true;
    await mount();

    fixture.componentInstance.toggle();
    fixture.detectChanges();

    const failed = document.querySelector('.kind-picker-failed') as HTMLElement;
    expect(failed).not.toBeNull();
    expect(failed.textContent).toContain('Could not load your kinds');
    // A create over an unknown list picks a colour another kind holds.
    expect(document.body.textContent).not.toContain('New kind…');
    fixture.componentInstance.startCreating();
    expect(fixture.componentInstance.creating()).toBe(false);

    kindsFail = false;
    (failed.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(document.querySelector('.kind-picker-failed')).toBeNull();
    expect(Array.from(document.querySelectorAll('.kind-choice')).length).toBe(3);
    expect(document.body.textContent).toContain('New kind…');
  });

  it('focuses the new kind field when it opens, and the button when it closes', async () => {
    fixture.componentInstance.toggle();
    fixture.detectChanges();

    fixture.componentInstance.startCreating();
    fixture.detectChanges();
    await tick();

    expect(document.activeElement).toBe(document.querySelector('#new-kind-name'));

    fixture.componentInstance.cancelCreating();
    fixture.detectChanges();
    await tick();

    expect(document.activeElement).toBe(document.querySelector('.kind-picker-action'));
  });

  it('reports a duplicate name without asking the server', () => {
    fixture.componentInstance.startCreating();
    fixture.componentInstance.draftName.set('person');
    fixture.componentInstance.createKind();

    expect(createKind).not.toHaveBeenCalled();
    expect(fixture.componentInstance.error()).toBe('A kind with this name already exists.');
  });
});
