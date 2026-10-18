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

  readonly topics = signal<TopicDto[]>([]);
  readonly topicsLoaded = signal(false);
  readonly topicsFailed = signal(false);
  readonly summary = signal<NoteSummary | null>(null);
  readonly topicSort = signal<TopicSort>(readStored(TOPIC_SORT_KEY, isTopicSort, 'usage'));
  /** The phone drawer holding the sidebar; closes on every navigation. */
  readonly drawerOpen = signal(false);

  /** The topics by lower-cased name, built once for every card on screen. */
  readonly lookup = computed(() => topicLookup(this.topics()));
  readonly sidebarTopics = computed(() => sidebarTopics(this.topics(), this.topicSort()));

  private loading = false;

  /** Loads the topics and counts the first time anything asks. */
  ensureLoaded(): void {
    if (this.topicsLoaded() || this.loading) return;
    this.refresh();
  }

  /** Reloads the topics and counts after notes or topics changed. */
  refresh(): void {
    this.loading = true;
    this.topicsService.list().subscribe({
      next: (topics) => {
        this.topics.set(topics);
        this.topicsLoaded.set(true);
        this.topicsFailed.set(false);
        this.loading = false;
      },
      error: () => {
        this.topicsFailed.set(!this.topicsLoaded());
        this.loading = false;
      },
    });
    this.notesService.summary().subscribe({
      next: (summary) => this.summary.set(summary),
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
