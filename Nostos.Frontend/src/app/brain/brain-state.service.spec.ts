import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';

import { AssistantService } from '../ui/assistant/assistant.service';
import { NotesService } from '../core/services/notes.service';
import { TopicsService, type TopicDto, type TopicKindDto } from '../core/services/topics.service';
import { MAP_COLOR_MODE_KEY } from './brain.helpers';
import { BrainStateService } from './brain-state.service';

describe('BrainStateService loading', () => {
  const topicsList = vi.fn();
  const listKinds = vi.fn();
  const summary = vi.fn();

  function create(): BrainStateService {
    TestBed.configureTestingModule({
      providers: [
        { provide: TopicsService, useValue: { list: topicsList, listKinds } },
        { provide: NotesService, useValue: { summary } },
        { provide: AssistantService, useValue: { actionExecuted: new Subject() } },
      ],
    });
    return TestBed.inject(BrainStateService);
  }

  beforeEach(() => {
    try {
      localStorage.removeItem(MAP_COLOR_MODE_KEY);
    } catch {
      // ignore
    }
    topicsList.mockReset().mockReturnValue(of([{ id: 't1', name: 'Stoicism', usageCount: 1 } as TopicDto]));
    listKinds.mockReset().mockReturnValue(of([] as TopicKindDto[]));
    summary.mockReset().mockReturnValue(of({ total: 3 }));
  });

  afterEach(() => {
    vi.restoreAllMocks();
    TestBed.resetTestingModule();
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

  it('loads kind metadata with the topics and tolerates a failing kinds call', () => {
    const kind = {
      id: 'k1', name: 'Schools', colorKey: 'kind-1', sortOrder: 1, createdAt: '2026-01-01T00:00:00Z', topicCount: 1,
    } satisfies TopicKindDto;
    listKinds.mockReturnValueOnce(of([kind]));
    const brain = create();
    brain.ensureLoaded();
    expect(listKinds).not.toHaveBeenCalled();
    brain.ensureKindsLoaded();
    brain.ensureKindsLoaded();
    expect(brain.topicKinds()).toEqual([kind]);
    expect(listKinds).toHaveBeenCalledTimes(1);

    // A failed kinds call leaves the map usable; the topics themselves still load.
    listKinds.mockReturnValueOnce(throwError(() => new Error('kinds unavailable')));
    expect(() => brain.refresh()).not.toThrow();
    expect(brain.topicsLoaded()).toBe(true);
    expect(brain.topicKinds()).toEqual([kind]);
  });

  it('marks kinds unavailable after a failure and retries on refresh', () => {
    const kind = {
      id: 'k1', name: 'Schools', colorKey: 'kind-1', sortOrder: 1, createdAt: '2026-01-01T00:00:00Z', topicCount: 1,
    } satisfies TopicKindDto;
    listKinds
      .mockReset()
      .mockReturnValueOnce(throwError(() => new Error('kinds unavailable')))
      .mockReturnValueOnce(of([kind]));

    const brain = create();
    brain.ensureKindsLoaded();
    expect(listKinds).toHaveBeenCalledTimes(1);
    expect(brain.kindsLoaded()).toBe(false);
    expect(brain.topicKinds()).toEqual([]);

    // The next refresh asks again rather than treating an empty list as "no kinds".
    topicsList.mockReturnValue(
      of([{ id: 't1', name: 'Stoicism', usageCount: 1, kindId: 'k1' } as TopicDto])
    );
    brain.refresh();
    expect(listKinds).toHaveBeenCalledTimes(2);
    expect(brain.kindsLoaded()).toBe(true);
    expect(brain.topicKinds()).toEqual([kind]);
    expect(brain.mapColorMode()).toBe('kind');
  });

  it('rereads the kinds a screen asked for on every refresh, so their counts do not age', () => {
    const kind = {
      id: 'k1', name: 'Schools', colorKey: 'kind-1', sortOrder: 1, createdAt: '2026-01-01T00:00:00Z', topicCount: 3,
    } satisfies TopicKindDto;
    listKinds.mockReturnValue(of([kind]));
    const brain = create();
    brain.ensureLoaded();
    expect(brain.topicsLoaded()).toBe(true);
    // Nothing asked for kinds, so refresh leaves the request count alone.
    brain.refresh();
    expect(listKinds).not.toHaveBeenCalled();

    brain.ensureKindsLoaded();
    expect(brain.topicKinds()[0].topicCount).toBe(3);

    // A topic-kind write calls refresh(); the counts must follow the write, or
    // the delete confirm says "3 topics" while 13 will have no kind.
    listKinds.mockReturnValue(of([{ ...kind, topicCount: 13 }]));
    brain.refresh();
    expect(listKinds).toHaveBeenCalledTimes(2);
    expect(brain.topicKinds()[0].topicCount).toBe(13);
  });

  it('defaults mapColorMode to kind if any topic has a kind, else cluster, and persists per viewer', () => {
    const brain = create();
    brain.ensureTopicsLoaded();
    expect(brain.hasAnyTopicKind()).toBe(false);
    expect(brain.mapColorMode()).toBe('cluster');

    topicsList.mockReturnValue(of([{ id: 't1', name: 'Stoicism', usageCount: 1, kindId: 'k1' } as TopicDto]));
    brain.refresh();
    expect(brain.hasAnyTopicKind()).toBe(true);
    expect(brain.mapColorMode()).toBe('kind');

    // An explicit choice wins over the default rule and survives a reload.
    brain.setMapColorMode('none');
    expect(brain.mapColorMode()).toBe('none');
    expect(localStorage.getItem(MAP_COLOR_MODE_KEY)).toBe('none');

    TestBed.resetTestingModule();
    topicsList.mockReturnValue(of([{ id: 't1', name: 'Stoicism', usageCount: 1, kindId: 'k1' } as TopicDto]));
    const reloaded = create();
    reloaded.ensureTopicsLoaded();
    expect(reloaded.mapColorMode()).toBe('none');
  });

  it('tolerates blocked localStorage without throwing', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('Blocked', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Blocked', 'SecurityError');
    });

    const brain = create();
    expect(brain.mapColorMode()).toBe('cluster');
    expect(() => brain.setMapColorMode('kind')).not.toThrow();
    expect(brain.mapColorMode()).toBe('kind');
  });
});
