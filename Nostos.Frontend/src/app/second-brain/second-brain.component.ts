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
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { ToastService } from '../core/services/toast.service';
import { NotesService } from '../core/services/notes.service';
import { Note, NoteSearchHit } from '../core/dtos/note.dtos';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { NoteCardComponent } from '../ui/note-card.component/note-card.component';
import { NoteFormatPipe } from '../ui/pipes/note-format.pipe';
import {
  ConceptsService,
  ConceptDto,
  ConceptDetailDto,
  NoteContextDto,
  ConceptStatsDto,
  RelatedConceptDto,
} from '../core/services/concepts.service';
import { ConceptMapComponent } from './concept-map/concept-map.component';
import { ConceptInputComponent } from '../ui/concept-input.component/concept-input.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
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

import {
  ALL_SOURCES,
  BRAIN_VIEW_MODES,
  BRAIN_VIEW_MODE_STORAGE_KEY,
  INDEX_SORTS,
  INDEX_SORT_STORAGE_KEY,
  NOTE_SEARCH_DEBOUNCE_MS,
  REVIEW_PAGE_SIZE,
  declaresConcept,
  declaredConceptNames,
  normalizeSearchText,
  searchRank,
  type BrainPaneMode,
  type BrainViewMode,
  type IndexSort,
  type MergeRequest,
  type NamePart,
  type NoteSort,
  type RenameSurface,
  type SourceOption,
} from './second-brain.helpers';

@Component({
  standalone: true,
  selector: 'app-brain',
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    NostosIconComponent,
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
    ConceptMapComponent,
    ConceptInputComponent,
    BrainWritingHandoffComponent,
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
  private conceptsService = inject(ConceptsService);
  private http = inject(HttpClient);
  private notesService = inject(NotesService);
  private toast = inject(ToastService);
  private readonly assistantContext = inject(AssistantContextService);
  private readonly assistant = inject(AssistantService);
  private readonly route = inject(ActivatedRoute);
  private readonly host = inject(ElementRef<HTMLElement>);

  /** The focused note context provider for browsing or optional review. */
  private assistantContextUnregister: (() => void) | null = null;

  proposalState = signal<'idle' | 'loading' | 'ready' | 'empty' | 'unavailable' | 'error' | 'linking'>('idle');
  proposalCandidates = signal<AssistantSuggestionDto[]>([]);
  proposalMessage = signal<string | null>(null);
  proposalDetail = signal<ConceptDetailDto | null>(null);
  proposalInspectingId = signal<string | null>(null);
  private proposalNoteKey: string | null = null;
  private proposalNoteId: string | null = null;
  private proposalTurnId: string | null = null;
  private proposalLinkTurnId: string | null = null;
  private proposalAccepted: AssistantSuggestionDto | null = null;
  private ignoredProposalTurns = new Set<string>();

  // Phase 5 consumes these outputs to open the rename and confirmation flows.
  readonly renameRequested = output<string>();
  readonly deleteRequested = output<string>();

  // Concept management state. Rename stays in the surface that initiated it;
  // the confirmation state is separate from note deletion because the latter
  // has a different consequence and tone.
  renameId = signal<string | null>(null);
  renameSurface = signal<RenameSurface>('index');
  renameValue = signal('');
  renameError = signal<string | null>(null);
  renaming = signal(false);

  mergePickerOpen = signal(false);
  mergeSearchQuery = signal('');
  mergeTargetId = signal<string | null>(null);
  mergeConfirmation = signal<MergeRequest | null>(null);
  mergingConcept = signal(false);

  conceptDeleteTarget = signal<ConceptDto | null>(null);
  deletingConcept = signal(false);

  // State
  concepts = signal<ConceptDto[]>([]);
  conceptStats = signal<ConceptStatsDto | null>(null);
  loadingConcepts = signal(true);
  searchQuery = signal('');

  // Note-text matches for the current query, from the server (issue #158). Empty
  // until a search runs, and cleared when the query is.
  noteMatches = signal<ConceptDto[]>([]);
  noteHits = signal<NoteSearchHit[]>([]);
  panelNote = signal<NoteSearchHit | null>(null);

  // Notes are a first-class way into Brain. The server returns bounded pages;
  // review remains a deliberate, non-persisted subtask of this view.
  browseNotes = signal<NoteSearchHit[]>([]);
  browseTotal = signal(0);
  browseQuery = signal('');
  browseBookId = signal<string | null>(null);
  browseBookTitle = signal('');
  browseWithoutConcepts = signal(false);
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
  browsePickerCandidates = computed(() => this.filterAndSortConcepts(this.browsePickerQuery(), null));
  browseNewConceptName = computed(() => {
    const name = this.browsePickerQuery().trim();
    if (!name || name.length > 100 || /[\[\]\r\n]/.test(name)) return null;
    return this.concepts().some((item) => item.name.toLowerCase() === name.toLowerCase())
      ? null : name;
  });
  browseSourceParams(note: NoteSearchHit): { sourcePage: number } | { sourceCfi: string } | null {
    const anchor = note.anchorVerified ? note.sourceAnchorValue?.trim() : null;
    if (note.sourceAnchorKind?.toLowerCase() === 'pdf_page' && anchor) {
      const page = Number(anchor);
      if (Number.isInteger(page) && page > 0) return { sourcePage: page };
    }
    if (note.sourceAnchorKind?.toLowerCase() === 'epub_cfi' && anchor) {
      return { sourceCfi: anchor };
    }
    const cfi = note.cfiRange?.trim();
    return cfi?.startsWith('epubcfi(') ? { sourceCfi: cfi } : null;
  }

  openBrowseConcept(name: string): void {
    const concept = this.concepts().find(
      (item) => item.name.trim().toLowerCase() === name.trim().toLowerCase());
    if (!concept) return;
    this.setViewMode('list');
    this.selectConcept(concept.id);
  }
  private browseSeq = 0;
  private browseTimer: ReturnType<typeof setTimeout> | null = null;
  isBrowsingNotes = computed(() => this.viewMode() === 'notes');

  // --- Unlinked-note review (issue #256) -------------------------------------
  //
  // Deliberately empty until the user asks for it. Nothing here is loaded on a
  // normal Brain visit: unlinked notes are a review task, not the rail's second
  // content type, so opening the Brain must not fetch them at all.
  /** Notes fetched so far and not yet resolved, in the server's order. */
  reviewQueue = signal<NoteSearchHit[]>([]);
  /** How many are still waiting, including rows not fetched yet. */
  reviewTotal = signal(0);
  reviewLoading = signal(false);
  reviewLoaded = signal(false);
  reviewError = signal(false);
  /** The note under review, chosen explicitly. `null` focuses the first row. */
  reviewId = signal<string | null>(null);
  reviewSaving = signal(false);
  reviewEditing = signal(false);
  reviewEditContent = signal('');
  reviewPickerOpen = signal(false);
  reviewPickerQuery = signal('');
  reviewPickerConceptId = signal<string | null>(null);
  reviewNewConceptName = computed(() => {
    const name = this.reviewPickerQuery().trim();
    if (!name || name.length > 100 || /[\[\]\r\n]/.test(name)) return null;
    return this.concepts().some((concept) => concept.name.toLowerCase() === name.toLowerCase())
      ? null : name;
  });
  private reviewSeq = 0;
  private reviewMutated = false;
  private reviewReturnNote: NoteSearchHit | null = null;
  private reviewBrowseQuery = '';
  private reviewBookId: string | null = null;
  private reviewOldestFirst = false;
  private noteSearchTimer: ReturnType<typeof setTimeout> | null = null;
  private noteSearchSeq = 0;
  indexSort = signal<IndexSort>(this.readStoredSort());
  viewMode = signal<BrainPaneMode>(this.readStoredViewMode());
  /** The Brain's two panes. Same control as the Library's, different second option. */
  readonly viewToggleOptions = [
    { value: 'list', icon: 'list-bullets', label: 'Concept view' },
    { value: 'map', icon: 'map-trifold', label: 'Map view' },
  ] satisfies readonly ViewToggleOption[];
  cursorIndex = signal<number | null>(null);

  selectedId = signal<string | null>(null);
  selectedDetail = signal<ConceptDetailDto | null>(null);
  loadingDetail = signal(false);

  noteSearchQuery = signal('');
  sourceFilter = signal(ALL_SOURCES);
  noteSort = signal<NoteSort>('newest');

  relatedConcepts = signal<RelatedConceptDto[]>([]);
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
   * Details already fetched, keyed by concept id.
   *
   * The wait-field that used to cover this pane is gone: the pane's background
   * is identical for every concept, so a full-cover loading surface only ever
   * read as a flash between two states that look the same. Instead the pane
   * keeps whatever it is already showing and swaps the content in place — which
   * means a concept we have seen before must not be re-fetched (that round trip
   * is the only thing that could still make the pane blink). Hover/focus
   * pre-warms this cache so even a first visit is usually instant.
   */
  private detailCache = new Map<string, ConceptDetailDto>();
  private pendingRequests = new Set<string>();
  private detailRequestVersion = 0;
  private relatedCache = new Map<string, RelatedConceptDto[]>();
  private pendingRelatedRequests = new Set<string>();
  private relatedRequestVersion = 0;

  @ViewChildren('indexRow') private indexRows!: QueryList<ElementRef<HTMLElement>>;
  @ViewChildren('noteCardHost', { read: ElementRef })
  private noteCardHosts!: QueryList<ElementRef<HTMLElement>>;

  // Computed Map for the Pipe to look up IDs efficiently
  conceptMap = computed(() => {
    const map = new Map<string, ConceptDto>();
    this.concepts().forEach((c) => {
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
  // concept that matches both keeps its name position and gains the label.
  filteredConcepts = computed(() => {
    const named = this.filterAndSortConcepts(this.searchQuery(), null);
    const noteRows = this.noteMatches();
    if (!noteRows.length) return named;

    const noteById = new Map(noteRows.map((row) => [row.id, row]));
    const withLabels = named.map((concept) => {
      const hit = noteById.get(concept.id);
      return hit
        ? { ...concept, noteMatchCount: hit.noteMatchCount, noteMatchSnippet: hit.noteMatchSnippet }
        : concept;
    });

    const namedIds = new Set(named.map((concept) => concept.id));
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
   * second content type under the concept index (issue #256). A search now shows
   * its own matches and nothing shows unlinked notes except review mode.
   */
  noteSearchHits = computed(() => (this.searchQuery().trim() ? this.noteHits() : []));

  /** True while the rail is the review queue rather than the concept index. */
  isReviewing = computed(() => this.viewMode() === 'unlinked');
  reviewReturnMode: 'list' | 'notes' = 'list';

  /** The note under review: the chosen one, or the head of the queue. */
  reviewNote = computed<NoteSearchHit | null>(() => {
    const queue = this.reviewQueue();
    if (!queue.length) return null;
    const id = this.reviewId();
    return (id ? queue.find((row) => row.id === id) : undefined) ?? queue[0];
  });

  /**
   * Whether the queue still holds rows this browser has not fetched. The local
   * queue is exactly the rows fetched so far that are still unresolved, so the
   * gap to the total IS the unfetched remainder.
   */
  reviewHasMore = computed(() => this.reviewQueue().length < this.reviewTotal());
  reviewCanGoBack = computed(() => this.reviewQueue().findIndex(
    (row) => row.id === this.reviewNote()?.id) > 0);
  reviewCanAdvance = computed(() => {
    const index = this.reviewQueue().findIndex((row) => row.id === this.reviewNote()?.id);
    return index >= 0 && (index < this.reviewQueue().length - 1 || this.reviewHasMore());
  });

  /** The concept the user picked to link the reviewed note to. */
  reviewPickerConcept = computed(() => {
    const id = this.reviewPickerConceptId();
    return id ? this.concepts().find((concept) => concept.id === id) ?? null : null;
  });

  reviewPickerCandidates = computed(() => this.filterAndSortConcepts(this.reviewPickerQuery(), null));

  mergeCandidates = computed(() =>
    this.filterAndSortConcepts(this.mergeSearchQuery(), this.selectedId())
  );

  mergeTarget = computed(() => {
    const targetId = this.mergeTargetId();
    return targetId ? this.concepts().find((concept) => concept.id === targetId) ?? null : null;
  });

  conceptDeleteHeading = computed(() => {
    const target = this.conceptDeleteTarget();
    return target ? `Delete “${target.name}”?` : 'Delete concept?';
  });

  conceptDeleteDescription = computed(() => {
    const target = this.conceptDeleteTarget();
    return target
      ? `Deleting this concept removes its note links, but does not edit note text. The [[${target.name}]] reference stays in notes, and saving a note again will re-create the concept.`
      : '';
  });

  mergeHeading = computed(() => {
    const request = this.mergeConfirmation();
    return request ? `Merge “${request.sourceName}” into “${request.targetName}”?` : 'Merge concepts?';
  });

  mergeDescription = computed(() => {
    const request = this.mergeConfirmation();
    if (!request) return '';
    const noteLabel = request.noteCount === 1 ? 'note' : 'notes';
    return `This will move ${request.noteCount} ${noteLabel} into “${request.targetName}” and the source concept “${request.sourceName}” will disappear.`;
  });

  showLetterSeparators = computed(
    () => this.indexSort() !== 'usage' && this.filteredConcepts().length > 0
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

  visibleRelatedConcepts = computed(() =>
    this.relatedExpanded() ? this.relatedConcepts() : this.relatedConcepts().slice(0, 8)
  );

  hiddenRelatedCount = computed(() => Math.max(0, this.relatedConcepts().length - 8));

  relatedEvidenceNotes = computed(() => {
    const relatedId = this.relatedEvidenceId();
    const detail = this.selectedDetail();
    if (!relatedId || !detail) return [];

    const related = this.relatedConcepts().find((candidate) => candidate.id === relatedId);
    const sharedIds = new Set(related?.sharedNoteIds ?? []);
    return detail.notes.filter((note) => sharedIds.has(note.noteId));
  });

  deleteHeading = computed(() => {
    const target = this.deleteTarget();
    return target ? `Delete this note from “${target.bookTitle}”?` : 'Delete note?';
  });

  private destroyRef = inject(DestroyRef);

  constructor() {
    const routeSubscription = this.route.queryParamMap.subscribe((params) => {
      const conceptId = params.get('conceptId');
      if (!conceptId || conceptId === this.selectedId()) return;

      // Links from notes and Book Detail land on the evidence, not merely on
      // the Brain route. List view is the surface that owns concept evidence.
      this.setViewMode('list');
      this.selectConcept(conceptId);
    });

    const assistantActionSubscription = this.assistant.actionExecuted.subscribe((event) => {
      if (event.capability !== 'notes_link_existing_concept') return;
      const noteId = event.context.brainReviewNoteId;
      if (!noteId || !this.reviewQueue().some((note) => note.id === noteId)) return;

      this.refreshIndexAndStats();
      this.reviewMutated = true;
      this.removeFromReview(noteId);
      this.toast.success('Note linked to a concept');
    });
    const assistantTurnSubscription = this.assistant.turnFinished.subscribe((event) => {
      if (this.ignoredProposalTurns.delete(event.turnId)) {
        this.assistant.dismissSuggestions(event.turnId);
        return;
      }
      if (event.turnId === this.proposalTurnId) {
        this.proposalTurnId = null;
        if (!this.proposalNoteKey || this.proposalNoteKey !== this.focusedNoteKey()) return;
        if (event.error) {
          this.proposalState.set(/offline|allowance|disabled|unavailable|configured/i.test(event.error)
            ? 'unavailable' : 'error');
          this.proposalMessage.set(event.error);
          return;
        }
        const noteId = this.focusedNote()?.id;
        const candidates = (event.response?.suggestions ?? []).filter((item) =>
          item.kind === 'concept' && item.noteId === noteId && item.value && item.reason?.trim()).slice(0, 3);
        this.proposalCandidates.set(candidates);
        this.proposalState.set(candidates.length ? 'ready' : 'empty');
        return;
      }
      if (event.turnId === this.proposalLinkTurnId) {
        this.proposalLinkTurnId = null;
        if (this.proposalNoteKey !== this.focusedNoteKey()) return;
        if (event.response?.executedCapabilities?.includes('notes_link_existing_concept')) {
          if (this.isBrowsingNotes() && this.proposalAccepted) {
            const note = this.panelNote();
            if (note && note.id === this.proposalAccepted.noteId) {
              const content = this.withConceptReference(note.content, this.proposalAccepted.label);
              const saved = { ...note, content, conceptNames: declaredConceptNames(content) };
              this.panelNote.set(saved);
              this.browseNotes.update((rows) => this.browseWithoutConcepts()
                ? rows.filter((row) => row.id !== saved.id)
                : rows.map((row) => row.id === saved.id ? saved : row));
              if (this.browseWithoutConcepts()) this.browseTotal.update((total) => Math.max(0, total - 1));
              this.refreshIndexAndStats();
              this.toast.success('Note linked to a concept');
            }
          }
          this.clearConceptProposals();
        } else {
          this.proposalState.set('error');
          this.proposalMessage.set(event.error ?? 'No link was made. You can retry or link manually.');
        }
      }
    });

    // A pending debounce and assistant receipt subscription must not outlive the surface.
    this.destroyRef.onDestroy(() => {
      if (this.noteSearchTimer !== null) clearTimeout(this.noteSearchTimer);
      if (this.browseTimer !== null) clearTimeout(this.browseTimer);
      this.unregisterAssistantContext();
      routeSubscription.unsubscribe();
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
      const key = this.focusedNoteKey();
      if (this.proposalNoteKey && this.proposalNoteKey !== key) this.clearConceptProposals();
    });

    this.conceptsService.list().subscribe({
      next: (data) => {
        this.concepts.set(data);
        this.loadingConcepts.set(false);
      },
      error: () => {
        this.loadingConcepts.set(false);
        this.toast.error('Failed to load concepts');
      },
    });

    this.conceptsService.getStats().subscribe({
      next: (stats) => this.conceptStats.set(stats),
      // Stats are editorial decoration. A failed request must not make the
      // index unavailable or produce a toast for an otherwise usable page.
      error: () => undefined,
    });

    if (this.viewMode() === 'notes') this.loadBrowsePage();

    // Deliberately nothing else. The rail used to fetch the unlinked notes here,
    // on every visit, purely so it could render them as a second section under
    // the concept index (issue #256). They now load only when the user opens
    // review mode.
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

  setBrowseWithoutConcepts(value: boolean): void {
    if (this.browseHasUnsavedEdit()) return;
    this.browseWithoutConcepts.set(value);
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

  private reviewHasUnsavedEdit(): boolean {
    if (!this.isReviewing()) return false;
    if (this.reviewSaving() ||
      (this.reviewEditing() && this.reviewEditContent() !== this.reviewNote()?.content)) {
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

  linkBrowseConcept(): void {
    const note = this.panelNote();
    const concept = this.concepts().find((item) => item.id === this.browsePickerId());
    if (!note || !concept || this.browseSaving()) return;
    const content = this.withConceptReference(note.content, concept.name);
    if (content === note.content) { this.browsePickerOpen.set(false); return; }
    this.saveBrowseNote(note, content);
  }

  createBrowseConcept(): void {
    const note = this.panelNote();
    const name = this.browseNewConceptName();
    if (!note || !name || this.browseSaving()) return;
    // The existing processor creates the concept from this same canonical link.
    this.saveBrowseNote(note, this.withConceptReference(note.content, name));
  }

  private saveBrowseNote(note: NoteSearchHit, content: string): void {
    this.browseSaving.set(true);
    this.notesService.update(note.id, { content, selectedText: note.selectedText ?? undefined }).subscribe({
      next: () => {
        const saved: NoteSearchHit = { ...note, content, conceptNames: declaredConceptNames(content),
          snippet: note.selectedText || content.slice(0, 160) };
        this.browseNotes.update((items) => this.browseWithoutConcepts() && saved.conceptNames.length
          ? items.filter((item) => item.id !== note.id)
          : items.map((item) => item.id === note.id ? saved : item));
        if (this.browseWithoutConcepts() && saved.conceptNames.length) {
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
      withoutConcepts: this.browseWithoutConcepts(),
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
    return this.isReviewing() ? this.reviewNote() : this.isBrowsingNotes() ? this.panelNote() : null;
  }

  private focusedNoteKey(): string | null {
    const note = this.focusedNote();
    return note ? JSON.stringify([note.id, note.content, note.selectedText, note.conceptNames]) : null;
  }

  /** Explicitly request the assistant's validated proposal artifact beside this note. */
  askNostos(): void {
    const note = this.focusedNote();
    if (!note || this.browseEditing() || this.reviewEditing()) return;
    this.clearConceptProposals();
    this.proposalNoteKey = this.focusedNoteKey();
    this.proposalNoteId = note.id;
    const turnId = this.assistant.requestConceptProposals(note.id);
    if (!turnId) {
      this.proposalState.set('unavailable');
      this.proposalMessage.set('Ask Nostos is busy or unavailable. You can still link a concept manually.');
      return;
    }
    this.proposalTurnId = turnId;
    this.proposalState.set('loading');
  }

  cancelConceptProposals(): void {
    this.clearConceptProposals();
    queueMicrotask(() => (this.host.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      this.isReviewing() ? '[data-testid="review-suggest-concepts"]' : '[data-testid="browse-suggest-concepts"]',
    )?.focus());
  }

  private clearConceptProposals(): void {
    if (this.proposalTurnId) {
      this.ignoredProposalTurns.add(this.proposalTurnId);
      if (this.assistant.activeTurnId() === this.proposalTurnId) this.assistant.stopActiveTurn();
    }
    this.proposalTurnId = null;
    this.proposalLinkTurnId = null;
    this.proposalAccepted = null;
    if (this.proposalNoteId) this.assistant.dismissSuggestionsForNote(this.proposalNoteId);
    this.proposalNoteId = null;
    this.proposalNoteKey = null;
    this.proposalCandidates.set([]);
    this.proposalDetail.set(null);
    this.proposalInspectingId.set(null);
    this.proposalMessage.set(null);
    this.proposalState.set('idle');
  }

  inspectConceptProposal(candidate: AssistantSuggestionDto): void {
    if (!candidate.value || this.proposalNoteKey !== this.focusedNoteKey()) return;
    if (this.proposalInspectingId() === candidate.value) {
      this.proposalInspectingId.set(null);
      this.proposalDetail.set(null);
      return;
    }
    this.proposalMessage.set(null);
    this.proposalDetail.set(null);
    this.proposalInspectingId.set(candidate.value);
    this.conceptsService.get(candidate.value).subscribe({
      next: (detail) => {
        if (this.proposalInspectingId() === detail.id && this.proposalNoteKey === this.focusedNoteKey())
          this.proposalDetail.set(detail);
      },
      error: () => {
        if (this.proposalInspectingId() === candidate.value) {
          this.proposalInspectingId.set(null);
          this.proposalMessage.set('Could not load this concept. Try again or choose another.');
        }
      },
    });
  }

  acceptConceptProposal(candidate: AssistantSuggestionDto): void {
    const note = this.focusedNote();
    if (!note || this.proposalState() !== 'ready' || this.proposalNoteKey !== this.focusedNoteKey()
      || candidate.noteId !== note.id || !candidate.value || this.assistant.sending()) return;
    this.proposalAccepted = candidate;
    const turnId = this.assistant.applySuggestion(candidate);
    if (!turnId) {
      this.proposalAccepted = null;
      this.proposalState.set('error');
      this.proposalMessage.set('Could not start the link. You can link manually or try again.');
      return;
    }
    this.proposalLinkTurnId = turnId;
    this.proposalState.set('linking');
  }

  canRetryProposalLink(): boolean {
    return !!this.proposalAccepted && this.proposalNoteKey === this.focusedNoteKey();
  }

  retryProposalLink(): void {
    if (!this.proposalAccepted || !this.canRetryProposalLink()) return;
    this.proposalState.set('ready');
    this.acceptConceptProposal(this.proposalAccepted);
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

  private compareForSort(a: ConceptDto, b: ConceptDto): number {
    switch (this.indexSort()) {
      case 'az':
        return a.name.localeCompare(b.name);
      case 'za':
        return b.name.localeCompare(a.name);
      default:
        return b.usageCount - a.usageCount || a.name.localeCompare(b.name);
    }
  }

  private filterAndSortConcepts(queryText: string, excludedId: string | null): ConceptDto[] {
    const query = normalizeSearchText(queryText.trim());
    const rows = this.concepts().filter(
      (concept) => concept.id !== excludedId && (!query || normalizeSearchText(concept.name).includes(query))
    );

    return rows.sort((a, b) => {
      if (query) {
        const rankDifference = searchRank(a.name, query) - searchRank(b.name, query);
        if (rankDifference !== 0) return rankDifference;
      }
      return this.compareForSort(a, b);
    });
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
    this.conceptsService.searchNotes(term).subscribe({
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
    this.reviewMutated = false;
    this.reviewBrowseQuery = this.reviewReturnMode === 'notes' ? this.browseQuery() : '';
    this.reviewBookId = this.reviewReturnMode === 'notes' ? this.browseBookId() : null;
    this.reviewOldestFirst = this.reviewReturnMode === 'notes' && this.browseOldestFirst();
    // A previous request may still be in flight. Its response belongs to the
    // previous review, including its previous filters.
    this.reviewSeq++;
    this.reviewLoading.set(false);
    this.reviewLoaded.set(false);
    this.reviewError.set(false);
    this.reviewQueue.set([]);
    this.reviewTotal.set(0);
    this.clearSourceSelectionState();
    this.closeWritingHandoff();
    this.clearSearch();
    this.closeNotePanel();
    this.reviewId.set(null);
    this.reviewEditing.set(false);
    this.closeReviewPicker();
    this.viewMode.set('unlinked');
    this.loadReviewPage();
  }

  /** Return to the previous Brain area, preserving an unchanged Notes list. */
  closeReview(): void {
    if (this.reviewHasUnsavedEdit()) return;
    this.setViewMode(this.reviewReturnMode);
    if (this.reviewReturnMode === 'notes') {
      if (this.reviewMutated) this.reloadBrowse();
      else if (this.reviewReturnNote) this.panelNote.set(this.reviewReturnNote);
    }
    this.reviewReturnNote = null;
  }

  /**
   * Fetch the next page of the queue.
   *
   * The offset is the number of rows still held, not the number fetched: a note
   * that has been resolved is gone from the server's set too, so the rows this
   * browser holds are exactly the first N of the server's current order.
   */
  loadReviewPage(advanceFromId: string | null = null): void {
    if (this.reviewLoading()) return;
    const seq = this.reviewSeq;
    this.reviewLoading.set(true);
    this.reviewError.set(false);
    const pageRequest = this.reviewBrowseQuery || this.reviewBookId || this.reviewOldestFirst
      ? this.notesService.browse({
          query: this.reviewBrowseQuery,
          bookId: this.reviewBookId ?? undefined,
          withoutConcepts: true,
          oldestFirst: this.reviewOldestFirst,
          limit: REVIEW_PAGE_SIZE,
          offset: this.reviewQueue().length,
        })
      : this.notesService.unlinkedPage(REVIEW_PAGE_SIZE, this.reviewQueue().length);
    pageRequest.subscribe({
      next: (page) => {
        if (seq !== this.reviewSeq) return;
        const held = new Set(this.reviewQueue().map((row) => row.id));
        const fresh = (page.items ?? []).filter((row) => !held.has(row.id));
        this.reviewQueue.set([...this.reviewQueue(), ...fresh]);
        this.reviewTotal.set(page.totalCount ?? this.reviewQueue().length);
        if (advanceFromId && this.reviewNote()?.id === advanceFromId && fresh.length) {
          this.reviewId.set(fresh[0].id);
        }
        this.reviewLoading.set(false);
        this.reviewLoaded.set(true);
      },
      error: () => {
        if (seq !== this.reviewSeq) return;
        this.reviewLoading.set(false);
        this.reviewError.set(true);
        this.toast.error('Notes with no concept could not be loaded');
      },
    });
  }

  /** Leave the focused note (mobile's way back to the queue). Focus decides nothing. */
  clearReviewFocus(): void {
    if (this.reviewHasUnsavedEdit()) return;
    this.reviewEditing.set(false);
    this.closeReviewPicker();
    this.reviewId.set(null);
  }

  /** Focus a queued note. Focusing decides nothing — it only moves the review on. */
  focusReviewNote(id: string): void {
    if (this.reviewHasUnsavedEdit()) return;
    this.reviewEditing.set(false);
    this.closeReviewPicker();
    this.reviewId.set(id);
  }

  /**
   * Move to the next queued note without touching it.
   *
   * Skipping is not a decision: the note is neither linked nor edited, so it
   * stays at the full count and the user can leave the mode and find it still
   * waiting.
   */
  skipReviewNote(): void {
    if (this.reviewHasUnsavedEdit()) return;
    const queue = this.reviewQueue();
    const current = this.reviewNote();
    if (!current) return;
    const index = current ? queue.findIndex((row) => row.id === current.id) : -1;
    const next = queue[index + 1];
    if (next) this.focusReviewNote(next.id);
    else if (this.reviewHasMore()) this.loadReviewPage(current.id);
  }

  previousReviewNote(): void {
    if (this.reviewHasUnsavedEdit()) return;
    const index = this.reviewQueue().findIndex((row) => row.id === this.reviewNote()?.id);
    if (index > 0) this.focusReviewNote(this.reviewQueue()[index - 1].id);
  }

  startReviewEdit(): void {
    const note = this.reviewNote();
    if (!note) return;
    this.reviewEditContent.set(note.content);
    this.closeReviewPicker();
    this.reviewEditing.set(true);
  }

  cancelReviewEdit(): void {
    this.reviewEditing.set(false);
    this.reviewEditContent.set('');
  }

  /**
   * Save the reviewed note's text.
   *
   * This is the canonical note edit path, unchanged: the server re-reads the
   * `[[Concept]]` links from the body on save. What review mode adds is the
   * consequence — a note that now declares a concept has been resolved, so it
   * leaves the queue immediately instead of waiting for a reload.
   */
  saveReviewEdit(): void {
    const note = this.reviewNote();
    if (!note || this.reviewSaving()) return;

    const content = this.reviewEditContent();
    if (content === note.content) {
      this.cancelReviewEdit();
      return;
    }

    this.reviewSaving.set(true);
    this.notesService.update(note.id, { content, selectedText: note.selectedText ?? undefined }).subscribe({
      next: () => {
        this.reviewSaving.set(false);
        this.reviewEditing.set(false);
        this.reviewMutated = true;
        this.refreshIndexAndStats();

        if (declaresConcept(content)) {
          this.removeFromReview(note.id);
          this.toast.success('Note linked to a concept');
        } else {
          // Still belongs to no concept. Keep it queued, showing what was just
          // written, rather than pretending the edit resolved anything.
          this.reviewQueue.update((rows) =>
            rows.map((row) => (row.id === note.id ? { ...row, content } : row))
          );
          this.toast.success('Note saved');
        }
      },
      error: () => {
        this.reviewSaving.set(false);
        this.toast.error('Failed to update note');
      },
    });
  }

  openReviewPicker(): void {
    this.reviewEditing.set(false);
    this.reviewPickerQuery.set('');
    this.reviewPickerConceptId.set(null);
    this.reviewPickerOpen.set(true);
  }

  closeReviewPicker(): void {
    this.reviewPickerOpen.set(false);
    this.reviewPickerQuery.set('');
    this.reviewPickerConceptId.set(null);
  }

  chooseReviewConcept(id: string): void {
    this.reviewPickerConceptId.set(id);
  }

  /**
   * Link the reviewed note to an existing concept.
   *
   * The association written here is the canonical one this codebase has: the note
   * body gains an explicit `[[Concept]]` reference and the server rebuilds the
   * note's concept links from it. Nothing is invented — the concept must already
   * exist, it is chosen by the user, and no prose is rewritten beyond appending
   * the reference. Membership is never stored as a link the next note save would
   * silently drop.
   */
  confirmLinkToConcept(): void {
    const note = this.reviewNote();
    const concept = this.reviewPickerConcept();
    if (!note || !concept || this.reviewSaving()) return;

    this.saveReviewLink(note, concept.name);
  }

  createReviewConcept(): void {
    const note = this.reviewNote();
    const name = this.reviewNewConceptName();
    if (!note || !name || this.reviewSaving()) return;
    this.saveReviewLink(note, name);
  }

  private saveReviewLink(note: NoteSearchHit, name: string): void {
    const content = this.withConceptReference(note.content, name);
    this.reviewSaving.set(true);
    this.notesService.update(note.id, { content, selectedText: note.selectedText ?? undefined }).subscribe({
      next: () => {
        this.reviewSaving.set(false);
        this.reviewMutated = true;
        this.closeReviewPicker();
        this.refreshIndexAndStats();
        this.removeFromReview(note.id);
        this.toast.success(`Linked to “${name}”`);
      },
      error: () => {
        this.reviewSaving.set(false);
        this.toast.error('Failed to link the note');
      },
    });
  }

  /** `content` with an explicit `[[name]]` reference, appended unless already there. */
  private withConceptReference(content: string, name: string): string {
    const trimmedName = name.trim();
    const declared = declaredConceptNames(content).some(
      (existing) => existing.toLowerCase() === trimmedName.toLowerCase()
    );
    if (declared) return content;

    const body = content.trimEnd();
    return body.length ? `${body}\n\n[[${trimmedName}]]` : `[[${trimmedName}]]`;
  }

  /**
   * Take a resolved note out of the queue and off the count, in place.
   *
   * Resolving is a deliberate act that only ever shrinks the waiting set, so the
   * row can go immediately — a refetch would make the user wait to see the effect
   * of their own decision. Focus moves to the row that took its place so the
   * queue keeps flowing.
   */
  private removeFromReview(noteId: string): void {
    const queue = this.reviewQueue();
    const index = queue.findIndex((row) => row.id === noteId);
    if (index < 0) return;

    const remaining = queue.filter((row) => row.id !== noteId);
    this.reviewQueue.set(remaining);
    this.reviewTotal.update((total) => Math.max(0, total - 1));
    this.reviewEditing.set(false);

    if (this.reviewId() === noteId) {
      const following = remaining[index] ?? remaining[index - 1] ?? null;
      this.reviewId.set(following ? following.id : null);
    }
    if (remaining.length === 0 && this.reviewTotal() > 0) this.loadReviewPage();
  }

  openNotePanel(hit: NoteSearchHit): void {
    if (this.browseHasUnsavedEdit()) return;
    this.clearSourceSelectionState();
    this.closeWritingHandoff();
    this.panelNote.set(hit);
    this.browseEditing.set(false);
    this.browsePickerOpen.set(false);
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

  /**
   * Selecting from the index. A content match is only useful if the notes that
   * matched are the ones on screen, so the note filter comes along with it.
   */
  selectIndexRow(concept: ConceptDto): void {
    this.selectConcept(concept.id);
    if (concept.noteMatchCount) this.setNoteSearchQuery(this.searchQuery());
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

    if (event.key === 'ArrowDown' && this.filteredConcepts().length > 0) {
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
      this.focusCursor(Math.min(index + 1, this.filteredConcepts().length - 1));
      return;
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.focusCursor(Math.max(index - 1, 0));
      return;
    }

    if (event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') {
      event.preventDefault();
      this.selectConceptFromIndex(id);
    }
  }

  /** Keyboard selection shares the row's behaviour, content filter included. */
  private selectConceptFromIndex(id: string): void {
    const concept = this.filteredConcepts().find((row) => row.id === id);
    if (concept) this.selectIndexRow(concept);
    else this.selectConcept(id);
  }

  private focusCursor(index: number): void {
    const rows = this.filteredConcepts();
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

  letterFor(concept: ConceptDto): string {
    const firstLetter = concept.name.trim().charAt(0);
    return firstLetter ? firstLetter.toLocaleUpperCase() : '#';
  }

  isLetterStart(index: number): boolean {
    if (!this.showLetterSeparators()) return false;
    if (index === 0) return true;
    const rows = this.filteredConcepts();
    return this.letterFor(rows[index]) !== this.letterFor(rows[index - 1]);
  }

  highlightName(name: string): NamePart[] {
    const query = normalizeSearchText(this.searchQuery().trim());
    if (!query) return [{ text: name, highlight: false }];

    const normalizedChars: string[] = [];
    const sourceStarts: number[] = [];
    const sourceEnds: number[] = [];

    for (let index = 0; index < name.length; ) {
      const codePoint = name.codePointAt(index);
      if (codePoint === undefined) break;
      const sourceChar = String.fromCodePoint(codePoint);
      const normalizedChar = normalizeSearchText(sourceChar);
      for (const character of normalizedChar) {
        normalizedChars.push(character);
        sourceStarts.push(index);
        sourceEnds.push(index + sourceChar.length);
      }
      index += sourceChar.length;
    }

    const matchStart = normalizedChars.join('').indexOf(query);
    if (matchStart < 0) return [{ text: name, highlight: false }];

    const matchEnd = matchStart + query.length - 1;
    const sourceStart = sourceStarts[matchStart];
    const sourceEnd = sourceEnds[matchEnd];
    return [
      { text: name.slice(0, sourceStart), highlight: false },
      { text: name.slice(sourceStart, sourceEnd), highlight: true },
      { text: name.slice(sourceEnd), highlight: false },
    ].filter((part) => part.text.length > 0);
  }

  startRename(id: string, surface: RenameSurface = 'index'): void {
    if (this.renaming()) return;
    const concept = this.concepts().find((candidate) => candidate.id === id);
    const detail = this.selectedDetail();
    const name = concept?.name ?? (detail?.id === id ? detail.name : null);
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

  selectRenameInput(event: FocusEvent): void {
    (event.target as HTMLInputElement).select();
  }

  commitRename(id: string): void {
    if (this.renaming() || this.renameId() !== id) return;

    const name = this.renameValue().trim();
    if (!name) {
      this.renameError.set('A concept name is required.');
      return;
    }

    const original = this.concepts().find((concept) => concept.id === id);
    if (!original) {
      this.cancelRename();
      return;
    }

    this.renaming.set(true);
    this.conceptsService.rename(id, name).subscribe({
      next: (survivor) => {
        this.renaming.set(false);
        this.renameId.set(null);
        this.renameValue.set('');
        this.renameError.set(null);
        this.applyRenameResult(id, survivor);
      },
      error: () => {
        this.renaming.set(false);
        this.cancelRename();
        this.toast.error('Could not rename concept — changes were not saved');
      },
    });
  }

  private applyRenameResult(requestedId: string, survivor: ConceptDto): void {
    const selectedBefore = this.selectedId();
    const detailBefore = this.selectedDetail();
    const merged = survivor.id !== requestedId;

    this.invalidateRelatedData(!merged);
    if (!merged) {
      // Preserve an already-fetched detail for its in-place name update, but
      // prevent an older prefetch response from restoring the old name.
      this.detailRequestVersion += 1;
    }

    if (!merged) {
      this.concepts.update((items) =>
        items.map((concept) => (concept.id === requestedId ? survivor : concept))
      );

      const cached = this.detailCache.get(requestedId);
      if (cached) this.commitDetail(requestedId, { ...cached, name: survivor.name });
      if (detailBefore?.id === requestedId) {
        this.selectedDetail.set({ ...detailBefore, name: survivor.name });
      }
      this.toast.success(`Renamed to ${survivor.name}`);
    } else {
      this.concepts.update((items) =>
        items
          .filter((concept) => concept.id !== requestedId)
          .map((concept) => (concept.id === survivor.id ? survivor : concept))
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
        this.selectConcept(survivor.id);
      }
      this.toast.success(`Merged into ${survivor.name}`);
    }

    this.refreshIndexAndStats();
  }

  openMergePicker(): void {
    const sourceId = this.selectedId();
    if (!sourceId || this.mergingConcept()) return;
    this.mergeSearchQuery.set('');
    this.mergeTargetId.set(null);
    this.mergePickerOpen.set(true);
  }

  closeMergePicker(): void {
    if (this.mergingConcept()) return;
    this.mergePickerOpen.set(false);
    this.mergeSearchQuery.set('');
    this.mergeTargetId.set(null);
  }

  chooseMergeTarget(id: string): void {
    if (id === this.selectedId()) return;
    this.mergeTargetId.set(id);
  }

  openMergeConfirmation(): void {
    const sourceId = this.selectedId();
    const target = this.mergeTarget();
    const source = sourceId ? this.concepts().find((concept) => concept.id === sourceId) : null;
    if (!source || !target || source.id === target.id) return;

    this.mergeConfirmation.set({
      sourceId: source.id,
      targetId: target.id,
      sourceName: this.selectedDetail()?.name ?? source.name,
      targetName: target.name,
      noteCount: this.selectedDetail()?.notes.length ?? source.usageCount,
    });
    this.mergePickerOpen.set(false);
  }

  cancelMergeConfirmation(): void {
    if (!this.mergingConcept()) this.mergeConfirmation.set(null);
  }

  confirmMerge(): void {
    const request = this.mergeConfirmation();
    if (!request || this.mergingConcept()) return;

    this.mergingConcept.set(true);
    this.conceptsService.merge(request.sourceId, request.targetId).subscribe({
      next: (survivor) => {
        this.mergingConcept.set(false);
        this.mergeConfirmation.set(null);
        const previousDetail = this.selectedDetail();

        this.concepts.update((items) =>
          items
            .filter((concept) => concept.id !== request.sourceId)
            .map((concept) => (concept.id === survivor.id ? survivor : concept))
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
          this.selectConcept(survivor.id);
        }

        this.toast.success(`Merged ${request.sourceName} into ${survivor.name}`);
        this.refreshIndexAndStats();
      },
      error: () => {
        this.mergingConcept.set(false);
        this.mergeConfirmation.set(null);
        this.toast.error('Could not merge concepts — changes were not saved');
      },
    });
  }

  openDeleteConcept(id: string): void {
    if (this.deletingConcept()) return;
    const concept = this.concepts().find((candidate) => candidate.id === id);
    if (concept) this.conceptDeleteTarget.set(concept);
  }

  cancelDeleteConcept(): void {
    if (!this.deletingConcept()) this.conceptDeleteTarget.set(null);
  }

  confirmDeleteConcept(): void {
    const target = this.conceptDeleteTarget();
    if (!target || this.deletingConcept()) return;

    this.deletingConcept.set(true);
    this.conceptsService.delete(target.id).subscribe({
      next: () => {
        this.deletingConcept.set(false);
        this.conceptDeleteTarget.set(null);
        this.concepts.update((items) => items.filter((concept) => concept.id !== target.id));
        this.invalidateDetailEntries(target.id);
        this.invalidateRelatedData(false);
        if (this.selectedId() === target.id) {
          this.clearSelection();
        }
        this.toast.success(`Deleted ${target.name}`);
        this.refreshIndexAndStats();
      },
      error: () => {
        this.deletingConcept.set(false);
        this.conceptDeleteTarget.set(null);
        this.toast.error('Could not delete concept — it is still in your index');
      },
    });
  }

  requestRename(id: string, event: MouseEvent): void {
    event.stopPropagation();
    this.startRename(id, 'index');
    this.renameRequested.emit(id);
  }

  requestDelete(id: string, event: MouseEvent): void {
    event.stopPropagation();
    this.openDeleteConcept(id);
    this.deleteRequested.emit(id);
  }

  /**
   * Warm the detail cache without changing the selection. Called on hover/focus
   * of an index row so the click itself is a cache hit and swaps with no wait.
   */
  prefetch(id: string): void {
    if (this.detailCache.has(id) || this.pendingRequests.has(id)) return;
    this.prefetchRequest(id);
  }

  private prefetchRequest(id: string): void {
    this.pendingRequests.add(id);
    const requestVersion = this.detailRequestVersion;
    this.conceptsService.get(id).subscribe({
      next: (detail) => {
        this.pendingRequests.delete(id);
        if (requestVersion !== this.detailRequestVersion) return;
        this.detailCache.set(id, detail);
        if (this.selectedId() !== id) return;
        this.selectedDetail.set(detail);
        this.loadingDetail.set(false);
        this.loadRelated(id);
      },
      error: () => {
        this.pendingRequests.delete(id);
        if (requestVersion !== this.detailRequestVersion) return;
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
    if (this.reviewHasUnsavedEdit()) return;
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
   * double-click on the node (`openConceptFromMap`), or the rail's "Read notes".
   */
  onMapConceptSelected(id: string): void {
    this.selectConcept(id);
  }

  /**
   * Open a concept from the map: double-click, or the rail's "Read notes".
   *
   * Always switches to list view, because the concept's notes ARE the detail
   * pane — there is nowhere to show them while the map owns the screen.
   */
  openConceptFromMap(id: string): void {
    this.selectConcept(id);
    this.setViewMode('list');
  }

  /** Leave the map to read the selected concept's notes (the rail action). */
  openSelectedConcept(): void {
    if (!this.selectedId()) return;
    this.setViewMode('list');
  }

  /** True when the map has a selection that can be opened. */
  canOpenSelectedConcept = computed(() => !!this.selectedId() && this.viewMode() === 'map');

  /** The selected concept's display name, for the map's selection bar. */
  selectedConceptName = computed(() => {
    const id = this.selectedId();
    if (!id || this.viewMode() !== 'map') return null;
    return this.concepts().find((concept) => concept.id === id)?.name ?? null;
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

  selectConcept(id: string): void {
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

    const cached = this.detailCache.get(id);
    if (cached) {
      // Instant, animation-free swap: same background, no loading surface, no
      // blur — the pane simply shows the concept that was asked for.
      this.selectedDetail.set(cached);
      this.loadingDetail.set(false);
      return;
    }

    if (this.pendingRequests.has(id)) {
      // A prefetch is already in flight for exactly this concept; let it land
      // and set the detail when it lands, so we do not start a duplicate
      // request or dim content twice.
      this.loadingDetail.set(true);
      return;
    } else {
      this.loadingDetail.set(true);
    }

    const requestVersion = this.detailRequestVersion;
    this.conceptsService.get(id).subscribe({
      next: (detail) => {
        if (requestVersion !== this.detailRequestVersion) return;
        this.detailCache.set(id, detail);
        // Ignore a response for a concept the user has already moved on from.
        if (this.selectedId() !== id) return;
        this.selectedDetail.set(detail);
        this.loadingDetail.set(false);
      },
      error: () => {
        if (requestVersion !== this.detailRequestVersion) return;
        if (this.selectedId() !== id) return;
        this.toast.error('Failed to load concept details');
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
    this.relatedConcepts.set([]);
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
    };
  }

  private loadRelated(id: string): void {
    const cached = this.relatedCache.get(id);
    if (cached) {
      if (this.selectedId() === id) {
        this.relatedConcepts.set(cached);
        this.relatedLoading.set(false);
      }
      return;
    }
    if (this.pendingRelatedRequests.has(id)) {
      if (this.selectedId() === id) this.relatedLoading.set(true);
      return;
    }

    this.pendingRelatedRequests.add(id);
    if (this.selectedId() === id) this.relatedLoading.set(true);
    const requestVersion = this.relatedRequestVersion;
    this.http.get<RelatedConceptDto[]>(`/api/concepts/${id}/related`).subscribe({
      next: (related) => {
        this.pendingRelatedRequests.delete(id);
        if (requestVersion !== this.relatedRequestVersion) return;
        this.relatedCache.set(id, related);
        if (this.selectedId() !== id) return;
        this.relatedConcepts.set(related);
        this.relatedLoading.set(false);
      },
      error: () => {
        this.pendingRelatedRequests.delete(id);
        if (requestVersion !== this.relatedRequestVersion) return;
        if (this.selectedId() !== id) return;
        this.relatedConcepts.set([]);
        this.relatedLoading.set(false);
      },
    });
  }

  private invalidateRelatedData(reloadSelected: boolean): void {
    this.relatedRequestVersion += 1;
    this.relatedCache.clear();
    this.pendingRelatedRequests.clear();
    this.relatedConcepts.set([]);
    this.relatedExpanded.set(false);
    this.relatedEvidenceId.set(null);
    this.relatedLoading.set(false);

    const selectedId = this.selectedId();
    if (reloadSelected && selectedId) this.loadRelated(selectedId);
  }

  private invalidateDetailEntries(...ids: string[]): void {
    this.detailRequestVersion += 1;
    for (const id of ids) {
      this.detailCache.delete(id);
      this.pendingRequests.delete(id);
    }
  }

  private invalidateAllDetailEntries(): void {
    this.detailRequestVersion += 1;
    this.detailCache.clear();
    this.pendingRequests.clear();
  }

  private refreshIndexAndStats(): void {
    // Mutation responses update the visible row immediately. These background
    // reads reconcile counts and cover server-side deduplication after a merge,
    // without toggling the list's wait field or covering the detail pane.
    this.conceptsService.list().subscribe({
      next: (data) => this.concepts.set(data),
      error: () => this.toast.error('The concept index could not be refreshed'),
    });
    this.conceptsService.getStats().subscribe({
      next: (stats) => this.conceptStats.set(stats),
      error: () => this.toast.error('The concept statistics could not be refreshed'),
    });
  }

  toggleRelated(): void {
    this.relatedExpanded.update((expanded) => !expanded);
  }

  toggleRelatedEvidence(id: string): void {
    this.relatedEvidenceId.update((current) => (current === id ? null : id));
  }

  onUpdateNote(event: { id: string; content: string; selectedText?: string }): void {
    const conceptId = this.selectedId();
    const detail = this.selectedDetail();
    const original = detail?.notes.find((note) => note.noteId === event.id);
    if (!conceptId || !detail || !original) return;

    const previousDetail = detail;
    const optimisticDetail: ConceptDetailDto = {
      ...detail,
      notes: detail.notes.map((note) =>
        note.noteId === event.id
          ? { ...note, content: event.content, selectedText: event.selectedText }
          : note
      ),
    };
    this.commitDetail(conceptId, optimisticDetail);

    this.notesService
      .update(event.id, { content: event.content, selectedText: event.selectedText })
      .subscribe({
        next: (updated) => {
          const current = this.detailCache.get(conceptId);
          if (current) {
            this.commitDetail(conceptId, {
              ...current,
              notes: current.notes.map((note) =>
                note.noteId === event.id ? this.contextFromNote(updated, note) : note
              ),
            });
          }

          // Updating note text re-processes every [[Concept]] link on the
          // server. A note can therefore leave one concept, join another, or
          // change the aggregate reference count without changing its own id.
          // Keep the optimistic card in place, but discard every potentially
          // stale concept detail and related graph before reloading the active
          // pane. The refresh is deliberately in-place: the pane background
          // does not change, so it must not show a loading surface or arrival
          // animation while the canonical membership lands.
          this.invalidateAllDetailEntries();
          this.invalidateRelatedData(true);
          this.reloadDetailInPlace(conceptId);
          this.refreshIndexAndStats();
          this.toast.success('Note updated');
        },
        error: () => {
          this.commitDetail(conceptId, previousDetail);
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
    const conceptId = this.selectedId();
    const detail = this.selectedDetail();
    if (!target || !conceptId || !detail || this.deletingNote()) return;

    const previousDetail = detail;
    this.deletingNote.set(true);
    this.commitDetail(conceptId, {
      ...detail,
      notes: detail.notes.filter((note) => note.noteId !== target.noteId),
    });

    this.notesService.delete(target.noteId).subscribe({
      next: () => {
        this.adjustConceptUsage(conceptId, -1);
        // Deleting a note removes all of its NoteConcept links, not just the
        // link for the concept currently open. The optimistic card/count above
        // makes the active pane immediate; these cache invalidations and
        // background reads reconcile every concept row and aggregate stat.
        this.invalidateAllDetailEntries();
        this.invalidateRelatedData(true);
        this.refreshIndexAndStats();
        this.deletingNote.set(false);
        this.deleteTarget.set(null);
        this.toast.success('Note deleted');
      },
      error: () => {
        this.commitDetail(conceptId, previousDetail);
        this.deletingNote.set(false);
        this.deleteTarget.set(null);
        this.toast.error('Failed to delete note — note restored');
      },
    });
  }

  private commitDetail(conceptId: string, detail: ConceptDetailDto): void {
    this.detailCache.set(conceptId, detail);
    if (this.selectedId() === conceptId) this.selectedDetail.set(detail);
  }

  private reloadDetailInPlace(id: string): void {
    this.loadingDetail.set(true);
    const requestVersion = this.detailRequestVersion;
    this.pendingRequests.add(id);
    this.conceptsService.get(id).subscribe({
      next: (detail) => {
        this.pendingRequests.delete(id);
        if (requestVersion !== this.detailRequestVersion) return;
        this.detailCache.set(id, detail);
        if (this.selectedId() !== id) return;
        this.selectedDetail.set(detail);
        this.loadingDetail.set(false);
        this.loadRelated(id);
      },
      error: () => {
        this.pendingRequests.delete(id);
        if (requestVersion !== this.detailRequestVersion) return;
        if (this.selectedId() !== id) return;
        this.loadingDetail.set(false);
        this.toast.error('Note saved, but this concept could not be refreshed');
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
    };
  }

  private adjustConceptUsage(conceptId: string, delta: number): void {
    this.concepts.update((items) =>
      items.map((concept) =>
        concept.id === conceptId
          ? { ...concept, usageCount: Math.max(0, concept.usageCount + delta) }
          : concept
      )
    );
    this.conceptStats.update((stats) =>
      stats
        ? { ...stats, totalReferences: Math.max(0, stats.totalReferences + delta) }
        : stats
    );
  }

  // Handler for clicking concepts inside the text
  handleContentClick(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    // The pipe adds the 'concept-tag' class and 'data-concept-id' attribute
    const conceptTag = target.closest('.concept-tag');
    if (conceptTag) {
      // Angular's innerHTML sanitizer may remove the data attribute in some
      // browser/test DOMs. The rendered label is still the canonical concept
      // name, so use it as a safe fallback while retaining the fast id path.
      const conceptId =
        conceptTag.getAttribute('data-concept-id') ??
        this.concepts().find(
          (concept) => concept.name.trim().toLowerCase() === conceptTag.textContent?.trim().toLowerCase()
        )?.id;
      if (conceptId) {
        event.preventDefault();
        event.stopPropagation();
        if (this.isBrowsingNotes()) this.setViewMode('list');
        this.selectConcept(conceptId);
      }
    }
  }
}
