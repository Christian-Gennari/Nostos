import { signal } from '@angular/core';
import { Subject } from 'rxjs';

import type { ToastService } from '../core/services/toast.service';
import type { TopicDetailDto, TopicDto, TopicsService } from '../core/services/topics.service';
import { BrainTopicActions, type BrainTopicActionsHost } from './brain-topic-actions';
import type { IndexSort, MergeRequest } from './second-brain.helpers';

const alpha: TopicDto = { id: 'a', name: 'Alpha', usageCount: 3 };
const beta: TopicDto = { id: 'b', name: 'Beta', usageCount: 1 };

function setup() {
  const rename = new Subject<TopicDto>();
  const merge = new Subject<TopicDto>();
  const remove = new Subject<void>();
  const calls: string[] = [];
  const service = {
    rename: (id: string, name: string) => (calls.push(`rename ${id} ${name}`), rename),
    merge: (source: string, target: string) => (calls.push(`merge ${source} ${target}`), merge),
    delete: (id: string) => (calls.push(`delete ${id}`), remove),
  } as unknown as TopicsService;
  const errors: string[] = [];
  const toast = { error: (message: string) => errors.push(message) } as unknown as ToastService;

  const renamed: [string, TopicDto][] = [];
  const merged: [MergeRequest, TopicDto][] = [];
  const deleted: TopicDto[] = [];
  const host = {
    topics: signal<TopicDto[]>([alpha, beta]),
    selectedId: signal<string | null>('a'),
    selectedDetail: signal<TopicDetailDto | null>(null),
    indexSort: signal<IndexSort>('usage'),
    renamed: (id: string, survivor: TopicDto) => renamed.push([id, survivor]),
    merged: (request: MergeRequest, survivor: TopicDto) => merged.push([request, survivor]),
    deleted: (target: TopicDto) => deleted.push(target),
  } satisfies BrainTopicActionsHost;

  const actions = new BrainTopicActions(service, toast, host);
  return { actions, host, calls, errors, rename, merge, remove, renamed, merged, deleted };
}

describe('BrainTopicActions', () => {
  describe('rename', () => {
    it('starts with the current name and remembers where it was started', () => {
      const { actions } = setup();

      actions.startRename('a', 'header');

      expect(actions.renameId()).toBe('a');
      expect(actions.renameSurface()).toBe('header');
      expect(actions.renameValue()).toBe('Alpha');
    });

    it('refuses an empty name without asking the server', () => {
      const { actions, calls } = setup();
      actions.startRename('a');
      actions.renameValue.set('   ');

      actions.commitRename('a');

      expect(actions.renameError()).toBe('A topic name is required.');
      expect(calls).toEqual([]);
    });

    it('sends the trimmed name, then closes and hands the result to the host', () => {
      const { actions, calls, rename, renamed } = setup();
      actions.startRename('a');
      actions.renameValue.set('  Gamma ');

      actions.commitRename('a');
      expect(calls).toEqual(['rename a Gamma']);
      expect(actions.renaming()).toBe(true);

      const survivor = { ...alpha, name: 'Gamma' };
      rename.next(survivor);

      expect(actions.renaming()).toBe(false);
      expect(actions.renameId()).toBeNull();
      expect(renamed).toEqual([['a', survivor]]);
    });

    it('cannot be cancelled or restarted while the request is running', () => {
      const { actions } = setup();
      actions.startRename('a');
      actions.commitRename('a');

      actions.cancelRename();
      actions.startRename('b');

      expect(actions.renameId()).toBe('a');
    });

    it('closes and reports a failed rename, leaving the host untouched', () => {
      const { actions, rename, errors, renamed } = setup();
      actions.startRename('a');
      actions.renameValue.set('Gamma');
      actions.commitRename('a');

      rename.error(new Error('offline'));

      expect(actions.renameId()).toBeNull();
      expect(errors).toEqual(['Could not rename topic — changes were not saved']);
      expect(renamed).toEqual([]);
    });
  });

  describe('merge', () => {
    it('offers every topic except the selected one, and will not pick it', () => {
      const { actions } = setup();
      actions.openMergePicker();

      expect(actions.mergeCandidates().map((topic) => topic.id)).toEqual(['b']);
      actions.chooseMergeTarget('a');
      expect(actions.mergeTarget()).toBeNull();
    });

    it('does not open without a selected topic', () => {
      const { actions, host } = setup();
      host.selectedId.set(null);

      actions.openMergePicker();

      expect(actions.mergePickerOpen()).toBe(false);
    });

    it('describes the merge from the open detail before asking for confirmation', () => {
      const { actions, host } = setup();
      host.selectedDetail.set({
        id: 'a',
        name: 'Alpha',
        notes: [{ noteId: 'n1', content: '', bookId: 'b1', bookTitle: 'Book' }],
      });
      actions.openMergePicker();
      actions.chooseMergeTarget('b');

      actions.openMergeConfirmation();

      expect(actions.mergePickerOpen()).toBe(false);
      expect(actions.mergeHeading()).toBe('Merge “Alpha” into “Beta”?');
      expect(actions.mergeDescription()).toContain('move 1 note into “Beta”');
    });

    it('merges once, then hands the request and the survivor to the host', () => {
      const { actions, calls, merge, merged } = setup();
      actions.openMergePicker();
      actions.chooseMergeTarget('b');
      actions.openMergeConfirmation();

      actions.confirmMerge();
      actions.confirmMerge();
      actions.cancelMergeConfirmation();
      expect(calls).toEqual(['merge a b']);
      expect(actions.mergeConfirmation()).not.toBeNull();

      merge.next({ ...beta, usageCount: 4 });

      expect(actions.mergingTopic()).toBe(false);
      expect(actions.mergeConfirmation()).toBeNull();
      expect(merged).toHaveLength(1);
      expect(merged[0][0]).toMatchObject({ sourceId: 'a', targetId: 'b', noteCount: 3 });
    });

    it('reports a failed merge and closes the confirmation', () => {
      const { actions, merge, errors, merged } = setup();
      actions.openMergePicker();
      actions.chooseMergeTarget('b');
      actions.openMergeConfirmation();
      actions.confirmMerge();

      merge.error(new Error('offline'));

      expect(actions.mergeConfirmation()).toBeNull();
      expect(errors).toEqual(['Could not merge topics — changes were not saved']);
      expect(merged).toEqual([]);
    });
  });

  describe('delete', () => {
    it('asks first, naming the topic and what deleting leaves behind', () => {
      const { actions, calls } = setup();

      actions.openDeleteTopic('b');

      expect(actions.topicDeleteHeading()).toBe('Delete “Beta”?');
      expect(actions.topicDeleteDescription()).toContain('[[Beta]]');
      expect(calls).toEqual([]);
    });

    it('deletes on confirmation and hands the topic to the host', () => {
      const { actions, calls, remove, deleted } = setup();
      actions.openDeleteTopic('b');

      actions.confirmDeleteTopic();
      actions.cancelDeleteTopic();
      expect(calls).toEqual(['delete b']);
      expect(actions.topicDeleteTarget()).not.toBeNull();

      remove.next();

      expect(actions.topicDeleteTarget()).toBeNull();
      expect(deleted).toEqual([beta]);
    });

    it('reports a failed delete without telling the host anything', () => {
      const { actions, remove, errors, deleted } = setup();
      actions.openDeleteTopic('b');
      actions.confirmDeleteTopic();

      remove.error(new Error('offline'));

      expect(errors).toEqual(['Could not delete topic — it is still in your index']);
      expect(deleted).toEqual([]);
    });
  });
});
