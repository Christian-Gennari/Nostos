import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { catchError, forkJoin, map, of } from 'rxjs';

import type { TopicDto, TopicKindDto } from '../../core/services/topics.service';
import { TopicsService } from '../../core/services/topics.service';
import { BooksService } from '../../core/services/books.service';
import { ToastService } from '../../core/services/toast.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { ChipComponent } from '../../ui/chip/chip.component';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { InputDirective } from '../../ui/form-control/form-control.directive';
import { InlineRenameComponent } from '../../ui/inline-rename/inline-rename.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { SearchFieldComponent } from '../../ui/search-field/search-field.component';
import { SelectionBarComponent } from '../../ui/selection-bar/selection-bar.component';
import { SettingsPopoverComponent } from '../../ui/settings-popover/settings-popover.component';
import { ConfirmModal } from '../../ui/confirm-modal/confirm-modal.component';
import { BrainStateService } from '../brain-state.service';
import { plural } from '../brain.helpers';
import { kindCssVar } from '../topic-map/topic-map.helpers';
import { KindChoiceListComponent } from './kind-choice-list.component';
import { TopicKindPillComponent } from './topic-kind-pill.component';
import {
  KIND_COLOR_KEYS,
  MAX_TOPIC_KINDS,
  UNTYPED_FILTER,
  authorNameIndex,
  firstFreeKindColor,
  kindCountLabel,
  kindDeleteDescription,
  kindFilterChips,
  kindSorterTopics,
  kindSourceLabel,
  kindUsingColor,
  normalizeKindFilter,
  normalizeName,
  personSuggestionFor,
  rangeSelection,
  toggleId,
} from './kinds.helpers';

/** The server's bulk limit; one request, so the UI refuses to exceed it. */
export const BULK_KIND_LIMIT = 500;
const SEARCH_DEBOUNCE_MS = 250;
/** Authors are read from the recent books, as the Studio's spelling guide is. */
const AUTHOR_BOOK_PAGE_SIZE = 100;
const AUTHOR_BOOK_PAGES = 5;

interface ColorChoice {
  key: string;
  other: TopicKindDto | null;
}

interface PendingSwap {
  kind: TopicKindDto;
  colorKey: string;
  other: TopicKindDto;
}

/**
 * Kinds: the reader's own categories for topics. Two jobs on one page.
 *
 * At the top, the kinds themselves: rename, recolour (a colour another kind
 * holds offers a swap), reorder and delete. Under it, the sorter: every topic
 * with its note count, kind and who set it, filtered by kind or source, with
 * shift-click ranges and one bulk Set kind / Clear kind / Keep. A topic named
 * after an author in the library is offered "Person?" without asking an AI.
 *
 * The filter and search are in the URL, so the map legend's "Sort them" lands
 * on untyped topics and Back returns to the same view.
 */
@Component({
  selector: 'app-brain-kinds-page',
  standalone: true,
  imports: [
    RouterLink,
    ButtonComponent,
    ChipComponent,
    EmptyStateComponent,
    IconButtonComponent,
    InputDirective,
    InlineRenameComponent,
    LoadingIndicatorComponent,
    NostosIconComponent,
    SearchFieldComponent,
    SelectionBarComponent,
    SettingsPopoverComponent,
    ConfirmModal,
    KindChoiceListComponent,
    TopicKindPillComponent,
  ],
  templateUrl: './brain-kinds.page.html',
  styleUrls: ['./brain-kinds.page.css', '../brain-touch.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrainKindsPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly topicsService = inject(TopicsService);
  private readonly books = inject(BooksService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly state = inject(BrainStateService);
  readonly plural = plural;
  readonly maxKinds = MAX_TOPIC_KINDS;
  readonly bulkLimit = BULK_KIND_LIMIT;

  private readonly queryParams = toSignal(this.route.queryParamMap, {
    initialValue: this.route.snapshot.queryParamMap,
  });

  /**
   * The sorter's URL state: `filter` (a kind id, `untyped`, a source) and `q`.
   * The filter's case is folded, so a hand-typed `?filter=Untyped` is the same
   * view as the legend's link.
   */
  readonly filter = computed(() => normalizeKindFilter(this.queryParams().get('filter')));
  readonly q = computed(() => this.queryParams().get('q')?.trim() ?? '');
  readonly searchDraft = signal('');

  readonly kinds = this.state.topicKinds;
  readonly kindChips = computed(() => kindFilterChips(this.kinds()));
  readonly topics = computed(() => kindSorterTopics(this.state.topics(), this.filter(), this.q()));
  readonly topicsLoaded = computed(() => this.state.topicsLoaded());
  readonly topicsFailed = computed(() => this.state.topicsFailed());
  /** A filter or a search is narrowing the list, so the header says "13 of 40". */
  readonly filtered = computed(() => this.filter() !== 'all' || !!this.q());
  readonly headerCount = computed(() =>
    kindCountLabel(this.topics().length, this.state.topics().length, this.filtered()));
  /** Under the "No kind" filter every row would repeat the same "No kind" pill. */
  readonly showRowKind = computed(() => this.filter() !== UNTYPED_FILTER);

  // --- The kinds editor -----------------------------------------------------

  readonly atKindLimit = computed(() => this.kinds().length >= MAX_TOPIC_KINDS);
  readonly kindsLoaded = computed(() => this.state.kindsLoaded());
  readonly kindsFailed = computed(() => this.state.kindsFailed());
  /**
   * Why "New kind" is unavailable, or null. Before the kind list arrives the
   * page cannot pick a free colour or refuse a duplicate, so the server would
   * answer the create with a 409.
   */
  readonly newKindBlocked = computed(() => {
    if (!this.kindsLoaded()) return 'Your kinds have not loaded yet. Try again first.';
    if (this.atKindLimit()) return 'All 12 kinds are in use. Delete one to add another.';
    return null;
  });
  readonly nextKindColor = computed(() => firstFreeKindColor(this.kinds()));
  readonly nextKindColorCss = computed(() => {
    const key = this.nextKindColor();
    return key ? kindCssVar(key) : 'var(--map-kind-none)';
  });

  readonly renamingId = signal<string | null>(null);
  readonly renameError = signal<string | null>(null);
  /**
   * The kinds whose rename is in flight. One flag for the page blocked a second
   * kind's rename until the first answered.
   */
  readonly renamingIds = signal<ReadonlySet<string>>(new Set());

  readonly colorPickId = signal<string | null>(null);
  readonly pendingSwap = signal<PendingSwap | null>(null);
  readonly swapBusy = signal(false);

  readonly newKindOpen = signal(false);
  readonly newKindName = signal('');
  readonly newKindError = signal<string | null>(null);
  readonly newKindBusy = signal(false);

  readonly deleteTarget = signal<TopicKindDto | null>(null);
  readonly deleting = signal(false);
  readonly deleteDescription = computed(() => {
    const kind = this.deleteTarget();
    return kind ? kindDeleteDescription(kind) : '';
  });

  // --- The sorter -----------------------------------------------------------

  readonly selected = signal<ReadonlySet<string>>(new Set());
  /** Only rows the reader can still see are selected, so the count is theirs. */
  readonly selectedIds = computed(() => {
    const selected = this.selected();
    return this.topics().filter((topic) => selected.has(topic.id)).map((topic) => topic.id);
  });
  readonly selectedCount = computed(() => this.selectedIds().length);
  readonly overBulkLimit = computed(() => this.selectedCount() > BULK_KIND_LIMIT);
  readonly bulkLimitNote = computed(() =>
    this.overBulkLimit() ? `Select at most ${BULK_KIND_LIMIT} topics at once.` : null);
  readonly allVisibleSelected = computed(() => {
    const topics = this.topics();
    return topics.length > 0 && topics.every((topic) => this.selected().has(topic.id));
  });
  /** "Select all" has done all it can: everything shown, or the cap. */
  readonly selectAllActive = computed(() => this.allVisibleSelected() || this.selectAllCapped());
  readonly setKindOpen = signal(false);
  readonly setKindOrigin = viewChild<ElementRef<HTMLElement>>('setKindTrigger');
  /** Where focus goes after a bulk action, so it is not dropped on `<body>`. */
  private readonly sorterHeading = viewChild<ElementRef<HTMLElement>>('sorterHeading');
  /** The button that opens the create form, and the field it reveals. */
  // `#newKindTrigger` is a `button[appButton]` (a component), so the query has
  // to ask for the ElementRef: the plain one is the ButtonComponent.
  private readonly newKindTrigger = viewChild<string, ElementRef<HTMLElement>>('newKindTrigger', {
    read: ElementRef,
  });
  private readonly newKindField = viewChild<ElementRef<HTMLInputElement>>('newKindField');
  readonly busy = signal(false);

  /** The authors in the library, for the offline "Person?" suggestion. */
  private readonly authors = signal<string[]>([]);
  /** The same names folded once, so no row re-normalises the library. */
  private readonly authorIndex = computed(() => authorNameIndex(this.authors()));

  private anchorId: string | null = null;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  /**
   * The last "Select all" selected the cap rather than the whole list. A signal,
   * so the button says "Clear selection": `allVisibleSelected` is false when the
   * list is longer than the cap, and the label stayed "Select all" after a press
   * that had already selected everything it could.
   */
  readonly selectAllCapped = signal(false);
  private destroyed = false;

  constructor() {
    this.state.ensureLoaded();
    this.state.ensureKindsLoaded();
    this.loadAuthors();

    // The field follows the URL when the search changes elsewhere (Back, a link).
    effect(() => {
      const q = this.q();
      untracked(() => {
        if (this.searchTimer === null && this.searchDraft().trim() !== q) this.searchDraft.set(q);
      });
    });

    // A new filter or search is a new list: selections from the old one would
    // act on rows the reader can no longer see.
    let previousKey = '';
    effect(() => {
      const key = `${this.filter()}\u0000${this.q()}`;
      if (key === previousKey) return;
      previousKey = key;
      untracked(() => this.clearSelection());
    });

    // A row can also leave the list without the filter changing (a kind set in
    // the "No kind" view, a topic renamed out of the search): keep the
    // selection to what is still on screen.
    effect(() => {
      const visible = new Set(this.topics().map((topic) => topic.id));
      untracked(() => {
        const current = this.selected();
        const gone = [...current].filter((id) => !visible.has(id));
        if (!gone.length) return;
        const kept = new Set([...current].filter((id) => visible.has(id)));
        this.selected.set(kept);
        if (this.anchorId && !visible.has(this.anchorId)) this.anchorId = null;
        // The bulk panel acted on a selection that no longer exists.
        if (!kept.size) this.setKindOpen.set(false);
      });
    });

    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    });
  }

  // --- URL and search -------------------------------------------------------

  setFilter(value: string): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { filter: value === 'all' ? null : value },
      queryParamsHandling: 'merge',
    });
  }

  onSearchInput(value: string): void {
    this.searchDraft.set(value);
    if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.searchTimer = null;
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { q: value.trim() || null },
        queryParamsHandling: 'merge',
        replaceUrl: true,
      });
    }, SEARCH_DEBOUNCE_MS);
  }

  clearSearch(): void {
    if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    this.searchTimer = null;
    this.searchDraft.set('');
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { q: null },
      queryParamsHandling: 'merge',
    });
  }

  /**
   * Empty list, filter and search in ONE navigation. Two calls queued two
   * router navigations, the second cancelled the first, and `q` survived the
   * "Clear filters" button it was meant to clear.
   */
  clearFilters(): void {
    if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    this.searchTimer = null;
    this.searchDraft.set('');
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { q: null, filter: null },
      queryParamsHandling: 'merge',
    });
  }

  openDrawer(): void {
    this.state.drawerOpen.set(true);
  }

  // --- The kinds editor -----------------------------------------------------

  kindColor(kind: TopicKindDto): string {
    return kindCssVar(kind.colorKey);
  }

  kindCount(kind: TopicKindDto): string {
    return plural(kind.topicCount, 'topic');
  }

  startRename(kind: TopicKindDto): void {
    this.renamingId.set(kind.id);
    this.renameError.set(null);
  }

  cancelRename(): void {
    this.renamingId.set(null);
    this.renameError.set(null);
  }

  /**
   * Saves a rename. The kind's id comes from the row being edited, not from
   * whichever field is open when the answer lands: the reader can start another
   * rename while this one is in flight. The in-flight set is per kind, so the
   * second field is not blocked by the first (and not shown as busy).
   */
  commitRename(kindId: string, name: string): void {
    const kind = this.kinds().find((item) => item.id === kindId);
    if (!kind || this.renamingIds().has(kindId)) return;
    const id = kind.id;
    const trimmed = name.trim();
    if (trimmed === kind.name) {
      if (this.renamingId() === id) this.cancelRename();
      return;
    }
    this.setRenaming(id, true);
    this.renameError.set(null);
    this.topicsService.updateKind(id, { name: trimmed }).subscribe({
      next: () => {
        this.setRenaming(id, false);
        this.state.refresh();
        // Only close the field this answer belongs to.
        if (this.renamingId() === id) this.renamingId.set(null);
      },
      error: (error: unknown) => {
        this.setRenaming(id, false);
        const message = serverMessage(error) ?? 'Could not rename the kind. Try again.';
        if (this.renamingId() === id) this.renameError.set(message);
        else this.toast.error(message);
      },
    });
  }

  /** Whether this kind's rename is in flight. */
  isRenaming(kindId: string): boolean {
    return this.renamingIds().has(kindId);
  }

  private setRenaming(kindId: string, renaming: boolean): void {
    this.renamingIds.update((ids) => {
      const next = new Set(ids);
      if (renaming) next.add(kindId);
      else next.delete(kindId);
      return next;
    });
  }

  /** The kind being renamed, so the field keeps its place while the list reloads. */
  readonly renamingKind = computed(() => {
    const id = this.renamingId();
    return id ? this.kinds().find((kind) => kind.id === id) ?? null : null;
  });

  toggleColor(kind: TopicKindDto): void {
    this.pendingSwap.set(null);
    this.colorPickId.update((id) => (id === kind.id ? null : kind.id));
  }

  colorChoices(kind: TopicKindDto): ColorChoice[] {
    return KIND_COLOR_KEYS.map((key) => ({
      key,
      other: kindUsingColor(this.kinds(), key, kind.id),
    }));
  }

  isCurrentColor(kind: TopicKindDto, key: string): boolean {
    return kind.colorKey === key;
  }

  chooseColor(kind: TopicKindDto, key: string): void {
    if (this.swapBusy()) return;
    if (kind.colorKey === key) {
      this.colorPickId.set(null);
      return;
    }
    const other = kindUsingColor(this.kinds(), key, kind.id);
    if (other) {
      this.pendingSwap.set({ kind, colorKey: key, other });
      return;
    }
    this.updateColor(kind, key, false);
  }

  confirmSwap(): void {
    const swap = this.pendingSwap();
    if (!swap || this.swapBusy()) return;
    this.updateColor(swap.kind, swap.colorKey, true);
  }

  cancelSwap(): void {
    this.pendingSwap.set(null);
  }

  private updateColor(kind: TopicKindDto, colorKey: string, swapColor: boolean): void {
    this.swapBusy.set(true);
    this.topicsService.updateKind(kind.id, { colorKey, swapColor }).subscribe({
      next: () => {
        this.swapBusy.set(false);
        this.pendingSwap.set(null);
        this.colorPickId.set(null);
        this.state.refresh();
      },
      error: (error: unknown) => {
        this.swapBusy.set(false);
        this.toast.error(serverMessage(error) ?? 'Could not change the colour. Try again.');
      },
    });
  }

  /**
   * Moves a kind one place up or down.
   *
   * The whole new order is computed here, then `1..N` is written for every row
   * whose stored order differs. Trading two values was wrong in two ways: a
   * second click landing before the refreshed list arrived read the same old
   * values and produced duplicates, and once two kinds held the same order the
   * trade could never separate them again. Writing the positions repairs both.
   */
  moveKind(kind: TopicKindDto, direction: -1 | 1): void {
    if (this.busy()) return;
    const list = this.kinds();
    const index = list.findIndex((item) => item.id === kind.id);
    const target = index + direction;
    if (index < 0 || target < 0 || target >= list.length) return;

    const reordered = [...list];
    const [moved] = reordered.splice(index, 1);
    reordered.splice(target, 0, moved);

    const changes = reordered
      .map((item, position) => ({ id: item.id, sortOrder: position + 1, current: item.sortOrder }))
      .filter((change) => change.current !== change.sortOrder);
    if (!changes.length) return;

    this.busy.set(true);
    forkJoin(
      changes.map((change) => this.topicsService.updateKind(change.id, { sortOrder: change.sortOrder }))
    ).subscribe({
      next: () => {
        this.busy.set(false);
        this.state.refresh();
      },
      error: () => {
        this.busy.set(false);
        // Some of the writes may have landed; read the server's list back so
        // the arrows start from the orders it actually holds.
        this.state.refresh();
        this.toast.error('Could not reorder the kinds. Try again.');
      },
    });
  }

  startNewKind(): void {
    if (this.newKindBlocked()) return;
    this.newKindOpen.set(true);
    this.newKindName.set('');
    this.newKindError.set(null);
    this.focusNewKindField();
  }

  cancelNewKind(): void {
    this.newKindOpen.set(false);
    this.newKindName.set('');
    this.newKindError.set(null);
    // The field the reader was in is gone; the button that opened it is the
    // next thing they need, not the page body.
    this.newKindTrigger()?.nativeElement.focus();
  }

  /** Focus the create field once Angular has painted it. */
  private focusNewKindField(): void {
    window.requestAnimationFrame(() => {
      if (this.destroyed) return;
      this.newKindField()?.nativeElement.focus();
    });
  }

  createKind(): void {
    if (this.newKindBusy() || this.newKindBlocked()) return;
    const name = this.newKindName().trim();
    if (!name) {
      this.newKindError.set('A kind needs a name.');
      return;
    }
    if (this.kinds().some((kind) => normalizeName(kind.name) === normalizeName(name))) {
      this.newKindError.set('A kind with this name already exists.');
      return;
    }
    const colorKey = this.nextKindColor();
    if (!colorKey) {
      this.newKindError.set(`All ${MAX_TOPIC_KINDS} kinds are in use. Delete one first.`);
      return;
    }
    this.newKindBusy.set(true);
    this.newKindError.set(null);
    this.topicsService.createKind({ name, colorKey }).subscribe({
      next: () => {
        this.newKindBusy.set(false);
        this.newKindOpen.set(false);
        this.newKindName.set('');
        this.state.refresh();
        this.toast.success(`Added “${name}”`);
      },
      error: (error: unknown) => {
        this.newKindBusy.set(false);
        this.newKindError.set(serverMessage(error) ?? 'Could not add the kind. Try again.');
      },
    });
  }

  askDelete(kind: TopicKindDto): void {
    this.deleteTarget.set(kind);
  }

  cancelDelete(): void {
    if (this.deleting()) return;
    this.deleteTarget.set(null);
  }

  deleteKind(): void {
    const kind = this.deleteTarget();
    if (!kind || this.deleting()) return;
    this.deleting.set(true);
    this.topicsService.deleteKind(kind.id).subscribe({
      next: () => {
        this.deleting.set(false);
        this.deleteTarget.set(null);
        // Anything still pointing at the deleted kind is stale: a swap waiting
        // for its other kind, a colour palette, a rename field.
        const swap = this.pendingSwap();
        if (swap && (swap.kind.id === kind.id || swap.other.id === kind.id)) this.pendingSwap.set(null);
        if (this.renamingId() === kind.id) this.renamingId.set(null);
        if (this.colorPickId() === kind.id) this.colorPickId.set(null);
        // The answer can land after the reader left the page; navigating from a
        // destroyed page would move them off wherever they went.
        if (this.destroyed) return;
        if (this.filter() === kind.id) this.setFilter('all');
        this.state.refresh();
        this.toast.success(`Deleted “${kind.name}”`);
      },
      error: () => {
        this.deleting.set(false);
        this.toast.error('Could not delete the kind. Try again.');
      },
    });
  }

  // --- The sorter -----------------------------------------------------------

  sourceLabel(topic: TopicDto): string {
    return kindSourceLabel(topic.kindSource) ?? '';
  }

  kindOf(topic: TopicDto): TopicKindDto | null {
    const id = topic.kindId;
    return id ? this.kinds().find((kind) => kind.id === id) ?? null : null;
  }

  personSuggestion(topic: TopicDto): TopicKindDto | null {
    return personSuggestionFor(topic, this.kinds(), this.authorIndex());
  }

  isSelected(id: string): boolean {
    return this.selected().has(id);
  }

  onCheckboxClick(index: number, event: Event): void {
    event.stopPropagation();
    const row = this.topics()[index];
    if (!row) return;
    const desired = (event.target as HTMLInputElement).checked;
    const shift = event instanceof MouseEvent && event.shiftKey;
    const orderedIds = this.topics().map((topic) => topic.id);
    if (shift) {
      this.selected.set(rangeSelection(orderedIds, this.selected(), this.anchorId, row.id, desired));
    } else {
      this.selected.set(toggleId(this.selected(), row.id, desired));
      this.anchorId = row.id;
    }
    this.selectAllCapped.set(false);
  }

  toggleAll(): void {
    // A capped "Select all" selected the first 500 of a longer list. Pressing
    // it again is the reader asking to undo that, not to select the same 500.
    if (this.selectAllActive()) {
      const visible = new Set(this.topics().map((topic) => topic.id));
      this.selected.set(new Set([...this.selected()].filter((id) => !visible.has(id))));
      this.anchorId = null;
      this.selectAllCapped.set(false);
      return;
    }
    // Exactly the first 500 rows, replacing any row picked past the cap: the
    // server refuses more than 500 in one request, and a union kept those rows
    // and produced 501.
    const ids = this.topics().slice(0, BULK_KIND_LIMIT).map((topic) => topic.id);
    this.selected.set(new Set(ids));
    this.anchorId = ids.at(-1) ?? null;
    const hidden = this.topics().length - ids.length;
    this.selectAllCapped.set(hidden > 0);
    if (hidden > 0) this.toast.info(`Selected ${BULK_KIND_LIMIT} topics. Select fewer at a time.`);
  }

  clearSelection(): void {
    this.selected.set(new Set());
    this.anchorId = null;
    this.selectAllCapped.set(false);
    this.setKindOpen.set(false);
  }

  applyKind(kindId: string | null): void {
    const ids = this.selectedIds();
    if (this.busy()) return;
    if (!ids.length) {
      this.toast.info('Select at least one topic first.');
      return;
    }
    if (ids.length > BULK_KIND_LIMIT) {
      this.toast.error(`Select at most ${BULK_KIND_LIMIT} topics at once.`);
      return;
    }
    this.busy.set(true);
    this.topicsService.setTopicsKind(ids, kindId).subscribe({
      next: () => {
        this.busy.set(false);
        this.setKindOpen.set(false);
        this.clearSelection();
        this.state.refresh();
        this.focusSorter();
        this.toast.success(kindId ? `Set the kind on ${plural(ids.length, 'topic')}` : `Cleared the kind on ${plural(ids.length, 'topic')}`);
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.toast.error(serverMessage(error) ?? 'Could not set the kinds. Try again.');
      },
    });
  }

  clearKind(): void {
    this.applyKind(null);
  }

  /**
   * Keep the reader's kinds: re-save each selected topic's current kind as
   * `reader`, so later AI runs leave it alone. Topics of different kinds are
   * grouped, one request per kind, because the API takes one kind at a time.
   * A group can fail on its own: the reader is told which part was kept, and
   * the selection shrinks to the rows that still need an answer.
   */
  keepSelection(): void {
    if (this.busy()) return;
    const byKind = new Map<string, string[]>();
    for (const id of this.selectedIds()) {
      const topic = this.state.topicById(id);
      if (!topic?.kindId) continue;
      const ids = byKind.get(topic.kindId) ?? [];
      ids.push(id);
      byKind.set(topic.kindId, ids);
    }
    if (!byKind.size) {
      this.toast.info('These topics have no kind to keep.');
      return;
    }
    this.busy.set(true);
    forkJoin([...byKind].map(([kindId, ids]) =>
      this.topicsService.setTopicsKind(ids, kindId).pipe(
        map(() => ({ ids, error: null as unknown })),
        catchError((error: unknown) => of({ ids, error: error ?? new Error('failed') })),
      ),
    )).subscribe({
      next: (results) => {
        this.busy.set(false);
        const kept = results.filter((result) => result.error === null);
        const failed = results.filter((result) => result.error !== null);
        const keptCount = kept.reduce((sum, result) => sum + result.ids.length, 0);
        const failedCount = failed.reduce((sum, result) => sum + result.ids.length, 0);
        this.state.refresh();
        // Clearing the selection throws away the only record of which rows the
        // reader still has to redo. Only a full success may do that; a partial
        // failure keeps the failed rows selected, and a total failure keeps the
        // whole selection so the reader can simply press Keep again.
        if (!failed.length) {
          this.clearSelection();
          this.focusSorter();
          this.toast.success(`Kept ${plural(keptCount, 'topic')} as yours`);
        } else if (!kept.length) {
          this.toast.error(
            serverMessage(failed[0].error) ?? 'Could not keep the kinds. Try again.');
        } else {
          this.selected.set(new Set(failed.flatMap((result) => result.ids)));
          this.anchorId = null;
          this.selectAllCapped.set(false);
          this.toast.error(
            `Kept ${plural(keptCount, 'topic')} as yours, but ${plural(failedCount, 'topic')} could not be kept. Try again.`);
        }
      },
    });
  }

  acceptPerson(topic: TopicDto, kind: TopicKindDto, event: Event): void {
    event.stopPropagation();
    if (this.busy()) return;
    this.busy.set(true);
    // The suggestion disappears once the row has a kind, dropping focus on the
    // page body. Remember where the row is so the next candidate keeps it.
    const row = (event.currentTarget ?? event.target) instanceof HTMLElement
      ? ((event.currentTarget ?? event.target) as HTMLElement).closest('.topic-row')
      : null;
    this.topicsService.setTopicsKind([topic.id], kind.id).subscribe({
      next: () => {
        this.busy.set(false);
        this.state.replaceTopic({ ...topic, kindId: kind.id, kindSource: 'reader' });
        this.toast.success(`“${topic.name}” is a ${kind.name}`);
        this.focusNextPerson(row);
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.toast.error(serverMessage(error) ?? 'Could not set the kind. Try again.');
      },
    });
  }

  /**
   * Focus the next row that still offers "Person?", or its name link: the row
   * the reader just answered is gone from under the pointer.
   */
  private focusNextPerson(row: Element | null): void {
    if (!row || this.destroyed) return;
    window.requestAnimationFrame(() => {
      if (this.destroyed) return;
      const next = row.nextElementSibling;
      const target = next?.querySelector<HTMLElement>('.person-suggestion, .topic-name');
      target?.focus();
    });
  }

  /** The topics failed to load: a failure state with a way to ask again. */
  retryTopics(): void {
    this.state.refresh();
  }

  /** The kind list failed to load: ask for it again without a full page reload. */
  retryKinds(): void {
    this.state.reloadKinds();
  }

  /**
   * After a bulk action the selection bar closes with the focused button in it,
   * so focus would fall to the page body. The sorter's heading is the next
   * thing the reader needs to hear.
   */
  private focusSorter(): void {
    this.sorterHeading()?.nativeElement.focus();
  }

  /**
   * Reads the recent books' authors for the offline "Person?" suggestion.
   *
   * Page 1 is read first because it says how many books there are: a library of
   * 40 books costs one request, not five. Each further page is one request, up
   * to the recent 500 the Studio's spelling guide also reads, and one page
   * failing leaves the names from the others in place.
   */
  private loadAuthors(): void {
    const page = (pageNumber: number) =>
      this.books.list({ page: pageNumber, pageSize: AUTHOR_BOOK_PAGE_SIZE }).pipe(
        catchError(() => of(null)),
      );
    page(1).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (first) => {
        if (!first) return;
        const pages = Math.min(
          AUTHOR_BOOK_PAGES,
          Math.max(1, Math.ceil((first.totalCount ?? 0) / AUTHOR_BOOK_PAGE_SIZE)),
        );
        if (pages === 1) {
          this.authors.set(authorsOf([first]));
          return;
        }
        forkJoin(Array.from({ length: pages - 1 }, (_, index) => page(index + 2)))
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: (rest) => this.authors.set(authorsOf([first, ...rest])),
          });
      },
    });
  }

  /** The words a swatch's title says: the colour is free, or whose it is. */
  colorTitle(choice: ColorChoice): string {
    return choice.other ? `Used by ${choice.other.name}` : 'Free';
  }

  /** A palette key for a template's style binding. */
  colorVar(key: string): string {
    return kindCssVar(key);
  }
}

function serverMessage(error: unknown): string | null {
  if (!(error instanceof HttpErrorResponse)) return null;
  const body = error.error as { error?: unknown } | null;
  return typeof body?.error === 'string' ? body.error : null;
}

/** The authors named by a set of book pages, skipping the pages that failed. */
function authorsOf(pages: ReadonlyArray<{ items: ReadonlyArray<{ author?: string | null }> } | null>): string[] {
  return pages
    .flatMap((page) => page?.items ?? [])
    .map((book) => book.author)
    .filter((author): author is string => !!author?.trim());
}
