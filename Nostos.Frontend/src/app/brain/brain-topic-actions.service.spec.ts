import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';

import { ToastService } from '../core/services/toast.service';
import { TopicsService, type TopicDto } from '../core/services/topics.service';
import { BrainStateService } from './brain-state.service';
import { BrainTopicActionsService } from './brain-topic-actions.service';

const topic = (id: string, name: string): TopicDto => ({ id, name, usageCount: 2 }) as TopicDto;

describe('BrainTopicActionsService rename', () => {
  const alpha = topic('a', 'Alpha');
  const beta = topic('b', 'Beta');
  let service: BrainTopicActionsService;
  let rename: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    rename = vi.fn().mockReturnValue(of(topic('a', 'Gamma')));
    const state = {
      topics: () => [alpha, beta],
      topicById: (id: string | null) => [alpha, beta].find((t) => t.id === id) ?? null,
      topicsChanged: vi.fn(),
    };
    TestBed.configureTestingModule({
      providers: [
        BrainTopicActionsService,
        { provide: TopicsService, useValue: { rename } },
        { provide: BrainStateService, useValue: state },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
        { provide: Router, useValue: { url: '/brain', navigate: vi.fn() } },
      ],
    });
    service = TestBed.inject(BrainTopicActionsService);
  });

  it('renames to a free name without asking', () => {
    service.startRename(alpha, 'sidebar');
    service.renameValue.set('Gamma');
    service.commitRename();
    expect(rename).toHaveBeenCalledWith('a', 'Gamma');
    expect(service.mergeConfirming()).toBe(false);
  });

  it('opens the merge confirmation instead of merging silently on a name collision', () => {
    service.startRename(alpha, 'title');
    service.renameValue.set('  beta ');
    service.commitRename();
    expect(rename).not.toHaveBeenCalled();
    expect(service.renameId()).toBeNull();
    expect(service.mergeSource()).toBe(alpha);
    expect(service.mergeTargetId()).toBe('b');
    expect(service.mergeConfirming()).toBe(true);
    expect(service.mergeHeading()).toBe('Merge “Alpha” into “Beta”?');
  });
});
