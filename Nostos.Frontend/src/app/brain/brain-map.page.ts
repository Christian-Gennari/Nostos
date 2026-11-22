import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { QuoteBlockComponent } from '../ui/quote-block/quote-block.component';
import { TopicOptionComponent } from '../ui/topic-option/topic-option.component';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, map, of, startWith, switchMap } from 'rxjs';

import { NotesService } from '../core/services/notes.service';
import { ButtonComponent } from '../ui/button/button.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { RangeDirective } from '../ui/range/range.directive';
import { SettingsPopoverComponent } from '../ui/settings-popover/settings-popover.component';
import { TopicChipComponent } from '../ui/topic-chip/topic-chip.component';
import { rankTopicChoices, thoughtForDisplay, topicChoices } from '../ui/note-item/note-item.helpers';
import { noteWikilinksToText } from '../ui/pipes/note-format.pipe';
import { TopicMapComponent, type MapCamera, type MapScopeInfo } from './topic-map/topic-map.component';
import {
  LOCAL_MAP_DEPTH_MAX,
  LOCAL_MAP_DEPTH_MIN,
  drawnTopics,
  kindFilterKeyOf,
  parseKindsParam,
  toggleKindFilter,
} from './topic-map/topic-map.helpers';
import { BrainStateService } from './brain-state.service';
import { type MapColorMode, plural } from './brain.helpers';
import { SearchFieldComponent } from '../ui/search-field/search-field.component';
import { SegmentedComponent, type SegmentedOption } from '../ui/segmented/segmented.component';

const FINDER_LIMIT = 6;

function parseDepth(raw: string | null, fallback = LOCAL_MAP_DEPTH_MIN): number {
  const parsed = raw !== null ? Number.parseInt(raw, 10) : NaN;
  if (!Number.isFinite(parsed)) return fallback;
  return Math.max(LOCAL_MAP_DEPTH_MIN, Math.min(LOCAL_MAP_DEPTH_MAX, parsed));
}

function parseMin(raw: string | null, fallback: number | null = null): number | null {
  const parsed = raw !== null ? Number.parseInt(raw, 10) : NaN;
  if (!Number.isFinite(parsed) || parsed < 1) return fallback;
  return parsed;
}

/**
 * The map as a place: the topic graph at full size, a finder that centres a
 * topic, local scoping (`?around=<topicId>&depth=<1-3>&min=<n>`), colour modes
 * (`Kind` / `Cluster` / `None`) with `?kinds=<id>,<id>` filtering, and a card
 * whose Open topic goes to the topic's page. Camera, depth, strength and kind
 * filter are kept, so Back returns to the same view; `?focus=<id>` centres
 * and selects a topic.
 */
@Component({
  selector: 'app-brain-map-page',
  standalone: true,
  imports: [
    QuoteBlockComponent,
    TopicOptionComponent,
    SearchFieldComponent,
    SegmentedComponent,
    ButtonComponent,
    InputDirective,
    NostosIconComponent,
    RangeDirective,
    SettingsPopoverComponent,
    TopicChipComponent,
    TopicMapComponent,
  ],
  templateUrl: './brain-map.page.html',
  styleUrls: ['./brain-map.page.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrainMapPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly notes = inject(NotesService);
  readonly state = inject(BrainStateService);

  private readonly map = viewChild(TopicMapComponent);
  private readonly controlsTrigger = viewChild<ElementRef<HTMLElement>>('mapControlsTrigger');

  private readonly queryParams = toSignal(this.route.queryParamMap, {
    initialValue: this.route.snapshot.queryParamMap,
  });

  readonly focus = computed(() => this.queryParams().get('focus'));
  readonly around = computed(() => this.queryParams().get('around'));
  readonly kinds = computed(() => this.queryParams().get('kinds'));
  readonly visibleKindKeys = computed(() => parseKindsParam(this.kinds()));

  readonly colorModeOptions: readonly SegmentedOption<MapColorMode>[] = [
    { value: 'kind', label: 'Kind' },
    { value: 'cluster', label: 'Cluster' },
    { value: 'none', label: 'None' },
  ];

  private restoredDepth = LOCAL_MAP_DEPTH_MIN;
  private restoredMin: number | null = null;

  /**
   * The view to open at, read once: Back to the map restores the camera, depth
   * and link strength left behind; any other arrival centres on the focus, or
   * frames the graph.
   */
  readonly startCamera: MapCamera | null = (() => {
    const kept = this.state.mapView();
    const back = this.router.currentNavigation()?.trigger === 'popstate';
    const params = this.route.snapshot.queryParamMap;
    if (
      back &&
      kept &&
      kept.focus === params.get('focus') &&
      kept.around === params.get('around') &&
      (kept.kinds ?? null) === params.get('kinds')
    ) {
      this.restoredDepth = Math.max(LOCAL_MAP_DEPTH_MIN, Math.min(LOCAL_MAP_DEPTH_MAX, kept.depth || LOCAL_MAP_DEPTH_MIN));
      this.restoredMin = kept.min;
      return kept.camera;
    }
    return null;
  })();

  readonly depth = computed(() => parseDepth(this.queryParams().get('depth'), this.restoredDepth));
  readonly min = computed(() => parseMin(this.queryParams().get('min'), this.restoredMin));

  readonly topics = this.state.topics;
  /**
   * The colour mode the map can paint. Kind mode without the kind list would
   * paint every typed topic as untyped, so Cluster stands in until it arrives.
   */
  readonly effectiveColorMode = computed<MapColorMode>(() =>
    this.state.kindsLoaded() ? this.state.mapColorMode() : 'cluster'
  );
  readonly aroundTopic = computed(() => this.state.topicById(this.around()));
  /** The selected topic's own words, which the card shows before a line from its notes. */
  readonly focusedWords = computed(() => this.state.topicById(this.focus())?.description?.trim() || null);
  readonly countText = computed(() => plural(this.state.sidebarTopics().length, 'topic'));

  readonly scope = signal<MapScopeInfo>({
    totalInScope: 0,
    shownCount: 0,
    maxShared: 1,
    effectiveMin: 1,
    capped: false,
    hiddenByKindCount: 0,
  });
  readonly effectiveMin = computed(() => this.min() ?? this.scope().effectiveMin);
  readonly maxShared = computed(() => Math.max(1, this.scope().maxShared));
  readonly sliderMaxShared = computed(() => Math.max(this.maxShared(), this.effectiveMin(), 1));
  readonly scopeCountText = computed(() => {
    if (!this.state.topicsLoaded()) return '';
    const info = this.scope();
    const hiddenSuffix =
      info.hiddenByKindCount > 0 ? ` (${info.hiddenByKindCount} hidden by kind)` : '';
    if (info.capped) {
      // One total everywhere: the scope AFTER the kind filter. Hidden topics
      // are stated separately, never added back into the denominator.
      return `Showing ${info.shownCount} of ${info.totalInScope} topics${hiddenSuffix}`;
    }
    return `${plural(info.shownCount, 'topic')}${hiddenSuffix}`;
  });

  readonly controlsOpen = signal(false);

  readonly finder = signal('');
  readonly finderOpen = signal(false);
  readonly finderActive = signal(0);
  /** Only topics the current map draws can be found on it. */
  private readonly choices = computed(() => {
    const drawn = this.map()?.accessibleNodes() ?? [];
    if (drawn.length > 0) {
      return topicChoices(drawn.map((n) => ({ id: n.id, name: n.name, usageCount: n.usageCount })));
    }
    return this.around()
      ? topicChoices(this.state.sidebarTopics())
      : topicChoices(drawnTopics(this.state.sidebarTopics()));
  });
  readonly finds = computed(() => {
    const typed = this.finder().trim();
    return typed && this.finderOpen() ? rankTopicChoices(this.choices(), typed, [], FINDER_LIMIT) : [];
  });
  /** The highlighted find, kept inside the list as it shrinks. */
  readonly activeFind = computed(() => Math.min(this.finderActive(), Math.max(0, this.finds().length - 1)));

  /** A line from the selected topic's newest note, so the card says what the topic holds. */
  readonly sample = toSignal(
    toObservable(this.focus).pipe(
      // A topic with its own words shows those instead, so its notes are not asked for.
      switchMap((topicId) => !topicId || this.state.topicById(topicId)?.description?.trim()
        ? of(null)
        : this.notes.browse({ topicId, order: 'newest', limit: 1, offset: 0 }).pipe(
          map((page) => {
            const note = page.items[0];
            if (!note) return null;
            const quote = note.selectedText?.trim();
            if (quote) return { text: quote, quote: true };
            const words = noteWikilinksToText(thoughtForDisplay(note.content)).trim();
            return words ? { text: words, quote: false } : null;
          }),
          catchError(() => of(null)),
          startWith(null),
        )),
    ),
    { initialValue: null },
  );

  constructor() {
    this.state.ensureLoaded();
    this.state.ensureKindsLoaded();
  }

  get controlsOrigin(): HTMLElement | null {
    return this.controlsTrigger()?.nativeElement ?? null;
  }

  keepCamera(camera: MapCamera): void {
    this.state.mapView.set({
      camera,
      focus: this.focus(),
      around: this.around(),
      depth: this.depth(),
      min: this.min(),
      kinds: this.kinds(),
    });
  }

  onScopeInfo(info: MapScopeInfo): void {
    this.scope.set(info);
  }

  onColorModeChange(mode: MapColorMode): void {
    this.state.setMapColorMode(mode);
  }

  onToggleKind(key: string): void {
    const legendKeys = (this.map()?.kindLegend() ?? []).map((entry) => entry.key);
    const presentKeys =
      legendKeys.length > 0
        ? legendKeys
        : Array.from(new Set(this.topics().map((t) => kindFilterKeyOf(t.kindId))));
    if (presentKeys.length === 0) return;
    const next = toggleKindFilter(this.visibleKindKeys(), key, presentKeys);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { kinds: next },
      queryParamsHandling: 'merge',
    });
  }

  onClearKindFilter(): void {
    if (this.kinds() === null) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { kinds: null },
      queryParamsHandling: 'merge',
    });
  }

  openTopic(topicId: string | null = this.focus()): void {
    if (topicId) void this.router.navigate(['/brain/topics', topicId]);
  }

  mapAroundTopic(topicId: string): void {
    this.restoredDepth = LOCAL_MAP_DEPTH_MIN;
    this.restoredMin = null;
    this.controlsOpen.set(false);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { around: topicId, depth: null, min: null, focus: null },
      queryParamsHandling: 'merge',
    });
  }

  clearAround(): void {
    this.restoredDepth = LOCAL_MAP_DEPTH_MIN;
    this.restoredMin = null;
    this.controlsOpen.set(false);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { around: null, depth: null, min: null, focus: null },
      queryParamsHandling: 'merge',
    });
  }

  onDepthInput(value: string): void {
    this.setDepth(parseDepth(value, this.depth()));
  }

  setDepth(depth: number): void {
    const clamped = Math.max(LOCAL_MAP_DEPTH_MIN, Math.min(LOCAL_MAP_DEPTH_MAX, Math.floor(depth)));
    this.restoredDepth = LOCAL_MAP_DEPTH_MIN;
    if (clamped === this.depth() && this.queryParams().get('depth') === String(clamped)) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { depth: String(clamped) },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  onMinInput(value: string): void {
    const parsed = parseMin(value, 1) ?? 1;
    this.setMin(parsed);
  }

  setMin(min: number): void {
    const clamped = Math.max(1, Math.floor(min));
    this.restoredMin = null;
    if (clamped === this.min() && this.queryParams().get('min') === String(clamped)) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { min: String(clamped) },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  // --- The finder -----------------------------------------------------------

  onFinderInput(value: string): void {
    this.finder.set(value);
    this.finderOpen.set(true);
    this.finderActive.set(0);
  }

  onFinderKeydown(event: KeyboardEvent): void {
    const finds = this.finds();
    if (event.key === 'Escape') {
      if (this.finder()) {
        event.preventDefault();
        this.finder.set('');
      }
      return;
    }
    if (!finds.length) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const step = event.key === 'ArrowDown' ? 1 : -1;
      this.finderActive.set((this.activeFind() + step + finds.length) % finds.length);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      this.find(finds[this.activeFind()].id);
    }
  }

  /** Selects a found topic and brings it to the middle of the map. */
  find(topicId: string): void {
    this.finder.set('');
    this.map()?.focusTopic(topicId);
  }

  openDrawer(): void {
    this.state.drawerOpen.set(true);
  }

  /** The selection lives in the URL (replacing, so Back leaves the map rather than stepping through clicks). */
  setFocus(topicId: string | null): void {
    if (topicId === this.focus()) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { focus: topicId },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
}
