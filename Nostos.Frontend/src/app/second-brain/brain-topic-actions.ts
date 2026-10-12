import { computed, signal, type Signal } from '@angular/core';

import type { ToastService } from '../core/services/toast.service';
import type { TopicDetailDto, TopicDto, TopicsService } from '../core/services/topics.service';
import {
  rankTopics,
  type IndexSort,
  type MergeRequest,
  type RenameSurface,
} from './second-brain.helpers';

/**
 * What the topic actions need to know about the Brain, and how they tell it
 * that something changed. The Brain owns the index, the selection and the
 * detail cache, so applying a result stays there.
 */
export interface BrainTopicActionsHost {
  readonly topics: Signal<TopicDto[]>;
  readonly selectedId: Signal<string | null>;
  readonly selectedDetail: Signal<TopicDetailDto | null>;
  readonly indexSort: Signal<IndexSort>;
  /** The server renamed `requestedId`; `survivor` differs when it merged instead. */
  renamed(requestedId: string, survivor: TopicDto): void;
  merged(request: MergeRequest, survivor: TopicDto): void;
  deleted(target: TopicDto): void;
}

/**
 * Renaming, merging and deleting a topic: what the user has open, what they
 * typed or picked, and the request each one ends in.
 *
 * Each action is a small conversation (open, choose, confirm or cancel) that
 * must not be interrupted by itself: while a request is running the same
 * action cannot be started, changed or dismissed. Success and failure are
 * announced here; what a success does to the index is the host's to decide.
 */
export class BrainTopicActions {
  // Rename stays in the surface that initiated it; the confirmation state is
  // separate from note deletion because the latter has a different
  // consequence and tone.
  readonly renameId = signal<string | null>(null);
  readonly renameSurface = signal<RenameSurface>('index');
  readonly renameValue = signal('');
  readonly renameError = signal<string | null>(null);
  readonly renaming = signal(false);

  readonly mergePickerOpen = signal(false);
  readonly mergeSearchQuery = signal('');
  readonly mergeTargetId = signal<string | null>(null);
  readonly mergeConfirmation = signal<MergeRequest | null>(null);
  readonly mergingTopic = signal(false);

  readonly topicDeleteTarget = signal<TopicDto | null>(null);
  readonly deletingTopic = signal(false);

  readonly mergeCandidates = computed(() =>
    rankTopics(
      this.host.topics(),
      this.mergeSearchQuery(),
      this.host.selectedId(),
      this.host.indexSort(),
    ),
  );

  readonly mergeTarget = computed(() => {
    const targetId = this.mergeTargetId();
    return targetId ? this.host.topics().find((topic) => topic.id === targetId) ?? null : null;
  });

  readonly topicDeleteHeading = computed(() => {
    const target = this.topicDeleteTarget();
    return target ? `Delete “${target.name}”?` : 'Delete topic?';
  });

  readonly topicDeleteDescription = computed(() => {
    const target = this.topicDeleteTarget();
    return target
      ? `Deleting this topic removes its note links, but does not edit note text. The [[${target.name}]] reference stays in notes, and saving a note again will re-create the topic.`
      : '';
  });

  readonly mergeHeading = computed(() => {
    const request = this.mergeConfirmation();
    return request ? `Merge “${request.sourceName}” into “${request.targetName}”?` : 'Merge topics?';
  });

  readonly mergeDescription = computed(() => {
    const request = this.mergeConfirmation();
    if (!request) return '';
    const noteLabel = request.noteCount === 1 ? 'note' : 'notes';
    return `This will move ${request.noteCount} ${noteLabel} into “${request.targetName}” and the source topic “${request.sourceName}” will disappear.`;
  });

  constructor(
    private readonly topicsService: TopicsService,
    private readonly toast: ToastService,
    private readonly host: BrainTopicActionsHost,
  ) {}

  startRename(id: string, surface: RenameSurface = 'index'): void {
    if (this.renaming()) return;
    const topic = this.host.topics().find((candidate) => candidate.id === id);
    const detail = this.host.selectedDetail();
    const name = topic?.name ?? (detail?.id === id ? detail.name : null);
    if (!name) return;

    this.renameId.set(id);
    this.renameSurface.set(surface);
    this.renameValue.set(name);
    this.renameError.set(null);
  }

  cancelRename(): void {
    if (this.renaming()) return;
    this.renameId.set(null);
    this.renameValue.set('');
    this.renameError.set(null);
  }

  commitRename(id: string): void {
    if (this.renaming() || this.renameId() !== id) return;

    const name = this.renameValue().trim();
    if (!name) {
      this.renameError.set('A topic name is required.');
      return;
    }

    const original = this.host.topics().find((topic) => topic.id === id);
    if (!original) {
      this.cancelRename();
      return;
    }

    this.renaming.set(true);
    this.topicsService.rename(id, name).subscribe({
      next: (survivor) => {
        this.renaming.set(false);
        this.renameId.set(null);
        this.renameValue.set('');
        this.renameError.set(null);
        this.host.renamed(id, survivor);
      },
      error: () => {
        this.renaming.set(false);
        this.cancelRename();
        this.toast.error('Could not rename topic — changes were not saved');
      },
    });
  }

  openMergePicker(): void {
    const sourceId = this.host.selectedId();
    if (!sourceId || this.mergingTopic()) return;
    this.mergeSearchQuery.set('');
    this.mergeTargetId.set(null);
    this.mergePickerOpen.set(true);
  }

  closeMergePicker(): void {
    if (this.mergingTopic()) return;
    this.mergePickerOpen.set(false);
    this.mergeSearchQuery.set('');
    this.mergeTargetId.set(null);
  }

  chooseMergeTarget(id: string): void {
    if (id === this.host.selectedId()) return;
    this.mergeTargetId.set(id);
  }

  openMergeConfirmation(): void {
    const sourceId = this.host.selectedId();
    const target = this.mergeTarget();
    const source = sourceId ? this.host.topics().find((topic) => topic.id === sourceId) : null;
    if (!source || !target || source.id === target.id) return;

    const detail = this.host.selectedDetail();
    this.mergeConfirmation.set({
      sourceId: source.id,
      targetId: target.id,
      sourceName: detail?.name ?? source.name,
      targetName: target.name,
      noteCount: detail?.notes.length ?? source.usageCount,
    });
    this.mergePickerOpen.set(false);
  }

  cancelMergeConfirmation(): void {
    if (!this.mergingTopic()) this.mergeConfirmation.set(null);
  }

  confirmMerge(): void {
    const request = this.mergeConfirmation();
    if (!request || this.mergingTopic()) return;

    this.mergingTopic.set(true);
    this.topicsService.merge(request.sourceId, request.targetId).subscribe({
      next: (survivor) => {
        this.mergingTopic.set(false);
        this.mergeConfirmation.set(null);
        this.host.merged(request, survivor);
      },
      error: () => {
        this.mergingTopic.set(false);
        this.mergeConfirmation.set(null);
        this.toast.error('Could not merge topics — changes were not saved');
      },
    });
  }

  openDeleteTopic(id: string): void {
    if (this.deletingTopic()) return;
    const topic = this.host.topics().find((candidate) => candidate.id === id);
    if (topic) this.topicDeleteTarget.set(topic);
  }

  cancelDeleteTopic(): void {
    if (!this.deletingTopic()) this.topicDeleteTarget.set(null);
  }

  confirmDeleteTopic(): void {
    const target = this.topicDeleteTarget();
    if (!target || this.deletingTopic()) return;

    this.deletingTopic.set(true);
    this.topicsService.delete(target.id).subscribe({
      next: () => {
        this.deletingTopic.set(false);
        this.topicDeleteTarget.set(null);
        this.host.deleted(target);
      },
      error: () => {
        this.deletingTopic.set(false);
        this.topicDeleteTarget.set(null);
        this.toast.error('Could not delete topic — it is still in your index');
      },
    });
  }
}
