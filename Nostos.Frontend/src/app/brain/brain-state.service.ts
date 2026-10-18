import { Injectable, computed, inject, signal } from '@angular/core';

import type { NoteSummary } from '../core/dtos/note.dtos';
import { NotesService } from '../core/services/notes.service';
import { TopicsService, type TopicDto } from '../core/services/topics.service';
import { topicLookup } from '../ui/note-item/note-item.helpers';
import {
  TOPIC_SORT_KEY,
  isTopicSort,
  readStored,
  sidebarTopics,
  writeStored,
  type TopicSort,
} from './brain.helpers';

/**
 * What every place of the Brain shares: the topics and the counts the sidebar
 * shows. Kept for the app's lifetime so moving between places, or leaving the
 * Brain and coming back, does not reload them; a change to notes refreshes
 * them.
 */
@Injectable({ providedIn: 'root' })
export class BrainStateService {
  private readonly topicsService = inject(TopicsService);
  private readonly notesService = inject(NotesService);

  readonly topics = signal<TopicDto[]>([], { equal: sameTopics });
  readonly topicsLoaded = signal(false);
  readonly topicsFailed = signal(false);
  readonly summary = signal<NoteSummary | null>(null);
  readonly topicSort = signal<TopicSort>(readStored(TOPIC_SORT_KEY, isTopicSort, 'usage'));
  /** The phone drawer holding the sidebar; closes on every navigation. */
  readonly drawerOpen = signal(false);
  /**
   * Bumped by every change to notes or topics, so a list remembered for Back
   * is not shown once something has changed since.
   */
  readonly dataVersion = signal(0);
  /**
   * Bumped when a topic was renamed, merged or deleted: the server rewrote
   * notes' [[ ]] references, so the notes on screen must be read again.
   */
  readonly topicsRewritten = signal(0);

  /** The topics by lower-cased name, built once for every card on screen. */
  readonly lookup = computed(() => topicLookup(this.topics()));
  readonly sidebarTopics = computed(() => sidebarTopics(this.topics(), this.topicSort()));

  private loading = false;
  private refreshSeq = 0;

  /** Loads the topics and counts the first time anything asks. */
  ensureLoaded(): void {
    if (this.topicsLoaded() || this.loading) return;
    this.load();
  }

  /** Reloads the topics and counts after notes or topics changed. */
  refresh(): void {
    this.dataVersion.update((version) => version + 1);
    this.load();
  }

  /** A topic was renamed, merged or deleted: refresh, and have lists reread their notes. */
  topicsChanged(): void {
    this.topicsRewritten.update((version) => version + 1);
    this.refresh();
  }

  private load(): void {
    // Only the latest request's answers are kept: an older response that
    // arrives last must not undo a newer one.
    const seq = ++this.refreshSeq;
    this.loading = true;
    this.topicsService.list().subscribe({
      next: (topics) => {
        if (seq !== this.refreshSeq) return;
        this.topics.set(topics);
        this.topicsLoaded.set(true);
        this.topicsFailed.set(false);
        this.loading = false;
      },
      error: () => {
        if (seq !== this.refreshSeq) return;
        this.topicsFailed.set(!this.topicsLoaded());
        this.loading = false;
      },
    });
    this.notesService.summary().subscribe({
      next: (summary) => {
        if (seq === this.refreshSeq) this.summary.set(summary);
      },
      // Counts decorate the sidebar; a failure must not make the Brain unusable.
      error: () => undefined,
    });
  }

  setTopicSort(sort: TopicSort): void {
    this.topicSort.set(sort);
    writeStored(TOPIC_SORT_KEY, sort);
  }

  topicById(id: string | null): TopicDto | null {
    return id ? this.topics().find((topic) => topic.id === id) ?? null : null;
  }
}

/** An unchanged list keeps its identity, so every card's lookup is not rebuilt. */
function sameTopics(a: readonly TopicDto[], b: readonly TopicDto[]): boolean {
  return a.length === b.length && a.every((topic, i) =>
    topic.id === b[i].id && topic.name === b[i].name && topic.usageCount === b[i].usageCount);
}
