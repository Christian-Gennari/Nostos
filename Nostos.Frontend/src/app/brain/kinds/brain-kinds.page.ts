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
import { forkJoin } from 'rxjs';

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
  firstFreeKindColor,
  kindDeleteDescription,
  kindFilterChips,
  kindSorterTopics,
  kindSourceLabel,
  kindUsingColor,
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

  /** The sorter's URL state: `filter` (a kind id, `untyped`, a source) and `q`. */
  readonly filter = computed(() => this.queryParams().get('filter')?.trim() || 'all');
  readonly q = computed(() => this.queryParams().get('q')?.trim() ?? '');
  readonly searchDraft = signal('');

  readonly kinds = this.state.topicKinds;
  readonly kindChips = computed(() => kindFilterChips(this.kinds()));
  readonly topics = computed(() => kindSorterTopics(this.state.topics(), this.filter(), this.q()));
  readonly topicsLoaded = computed(() => this.state.topicsLoaded());

  // --- The kinds editor -----------------------------------------------------

  readonly atKindLimit = computed(() => this.kinds().length >= MAX_TOPIC_KINDS);
  readonly nextKindColor = computed(() => firstFreeKindColor(this.kinds()));
  readonly nextKindColorCss = computed(() => {
    const key = this.nextKindColor();
    return key ? kindCssVar(key) : 'var(--map-kind-none)';
  });

  readonly renamingId = signal<string | null>(null);
  readonly renameError = signal<string | null>(null);
  readonly renaming = signal(false);

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
  readonly selectedCount = computed(() => this.selected().size);
  readonly overBulkLimit = computed(() => this.selectedCount() > BULK_KIND_LIMIT);
  readonly allVisibleSelected = computed(() => {
    const topics = this.topics();
    return topics.length > 0 && topics.every((topic) => this.selected().has(topic.id));
  });
  readonly setKindOpen = signal(false);
  readonly setKindOrigin = viewChild<ElementRef<HTMLElement>>('setKindTrigger');
  readonly busy = signal(false);

  /** The authors in the library, for the offline "Person?" suggestion. */
  private readonly authors = signal<string[]>([]);

  private anchorId: string | null = null;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

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

    this.destroyRef.onDestroy(() => {
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

  commitRename(name: string): void {
    const kind = this.renamingKind();
    if (!kind || this.renaming()) return;
    const trimmed = name.trim();
    if (trimmed === kind.name) {
      this.cancelRename();
      return;
    }
    this.renaming.set(true);
    this.renameError.set(null);
    this.topicsService.updateKind(kind.id, { name: trimmed }).subscribe({
      next: () => {
        this.renaming.set(false);
        this.renamingId.set(null);
        this.state.refresh();
        this.state.reloadKinds();
      },
      error: (error: unknown) => {
        this.renaming.set(false);
        this.renameError.set(serverMessage(error) ?? 'Could not rename the kind. Try again.');
      },
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
        this.state.reloadKinds();
      },
      error: (error: unknown) => {
        this.swapBusy.set(false);
        this.toast.error(serverMessage(error) ?? 'Could not change the colour. Try again.');
      },
    });
  }

  /** Moves a kind one place up or down by trading sort orders with its neighbour. */
  moveKind(kind: TopicKindDto, direction: -1 | 1): void {
    const list = this.kinds();
    const index = list.findIndex((item) => item.id === kind.id);
    const other = list[index + direction];
    if (index < 0 || !other) return;
    this.busy.set(true);
    forkJoin([
      this.topicsService.updateKind(kind.id, { sortOrder: other.sortOrder }),
      this.topicsService.updateKind(other.id, { sortOrder: kind.sortOrder }),
    ]).subscribe({
      next: () => {
        this.busy.set(false);
        this.state.refresh();
        this.state.reloadKinds();
      },
      error: () => {
        this.busy.set(false);
        this.toast.error('Could not reorder the kinds. Try again.');
      },
    });
  }

  startNewKind(): void {
    if (this.atKindLimit()) return;
    this.newKindOpen.set(true);
    this.newKindName.set('');
    this.newKindError.set(null);
  }

  cancelNewKind(): void {
    this.newKindOpen.set(false);
    this.newKindName.set('');
    this.newKindError.set(null);
  }

  createKind(): void {
    if (this.newKindBusy() || this.atKindLimit()) return;
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
        this.state.reloadKinds();
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
        if (this.filter() === kind.id) this.setFilter('all');
        if (this.colorPickId() === kind.id) this.colorPickId.set(null);
        this.state.refresh();
        this.state.reloadKinds();
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
    return personSuggestionFor(topic, this.kinds(), this.authors());
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
  }

  toggleAll(): void {
    if (this.allVisibleSelected()) {
      const visible = new Set(this.topics().map((topic) => topic.id));
      this.selected.set(new Set([...this.selected()].filter((id) => !visible.has(id))));
      this.anchorId = null;
      return;
    }
    const ids = this.topics().slice(0, BULK_KIND_LIMIT).map((topic) => topic.id);
    this.selected.set(new Set([...this.selected(), ...ids]));
    this.anchorId = ids.at(-1) ?? null;
    const hidden = this.topics().length - ids.length;
    if (hidden > 0) this.toast.info(`Selected ${BULK_KIND_LIMIT} topics. Select fewer at a time.`);
  }

  clearSelection(): void {
    this.selected.set(new Set());
    this.anchorId = null;
    this.setKindOpen.set(false);
  }

  selectedIds(): string[] {
    const selected = this.selected();
    return this.topics().filter((topic) => selected.has(topic.id)).map((topic) => topic.id);
  }

  applyKind(kindId: string | null): void {
    const ids = this.selectedIds();
    if (!ids.length || this.busy()) return;
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
    forkJoin([...byKind].map(([kindId, ids]) => this.topicsService.setTopicsKind(ids, kindId))).subscribe({
      next: () => {
        this.busy.set(false);
        const count = [...byKind.values()].reduce((sum, ids) => sum + ids.length, 0);
        this.clearSelection();
        this.state.refresh();
        this.toast.success(`Kept ${plural(count, 'topic')} as yours`);
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.toast.error(serverMessage(error) ?? 'Could not keep the kinds. Try again.');
      },
    });
  }

  acceptPerson(topic: TopicDto, kind: TopicKindDto, event: Event): void {
    event.stopPropagation();
    if (this.busy()) return;
    this.busy.set(true);
    this.topicsService.setTopicsKind([topic.id], kind.id).subscribe({
      next: () => {
        this.busy.set(false);
        this.state.replaceTopic({ ...topic, kindId: kind.id, kindSource: 'reader' });
        this.toast.success(`“${topic.name}” is a ${kind.name}`);
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.toast.error(serverMessage(error) ?? 'Could not set the kind. Try again.');
      },
    });
  }

  private loadAuthors(): void {
    // The library endpoint caps a page at 100, so five pages are the recent 500
    // books the Studio's spelling guide also reads.
    forkJoin(
      Array.from({ length: AUTHOR_BOOK_PAGES }, (_, index) =>
        this.books.list({ page: index + 1, pageSize: AUTHOR_BOOK_PAGE_SIZE })),
    ).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (pages) => this.authors.set(
        pages
          .flatMap((page) => page.items)
          .map((book) => book.author)
          .filter((author): author is string => !!author?.trim()),
      ),
      // The suggestion is a convenience: a failed load simply offers none.
      error: () => undefined,
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
