import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';

import { ToastService } from '../core/services/toast.service';
import { TopicsService, type TopicDto } from '../core/services/topics.service';
import { rankTopicChoices, topicChoices } from '../ui/note-item/note-item.helpers';
import { BrainStateService } from './brain-state.service';
import { isListedTopic } from './brain.helpers';

/** Where a rename was started: the sidebar row or the topic page's title. */
export type RenameSurface = 'sidebar' | 'title';

/**
 * Renaming, merging and deleting a topic, from the sidebar or the topic page.
 * One owner for the field, the pickers and the confirmations, which the shell
 * renders once; a result refreshes the Brain and, when the topic on screen
 * changed identity, goes to the one that survives.
 */
@Injectable()
export class BrainTopicActionsService {
  private readonly topicsService = inject(TopicsService);
  private readonly state = inject(BrainStateService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  // Rename, in place.
  readonly renameId = signal<string | null>(null);
  readonly renameSurface = signal<RenameSurface>('sidebar');
  readonly renameValue = signal('');
  readonly renameError = signal<string | null>(null);
  readonly renaming = signal(false);

  // Merge: choose a target, then confirm.
  readonly mergeSource = signal<TopicDto | null>(null);
  readonly mergeQuery = signal('');
  readonly mergeTargetId = signal<string | null>(null);
  readonly mergeConfirming = signal(false);
  readonly merging = signal(false);

  // Delete: confirm.
  readonly deleteTarget = signal<TopicDto | null>(null);
  readonly deleting = signal(false);

  readonly mergeCandidates = computed(() => {
    const source = this.mergeSource();
    if (!source) return [];
    return rankTopicChoices(topicChoices(this.state.topics().filter(isListedTopic)), this.mergeQuery(), [source.name], 50);
  });

  readonly mergeTarget = computed(() => this.state.topicById(this.mergeTargetId()));

  readonly mergeHeading = computed(() => {
    const source = this.mergeSource();
    const target = this.mergeTarget();
    return source && target ? `Merge “${source.name}” into “${target.name}”?` : 'Merge topics?';
  });

  readonly mergeDescription = computed(() => {
    const source = this.mergeSource();
    const target = this.mergeTarget();
    if (!source || !target) return '';
    const notes = source.usageCount === 1 ? '1 note' : `${source.usageCount} notes`;
    return `${notes} move to “${target.name}”, and their [[${source.name}]] becomes [[${target.name}]]. “${source.name}” goes away.`;
  });

  readonly deleteHeading = computed(() => {
    const target = this.deleteTarget();
    return target ? `Delete “${target.name}”?` : 'Delete topic?';
  });

  readonly deleteDescription = computed(() => {
    const target = this.deleteTarget();
    return target
      ? `It is removed from its notes. In their text, [[${target.name}]] becomes plain words, so nothing you wrote is lost.`
      : '';
  });

  startRename(topic: TopicDto, surface: RenameSurface): void {
    this.renameId.set(topic.id);
    this.renameSurface.set(surface);
    this.renameValue.set(topic.name);
    this.renameError.set(null);
  }

  cancelRename(): void {
    if (this.renaming()) return;
    this.renameId.set(null);
    this.renameError.set(null);
  }

  commitRename(): void {
    const id = this.renameId();
    const topic = this.state.topicById(id);
    const name = this.renameValue().trim();
    if (!id || !topic || this.renaming()) return;
    if (!name) {
      this.renameError.set('A topic needs a name.');
      return;
    }
    if (name === topic.name) {
      this.cancelRename();
      return;
    }

    // A name another topic already has is a merge: ask, as "Merge into…" does.
    const key = name.toLocaleUpperCase();
    const existing = this.state.topics().find((t) => t.id !== id && t.name.trim().toLocaleUpperCase() === key);
    if (existing) {
      this.renameId.set(null);
      this.renameError.set(null);
      this.mergeSource.set(topic);
      this.mergeQuery.set('');
      this.mergeTargetId.set(existing.id);
      this.mergeConfirming.set(true);
      return;
    }

    this.renaming.set(true);
    this.renameError.set(null);
    this.topicsService.rename(id, name).subscribe({
      next: (survivor) => {
        this.renaming.set(false);
        this.renameId.set(null);
        this.state.topicsChanged();
        this.toast.success(survivor.id === id ? `Renamed to “${survivor.name}”` : `Merged into “${survivor.name}”`);
        this.followSurvivor(id, survivor.id);
      },
      error: (error: unknown) => {
        this.renaming.set(false);
        this.renameError.set(serverMessage(error) ?? 'Could not rename the topic. Try again.');
      },
    });
  }

  openMerge(topic: TopicDto): void {
    this.mergeSource.set(topic);
    this.mergeQuery.set('');
    this.mergeTargetId.set(null);
    this.mergeConfirming.set(false);
  }

  closeMerge(): void {
    if (this.merging()) return;
    this.mergeSource.set(null);
    this.mergeConfirming.set(false);
  }

  /** The picker's choice: ask to confirm the merge. */
  confirmMergeTarget(): void {
    if (this.mergeTarget()) this.mergeConfirming.set(true);
  }

  cancelMergeConfirmation(): void {
    if (!this.merging()) this.mergeConfirming.set(false);
  }

  merge(): void {
    const source = this.mergeSource();
    const target = this.mergeTarget();
    if (!source || !target || this.merging()) return;
    this.merging.set(true);
    this.topicsService.merge(source.id, target.id).subscribe({
      next: (survivor) => {
        this.merging.set(false);
        this.mergeSource.set(null);
        this.mergeConfirming.set(false);
        this.state.topicsChanged();
        this.toast.success(`Merged into “${survivor.name}”`);
        this.followSurvivor(source.id, survivor.id);
      },
      error: () => {
        this.merging.set(false);
        this.toast.error('Could not merge the topics. Try again.');
      },
    });
  }

  openDelete(topic: TopicDto): void {
    this.deleteTarget.set(topic);
  }

  cancelDelete(): void {
    if (!this.deleting()) this.deleteTarget.set(null);
  }

  delete(): void {
    const target = this.deleteTarget();
    if (!target || this.deleting()) return;
    this.deleting.set(true);
    this.topicsService.delete(target.id).subscribe({
      next: () => {
        this.deleting.set(false);
        this.deleteTarget.set(null);
        this.state.topicsChanged();
        this.toast.success(`Deleted “${target.name}”`);
        if (this.onTopicPage(target.id)) void this.router.navigate(['/brain']);
      },
      error: () => {
        this.deleting.set(false);
        this.toast.error('Could not delete the topic. Try again.');
      },
    });
  }

  /** A topic page whose topic merged away moves to the topic that took its notes. */
  private followSurvivor(previousId: string, survivorId: string): void {
    if (previousId !== survivorId && this.onTopicPage(previousId)) {
      void this.router.navigate(['/brain/topics', survivorId]);
    }
  }

  private onTopicPage(id: string): boolean {
    return this.router.url.split(/[?#]/)[0] === `/brain/topics/${id}`;
  }
}

function serverMessage(error: unknown): string | null {
  if (!(error instanceof HttpErrorResponse)) return null;
  const body = error.error as { error?: unknown } | null;
  return typeof body?.error === 'string' ? body.error : null;
}
