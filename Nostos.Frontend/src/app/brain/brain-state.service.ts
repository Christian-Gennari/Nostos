import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import type { NoteSummary } from '../core/dtos/note.dtos';
import { NotesService } from '../core/services/notes.service';
import { AssistantService } from '../ui/assistant/assistant.service';
import { TopicsService, type TopicDto, type TopicKindDto } from '../core/services/topics.service';
import type { MapCamera } from './topic-map/topic-map.helpers';
import { topicLookup } from '../ui/note-item/note-item.helpers';
import {
  MAP_COLOR_MODE_KEY,
  TOPIC_SORT_KEY,
  isMapColorMode,
  isTopicSort,
  readStored,
  readStoredNullable,
  sidebarTopics,
  writeStored,
  type MapColorMode,
  type TopicSort,
} from './brain.helpers';

export interface BrainMapView {
  camera: MapCamera;
  focus: string | null;
  around: string | null;
  depth: number;
  min: number | null;
  kinds?: string | null;
}

/** Ask Nostos capabilities that change notes or their topics. */
const NOTE_WRITING_CAPABILITIES = new Set(['notes_capture', 'notes_rewrite_capture', 'notes_link_existing_topic']);

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
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    // Ask Nostos can capture a note or link a topic: the Brain's lists and
    // counts are read again, wherever the reader is.
    inject(AssistantService).actionExecuted.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(({ capability }) => {
      if (NOTE_WRITING_CAPABILITIES.has(capability)) this.topicsChanged();
    });
  }

  readonly topics = signal<TopicDto[]>([], { equal: sameTopics });
  readonly topicKinds = signal<TopicKindDto[]>([], { equal: sameKinds });
  readonly topicsLoaded = signal(false);
  readonly topicsFailed = signal(false);
  /**
   * True once the kind list actually arrived. A failure leaves it false, which
   * tells the map to hide kind colouring and the legend rather than counting
   * every typed topic as untyped.
   */
  readonly kindsLoaded = signal(false);
  /**
   * The kind list was asked for and did not arrive. The screens that can add or
   * edit a kind say so, and offer the same "Try again" as the topics list: a
   * create form over an unknown list would pick a colour another kind holds and
   * be refused by the server.
   */
  readonly kindsFailed = signal(false);
  /**
   * A read of the kind list is open. The screens with a "Try again" keep it from
   * starting a second read, whose answer would replace the first one's.
   */
  readonly kindsLoading = signal(false);
  readonly summary = signal<NoteSummary | null>(null);
  readonly topicSort = signal<TopicSort>(readStored(TOPIC_SORT_KEY, isTopicSort, 'usage'));
  private readonly mapColorModeChoice = signal<MapColorMode | null>(
    readStoredNullable(MAP_COLOR_MODE_KEY, isMapColorMode)
  );
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
  /** Where the map was left (camera and selected topic), so Back returns to it. */
  readonly mapView = signal<BrainMapView | null>(null);

  /** The topics by lower-cased name, built once for every card on screen. */
  readonly lookup = computed(() => topicLookup(this.topics()));
  readonly sidebarTopics = computed(() => sidebarTopics(this.topics(), this.topicSort()));
  readonly hasAnyTopicKind = computed(() => this.topics().some((topic) => !!topic.kindId));
  readonly mapColorMode = computed<MapColorMode>(
    () => this.mapColorModeChoice() ?? (this.hasAnyTopicKind() ? 'kind' : 'cluster')
  );

  private loading = false;
  private refreshSeq = 0;
  /** The counts were asked for and have not failed; they are not asked for twice. */
  private summaryRequested = false;
  /** A screen asked for kinds, so every later refresh reads them again. */
  private kindsRequested = false;
  /** A reload was asked for while one was in flight; it runs when that one lands. */
  private kindsQueued = false;
  /**
   * Bumped when a kind read is superseded by another. The answer to a
   * superseded read is the server's order from before the write that replaced
   * it, so applying it would put that order back on screen until the queued
   * read lands.
   */
  private kindsSeq = 0;

  /** Loads the topics and counts the first time anything asks. */
  ensureLoaded(): void {
    this.ensureTopicsLoaded();
    if (!this.summaryRequested) this.loadSummary(this.refreshSeq);
  }

  /**
   * Loads kind metadata (names and palette keys) for the screens that show it —
   * the map today, the kind picker tomorrow. Kept out of `ensureLoaded` so a
   * reader opening a book does not pay for a request only the map reads.
   */
  ensureKindsLoaded(): void {
    this.kindsRequested = true;
    // A read already on its way answers this caller too. Starting another here
    // would number it over the first, whose valid answer was then thrown away.
    if (this.kindsLoaded() || this.kindsLoading()) return;
    this.loadKinds();
  }

  /**
   * Re-reads kind metadata now, after the reader changed a kind (renamed,
   * recoloured, reordered, added or deleted). A write to a topic's kind only
   * has to call `refresh()`, which rereads the kinds whenever a screen asked
   * for them.
   */
  reloadKinds(): void {
    this.kindsRequested = true;
    this.loadKinds();
  }

  /**
   * Loads the topics the first time anything asks, without the counts: for a
   * page that offers topics but shows no sidebar. `ensureLoaded` called later
   * still fetches the counts.
   */
  ensureTopicsLoaded(): void {
    if (this.topicsLoaded() || this.loading) return;
    // Kind metadata is for the map; a captured note's topic pills only need the
    // topic list, so this path keeps the request count unchanged.
    this.load({ summary: false, kinds: false });
  }

  /** Reloads the topics and counts after notes or topics changed. */
  refresh(): void {
    this.dataVersion.update((version) => version + 1);
    // The kinds carry `topicCount`, so they age with every topic-kind write:
    // a screen that asked for them (the picker, the sorter) gets them read
    // again here, or its delete confirm counts topics that no longer have the
    // kind. A screen that never asked keeps its request count unchanged.
    this.load({ summary: true, kinds: this.kindsRequested });
  }

  /** A topic was renamed, merged or deleted: refresh, and have lists reread their notes. */
  topicsChanged(): void {
    this.topicsRewritten.update((version) => version + 1);
    this.refresh();
  }

  private load(options: { summary: boolean; kinds: boolean } = { summary: true, kinds: false }): void {
    // Only the latest request's answers are kept: an older response that
    // arrives last must not undo a newer one.
    const seq = ++this.refreshSeq;
    this.loading = true;
    this.topicsService.list().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
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
    if (options.kinds) this.loadKinds();
    if (options.summary) this.loadSummary(seq);
  }

  private loadKinds(): void {
    if (this.kindsLoading()) {
      // A write landed while the last answer was in flight: read again once it
      // does, so the counts are the ones after the write. That answer is stale
      // now — it holds the server's order from before the write — so it must
      // not be applied when it lands.
      this.kindsQueued = true;
      this.kindsSeq += 1;
      return;
    }
    this.kindsLoading.set(true);
    // A read that starts is no longer a failure: the screens show they are
    // loading, and only a read that actually fails says so again.
    this.kindsFailed.set(false);
    const seq = ++this.kindsSeq;
    this.topicsService.listKinds().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (kinds) => {
        // A newer read replaced this one: applying its order would revert a
        // reorder the reader can see for as long as the queued read takes.
        if (seq === this.kindsSeq) {
          this.topicKinds.set(kinds);
          this.kindsLoaded.set(true);
          this.kindsFailed.set(false);
        }
        this.kindsLoading.set(false);
        this.runQueuedKinds();
      },
      // Kind metadata decorates the map and topic views, so a failure leaves the
      // loaded list (if any) in place; a first failure is reported to the
      // screens that let the reader add or edit a kind.
      error: () => {
        if (seq === this.kindsSeq) this.kindsFailed.set(!this.kindsLoaded());
        this.kindsLoading.set(false);
        this.runQueuedKinds();
      },
    });
  }

  private runQueuedKinds(): void {
    if (!this.kindsQueued) return;
    this.kindsQueued = false;
    this.loadKinds();
  }

  private loadSummary(seq: number): void {
    this.summaryRequested = true;
    this.notesService.summary().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (summary) => {
        if (seq === this.refreshSeq) this.summary.set(summary);
      },
      // Counts decorate the sidebar; a failure must not make the Brain unusable.
      // The next `ensureLoaded` asks again.
      error: () => {
        if (seq === this.refreshSeq) this.summaryRequested = false;
      },
    });
  }

  setTopicSort(sort: TopicSort): void {
    this.topicSort.set(sort);
    writeStored(TOPIC_SORT_KEY, sort);
  }

  setMapColorMode(mode: MapColorMode): void {
    this.mapColorModeChoice.set(mode);
    writeStored(MAP_COLOR_MODE_KEY, mode);
  }

  /**
   * Shows a topic as the server now has it, then reloads: the reload also
   * supersedes any list still in flight from before the change.
   */
  replaceTopic(changed: TopicDto): void {
    this.topics.update((topics) => topics.map((topic) => (topic.id === changed.id ? { ...topic, ...changed } : topic)));
    this.refresh();
  }

  topicById(id: string | null): TopicDto | null {
    return id ? this.topics().find((topic) => topic.id === id) ?? null : null;
  }
}

/** An unchanged list keeps its identity, so every card's lookup is not rebuilt. */
function sameTopics(a: readonly TopicDto[], b: readonly TopicDto[]): boolean {
  return a.length === b.length && a.every((topic, i) =>
    topic.id === b[i].id && topic.name === b[i].name && topic.usageCount === b[i].usageCount
    && (topic.description ?? null) === (b[i].description ?? null)
    && (topic.kindId ?? null) === (b[i].kindId ?? null)
    && (topic.kindSource ?? null) === (b[i].kindSource ?? null));
}

function sameKinds(a: readonly TopicKindDto[], b: readonly TopicKindDto[]): boolean {
  return a.length === b.length && a.every((kind, i) =>
    kind.id === b[i].id && kind.name === b[i].name && kind.colorKey === b[i].colorKey
    && kind.sortOrder === b[i].sortOrder && kind.topicCount === b[i].topicCount);
}
