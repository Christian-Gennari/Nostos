import {
  AfterViewChecked,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  output,
  QueryList,
  signal,
  ViewChildren,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, NavigationStart, Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { ToastService } from '../core/services/toast.service';
import { CloudAuthService } from '../core/services/cloud-auth.service';
import { DeploymentCapabilitiesService } from '../core/services/deployment-capabilities.service';
import { NotesService } from '../core/services/notes.service';
import { Note, NoteSearchHit } from '../core/dtos/note.dtos';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { NoteCardComponent, type NoteSourceNavigation } from '../ui/note-card.component/note-card.component';
import { NoteCaptureDetailsComponent } from '../ui/note-capture-details/note-capture-details.component';
import { NoteFormatPipe } from '../ui/pipes/note-format.pipe';
import {
  TopicsService,
  TopicDto,
  TopicDetailDto,
  NoteContextDto,
  TopicStatsDto,
  RelatedTopicDto,
} from '../core/services/topics.service';
import { TopicMapComponent } from './topic-map/topic-map.component';
import { TopicInputComponent } from '../ui/topic-input.component/topic-input.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { ViewToggleComponent, type ViewToggleOption } from '../ui/view-toggle/view-toggle.component';
import { ButtonComponent } from '../ui/button/button.component';
import { BadgeComponent } from '../ui/badge/badge.component';
import { ChipComponent } from '../ui/chip/chip.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { DropdownComponent, type DropdownOption } from '../ui/dropdown/dropdown.component';
import { AssistantContextService } from '../ui/assistant/assistant-context.service';
import { AssistantService, type AssistantSuggestionDto } from '../ui/assistant/assistant.service';
import {
  BrainWritingHandoffComponent,
  type BrainWritingHandoffResult,
} from './writing-handoff/brain-writing-handoff.component';
import { BrainNoteReview } from './brain-note-review';
import { BrainTopicActions } from './brain-topic-actions';
import { BrainTopicProposals } from './brain-topic-proposals';
import { TopicDataCache } from './topic-data-cache';
import { BrainTopicPickerComponent } from './topic-picker/brain-topic-picker.component';
import { BrainTopicProposalsComponent } from './topic-proposals/brain-topic-proposals.component';
import { BrainTopicRenameComponent } from './topic-rename/brain-topic-rename.component';

import {
  ALL_SOURCES,
  BRAIN_VIEW_MODES,
  BRAIN_VIEW_MODE_STORAGE_KEY,
  INDEX_SORTS,
  INDEX_SORT_STORAGE_KEY,
  NOTE_SEARCH_DEBOUNCE_MS,
  REVIEW_PAGE_SIZE,
  declaredTopicNames,
  highlightNameParts,
  newTopicNameFor,
  normalizeSearchText,
  noteSourceParams,
  rankTopics,
  withTopicReference,
  type BrainPaneMode,
  type BrainViewMode,
  type IndexSort,
  type MergeRequest,
  type NamePart,
  type NoteSort,
  type NoteSourceParams,
  type SourceOption,
} from './second-brain.helpers';

const REVIEW_STORAGE_PREFIX = 'nostos.brain.reviewReturn.v1';

interface ReviewReturnState {
  returnMode: 'list' | 'notes';
  searchQuery: string;
  selectedId: string | null;
  noteSearchQuery: string;
  sourceFilter: string;
  noteSort: NoteSort;
  browseQuery: string;
  browseBookId: string | null;
  browseBookTitle: string;
  browseWithoutTopics: boolean;
  browseOldestFirst: boolean;
  focusedNoteId: string | null;
  reviewId: string | null;
}

@Component({
  standalone: true,
  selector: 'app-brain',
  imports: [
    NoteCaptureDetailsComponent,
    CommonModule,
    FormsModule,
    RouterLink,
    NostosIconComponent,
    LoadingIndicatorComponent,
    ViewToggleComponent,
    ButtonComponent,
    BadgeComponent,
    ChipComponent,
    IconButtonComponent,
    InputDirective,
    DropdownComponent,
    NoteCardComponent,
    NoteFormatPipe,
    ConfirmModal,
    TopicMapComponent,
    TopicInputComponent,
    BrainWritingHandoffComponent,
    BrainTopicPickerComponent,
    BrainTopicRenameComponent,
    BrainTopicProposalsComponent,
  ],
  templateUrl: './second-brain.component.html',
  styleUrls: ['./second-brain.component.css'],
})
export class SecondBrain implements AfterViewChecked {
  readonly indexSortOptions = [
    { value: 'usage', label: 'Sort: Most used' },
    { value: 'az', label: 'Sort: A → Z' },
    { value: 'za', label: 'Sort: Z → A' },
  ] satisfies readonly DropdownOption[];

  readonly noteSortOptions = [
    { value: 'newest', label: 'Newest first' },
    { value: 'oldest', label: 'Oldest first' },
    { value: 'source', label: 'Source' },
  ] satisfies readonly DropdownOption[];

  readonly browseOrderOptions = [
    { value: 'newest', label: 'Newest first' },
    { value: 'oldest', label: 'Oldest first' },
  ] satisfies readonly DropdownOption[];
  private topicsService = inject(TopicsService);
  private notesService = inject(NotesService);
  private toast = inject(ToastService);
  private readonly assistantContext = inject(AssistantContextService);
  private readonly assistant = inject(AssistantService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly cloudAuth = inject(CloudAuthService);
  private readonly capabilities = inject(DeploymentCapabilitiesService);
  private readonly host = inject(ElementRef<HTMLElement>);

  /** The focused note context provider for browsing or optional review. */
  private assistantContextUnregister: (() => void) | null = null;

  /** The assistant's suggested topics for the note in focus. */
  readonly proposals: BrainTopicProposals = new BrainTopicProposals(this.assistant, this.topicsService, {
    focusedNote: () => this.focusedNote(),
    focusedNoteKey: () => this.focusedNoteKey(),
    isEditingNote: () => this.browseEditing() || this.review.editing(),
    linked: (accepted) => this.applyProposalLink(accepted),
    restoreFocus: () => this.focusSuggestTopicsButton(),
  });

  // Phase 5 consumes these outputs to open the rename and confirmation flows.
  readonly renameRequested = output<string>();
  readonly deleteRequested = output<string>();

  // State
  topics = signal<TopicDto[]>([]);
  topicStats = signal<TopicStatsDto | null>(null);
  loadingTopics = signal(true);
  searchQuery = signal('');

  // Note-text matches for the current query, from the server (issue #158). Empty
  // until a search runs, and cleared when the query is.
  noteMatches = signal<TopicDto[]>([]);
  noteHits = signal<NoteSearchHit[]>([]);
  panelNote = signal<NoteSearchHit | null>(null);

  // Notes are a first-class way into Brain. The server returns bounded pages;
  // review remains a deliberate, non-persisted subtask of this view.
  browseNotes = signal<NoteSearchHit[]>([]);
  browseTotal = signal(0);
  browseQuery = signal('');
  browseBookId = signal<string | null>(null);
  browseBookTitle = signal('');
  browseWithoutTopics = signal(false);
  browseOldestFirst = signal(false);
  browseLoading = signal(false);
  browseLoaded = signal(false);
  browseError = signal(false);
  browseEditing = signal(false);
  browseEditContent = signal('');
  browsePickerOpen = signal(false);
  browsePickerQuery = signal('');
  browsePickerId = signal<string | null>(null);
  browseSaving = signal(false);
  browsePickerCandidates = computed(() => this.filterAndSortTopics(this.browsePickerQuery(), null));
  browseNewTopicName = computed(() => newTopicNameFor(this.browsePickerQuery(), this.topics()));
  browseSourceParams(note: NoteSearchHit | NoteContextDto): NoteSourceParams | null {
    return noteSourceParams(note);
  }

  noteSourceNavigation(note: NoteSearchHit | NoteContextDto): NoteSourceNavigation {
    const queryParams = this.browseSourceParams(note);
    return {
      commands: [queryParams ? '/read' : '/library', note.bookId],
      queryParams,
      label: queryParams ? 'Return to passage' : 'Open book',
    };
  }

  openBrowseTopic(name: string): void {
    const topic = this.topics().find(
      (item) => item.name.trim().toLowerCase() === name.trim().toLowerCase());
    if (!topic) return;
    this.setViewMode('list');
    this.selectTopic(topic.id);
  }

  private deepLinkNoteSeq = 0;

  private openDeepLinkedNote(noteId: string): void {
    const seq = ++this.deepLinkNoteSeq;
    this.setViewMode('notes');
    this.notesService.get(noteId).subscribe({
      next: (note) => {
        if (seq !== this.deepLinkNoteSeq) return;
        this.panelNote.set(note);
      },
      error: () => {
        if (seq !== this.deepLinkNoteSeq) return;
        this.toast.error('Could not open that note');
      },
    });
  }

  private browseSeq = 0;
  private browseTimer: ReturnType<typeof setTimeout> | null = null;
  isBrowsingNotes = computed(() => this.viewMode() === 'notes');

  noteMoreOpenKey = signal<string | null>(null);
  private noteMoreTrigger: HTMLButtonElement | null = null;
  private reviewReturnState: ReviewReturnState | null = null;
  private reviewReturnNote: NoteSearchHit | null = null;
  private reviewStorageKey: string | null = null;
  private noteSearchTimer: ReturnType<typeof setTimeout> | null = null;
  private noteSearchSeq = 0;
  indexSort = signal<IndexSort>(this.readStoredSort());
  viewMode = signal<BrainPaneMode>(this.readStoredViewMode());
  /** The Brain's two panes. Same control as the Library's, different second option. */
  readonly viewToggleOptions = [
    { value: 'list', icon: 'list-bullets', label: 'Topic view' },
    { value: 'map', icon: 'map-trifold', label: 'Map view' },
  ] satisfies readonly ViewToggleOption[];
  cursorIndex = signal<number | null>(null);

  selectedId = signal<string | null>(null);
  selectedDetail = signal<TopicDetailDto | null>(null);
  loadingDetail = signal(false);

  /**
   * Rename, merge and delete: what is open, what was typed or picked, and the
   * requests. The Brain keeps what a result does to the index, the selection
   * and the detail cache.
   */
  readonly topicActions: BrainTopicActions = new BrainTopicActions(this.topicsService, this.toast, {
    topics: this.topics,
    selectedId: this.selectedId,
    selectedDetail: this.selectedDetail,
    indexSort: this.indexSort,
    renamed: (requestedId, survivor) => this.applyRenameResult(requestedId, survivor),
    merged: (request, survivor) => this.applyMergeResult(request, survivor),
    deleted: (target) => this.applyDeleteResult(target),
  });

  /**
   * The unlinked-note review queue. Entering and leaving review, and what the
   * Brain shows afterwards, stay here (openReview, closeReview).
   */
  readonly review: BrainNoteReview = new BrainNoteReview(this.notesService, this.toast, {
    topics: this.topics,
    indexSort: this.indexSort,
    isReviewing: () => this.isReviewing(),
    noteSaved: () => this.refreshIndexAndStats(),
    focusChanged: () => this.persistReviewReturnState(),
  });

  noteSearchQuery = signal('');
  sourceFilter = signal(ALL_SOURCES);
  noteSort = signal<NoteSort>('newest');

  relatedTopics = signal<RelatedTopicDto[]>([]);
  relatedLoading = signal(false);
  relatedExpanded = signal(false);
  relatedEvidenceId = signal<string | null>(null);

  // Deliberate Brain → Writing handoff (#492). Selection is opt-in and scoped
  // to the currently inspected evidence; canonical notes are never mutated.
  sourceSelectionMode = signal(false);
  selectedSourceNoteIds = signal<Set<string>>(new Set());
  handoffNoteIds = signal<string[]>([]);
  selectedSourceCount = computed(() => this.selectedSourceNoteIds().size);

  deleteTarget = signal<NoteContextDto | null>(null);
  deletingNote = signal(false);

  /**
   * Details already fetched, keyed by topic id.
   *
   * Cache/pending/version mechanics live in a component-scoped coordinator.
   * This component still decides when reads and invalidations happen and owns
   * all selection, loading, error and focus behaviour.
   */
  private readonly topicData = new TopicDataCache();

  @ViewChildren('indexRow') private indexRows!: QueryList<ElementRef<HTMLElement>>;
  @ViewChildren('noteCardHost', { read: ElementRef })
  private noteCardHosts!: QueryList<ElementRef<HTMLElement>>;

  // Computed Map for the Pipe to look up IDs efficiently
  topicMap = computed(() => {
    const map = new Map<string, TopicDto>();
    this.topics().forEach((c) => {
      map.set(c.name.trim().toLowerCase(), c);
    });
    return map;
  });

  // Computed filter + sort. Sorting is client-side because the index is already
  // fully in memory; the preference persists so the column comes back the way it
  // was left. Search ranking is deliberately separate from the active sort: an
  // exact match always leads, while each match group retains the chosen order.
  //
  // Name matching stays client-side and untouched — it is the only matcher that
  // also strips accents. Note-text matches arrive from the server (issue #158),
  // because the index payload carries no note text at all, and are merged after
  // the name matches: name matches lead, then content matches by match count. A
  // topic that matches both keeps its name position and gains the label.
  filteredTopics = computed(() => {
    const named = this.filterAndSortTopics(this.searchQuery(), null);
    const noteRows = this.noteMatches();
    if (!noteRows.length) return named;

    const noteById = new Map(noteRows.map((row) => [row.id, row]));
    const withLabels = named.map((topic) => {
      const hit = noteById.get(topic.id);
      return hit
        ? { ...topic, noteMatchCount: hit.noteMatchCount, noteMatchSnippet: hit.noteMatchSnippet }
        : topic;
    });

    const namedIds = new Set(named.map((topic) => topic.id));
    const contentOnly = noteRows
      .filter((row) => !namedIds.has(row.id))
      .sort(
        (a, b) =>
          (b.noteMatchCount ?? 0) - (a.noteMatchCount ?? 0) || a.name.localeCompare(b.name)
      );

    return [...withLabels, ...contentOnly];
  });

  /**
   * The search-hits section of the rail, and the ONLY thing that fills it.
   *
   * It used to be `notesSectionRows()`/`notesSectionHeading()`, which silently
   * switched between two unrelated collections: the server's matches for the
   * current query, and — whenever the query happened to be empty — the whole
   * unlinked-note list. That is what let a maintenance queue read as a permanent
   * second content type under the topic index (issue #256). A search now shows
   * its own matches and nothing shows unlinked notes except review mode.
   */
  noteSearchHits = computed(() => (this.searchQuery().trim() ? this.noteHits() : []));

  /** True while the rail is the review queue rather than the topic index. */
  isReviewing = computed(() => this.viewMode() === 'unlinked');
  reviewReturnMode: 'list' | 'notes' = 'list';

  showLetterSeparators = computed(
    () => this.indexSort() !== 'usage' && this.filteredTopics().length > 0
  );

  sourceOptions = computed<SourceOption[]>(() => {
    const counts = new Map<string, number>();
    for (const note of this.selectedDetail()?.notes ?? []) {
      const source = this.sourceName(note);
      counts.set(source, (counts.get(source) ?? 0) + 1);
    }

    return [...counts.entries()]
      .map(([value, count]) => ({ value, label: value, count }))
      .sort((a, b) => a.label.localeCompare(b.label));
  });

  readonly sourceDropdownOptions = computed<readonly DropdownOption[]>(() => [
    {
      value: ALL_SOURCES,
      label: `All sources (${this.selectedDetail()?.notes.length ?? 0})`,
    },
    ...this.sourceOptions().map((source) => ({
      value: source.value,
      label: `${source.label} (${source.count})`,
    })),
  ]);

  filteredNotes = computed(() => {
    const detail = this.selectedDetail();
    if (!detail) return [];

    const query = normalizeSearchText(this.noteSearchQuery().trim());
    const source = this.sourceFilter();
    const notes = detail.notes.filter((note) => {
      const matchesSource = source === ALL_SOURCES || this.sourceName(note) === source;
      if (!matchesSource) return false;
      if (!query) return true;

      return normalizeSearchText(
        [note.content, note.selectedText, note.bookTitle].filter(Boolean).join(' ')
      ).includes(query);
    });

    return notes.sort((a, b) => this.compareNotes(a, b));
  });

  noteFiltersActive = computed(
    () => this.sourceFilter() !== ALL_SOURCES || this.noteSearchQuery().trim().length > 0
  );

  visibleRelatedTopics = computed(() =>
    this.relatedExpanded() ? this.relatedTopics() : this.relatedTopics().slice(0, 8)
  );

  hiddenRelatedCount = computed(() => Math.max(0, this.relatedTopics().length - 8));

  relatedEvidenceNotes = computed(() => {
    const relatedId = this.relatedEvidenceId();
    const detail = this.selectedDetail();
    if (!relatedId || !detail) return [];

    const related = this.relatedTopics().find((candidate) => candidate.id === relatedId);
    const sharedIds = new Set(related?.sharedNoteIds ?? []);
    return detail.notes.filter((note) => sharedIds.has(note.noteId));
  });

  deleteHeading = computed(() => {
    const target = this.deleteTarget();
    return target ? `Delete this note from “${target.bookTitle}”?` : 'Delete note?';
  });

  private destroyRef = inject(DestroyRef);

  constructor() {
    const navigationSubscription = this.router.events.subscribe((event) => {
      if (
        event instanceof NavigationStart &&
        (event.url.split(/[?#]/)[0] !== this.router.url.split(/[?#]/)[0] ||
         !event.url.includes('/second-brain'))
      ) {
        this.clearReviewReturnState();
      }
    });

    const routeSubscription = this.route.queryParamMap.subscribe((params) => {
      const noteId = params.get('noteId');
      if (noteId) {
        if (noteId !== this.panelNote()?.id) this.openDeepLinkedNote(noteId);
        return;
      }

      // `conceptId` is the pre-rename name; keep old bookmarks and shared links working.
      const topicId = params.get('topicId') ?? params.get('conceptId');
      if (!topicId || topicId === this.selectedId()) return;

      // Links from notes and Book Detail land on the evidence, not merely on
      // the Brain route. List view is the surface that owns topic evidence.
      this.setViewMode('list');
      this.selectTopic(topicId);
    });

    const assistantActionSubscription = this.assistant.actionExecuted.subscribe((event) => {
      if (event.capability !== 'notes_link_existing_topic') return;
      const noteId = event.context.brainReviewNoteId;
      if (!noteId || !this.review.queue().some((note) => note.id === noteId)) return;

      this.refreshIndexAndStats();
      this.review.mutated = true;
      this.review.remove(noteId);
      this.toast.success('Note linked to a topic');
    });
    const assistantTurnSubscription = this.assistant.turnFinished.subscribe((event) =>
      this.proposals.handleTurnFinished(event));

    // A pending debounce and assistant receipt subscription must not outlive the surface.
    this.destroyRef.onDestroy(() => {
      if (this.noteSearchTimer !== null) clearTimeout(this.noteSearchTimer);
      if (this.browseTimer !== null) clearTimeout(this.browseTimer);
      this.unregisterAssistantContext();
      routeSubscription.unsubscribe();
      navigationSubscription.unsubscribe();
      assistantActionSubscription.unsubscribe();
      assistantTurnSubscription.unsubscribe();
    });

    // The assistant needs to know which note is in focus. The
    // provider is registered as `explicit` (it beats route-derived ambient
    // context) and re-registered whenever the focused note changes, then
    // removed when review ends or the surface is destroyed.
    effect(() => {
      const note = this.focusedNote();

      this.unregisterAssistantContext();
      if (!note) return;

      this.assistantContextUnregister = this.assistantContext.register(
        () => ({
          brainReviewNoteId: note.id,
          bookId: note.bookId,
          bookTitle: note.bookTitle ?? undefined,
          selectedText: note.selectedText ?? undefined,
        }),
        { explicit: true },
      );
    });

    effect(() => {
      this.proposals.noteFocusChanged(this.focusedNoteKey());
    });

    this.topicsService.list().subscribe({
      next: (data) => {
        this.topics.set(data);
        this.loadingTopics.set(false);
      },
      error: () => {
        this.loadingTopics.set(false);
        this.toast.error('Failed to load topics');
      },
    });

    this.topicsService.getStats().subscribe({
      next: (stats) => this.topicStats.set(stats),
      // Stats are editorial decoration. A failed request must not make the
      // index unavailable or produce a toast for an otherwise usable page.
      error: () => undefined,
    });

    this.initializeReviewSession();
    if (this.viewMode() === 'notes' && !this.reviewReturnState) this.loadBrowsePage();

    // Deliberately nothing else. The rail used to fetch the unlinked notes here,
    // on every visit, purely so it could render them as a second section under
    // the topic index (issue #256). They now load only when the user opens
    // review mode.
  }

  private readReviewReturnState(): ReviewReturnState | null {
    if (!this.reviewStorageKey) return null;

    try {
      const raw = sessionStorage.getItem(this.reviewStorageKey);
      if (!raw) return null;

      const value: unknown = JSON.parse(raw);
      if (!value || typeof value !== 'object') return null;

      const state = value as Partial<ReviewReturnState>;
      if (
        (state.returnMode !== 'list' && state.returnMode !== 'notes') ||
        typeof state.searchQuery !== 'string' ||
        typeof state.noteSearchQuery !== 'string' ||
        typeof state.sourceFilter !== 'string' ||
        !['newest', 'oldest', 'source'].includes(state.noteSort ?? '') ||
        typeof state.browseQuery !== 'string' ||
        typeof state.browseBookTitle !== 'string' ||
        typeof state.browseWithoutTopics !== 'boolean' ||
        typeof state.browseOldestFirst !== 'boolean' ||
        (state.selectedId != null && typeof state.selectedId !== 'string') ||
        (state.browseBookId != null && typeof state.browseBookId !== 'string') ||
        (state.focusedNoteId != null && typeof state.focusedNoteId !== 'string') ||
        (state.reviewId != null && typeof state.reviewId !== 'string')
      ) {
        sessionStorage.removeItem(this.reviewStorageKey);
        return null;
      }

      return state as ReviewReturnState;
    } catch {
      return null;
    }
  }

  private persistReviewReturnState(): void {
    if (!this.reviewStorageKey || !this.reviewReturnState) return;
    try {
      sessionStorage.setItem(
        this.reviewStorageKey,
        JSON.stringify({
          ...this.reviewReturnState,
          reviewId: this.review.focusId(),
        }),
      );
    } catch {
      // Storage unavailable: review remains functional within this visit.
    }
  }

  private clearReviewReturnState(): void {
    this.reviewReturnState = null;
    this.review.restoringFocusId.set(null);
    if (!this.reviewStorageKey) return;

    try {
      sessionStorage.removeItem(this.reviewStorageKey);
    } catch {
      // Storage unavailable.
    }
  }

  private initializeReviewSession(): void {
    this.capabilities.get().pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: (capabilities) => {
        if (capabilities.deploymentMode === 'SelfHosted') {
          this.setReviewIdentity('selfhosted');
          return;
        }

        if (capabilities.deploymentMode !== 'Cloud') return;

        this.cloudAuth.sessionChanges$.pipe(
          takeUntilDestroyed(this.destroyRef),
        ).subscribe((session) => {
          const identity = session.authenticated ? session.accountId : null;
          this.setReviewIdentity(identity ? `account:${identity}` : null);
        });

        this.cloudAuth.getSession().pipe(
          takeUntilDestroyed(this.destroyRef),
        ).subscribe({
          error: () => {
            // Do not infer a user or self-hosted context from an auth failure.
            this.setReviewIdentity(null);
          },
        });
      },
      error: () => {
        // Deployment identity is unknown: persistence stays disabled.
      },
    });
  }

  private setReviewIdentity(identity: string | null): void {
    const nextKey = identity
      ? `${REVIEW_STORAGE_PREFIX}:${encodeURIComponent(identity)}`
      : null;

    if (nextKey === this.reviewStorageKey) return;

    const previousKey = this.reviewStorageKey;
    this.reviewStorageKey = nextKey;

    if (previousKey) {
      try {
        sessionStorage.removeItem(previousKey);
      } catch {
        // Storage unavailable.
      }

      if (this.isReviewing()) {
        this.review.abandon();
        this.viewMode.set('list');
      }

      this.reviewReturnState = null;
      this.review.restoringFocusId.set(null);
    }

    if (!nextKey) return;

    // Never carry the previous account's active UI into a new account.
    // A fresh instance may restore only the authenticated account's own key.
    if (previousKey) return;

    // Explicit route query params (noteId, topicId, conceptId) take precedence
    // over any asynchronous restoration of stored review state.
    const currentParams = this.route.snapshot.queryParamMap;
    if (currentParams.has('noteId') || currentParams.has('topicId') || currentParams.has('conceptId')) {
      return;
    }

    if (this.isReviewing()) {
      this.persistReviewReturnState();
      return;
    }

    const saved = this.readReviewReturnState();
    if (!saved) return;

    this.reviewReturnState = saved;
    this.reviewReturnMode = saved.returnMode;
    this.review.resume(
      {
        query: saved.returnMode === 'notes' ? saved.browseQuery : '',
        bookId: saved.returnMode === 'notes' ? saved.browseBookId : null,
        oldestFirst: saved.returnMode === 'notes' && saved.browseOldestFirst,
      },
      saved.reviewId,
    );
    this.viewMode.set('unlinked');
    this.review.loadPage();
  }

  private unregisterAssistantContext(): void {
    if (!this.assistantContextUnregister) return;
    this.assistantContextUnregister();
    this.assistantContextUnregister = null;
  }

  setBrowseQuery(value: string): void {
    if (this.browseHasUnsavedEdit()) return;
    this.browseQuery.set(value);
    if (this.browseTimer !== null) clearTimeout(this.browseTimer);
    this.browseSeq++;
    this.browseTimer = setTimeout(() => {
      this.browseTimer = null;
      this.reloadBrowse();
    }, NOTE_SEARCH_DEBOUNCE_MS);
  }

  setBrowseWithoutTopics(value: boolean): void {
    if (this.browseHasUnsavedEdit()) return;
    this.browseWithoutTopics.set(value);
    this.reloadBrowse();
  }

  setBrowseBook(bookId: string | null, title = ''): void {
    if (this.browseHasUnsavedEdit()) return;
    this.browseBookId.set(bookId);
    this.browseBookTitle.set(bookId ? title : '');
    this.reloadBrowse();
  }

  setBrowseOldestFirst(value: boolean): void {
    if (this.browseHasUnsavedEdit()) return;
    this.browseOldestFirst.set(value);
    this.reloadBrowse();
  }

  private reloadBrowse(): void {
    this.browseSeq++;
    this.browseNotes.set([]);
    this.browseTotal.set(0);
    this.browseLoaded.set(false);
    this.panelNote.set(null);
    this.loadBrowsePage();
  }

  private browseHasUnsavedEdit(): boolean {
    if (!this.isBrowsingNotes()) return false;
    if (this.browseSaving() ||
      (this.browseEditing() && this.browseEditContent() !== this.panelNote()?.content)) {
      this.toast.error('Save or cancel the note edit before leaving it');
      return true;
    }
    return false;
  }

  startBrowseEdit(): void {
    const note = this.panelNote();
    if (!note) return;
    this.browsePickerOpen.set(false);
    this.browseEditContent.set(note.content);
    this.browseEditing.set(true);
  }

  cancelBrowseEdit(): void {
    if (this.browseSaving()) return;
    this.browseEditing.set(false);
  }

  saveBrowseEdit(): void {
    const note = this.panelNote();
    if (!note || this.browseSaving()) return;
    const content = this.browseEditContent();
    if (content === note.content) { this.cancelBrowseEdit(); return; }
    this.saveBrowseNote(note, content);
  }

  openBrowsePicker(): void {
    this.browsePickerQuery.set('');
    this.browsePickerId.set(null);
    this.browsePickerOpen.set(true);
  }

  linkBrowseTopic(): void {
    const note = this.panelNote();
    const topic = this.topics().find((item) => item.id === this.browsePickerId());
    if (!note || !topic || this.browseSaving()) return;
    const content = withTopicReference(note.content, topic.name);
    if (content === note.content) { this.browsePickerOpen.set(false); return; }
    this.saveBrowseNote(note, content);
  }

  createBrowseTopic(): void {
    const note = this.panelNote();
    const name = this.browseNewTopicName();
    if (!note || !name || this.browseSaving()) return;
    // The existing processor creates the topic from this same canonical link.
    this.saveBrowseNote(note, withTopicReference(note.content, name));
  }

  private saveBrowseNote(note: NoteSearchHit, content: string): void {
    this.browseSaving.set(true);
    this.notesService.update(note.id, { content, selectedText: note.selectedText ?? undefined }).subscribe({
      next: () => {
        const saved: NoteSearchHit = { ...note, content, topicNames: declaredTopicNames(content),
          snippet: note.selectedText || content.slice(0, 160) };
        this.browseNotes.update((items) => this.browseWithoutTopics() && saved.topicNames.length
          ? items.filter((item) => item.id !== note.id)
          : items.map((item) => item.id === note.id ? saved : item));
        if (this.browseWithoutTopics() && saved.topicNames.length) {
          this.browseTotal.update((total) => Math.max(0, total - 1));
        }
        if (this.panelNote()?.id === note.id) this.panelNote.set(saved);
        this.browseSaving.set(false);
        this.browseEditing.set(false);
        this.browsePickerOpen.set(false);
        this.refreshIndexAndStats();
        this.toast.success('Note saved');
      },
      error: () => {
        this.browseSaving.set(false);
        this.toast.error('Could not save the note');
      },
    });
  }

  loadBrowsePage(): void {
    if (this.browseLoading() && this.browseLoaded()) return;
    const seq = ++this.browseSeq;
    const offset = this.browseNotes().length;
    this.browseLoading.set(true);
    this.browseError.set(false);
    this.notesService.browse({
      query: this.browseQuery(),
      bookId: this.browseBookId() ?? undefined,
      withoutTopics: this.browseWithoutTopics(),
      oldestFirst: this.browseOldestFirst(),
      limit: REVIEW_PAGE_SIZE,
      offset,
    }).subscribe({
      next: (page) => {
        if (seq !== this.browseSeq) return;
        const held = new Set(this.browseNotes().map((note) => note.id));
        this.browseNotes.update((notes) => [
          ...notes,
          ...(page.items ?? []).filter((note) => !held.has(note.id)),
        ]);
        this.browseTotal.set(page.totalCount);
        this.browseLoading.set(false);
        this.browseLoaded.set(true);
      },
      error: () => {
        if (seq !== this.browseSeq) return;
        this.browseLoading.set(false);
        this.browseError.set(true);
      },
    });
  }

  private focusedNote(): NoteSearchHit | null {
    return this.isReviewing() ? this.review.note() : this.isBrowsingNotes() ? this.panelNote() : null;
  }

  private focusedNoteKey(): string | null {
    const note = this.focusedNote();
    return note ? JSON.stringify([note.id, note.content, note.selectedText, note.topicNames]) : null;
  }

  /**
   * The assistant linked the note in the browse inspector: show the link at
   * once instead of waiting for a reload. Review removes its own note when the
   * assistant reports the action.
   */
  private applyProposalLink(accepted: AssistantSuggestionDto): void {
    if (!this.isBrowsingNotes()) return;
    const note = this.panelNote();
    if (!note || note.id !== accepted.noteId) return;

    const content = withTopicReference(note.content, accepted.label);
    const saved = { ...note, content, topicNames: declaredTopicNames(content) };
    this.panelNote.set(saved);
    this.browseNotes.update((rows) => this.browseWithoutTopics()
      ? rows.filter((row) => row.id !== saved.id)
      : rows.map((row) => row.id === saved.id ? saved : row));
    if (this.browseWithoutTopics()) this.browseTotal.update((total) => Math.max(0, total - 1));
    this.refreshIndexAndStats();
    this.toast.success('Note linked to a topic');
  }

  private focusSuggestTopicsButton(): void {
    queueMicrotask(() => (this.host.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      this.isReviewing() ? '[data-testid="review-suggest-topics"]' : '[data-testid="browse-suggest-topics"]',
    )?.focus());
  }

  openAskNostosForNote(): void {
    this.assistant.open();
    this.assistant.updateDraft('I have a question about this note.');
  }

  /**
   * NoteCardComponent is shared with older surfaces and its icon buttons do
   * not all carry explicit labels. Label the buttons only in this surface,
   * after Angular has rendered or switched a card into edit mode, without
   * changing the shared component outside this phase's ownership boundary.
   */
  ngAfterViewChecked(): void {
    for (const host of this.noteCardHosts ?? []) {
      const actionButtons = host.nativeElement.querySelectorAll<HTMLButtonElement>(
        '.note-actions .icon-btn, .edit-actions .icon-btn'
      );
      const editButtons = host.nativeElement.querySelectorAll<HTMLButtonElement>(
        '.edit-actions .icon-btn'
      );

      actionButtons.forEach((button) => {
        if (button.closest('.edit-actions')) {
          const editIndex = Array.from(editButtons).indexOf(button);
          button.setAttribute('aria-label', editIndex === 0 ? 'Save note' : 'Cancel note edit');
        } else if (button.classList.contains('delete')) {
          button.setAttribute('aria-label', 'Delete note');
        } else if (button.getAttribute('title') === 'Jump to location') {
          button.setAttribute('aria-label', 'Jump to note location');
        } else {
          button.setAttribute('aria-label', 'Edit note');
        }
      });
    }
  }

  private filterAndSortTopics(queryText: string, excludedId: string | null): TopicDto[] {
    return rankTopics(this.topics(), queryText, excludedId, this.indexSort());
  }

  setSearchQuery(query: string): void {
    this.searchQuery.set(query);
    this.cursorIndex.set(null);
    this.scheduleNoteSearch(query);
  }

  /**
   * Server-side note-text search (issue #158). Debounced because it runs per
   * keystroke, and sequenced so a slow response cannot overwrite a newer query.
   */
  private scheduleNoteSearch(query: string): void {
    if (this.noteSearchTimer !== null) clearTimeout(this.noteSearchTimer);
    const term = query.trim();
    if (!term) {
      this.noteMatches.set([]);
      this.noteHits.set([]);
      this.noteSearchTimer = null;
      return;
    }
    this.noteSearchTimer = setTimeout(() => {
      this.noteSearchTimer = null;
      this.runNoteSearch(term);
    }, NOTE_SEARCH_DEBOUNCE_MS);
  }

  private runNoteSearch(term: string): void {
    const seq = ++this.noteSearchSeq;
    this.topicsService.searchNotes(term).subscribe({
      next: (rows) => {
        if (seq === this.noteSearchSeq) this.noteMatches.set(rows ?? []);
      },
      error: () => {
        // A failed content search must not take the index down with it: the name
        // matches are already on screen and stay there.
        if (seq === this.noteSearchSeq) this.noteMatches.set([]);
      },
    });
    this.notesService.search(term, 50).subscribe({
      next: (rows) => {
        if (seq === this.noteSearchSeq) this.noteHits.set(rows ?? []);
      },
      error: () => {
        if (seq === this.noteSearchSeq) this.noteHits.set([]);
      },
    });
  }

  /**
   * Enter the unlinked-note review task (issue #256).
   *
   * The mode is entered, not persisted, and the queue is fetched only here — so
   * an ordinary Brain visit never pays for it. The search is cleared on the way
   * in for the reason the mode toggle has always cleared it: the header search
   * box is hidden while reviewing, and a filter whose control is off screen is a
   * filter nobody can explain or clear. Search matches and the review queue are
   * different jobs either way, so a query must never appear to filter the queue.
   */
  openReview(): void {
    if (this.browseHasUnsavedEdit()) return;
    this.reviewReturnMode = this.isBrowsingNotes() ? 'notes' : 'list';
    this.reviewReturnNote = this.reviewReturnMode === 'notes' ? this.panelNote() : null;
    this.reviewReturnState = {
      returnMode: this.reviewReturnMode,
      searchQuery: this.searchQuery(),
      selectedId: this.selectedId(),
      noteSearchQuery: this.noteSearchQuery(),
      sourceFilter: this.sourceFilter(),
      noteSort: this.noteSort(),
      browseQuery: this.browseQuery(),
      browseBookId: this.browseBookId(),
      browseBookTitle: this.browseBookTitle(),
      browseWithoutTopics: this.browseWithoutTopics(),
      browseOldestFirst: this.browseOldestFirst(),
      focusedNoteId: this.panelNote()?.id ?? null,
      reviewId: null,
    };
    this.review.begin({
      query: this.reviewReturnMode === 'notes' ? this.browseQuery() : '',
      bookId: this.reviewReturnMode === 'notes' ? this.browseBookId() : null,
      oldestFirst: this.reviewReturnMode === 'notes' && this.browseOldestFirst(),
    });
    this.clearSourceSelectionState();
    this.closeWritingHandoff();
    this.clearSearch();
    this.closeNotePanel();
    this.viewMode.set('unlinked');
    this.persistReviewReturnState();
    this.review.loadPage();
  }

  /** Return to the previous Brain area, preserving an unchanged Notes list. */
  closeReview(): void {
    if (this.review.hasUnsavedEdit()) return;

    const state = this.reviewReturnState;
    const mode = state?.returnMode ?? this.reviewReturnMode;

    if (state && mode === 'notes') {
      this.browseQuery.set(state.browseQuery);
      this.browseBookId.set(state.browseBookId);
      this.browseBookTitle.set(state.browseBookTitle);
      this.browseWithoutTopics.set(state.browseWithoutTopics);
      this.browseOldestFirst.set(state.browseOldestFirst);
      if (this.review.mutated) {
        this.browseLoaded.set(false);
        this.browseNotes.set([]);
        this.browseTotal.set(0);
      }
    }

    if (this.review.mutated) {
      this.setViewMode(mode);
      if (mode === 'notes') {
        this.reloadBrowse();
      }
    } else {
      // Unchanged review: preserve current loaded Notes page and selected note without refetch
      this.viewMode.set(mode as BrainViewMode);
      try {
        localStorage.setItem(BRAIN_VIEW_MODE_STORAGE_KEY, mode);
      } catch {
        /* storage unavailable */
      }
      if (mode === 'notes') {
        if (this.reviewReturnNote) {
          this.panelNote.set(this.reviewReturnNote);
        }
        // After a full reload, review can restore its return filters and note
        // but the previous Notes page is no longer in memory. Re-fetch it so
        // returning from review does not show an empty list beside the restored
        // inspector.
        if (!this.browseLoaded() && !this.browseLoading()) this.loadBrowsePage();
      }
      this.clearReviewReturnState();
    }

    if (state) {
      this.setSearchQuery(state.searchQuery);

      if (mode === 'list' && state.selectedId) {
        const topicId = state.selectedId;
        this.topicsService.get(topicId).subscribe({
          next: () => {
            if (this.viewMode() !== 'list') return;
            this.selectTopic(topicId);
            this.noteSearchQuery.set(state.noteSearchQuery);
            this.sourceFilter.set(state.sourceFilter);
            this.noteSort.set(state.noteSort);
          },
          error: () => {
            this.clearSelection();
          },
        });
      }

      if (mode === 'notes' && state.focusedNoteId && !this.panelNote()) {
        const noteId = state.focusedNoteId;
        this.notesService.get(noteId).subscribe({
          next: (note) => {
            if (this.isBrowsingNotes() && !this.panelNote()) {
              this.panelNote.set(note);
            }
          },
          error: () => {},
        });
      }
    }

    this.clearReviewReturnState();
    this.reviewReturnNote = null;
  }

  openNotePanel(hit: NoteSearchHit): void {
    if (this.browseHasUnsavedEdit()) return;
    this.clearSourceSelectionState();
    this.closeWritingHandoff();
    this.panelNote.set(hit);
    this.browseEditing.set(false);
    this.browsePickerOpen.set(false);
  }

  captureRestored(saved: Note): void {
    this.review.mutated = true;
    // Apply the committed text immediately, then read canonical topic links.
    if (this.panelNote()?.id === saved.id) {
      this.panelNote.update((note) => note ? { ...note, content: saved.content,
        processingMode: saved.processingMode } : note);
    }
    this.review.queue.update((rows) => rows.map((note) => note.id === saved.id
      ? { ...note, content: saved.content, processingMode: saved.processingMode } : note));
    this.browseNotes.update((rows) => rows.map((note) => note.id === saved.id
      ? { ...note, content: saved.content, processingMode: saved.processingMode } : note));
    this.invalidateAllDetailEntries();
    if (this.selectedId()) this.selectTopic(this.selectedId()!);
    this.invalidateRelatedData(true);
    this.refreshIndexAndStats();
    this.notesService.get(saved.id).subscribe({
      next: (note) => {
        if (this.panelNote()?.id === saved.id) this.panelNote.set(note);
        this.review.queue.update((rows) => rows.map((row) => row.id === saved.id ? note : row));
        if (note.topicNames.length) this.review.remove(saved.id);
        if (this.browseLoaded() && !this.browseEditing()) {
          // Reconcile filtering/counts without closing the inspector the user
          // just restored (or a different note they selected meanwhile).
          const focused = this.panelNote();
          this.reloadBrowse();
          this.panelNote.set(focused);
        }
      },
      error: () => this.toast.error('Original restored, but note details could not be refreshed'),
    });
    this.toast.success('Original wording restored');
  }

  openSearchNoteInNotes(): void {
    const hit = this.panelNote();
    if (!hit || this.isBrowsingNotes()) return;
    const term = this.searchQuery();
    this.browseQuery.set(term);
    this.browseBookId.set(null);
    this.browseBookTitle.set('');
    const hadBrowsePage = this.browseLoaded();
    this.setViewMode('notes');
    if (hadBrowsePage) this.reloadBrowse();
    this.panelNote.set(hit);
  }

  closeNotePanel(): void {
    if (this.browseHasUnsavedEdit()) return;
    this.panelNote.set(null);
  }

  noteMoreKey(mode: string, noteId: string): string {
    return mode + ':' + noteId;
  }

  toggleNoteMore(key: string, event: MouseEvent): void {
    if (this.noteMoreOpenKey() === key) {
      this.noteMoreOpenKey.set(null);
      this.noteMoreTrigger = null;
      return;
    }

    this.noteMoreOpenKey.set(key);
    this.noteMoreTrigger = event.currentTarget as HTMLButtonElement;
  }

  handleNoteMoreKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.closeNoteMore(true);
      return;
    }

    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;

    const menu = event.currentTarget as HTMLElement;
    const items = Array.from(menu.querySelectorAll<HTMLButtonElement>('[data-note-more-item]'));
    if (!items.length) return;

    const index = items.indexOf(document.activeElement as HTMLButtonElement);
    const direction = event.key === 'ArrowDown' ? 1 : -1;
    const next = index < 0
      ? (direction > 0 ? 0 : items.length - 1)
      : (index + direction + items.length) % items.length;
    event.preventDefault();
    items[next].focus();
  }

  private closeNoteMore(restoreFocus: boolean): void {
    const trigger = this.noteMoreTrigger;
    this.noteMoreOpenKey.set(null);
    this.noteMoreTrigger = null;
    if (restoreFocus) queueMicrotask(() => trigger?.focus());
  }

  performNoteMoreAction(
    action: 'edit' | 'show-book' | 'delete',
    mode: string,
    note: NoteSearchHit | Note,
    card: NoteCardComponent | null,
    event: MouseEvent
  ): void {
    this.closeNoteMore(false);

    if (action === 'edit') {
      if (mode === 'review') this.review.startEdit();
      else if (mode === 'browse') this.startBrowseEdit();
      else card?.startEdit(event);
      return;
    }

    if (action === 'show-book' && mode === 'browse') {
      this.setBrowseBook(note.bookId, note.bookTitle || 'Unknown source');
      return;
    }

    if (action === 'delete' && mode === 'evidence') this.onDeleteNote(note.id);
  }

  /**
   * Selecting from the index. A content match is only useful if the notes that
   * matched are the ones on screen, so the note filter comes along with it.
   */
  selectIndexRow(topic: TopicDto): void {
    this.selectTopic(topic.id);
    if (topic.noteMatchCount) this.setNoteSearchQuery(this.searchQuery());
  }

  setNoteSearchQuery(query: string): void {
    this.noteSearchQuery.set(query);
  }

  clearNoteSearch(): void {
    this.setNoteSearchQuery('');
  }

  setSourceFilter(source: string): void {
    this.sourceFilter.set(source || ALL_SOURCES);
  }

  setNoteSort(sort: string): void {
    if (sort !== 'newest' && sort !== 'oldest' && sort !== 'source') return;
    this.noteSort.set(sort);
  }

  clearSearch(): void {
    this.setSearchQuery('');
  }

  handleSearchKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      if (this.searchQuery()) event.preventDefault();
      this.clearSearch();
      return;
    }

    if (event.key === 'ArrowDown' && this.filteredTopics().length > 0) {
      event.preventDefault();
      this.focusCursor(0);
    }
  }

  handleIndexFocus(index: number): void {
    this.cursorIndex.set(index);
  }

  handleIndexKeydown(event: KeyboardEvent, index: number, id: string): void {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      this.focusCursor(Math.min(index + 1, this.filteredTopics().length - 1));
      return;
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.focusCursor(Math.max(index - 1, 0));
      return;
    }

    if (event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') {
      event.preventDefault();
      this.selectTopicFromIndex(id);
    }
  }

  /** Keyboard selection shares the row's behaviour, content filter included. */
  private selectTopicFromIndex(id: string): void {
    const topic = this.filteredTopics().find((row) => row.id === id);
    if (topic) this.selectIndexRow(topic);
    else this.selectTopic(id);
  }

  private focusCursor(index: number): void {
    const rows = this.filteredTopics();
    if (rows.length === 0) return;

    const boundedIndex = Math.max(0, Math.min(index, rows.length - 1));
    this.cursorIndex.set(boundedIndex);
    // Arrow-key navigation should warm the same detail cache as pointer
    // hover/focus, before Enter commits the selection.
    this.prefetch(rows[boundedIndex].id);

    const row = this.indexRows?.get(boundedIndex)?.nativeElement;
    if (!row) return;
    row.focus();
    row.scrollIntoView?.({ block: 'nearest' });
  }

  letterFor(topic: TopicDto): string {
    const firstLetter = topic.name.trim().charAt(0);
    return firstLetter ? firstLetter.toLocaleUpperCase() : '#';
  }

  isLetterStart(index: number): boolean {
    if (!this.showLetterSeparators()) return false;
    if (index === 0) return true;
    const rows = this.filteredTopics();
    return this.letterFor(rows[index]) !== this.letterFor(rows[index - 1]);
  }

  highlightName(name: string): NamePart[] {
    return highlightNameParts(name, this.searchQuery());
  }

  private applyRenameResult(requestedId: string, survivor: TopicDto): void {
    const selectedBefore = this.selectedId();
    const detailBefore = this.selectedDetail();
    const merged = survivor.id !== requestedId;

    this.invalidateRelatedData(!merged);
    if (!merged) {
      // Preserve an already-fetched detail for its in-place name update, but
      // prevent an older prefetch response from restoring the old name.
      this.topicData.advanceDetailVersion();
    }

    if (!merged) {
      this.topics.update((items) =>
        items.map((topic) => (topic.id === requestedId ? survivor : topic))
      );

      const cached = this.topicData.detail(requestedId);
      if (cached) this.commitDetail(requestedId, { ...cached, name: survivor.name });
      if (detailBefore?.id === requestedId) {
        this.selectedDetail.set({ ...detailBefore, name: survivor.name });
      }
      this.toast.success(`Renamed to ${survivor.name}`);
    } else {
      this.topics.update((items) =>
        items
          .filter((topic) => topic.id !== requestedId)
          .map((topic) => (topic.id === survivor.id ? survivor : topic))
      );
      this.invalidateDetailEntries(requestedId, survivor.id);

      if (selectedBefore === requestedId) {
        // Keep the existing pane painted while the surviving detail is
        // re-fetched. This is deliberately not a loading overlay or landing
        // state: the pane background does not change during the merge.
        if (detailBefore) {
          this.selectedDetail.set({
            ...detailBefore,
            id: survivor.id,
            name: survivor.name,
          });
        }
        this.selectTopic(survivor.id);
      }
      this.toast.success(`Merged into ${survivor.name}`);
    }

    this.refreshIndexAndStats();
  }

  private applyMergeResult(request: MergeRequest, survivor: TopicDto): void {
    const previousDetail = this.selectedDetail();

    this.topics.update((items) =>
      items
        .filter((topic) => topic.id !== request.sourceId)
        .map((topic) => (topic.id === survivor.id ? survivor : topic))
    );
    this.invalidateDetailEntries(request.sourceId, request.targetId);
    this.invalidateRelatedData(false);

    if (this.selectedId() === request.sourceId) {
      if (previousDetail) {
        this.selectedDetail.set({
          ...previousDetail,
          id: survivor.id,
          name: survivor.name,
        });
      }
      this.selectTopic(survivor.id);
    }

    this.toast.success(`Merged ${request.sourceName} into ${survivor.name}`);
    this.refreshIndexAndStats();
  }

  private applyDeleteResult(target: TopicDto): void {
    this.topics.update((items) => items.filter((topic) => topic.id !== target.id));
    this.invalidateDetailEntries(target.id);
    this.invalidateRelatedData(false);
    if (this.selectedId() === target.id) {
      this.clearSelection();
    }
    this.toast.success(`Deleted ${target.name}`);
    this.refreshIndexAndStats();
  }

  requestRename(id: string, event: MouseEvent): void {
    event.stopPropagation();
    this.topicActions.startRename(id, 'index');
    this.renameRequested.emit(id);
  }

  requestDelete(id: string, event: MouseEvent): void {
    event.stopPropagation();
    this.topicActions.openDeleteTopic(id);
    this.deleteRequested.emit(id);
  }

  /**
   * Warm the detail cache without changing the selection. Called on hover/focus
   * of an index row so the click itself is a cache hit and swaps with no wait.
   */
  prefetch(id: string): void {
    if (this.topicData.detail(id) || this.topicData.isDetailPending(id)) return;
    this.prefetchRequest(id);
  }

  private prefetchRequest(id: string): void {
    const requestVersion = this.topicData.beginPendingDetail(id);
    this.topicsService.get(id).subscribe({
      next: (detail) => {
        if (!this.topicData.resolveDetail(id, requestVersion, detail, true)) return;
        if (this.selectedId() !== id) return;
        this.selectedDetail.set(detail);
        this.loadingDetail.set(false);
        this.loadRelated(id);
      },
      error: () => {
        if (!this.topicData.rejectDetail(id, requestVersion, true)) return;
        if (this.selectedId() === id) this.loadingDetail.set(false);
      },
    });
  }

  setSort(sort: string): void {
    if (!INDEX_SORTS.includes(sort as IndexSort)) return;
    this.indexSort.set(sort as IndexSort);
    try {
      localStorage.setItem(INDEX_SORT_STORAGE_KEY, sort);
    } catch {
      /* private mode / storage disabled — a non-persisted sort is acceptable */
    }
  }

  setViewMode(mode: string): void {
    if (!BRAIN_VIEW_MODES.includes(mode as BrainViewMode)) return;
    if (mode !== 'notes' && this.browseHasUnsavedEdit()) return;
    if (this.review.hasUnsavedEdit()) return;
    if (mode === 'notes' && this.viewMode() !== 'notes') this.clearSearch();
    if (this.isBrowsingNotes() && mode !== 'notes') this.panelNote.set(null);
    if (mode !== 'list') {
      this.clearSourceSelectionState();
      this.closeWritingHandoff();
    }
    // Deliberately does NOT clear the search any more.
    //
    // It used to, because the search lived in the index rail and map view closes
    // that rail: a query left over from the list would shrink the graph with the
    // control that caused it off screen. The search now lives in the persistent
    // header, which stays visible in BOTH modes — so the filter is always
    // visible, always explainable, and always clearable, and the query simply
    // carries across the toggle the way a persistent filter should.
    this.viewMode.set(mode as BrainViewMode);
    if (mode !== 'unlinked') {
      this.clearReviewReturnState();
    }
    if (mode === 'notes' && !this.browseLoaded() && !this.browseLoading()) this.loadBrowsePage();
    try {
      localStorage.setItem(BRAIN_VIEW_MODE_STORAGE_KEY, mode);
    } catch {
      /* private mode / storage disabled — a non-persisted view is acceptable */
    }
  }

  /**
   * Selecting a node on the map.
   *
   * A single click must NOT navigate away from the graph — that would hide the
   * map the moment you used it, which is the opposite of a whole-brain view.
   * Selection highlights the node (and its index row); the detail is still
   * fetched so the cache is warm. Opening the notes is an explicit action: a
   * double-click on the node (`openTopicFromMap`), or the rail's "Read notes".
   */
  onMapTopicSelected(id: string): void {
    this.selectTopic(id);
  }

  /**
   * Open a topic from the map: double-click, or the rail's "Read notes".
   *
   * Always switches to list view, because the topic's notes ARE the detail
   * pane — there is nowhere to show them while the map owns the screen.
   */
  openTopicFromMap(id: string): void {
    this.selectTopic(id);
    this.setViewMode('list');
  }

  /** Leave the map to read the selected topic's notes (the rail action). */
  openSelectedTopic(): void {
    if (!this.selectedId()) return;
    this.setViewMode('list');
  }

  /** True when the map has a selection that can be opened. */
  canOpenSelectedTopic = computed(() => !!this.selectedId() && this.viewMode() === 'map');

  /** The selected topic's display name, for the map's selection bar. */
  selectedTopicName = computed(() => {
    const id = this.selectedId();
    if (!id || this.viewMode() !== 'map') return null;
    return this.topics().find((topic) => topic.id === id)?.name ?? null;
  });

  private readStoredSort(): IndexSort {
    try {
      const stored = localStorage.getItem(INDEX_SORT_STORAGE_KEY);
      return INDEX_SORTS.includes(stored as IndexSort) ? (stored as IndexSort) : 'usage';
    } catch {
      return 'usage';
    }
  }

  private readStoredViewMode(): BrainViewMode {
    try {
      const stored = localStorage.getItem(BRAIN_VIEW_MODE_STORAGE_KEY);
      return BRAIN_VIEW_MODES.includes(stored as BrainViewMode)
        ? (stored as BrainViewMode)
        : 'list';
    } catch {
      return 'list';
    }
  }

  private sourceName(note: NoteContextDto): string {
    return note.bookTitle?.trim() || 'Unknown source';
  }

  private compareNotes(a: NoteContextDto, b: NoteContextDto): number {
    if (this.noteSort() === 'source') return this.compareBySource(a, b);

    const aTime = this.noteTime(a);
    const bTime = this.noteTime(b);
    // Some local fixtures predate createdAt. Keep their source ordering stable
    // instead of inventing timestamps that would make the sort misleading.
    if (aTime === null || bTime === null) return this.compareBySource(a, b);

    const difference = aTime - bTime;
    if (difference !== 0) return this.noteSort() === 'newest' ? -difference : difference;
    return a.noteId.localeCompare(b.noteId);
  }

  private compareBySource(a: NoteContextDto, b: NoteContextDto): number {
    return this.sourceName(a).localeCompare(this.sourceName(b)) || a.noteId.localeCompare(b.noteId);
  }

  private noteTime(note: NoteContextDto): number | null {
    if (!note.createdAt) return null;
    const timestamp = Date.parse(note.createdAt);
    return Number.isNaN(timestamp) ? null : timestamp;
  }

  selectTopic(id: string): void {
    if (id !== this.selectedId()) {
      this.clearSourceSelectionState();
      this.closeWritingHandoff();
    }
    this.selectedId.set(id);
    this.relatedEvidenceId.set(null);
    this.noteSearchQuery.set('');
    this.sourceFilter.set(ALL_SOURCES);
    this.noteSort.set('newest');
    this.relatedExpanded.set(false);
    this.loadRelated(id);

    const cached = this.topicData.detail(id);
    if (cached) {
      // Instant, animation-free swap: same background, no loading surface, no
      // blur — the pane simply shows the topic that was asked for.
      this.selectedDetail.set(cached);
      this.loadingDetail.set(false);
      return;
    }

    if (this.topicData.isDetailPending(id)) {
      // A prefetch is already in flight for exactly this topic; let it land
      // and set the detail when it lands, so we do not start a duplicate
      // request or dim content twice.
      this.loadingDetail.set(true);
      return;
    } else {
      this.loadingDetail.set(true);
    }

    // Direct selection historically did not participate in pending-request
    // dedupe; preserve that timing/coordination contract here.
    const requestVersion = this.topicData.detailVersion();
    this.topicsService.get(id).subscribe({
      next: (detail) => {
        if (!this.topicData.resolveDetail(id, requestVersion, detail, false)) return;
        // Ignore a response for a topic the user has already moved on from.
        if (this.selectedId() !== id) return;
        this.selectedDetail.set(detail);
        this.loadingDetail.set(false);
      },
      error: () => {
        if (!this.topicData.rejectDetail(id, requestVersion, false)) return;
        if (this.selectedId() !== id) return;
        this.toast.error('Failed to load topic details');
        this.loadingDetail.set(false);
      },
    });
  }

  // Clears selection to return to Index on mobile
  clearSelection(): void {
    this.clearSourceSelectionState();
    this.closeWritingHandoff();
    this.selectedId.set(null);
    this.relatedEvidenceId.set(null);
    this.loadingDetail.set(false);
    this.selectedDetail.set(null);
    this.relatedTopics.set([]);
  }

  startSourceSelection(): void {
    this.sourceSelectionMode.set(true);
    this.selectedSourceNoteIds.set(new Set());
  }

  cancelSourceSelection(): void {
    this.clearSourceSelectionState();
    this.closeWritingHandoff();
  }

  setSourceSelected(noteId: string, selected: boolean): void {
    if (!this.sourceSelectionMode()) return;
    this.selectedSourceNoteIds.update((current) => {
      const next = new Set(current);
      if (selected) next.add(noteId);
      else next.delete(noteId);
      return next;
    });
  }

  isSourceSelected(noteId: string): boolean {
    return this.selectedSourceNoteIds().has(noteId);
  }

  openSelectedSourcesHandoff(): void {
    this.openWritingHandoff([...this.selectedSourceNoteIds()]);
  }

  openWritingHandoff(noteIds: readonly string[]): void {
    const unique = [...new Set(noteIds.filter((noteId) => !!noteId))];
    if (!unique.length) return;
    this.handoffNoteIds.set(unique);
  }

  closeWritingHandoff(): void {
    this.handoffNoteIds.set([]);
  }

  handleWritingHandoffCompleted(result: BrainWritingHandoffResult): void {
    if (result.failedNoteIds.length) {
      const failed = [...new Set(result.failedNoteIds)];
      this.handoffNoteIds.set(failed);
      if (this.sourceSelectionMode()) {
        this.selectedSourceNoteIds.set(new Set(failed));
      }
      return;
    }

    this.closeWritingHandoff();
    this.clearSourceSelectionState();
  }

  private clearSourceSelectionState(): void {
    this.sourceSelectionMode.set(false);
    this.selectedSourceNoteIds.set(new Set());
  }

  /**
   * Cards stay in one grid track. The shared card clamps long bodies and gives
   * them a Show more disclosure, so one long note cannot create an empty half
   * row or make sibling cards in that row adopt a different rhythm.
   */
  shouldCardSpanTwoColumns(_note: NoteContextDto): boolean {
    return false;
  }

  noteForCard(note: NoteContextDto): Note {
    return {
      id: note.noteId,
      bookId: note.bookId,
      content: note.content,
      cfiRange: note.cfiRange,
      selectedText: note.selectedText,
      createdAt: note.createdAt ?? '',
      bookTitle: note.bookTitle,
      sourceAnchorKind: note.sourceAnchorKind,
      sourceAnchorValue: note.sourceAnchorValue,
      anchorVerified: note.anchorVerified,
      highlightColour: note.highlightColour,
    };
  }

  /**
   * Notes opened from the Notes index and notes shown as topic evidence are the
   * same saved object. Feed both through the shared NoteCard presentation so a
   * quotation/reflection does not acquire a second Brain-only visual language.
   */
  noteSearchHitForCard(note: NoteSearchHit): Note {
    return {
      id: note.id,
      bookId: note.bookId,
      content: note.content,
      cfiRange: note.cfiRange ?? undefined,
      selectedText: note.selectedText ?? undefined,
      createdAt: note.createdAt,
      bookTitle: note.bookTitle ?? undefined,
      sourceAnchorKind: note.sourceAnchorKind,
      sourceAnchorValue: note.sourceAnchorValue,
      anchorVerified: note.anchorVerified,
    };
  }

  private loadRelated(id: string): void {
    const cached = this.topicData.related(id);
    if (cached) {
      if (this.selectedId() === id) {
        this.relatedTopics.set(cached);
        this.relatedLoading.set(false);
      }
      return;
    }
    if (this.topicData.isRelatedPending(id)) {
      if (this.selectedId() === id) this.relatedLoading.set(true);
      return;
    }

    if (this.selectedId() === id) this.relatedLoading.set(true);
    const requestVersion = this.topicData.beginRelated(id);
    this.topicsService.getRelated(id).subscribe({
      next: (related) => {
        if (!this.topicData.resolveRelated(id, requestVersion, related)) return;
        if (this.selectedId() !== id) return;
        this.relatedTopics.set(related);
        this.relatedLoading.set(false);
      },
      error: () => {
        if (!this.topicData.rejectRelated(id, requestVersion)) return;
        if (this.selectedId() !== id) return;
        this.relatedTopics.set([]);
        this.relatedLoading.set(false);
      },
    });
  }

  private invalidateRelatedData(reloadSelected: boolean): void {
    this.topicData.invalidateRelatedData();
    this.relatedTopics.set([]);
    this.relatedExpanded.set(false);
    this.relatedEvidenceId.set(null);
    this.relatedLoading.set(false);

    const selectedId = this.selectedId();
    if (reloadSelected && selectedId) this.loadRelated(selectedId);
  }

  private invalidateDetailEntries(...ids: string[]): void {
    this.topicData.invalidateDetailEntries(...ids);
  }

  private invalidateAllDetailEntries(): void {
    this.topicData.invalidateAllDetailEntries();
  }

  private refreshIndexAndStats(): void {
    // Mutation responses update the visible row immediately. These background
    // reads reconcile counts and cover server-side deduplication after a merge,
    // without toggling the list's wait field or covering the detail pane.
    this.topicsService.list().subscribe({
      next: (data) => this.topics.set(data),
      error: () => this.toast.error('The topic index could not be refreshed'),
    });
    this.topicsService.getStats().subscribe({
      next: (stats) => this.topicStats.set(stats),
      error: () => this.toast.error('The topic statistics could not be refreshed'),
    });
  }

  toggleRelated(): void {
    this.relatedExpanded.update((expanded) => !expanded);
  }

  toggleRelatedEvidence(id: string): void {
    this.relatedEvidenceId.update((current) => (current === id ? null : id));
  }

  onUpdateNote(event: {
    id: string;
    content: string;
    selectedText?: string;
    highlightColour?: string;
  }): void {
    const topicId = this.selectedId();
    const detail = this.selectedDetail();
    const original = detail?.notes.find((note) => note.noteId === event.id);
    if (!topicId || !detail || !original) return;

    const previousDetail = detail;
    const optimisticDetail: TopicDetailDto = {
      ...detail,
      notes: detail.notes.map((note) =>
        note.noteId === event.id
          ? {
              ...note,
              content: event.content,
              selectedText: event.selectedText,
              highlightColour: event.highlightColour ?? note.highlightColour,
            }
          : note
      ),
    };
    this.commitDetail(topicId, optimisticDetail);

    this.notesService
      .update(event.id, {
        content: event.content,
        selectedText: event.selectedText,
        highlightColour: event.highlightColour,
      })
      .subscribe({
        next: (updated) => {
          const current = this.topicData.detail(topicId);
          if (current) {
            this.commitDetail(topicId, {
              ...current,
              notes: current.notes.map((note) =>
                note.noteId === event.id ? this.contextFromNote(updated, note) : note
              ),
            });
          }

          // Updating note text re-processes every [[Topic]] link on the
          // server. A note can therefore leave one topic, join another, or
          // change the aggregate reference count without changing its own id.
          // Keep the optimistic card in place, but discard every potentially
          // stale topic detail and related graph before reloading the active
          // pane. The refresh is deliberately in-place: the pane background
          // does not change, so it must not show a loading surface or arrival
          // animation while the canonical membership lands.
          this.invalidateAllDetailEntries();
          this.invalidateRelatedData(true);
          this.reloadDetailInPlace(topicId);
          this.refreshIndexAndStats();
          this.toast.success('Note updated');
        },
        error: () => {
          this.commitDetail(topicId, previousDetail);
          this.toast.error('Failed to update note — changes reverted');
        },
      });
  }

  onDeleteNote(noteId: string): void {
    if (this.deletingNote()) return;
    const note = this.selectedDetail()?.notes.find((candidate) => candidate.noteId === noteId);
    if (note) this.deleteTarget.set(note);
  }

  cancelDeleteNote(): void {
    if (!this.deletingNote()) this.deleteTarget.set(null);
  }

  confirmDeleteNote(): void {
    const target = this.deleteTarget();
    const topicId = this.selectedId();
    const detail = this.selectedDetail();
    if (!target || !topicId || !detail || this.deletingNote()) return;

    const previousDetail = detail;
    this.deletingNote.set(true);
    this.commitDetail(topicId, {
      ...detail,
      notes: detail.notes.filter((note) => note.noteId !== target.noteId),
    });

    this.notesService.delete(target.noteId).subscribe({
      next: () => {
        this.adjustTopicUsage(topicId, -1);
        // Deleting a note removes all of its NoteTopic links, not just the
        // link for the topic currently open. The optimistic card/count above
        // makes the active pane immediate; these cache invalidations and
        // background reads reconcile every topic row and aggregate stat.
        this.invalidateAllDetailEntries();
        this.invalidateRelatedData(true);
        this.refreshIndexAndStats();
        this.deletingNote.set(false);
        this.deleteTarget.set(null);
        this.toast.success('Note deleted');
      },
      error: () => {
        this.commitDetail(topicId, previousDetail);
        this.deletingNote.set(false);
        this.deleteTarget.set(null);
        this.toast.error('Failed to delete note — note restored');
      },
    });
  }

  private commitDetail(topicId: string, detail: TopicDetailDto): void {
    this.topicData.commitDetail(topicId, detail);
    if (this.selectedId() === topicId) this.selectedDetail.set(detail);
  }

  private reloadDetailInPlace(id: string): void {
    this.loadingDetail.set(true);
    const requestVersion = this.topicData.beginPendingDetail(id);
    this.topicsService.get(id).subscribe({
      next: (detail) => {
        if (!this.topicData.resolveDetail(id, requestVersion, detail, true)) return;
        if (this.selectedId() !== id) return;
        this.selectedDetail.set(detail);
        this.loadingDetail.set(false);
        this.loadRelated(id);
      },
      error: () => {
        if (!this.topicData.rejectDetail(id, requestVersion, true)) return;
        if (this.selectedId() !== id) return;
        this.loadingDetail.set(false);
        this.toast.error('Note saved, but this topic could not be refreshed');
      },
    });
  }

  private contextFromNote(updated: Note, previous: NoteContextDto): NoteContextDto {
    return {
      noteId: updated.id,
      content: updated.content,
      selectedText: updated.selectedText,
      cfiRange: updated.cfiRange ?? previous.cfiRange,
      bookId: updated.bookId ?? previous.bookId,
      bookTitle: updated.bookTitle ?? previous.bookTitle,
      createdAt: updated.createdAt || previous.createdAt,
      highlightColour: updated.highlightColour ?? previous.highlightColour,
    };
  }

  private adjustTopicUsage(topicId: string, delta: number): void {
    this.topics.update((items) =>
      items.map((topic) =>
        topic.id === topicId
          ? { ...topic, usageCount: Math.max(0, topic.usageCount + delta) }
          : topic
      )
    );
    this.topicStats.update((stats) =>
      stats
        ? { ...stats, totalReferences: Math.max(0, stats.totalReferences + delta) }
        : stats
    );
  }

  // Handler for clicking topics inside the text
  handleContentClick(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    // The pipe adds the 'topic-tag' class and 'data-topic-id' attribute
    const topicTag = target.closest('.topic-tag');
    if (topicTag) {
      // Angular's innerHTML sanitizer may remove the data attribute in some
      // browser/test DOMs. The rendered label is still the canonical topic
      // name, so use it as a safe fallback while retaining the fast id path.
      const topicId =
        topicTag.getAttribute('data-topic-id') ??
        this.topics().find(
          (topic) => topic.name.trim().toLowerCase() === topicTag.textContent?.trim().toLowerCase()
        )?.id;
      if (topicId) {
        event.preventDefault();
        event.stopPropagation();
        if (this.isBrowsingNotes()) this.setViewMode('list');
        this.selectTopic(topicId);
      }
    }
  }
}
