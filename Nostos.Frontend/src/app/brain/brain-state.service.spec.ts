import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';

import { AssistantService } from '../ui/assistant/assistant.service';
import { NotesService } from '../core/services/notes.service';
import { TopicsService } from '../core/services/topics.service';
import { BrainStateService } from './brain-state.service';

describe('BrainStateService loading', () => {
  const topicsList = vi.fn();
  const summary = vi.fn();

  function create(): BrainStateService {
    TestBed.configureTestingModule({
      providers: [
        { provide: TopicsService, useValue: { list: topicsList } },
        { provide: NotesService, useValue: { summary } },
        { provide: AssistantService, useValue: { actionExecuted: new Subject() } },
      ],
    });
    return TestBed.inject(BrainStateService);
  }

  beforeEach(() => {
    topicsList.mockReset().mockReturnValue(of([{ id: 't1', name: 'Stoicism', usageCount: 1 }]));
    summary.mockReset().mockReturnValue(of({ total: 3 }));
  });

  it('ensureLoaded reads the topics and the counts once', () => {
    const brain = create();
    brain.ensureLoaded();
    brain.ensureLoaded();

    expect(topicsList).toHaveBeenCalledTimes(1);
    expect(summary).toHaveBeenCalledTimes(1);
    expect(brain.topicsLoaded()).toBe(true);
  });

  it('ensureTopicsLoaded reads the topics and not the counts, once', () => {
    const brain = create();
    brain.ensureTopicsLoaded();
    brain.ensureTopicsLoaded();

    expect(topicsList).toHaveBeenCalledTimes(1);
    expect(summary).not.toHaveBeenCalled();
    expect(brain.topics().map((t) => t.name)).toEqual(['Stoicism']);
  });

  it('gives a later ensureLoaded the counts it would have had, without reading the topics again', () => {
    const brain = create();
    brain.ensureTopicsLoaded();
    brain.ensureLoaded();
    brain.ensureLoaded();

    expect(topicsList).toHaveBeenCalledTimes(1);
    expect(summary).toHaveBeenCalledTimes(1);
    expect(brain.summary()).toEqual({ total: 3 });
  });

  it('asks for the counts again after they failed', () => {
    const failing = new Subject<never>();
    summary.mockReset().mockReturnValueOnce(failing).mockReturnValue(of({ total: 3 }));
    const brain = create();
    brain.ensureLoaded();
    failing.error(new Error('500'));
    brain.ensureLoaded();

    expect(summary).toHaveBeenCalledTimes(2);
    expect(topicsList).toHaveBeenCalledTimes(1);
  });
});
