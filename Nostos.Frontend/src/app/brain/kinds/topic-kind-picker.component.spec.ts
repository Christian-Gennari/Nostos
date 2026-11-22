import { ComponentFixture, TestBed } from '@angular/core/testing';
import { OverlayModule } from '@angular/cdk/overlay';
import { provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';

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

describe('TopicKindPickerComponent', () => {
  let fixture: ComponentFixture<TopicKindPickerComponent>;
  let kinds: TopicKindDto[];
  let setTopicsKind: ReturnType<typeof vi.fn>;
  let createKind: ReturnType<typeof vi.fn>;

  const topic: TopicDto = { id: 't1', name: 'Tolkien', usageCount: 3, kindId: null, kindSource: null };

  beforeEach(async () => {
    kinds = [person, idea];
    setTopicsKind = vi.fn((ids: string[], kindId: string | null) =>
      of({ updatedTopicIds: ids, kindId, kindSource: kindId ? 'reader' : null }));
    createKind = vi.fn(() => of(kind('k3', 'Field', 'kind-3')));

    await TestBed.configureTestingModule({
      imports: [TopicKindPickerComponent, OverlayModule],
      providers: [
        provideRouter([]),
        {
          provide: TopicsService,
          useValue: {
            list: () => of([topic]),
            listKinds: () => of(kinds),
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
  });

  afterEach(() => {
    fixture.destroy();
    document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
  });

  it('shows a quiet Add a kind line when the topic has none', () => {
    expect(fixture.nativeElement.querySelector('.kind-add')?.textContent?.trim()).toBe('Add a kind');
    expect(fixture.nativeElement.querySelector('.kind-trigger')).toBeNull();
  });

  it('shows the current kind as a pill', () => {
    fixture.componentRef.setInput('topic', { ...topic, kindId: 'k1', kindSource: 'reader' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.kind-trigger')?.textContent?.trim()).toContain('Person');
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

  it('does not offer a new kind at twelve, and says why', () => {
    kinds = Array.from({ length: 12 }, (_, i) => kind(`k${i}`, `Kind ${i}`, `kind-${i + 1}`));
    TestBed.inject(BrainStateService).reloadKinds();
    fixture.componentInstance.toggle();
    fixture.detectChanges();

    expect(fixture.componentInstance.atKindLimit()).toBe(true);
    expect(document.body.textContent).toContain('All 12 kinds are in use. Delete one to add another.');
    expect(document.body.textContent).not.toContain('New kind…');
  });

  it('reports a duplicate name without asking the server', () => {
    fixture.componentInstance.startCreating();
    fixture.componentInstance.draftName.set('person');
    fixture.componentInstance.createKind();

    expect(createKind).not.toHaveBeenCalled();
    expect(fixture.componentInstance.error()).toBe('A kind with this name already exists.');
  });
});
