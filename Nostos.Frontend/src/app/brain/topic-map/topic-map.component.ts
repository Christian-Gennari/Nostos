import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  EventEmitter,
  HostListener,
  inject,
  Input,
  OnChanges,
  OnDestroy,
  Output,
  signal,
  SimpleChanges,
  ViewChild,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { Subscription } from 'rxjs';

import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { ButtonComponent } from '../../ui/button/button.component';
import { ChipComponent } from '../../ui/chip/chip.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';
import {
  TopicDto,
  TopicKindDto,
  TopicsService,
  TopicGraphDto,
  TopicGraphNodeDto,
} from '../../core/services/topics.service';
import type { MapColorMode } from '../brain.helpers';

import {
  LOCAL_MAP_DEPTH_MAX,
  LOCAL_MAP_DEPTH_MIN,
  MAX_MAP_TOPICS,
  buildKindLegendEntries,
  drawnTopics,
  filterScopedGraphByKind,
  filterTopicsByKind,
  kindCssVar,
  kindFilterKeyOf,
  scopeGraph,
  type KindLegendEntry,
  type MapCamera,
} from './topic-map.helpers';
import { MapKindLegendComponent } from './map-kind-legend.component';
import { TopicMapRenderer } from './topic-map-renderer';
import { TopicMapSimulation } from './topic-map-simulation';
import { TopicMapViewport } from './topic-map-viewport';

export { MAX_MAP_TOPICS } from './topic-map.helpers';
export type { MapCamera } from './topic-map.helpers';

/** How many related topics the selection card lists. */
const SELECTION_RELATED_MAX = 4;
/** Share of total graph topics above which a large depth >= 2 scope defaults to strength 2. */
const AUTO_MIN_SHARED_RATIO = 0.6;

export interface MapScopeInfo {
  totalInScope: number;
  shownCount: number;
  maxShared: number;
  effectiveMin: number;
  capped: boolean;
  hiddenByKindCount: number;
}

interface MapModel {
  names: Map<string, string>;
  usage: Map<string, number>;
  /** Neighbours by shared notes, strongest first. */
  neighbours: Map<string, Array<{ id: string; sharedNotes: number }>>;
  hue: Map<string, string | null>;
  kindId: Map<string, string | null>;
}

/** Where the map's camera stands, as Sigma reports it. */
export interface MapSelection {
  id: string;
  name: string;
  usageCount: number;
  connectionCount: number;
  hue: string | null;
  kindId: string | null;
  kindName: string | null;
  kindColor: string | null;
  related: Array<{ id: string; name: string; sharedNotes: number }>;
}

@Component({
  selector: 'app-topic-map',
  standalone: true,
  imports: [
    CommonModule,
    IconButtonComponent,
    ButtonComponent,
    ChipComponent,
    NostosIconComponent,
    LoadingIndicatorComponent,
    EmptyStateComponent,
    MapKindLegendComponent,
  ],
  templateUrl: './topic-map.component.html',
  styleUrl: './topic-map.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TopicMapComponent implements OnChanges, AfterViewInit, OnDestroy {
  @Input() topics: TopicDto[] = [];
  @Input() selectedId: string | null = null;
  @Input() selectedName: string | null = null;
  @Input() searchQuery = '';
  @Input() camera: MapCamera | null = null;
  @Input() centerOnLoad = false;
  @Input() notesLabel = 'Read notes';
  @Input() rootId: string | null = null;
  @Input() depth = LOCAL_MAP_DEPTH_MIN;
  @Input() minShared: number | null = null;
  @Input() colorMode: MapColorMode = 'cluster';
  @Input() topicKinds: TopicKindDto[] = [];
  /**
   * Whether the kind list itself arrived. When it did not, the map cannot
   * colour or name kinds, so it falls back to Cluster and hides the legend
   * rather than presenting every typed topic as "No kind".
   */
  @Input() kindsLoaded = true;
  /** Visible kind keys (UUIDs or `'none'` for untyped); null means all kinds are shown. */
  @Input() visibleKindKeys: ReadonlySet<string> | null = null;
  /**
   * Makes the selected-topic card's kind pill a button. Off by default, so the
   * pill is plain text until the kind picker (#1769) is wired to
   * `kindPickerRequested`.
   */
  @Input() kindPickerEnabled = false;

  @Output() readonly cameraKept = new EventEmitter<MapCamera>();
  @Output() readonly topicSelected = new EventEmitter<string>();
  @Output() readonly openNotes = new EventEmitter<void>();
  @Output() readonly openTopic = new EventEmitter<string>();
  @Output() readonly selectionCleared = new EventEmitter<void>();
  @Output() readonly mapAround = new EventEmitter<string>();
  @Output() readonly scopeInfo = new EventEmitter<MapScopeInfo>();
  @Output() readonly kindToggled = new EventEmitter<string>();
  @Output() readonly clearKindFilter = new EventEmitter<void>();
  /** Asks the page to open the kind picker for this topic (wired by #1769). */
  @Output() readonly kindPickerRequested = new EventEmitter<{
    topicId: string;
    kindId: string | null;
    /** The pill that was clicked, so the panel hangs from it rather than the page body. */
    origin: HTMLElement | null;
  }>();

  @ViewChild('sigmaContainer', { static: false }) sigmaContainer!: ElementRef<HTMLDivElement>;
  @ViewChild('mapStage', { static: false }) mapStage!: ElementRef<HTMLElement>;
  @ViewChild('mapHud', { static: false }) mapHud?: ElementRef<HTMLElement>;
  @ViewChild('mapLegend', { static: false }) mapLegend?: ElementRef<HTMLElement>;

  private readonly topicsService = inject(TopicsService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly renderer = new TopicMapRenderer();
  private readonly simulation = new TopicMapSimulation();
  private readonly viewport = new TopicMapViewport();

  private graphRequest: Subscription | null = null;
  private graphData: TopicGraphDto | null = null;
  private destroyed = false;
  private viewInitialized = false;
  private pendingRebuild = false;
  private lastBuilt = false;
  private lastRootId: string | null = null;
  private lastDepth = LOCAL_MAP_DEPTH_MIN;
  private lastEffectiveMin = 1;
  private activeRootId: string | null = null;
  /** The scope before the cap, and the topics actually drawn, for legend counts. */
  private lastLegendItems: ReadonlyArray<{ id: string; kindId?: string | null }> = [];
  private lastDrawnItems: ReadonlyArray<{ id: string; kindId?: string | null }> = [];

  /* State signals for the template. */
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly hoveredId = signal<string | null>(null);
  readonly selectedNodeId = signal<string | null>(null);
  readonly isFullscreen = signal(false);
  readonly sourceCount = signal(0);
  readonly scopeTotal = signal(0);
  readonly shownCount = signal(0);
  readonly hiddenByKindCount = signal(0);
  readonly isLocal = signal(false);
  readonly isCapped = computed(() =>
    this.isLocal() ? this.scopeTotal() > MAX_MAP_TOPICS : this.sourceCount() > MAX_MAP_TOPICS
  );
  readonly noConnections = signal(false);
  readonly allHiddenByKind = computed(() => this.hiddenByKindCount() > 0 && this.shownCount() === 0);
  readonly kindLegend = signal<KindLegendEntry[]>([]);
  readonly accessibleNodes = signal<
    Array<{ id: string; name: string; usageCount: number; connectionCount: number }>
  >([]);

  /**
   * A reactive copy of the kind inputs. The selection card is a computed over
   * the model, so it has to depend on a signal to see a new kind list.
   */
  private readonly kindView = signal<{
    colorMode: MapColorMode;
    topicKinds: TopicKindDto[];
    kindsLoaded: boolean;
    kindPickerEnabled: boolean;
  }>({
    colorMode: 'cluster',
    topicKinds: [],
    kindsLoaded: true,
    kindPickerEnabled: false,
  });

  private readonly model = signal<MapModel | null>(null);

  /** The selected topic, as the selection card presents it. */
  readonly selection = computed<MapSelection | null>(() => {
    const id = this.selectedNodeId();
    const model = this.model();
    if (!id || !model || !model.names.has(id)) return null;
    const neighbours = model.neighbours.get(id) ?? [];
    const kindId = model.kindId.get(id) ?? null;
    const { topicKinds } = this.kindView();
    const kind = kindId ? topicKinds.find((k) => k.id === kindId) ?? null : null;
    return {
      id,
      name: model.names.get(id)!,
      usageCount: model.usage.get(id) ?? 0,
      connectionCount: neighbours.length,
      hue: model.hue.get(id) ?? null,
      kindId: kind ? kind.id : null,
      kindName: kind ? kind.name : null,
      kindColor: kind ? kindCssVar(kind.colorKey) : null,
      related: neighbours.slice(0, SELECTION_RELATED_MAX).map((n) => ({
        id: n.id,
        name: model.names.get(n.id) ?? n.id,
        sharedNotes: n.sharedNotes,
      })),
    };
  });

  /** In Kind mode an untyped topic still says so; other modes show no pill for it. */
  readonly showUntypedCardPill = computed(() => {
    const { colorMode, kindsLoaded, kindPickerEnabled } = this.kindView();
    // Without the kind list a typed topic's kind name cannot be resolved, so
    // the card would call it "No kind" — worse than saying nothing.
    if (!kindsLoaded) return false;
    return colorMode === 'kind' || kindPickerEnabled;
  });

  @HostListener('document:keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && this.isFullscreen()) {
      void this.viewport.exitFullscreen();
    }
  }

  /* ── Lifecycle ── */

  ngOnChanges(changes: SimpleChanges): void {
    const colourChanged = !!(
      changes['colorMode'] ||
      changes['topicKinds'] ||
      changes['kindsLoaded']
    );
    if (colourChanged || changes['kindPickerEnabled']) {
      this.kindView.set({
        colorMode: this.colorMode,
        topicKinds: this.topicKinds ?? [],
        kindsLoaded: this.kindsLoaded,
        kindPickerEnabled: this.kindPickerEnabled,
      });
    }
    if (changes['selectedId']) {
      this.selectedNodeId.set(this.selectedId);
      this.renderer.refresh();
    }
    if (changes['topics']) {
      const nextTopics = this.topics ?? [];
      this.sourceCount.set(nextTopics.length);
      // A kind (or the topic's own words) changing on the topics already drawn
      // is not a different graph. Reading the graph back and disposing Sigma
      // for it dropped the reader's view and re-ran the layout for a colour;
      // only a change to which nodes the kind filter draws needs a rebuild, and
      // that one can reuse the graph in hand.
      if (
        this.isTopicMetadataUpdate(
          changes['topics'].previousValue as TopicDto[] | undefined,
          nextTopics
        )
      ) {
        this.applyTopicMetadata(
          changes['topics'].previousValue as TopicDto[],
          nextTopics
        );
        return;
      }
      this.loadGraphData();
      return;
    }
    // Colour and kind metadata change how the graph LOOKS, not which graph it
    // is. Rebuilding Sigma for them disposed a renderer mid-mount-animation and
    // the carried camera stranded the map at its default or half-zoomed view.
    if (colourChanged && this.graphData && !this.loading() && this.lastBuilt) {
      this.applyKindAppearance();
    }
    if (
      (changes['rootId'] ||
        changes['depth'] ||
        changes['minShared'] ||
        changes['visibleKindKeys']) &&
      this.graphData &&
      !this.loading()
    ) {
      if (this.viewInitialized) {
        this.rebuildSigma();
      } else {
        this.pendingRebuild = true;
      }
    }
  }

  ngAfterViewInit(): void {
    this.viewInitialized = true;
    this.viewport.attachFullscreen(
      this.mapStage.nativeElement,
      (fullscreen) => this.isFullscreen.set(fullscreen),
      () => this.renderer.refresh()
    );
    if (this.pendingRebuild) {
      this.pendingRebuild = false;
      this.rebuildSigma();
    }
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.graphRequest?.unsubscribe();
    this.graphRequest = null;
    const camera = this.renderer.getCameraState();
    if (camera) this.cameraKept.emit(camera);
    this.disposeSigma();
    this.viewport.dispose();
  }

  /* ── Data loading ── */

  /** "Try again" after the graph failed to load. */
  retryLoad(): void {
    this.loadGraphData();
  }

  private loadGraphData(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    // A newer topic set supersedes a graph still on its way.
    this.graphRequest?.unsubscribe();
    this.graphRequest = this.topicsService
      .getGraph()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (data) => {
          if (this.destroyed) return;
          this.graphData = data;
          this.loading.set(false);
          if (this.viewInitialized) {
            this.rebuildSigma();
          } else {
            this.pendingRebuild = true;
          }
        },
        error: () => {
          if (this.destroyed) return;
          this.graphData = { nodes: [], edges: [] };
          this.lastBuilt = false;
          this.loadFailed.set(true);
          this.loading.set(false);
        },
      });
  }

  /* ── Graph construction ── */

  /**
   * The colour mode the map can actually paint. Without the kind list, Kind
   * mode would paint every typed topic with the untyped neutral, so Cluster is
   * the honest fallback.
   */
  private effectiveColorMode(): MapColorMode {
    return this.kindsLoaded ? this.colorMode : 'cluster';
  }

  /** Recolour the mounted graph after kind metadata or the colour mode changed. */
  private applyKindAppearance(): void {
    // With every topic hidden by the kind filter there is no Sigma to recolour,
    // but the legend is exactly what the reader needs to bring the topics back.
    if (!this.lastBuilt) return;
    if (this.renderer.graphInstance) {
      const hueById = this.renderer.recolor(this.effectiveColorMode(), this.topicKinds ?? []);
      this.model.update((model) => (model ? { ...model, hue: hueById } : model));
      this.renderer.refresh();
    }
    this.updateKindLegend();
  }

  /**
   * Whether a new topic list describes the same graph: the same ids and the same
   * reference counts, so only kind (or the topic's own words) differs.
   */
  private isTopicMetadataUpdate(
    previous: readonly TopicDto[] | undefined,
    next: readonly TopicDto[]
  ): boolean {
    if (!this.lastBuilt || !previous || previous.length !== next.length) return false;
    const before = new Map(previous.map((topic) => [topic.id, topic]));
    return next.every((topic) => {
      const old = before.get(topic.id);
      return !!old && old.usageCount === topic.usageCount && old.name === topic.name;
    });
  }

  /**
   * Show new kind metadata on the graph already drawn, without reading the graph
   * back: the nodes, the edges and the layout are unchanged. Only when the kind
   * filter now draws a different set does the graph have to be built again, and
   * even then from the data in hand.
   */
  private applyTopicMetadata(
    previous: readonly TopicDto[],
    next: readonly TopicDto[]
  ): void {
    const before = new Map(previous.map((topic) => [topic.id, topic]));
    const kindById = new Map(next.map((topic) => [topic.id, topic.kindId ?? null]));
    const withKind = <T extends { id: string; kindId?: string | null }>(items: readonly T[]): T[] =>
      items.map((item) =>
        kindById.has(item.id) ? { ...item, kindId: kindById.get(item.id)! } : item
      );

    const visibleKindKeys = this.visibleKindKeys;
    const drawingChanged =
      visibleKindKeys !== null &&
      next.some((topic) => {
        const old = before.get(topic.id);
        if (!old) return false;
        const wasDrawn = visibleKindKeys.has(kindFilterKeyOf(old.kindId));
        const isDrawn = visibleKindKeys.has(kindFilterKeyOf(topic.kindId));
        return wasDrawn !== isDrawn;
      });

    if (this.graphData) {
      this.graphData = { nodes: withKind(this.graphData.nodes), edges: this.graphData.edges };
    }
    this.lastLegendItems = withKind(this.lastLegendItems);
    this.lastDrawnItems = withKind(this.lastDrawnItems);

    if (drawingChanged) {
      // No request: the graph the map was built from is still the right one.
      this.rebuildSigma();
      return;
    }

    const graph = this.renderer.graphInstance;
    if (graph) {
      graph.forEachNode((node) => {
        if (!kindById.has(node)) return;
        graph.setNodeAttribute(node, 'kindId', kindById.get(node)!);
      });
    }
    this.model.update((model) => {
      if (!model) return model;
      const kindId = new Map(model.kindId);
      for (const [id, value] of kindById) {
        if (kindId.has(id)) kindId.set(id, value);
      }
      return { ...model, kindId };
    });
    this.applyKindAppearance();
  }

  /** Recompute the legend from the scope and the topics actually drawn. */
  private updateKindLegend(): void {
    const showLegend = this.kindsLoaded && this.effectiveColorMode() === 'kind';
    this.kindLegend.set(
      showLegend
        ? buildKindLegendEntries(this.lastLegendItems, this.topicKinds ?? [], this.visibleKindKeys, {
            drawnItems: this.lastDrawnItems,
            kindsLoaded: this.kindsLoaded,
          })
        : []
    );
  }

  private rebuildSigma(): void {
    if (!this.graphData || !this.sigmaContainer?.nativeElement) {
      this.disposeSigma();
      return;
    }

    const container = this.sigmaContainer.nativeElement;
    const normalizedRootId = this.rootId?.trim() || null;
    const clampedDepth = Math.max(
      LOCAL_MAP_DEPTH_MIN,
      Math.min(LOCAL_MAP_DEPTH_MAX, Math.floor(this.depth || LOCAL_MAP_DEPTH_MIN))
    );
    const explicitMin =
      this.minShared !== null && this.minShared !== undefined && Number.isFinite(this.minShared)
        ? Math.max(1, Math.floor(this.minShared))
        : null;

    const allTopics = this.topics ?? [];
    const topicKindById = new Map(allTopics.map((t) => [t.id, t.kindId ?? null]));
    const enrichedGraphNodes = this.graphData.nodes.map((node) => ({
      ...node,
      kindId: node.kindId !== undefined ? node.kindId : (topicKindById.get(node.id) ?? null),
    }));
    const nodeKindById = new Map(enrichedGraphNodes.map((n) => [n.id, n.kindId ?? null]));

    let visibleNodes: TopicGraphNodeDto[] = enrichedGraphNodes;
    let visibleEdges = this.graphData.edges;
    let hops: Map<string, number> | null = null;
    let totalInScope = 0;
    let filteredSourceTotal = allTopics.length;
    let hiddenByKind = 0;
    let maxShared = 1;
    let effectiveMin = explicitMin ?? 1;
    let legendItems: ReadonlyArray<{ id: string; kindId?: string | null }> = [];

    if (normalizedRootId) {
      const allowedIds = new Set(allTopics.map((t) => t.id));
      const allowedGraph: TopicGraphDto = {
        nodes: enrichedGraphNodes.filter((n) => allowedIds.has(n.id)),
        edges: this.graphData.edges.filter((e) => allowedIds.has(e.sourceId) && allowedIds.has(e.targetId)),
      };
      const baseScope = scopeGraph(allowedGraph, normalizedRootId, clampedDepth, 1, Number.MAX_SAFE_INTEGER);
      for (const edge of baseScope.edges) {
        if (edge.sharedNotes > maxShared) maxShared = edge.sharedNotes;
      }
      if (explicitMin === null) {
        const totalNodes = allowedGraph.nodes.length;
        const denseDepth2Plus =
          clampedDepth >= 2 &&
          baseScope.totalInScope > MAX_MAP_TOPICS &&
          totalNodes > 0 &&
          baseScope.totalInScope / totalNodes > AUTO_MIN_SHARED_RATIO &&
          maxShared >= 2;
        effectiveMin = denseDepth2Plus ? 2 : 1;
      }
      // Scope the whole graph first, then filter, then cap: hiding a kind must
      // not cut the co-occurrence paths that define "within N steps", and the
      // filter must free cap room for the kinds that remain visible.
      const unfilteredScope = scopeGraph(
        allowedGraph,
        normalizedRootId,
        clampedDepth,
        effectiveMin,
        Number.MAX_SAFE_INTEGER
      );
      legendItems = unfilteredScope.nodes;

      const scoped = filterScopedGraphByKind(unfilteredScope, this.visibleKindKeys, normalizedRootId);
      visibleNodes = scoped.nodes;
      visibleEdges = scoped.edges;
      hops = scoped.hops;
      totalInScope = scoped.totalInScope;
      hiddenByKind = scoped.hiddenByKind;
    } else {
      const mergedTopics = allTopics.map((t) => ({
        ...t,
        kindId: t.kindId !== undefined ? t.kindId : (nodeKindById.get(t.id) ?? null),
      }));
      legendItems = mergedTopics;

      const filteredTopics = filterTopicsByKind(mergedTopics, this.visibleKindKeys);
      filteredSourceTotal = filteredTopics.length;
      hiddenByKind = mergedTopics.length - filteredTopics.length;

      const visibleIds = new Set(drawnTopics(filteredTopics).map((c) => c.id));
      visibleNodes = enrichedGraphNodes.filter((n) => visibleIds.has(n.id));
      const candidateEdges = this.graphData.edges.filter(
        (e) => visibleIds.has(e.sourceId) && visibleIds.has(e.targetId) && e.sourceId !== e.targetId
      );
      for (const edge of candidateEdges) {
        if (edge.sharedNotes > maxShared) maxShared = edge.sharedNotes;
      }
      visibleEdges = candidateEdges.filter((e) => e.sharedNotes >= effectiveMin);
      totalInScope = filteredSourceTotal;
    }

    this.lastLegendItems = legendItems;
    this.lastDrawnItems = visibleNodes;
    this.updateKindLegend();

    const scopeChanged =
      this.lastBuilt &&
      (this.lastRootId !== normalizedRootId ||
        this.lastDepth !== clampedDepth ||
        this.lastEffectiveMin !== effectiveMin);
    const keepPositions =
      this.lastBuilt &&
      (!scopeChanged ||
        (normalizedRootId !== null &&
          this.lastRootId === normalizedRootId &&
          (this.lastDepth !== clampedDepth || this.lastEffectiveMin !== effectiveMin)));

    let previousPositions: Map<string, { x: number; y: number }> | undefined;
    if (keepPositions && this.renderer.graphInstance) {
      previousPositions = new Map<string, { x: number; y: number }>();
      this.renderer.graphInstance.forEachNode((node, attrs) => {
        previousPositions!.set(node, { x: Number(attrs['x']) || 0, y: Number(attrs['y']) || 0 });
      });
    }

    const keepsView =
      this.lastBuilt && !scopeChanged && (this.camera !== null || this.centerOnLoad);
    this.viewport.captureCarriedCamera(this.renderer.sigmaInstance, keepsView);
    this.disposeSigma();

    this.lastBuilt = true;
    this.lastRootId = normalizedRootId;
    this.lastDepth = clampedDepth;
    this.lastEffectiveMin = effectiveMin;
    this.activeRootId = normalizedRootId;

    this.isLocal.set(normalizedRootId !== null);
    this.sourceCount.set(filteredSourceTotal);
    this.scopeTotal.set(totalInScope);
    this.shownCount.set(visibleNodes.length);
    this.hiddenByKindCount.set(hiddenByKind);
    this.scopeInfo.emit({
      totalInScope,
      shownCount: visibleNodes.length,
      maxShared,
      effectiveMin,
      capped: this.isCapped(),
      hiddenByKindCount: hiddenByKind,
    });

    if (visibleNodes.length === 0) {
      this.model.set(null);
      this.noConnections.set(hiddenByKind === 0);
      this.accessibleNodes.set([]);
      return;
    }

    const { graph, hueById } = this.renderer.buildGraph(visibleNodes, visibleEdges, {
      rootId: normalizedRootId,
      initialPositions: previousPositions,
      colorMode: this.effectiveColorMode(),
      topicKinds: this.topicKinds ?? [],
    });

    // Build the selection card's view of the graph.
    const names = new Map(visibleNodes.map((n) => [n.id, n.name]));
    const neighbours = new Map<string, Array<{ id: string; sharedNotes: number }>>();
    for (const edge of visibleEdges) {
      if (edge.sourceId === edge.targetId) continue;
      for (const [from, to] of [
        [edge.sourceId, edge.targetId],
        [edge.targetId, edge.sourceId],
      ]) {
        const list = neighbours.get(from) ?? [];
        if (!list.some((n) => n.id === to)) list.push({ id: to, sharedNotes: edge.sharedNotes });
        neighbours.set(from, list);
      }
    }
    for (const list of neighbours.values()) {
      list.sort(
        (a, b) =>
          b.sharedNotes - a.sharedNotes ||
          (names.get(a.id) ?? '').localeCompare(names.get(b.id) ?? '')
      );
    }
    this.model.set({
      names,
      usage: new Map(visibleNodes.map((n) => [n.id, n.usageCount])),
      neighbours,
      hue: hueById,
      kindId: new Map(visibleNodes.map((n) => [n.id, n.kindId ?? null])),
    });

    this.noConnections.set(normalizedRootId ? false : graph.size === 0);

    const connectionCounts = new Map<string, number>();
    graph.forEachEdge((_edge, _attrs, source, target) => {
      connectionCounts.set(source, (connectionCounts.get(source) ?? 0) + 1);
      connectionCounts.set(target, (connectionCounts.get(target) ?? 0) + 1);
    });
    this.accessibleNodes.set(
      visibleNodes.map((n) => ({
        id: n.id,
        name: n.name,
        usageCount: n.usageCount,
        connectionCount: connectionCounts.get(n.id) ?? 0,
      }))
    );

    this.simulation.initLayout({
      graph,
      visibleNodes,
      visibleEdges,
      stageWidth: container.clientWidth,
      stageHeight: container.clientHeight,
      rootId: normalizedRootId,
      hops,
      keepPositions,
    });

    const sigma = this.renderer.mount(graph, container, {
      getHoveredId: () => this.hoveredId(),
      getSelectedId: () => this.selectedNodeId(),
      getRootId: () => this.activeRootId,
      isDragging: () => this.simulation.isDragging,
      hasDraggedNode: () => !!this.simulation.activeDraggedNode,
      onHoverChange: (id) => this.hoveredId.set(id),
      onSelectNode: (id) => {
        this.selectedNodeId.set(id);
        this.topicSelected.emit(id);
        this.revealSelected();
      },
      onOpenTopic: (id) => {
        this.selectedNodeId.set(id);
        this.openTopic.emit(id);
      },
      onClearSelection: () => {
        this.hoveredId.set(null);
        this.selectedNodeId.set(null);
        this.selectionCleared.emit();
      },
    });

    this.simulation.attachDrag({
      sigma,
      graph,
      container,
      onRefresh: () => this.renderer.refresh(),
    });

    // Keep the renderer and the simulation reachable for diagnostics.
    const globals = globalThis as unknown as {
      __nostosSigma?: unknown;
      __nostosGraph?: unknown;
      __nostosLayout?: unknown;
    };
    globals.__nostosSigma = sigma;
    globals.__nostosGraph = graph;
    globals.__nostosLayout = this.simulation.simulation;

    this.viewport.observeStage({
      container,
      getSigma: () => this.renderer.sigmaInstance,
      getGraph: () => this.renderer.graphInstance,
      getSelectedId: () => this.selectedNodeId(),
      getCameraInput: () => this.camera,
      getCenterOnLoad: () => this.centerOnLoad,
      getOverlayElements: () => ({
        legend: this.mapLegend?.nativeElement,
        hud: this.mapHud?.nativeElement,
      }),
    });
  }

  private revealSelected(): void {
    this.viewport.revealSelected({
      getSigma: () => this.renderer.sigmaInstance,
      getGraph: () => this.renderer.graphInstance,
      getSelectedId: () => this.selectedNodeId(),
      getContainerEl: () => this.sigmaContainer?.nativeElement,
      getHudEl: () => this.mapHud?.nativeElement,
    });
  }

  private disposeSigma(): void {
    this.viewport.disposeRebuildObservers();
    this.simulation.dispose();
    this.renderer.dispose();
  }

  /* ── Template actions ── */

  /** Frame the whole graph. */
  fitGraph(): void {
    this.viewport.fitGraph(
      this.renderer.sigmaInstance,
      this.renderer.graphInstance,
      this.sigmaContainer?.nativeElement,
      this.overlays()
    );
  }

  /** Restore the settled layout and re-frame it. */
  resetView(): void {
    if (this.simulation.resetToHome()) {
      this.renderer.refresh();
    }
    this.fitGraph();
  }

  /** The overlays that steal map area, measured from the live DOM. */
  private overlays(): { legend: HTMLElement | undefined; hud: HTMLElement | undefined } {
    return { legend: this.mapLegend?.nativeElement, hud: this.mapHud?.nativeElement };
  }

  /** Bring the selected node to the centre of the visible map area. */
  centerSelected(): void {
    this.viewport.centerSelected(
      this.renderer.sigmaInstance,
      this.renderer.graphInstance,
      this.selectedNodeId(),
      this.viewport.overlayInsets(this.sigmaContainer?.nativeElement, this.overlays())
    );
  }

  async toggleFullscreen(): Promise<void> {
    await this.viewport.toggleFullscreen(this.isFullscreen());
  }

  /** Close the selection card: the same as clicking empty space. */
  clearSelection(): void {
    this.hoveredId.set(null);
    this.selectedNodeId.set(null);
    this.selectionCleared.emit();
    this.renderer.refresh();
  }

  /** Select a related topic from the card and bring it into view. */
  selectRelated(id: string): void {
    this.focusTopic(id);
  }

  /** Select a topic and bring it to the middle of the stage. */
  focusTopic(id: string): void {
    this.selectAccessibleNode(id);
    // The selection card that shrinks the visible band is painted on the next
    // change-detection pass; measure the HUD only after it exists.
    window.requestAnimationFrame(() => this.centerSelected());
  }

  onToggleKind(key: string): void {
    this.kindToggled.emit(key);
  }

  /**
   * The card's kind pill was clicked. The element travels with the request: the
   * popover hangs from what the reader actually touched. `document.activeElement`
   * is `<body>` in Safari after a touch, which anchored the panel to the page.
   */
  requestKindPicker(event: Event, selection: MapSelection): void {
    const origin = event.currentTarget ?? event.target;
    this.kindPickerRequested.emit({
      topicId: selection.id,
      kindId: selection.kindId,
      origin: origin instanceof HTMLElement ? origin : null,
    });
  }

  zoomIn(): void {
    this.viewport.zoomIn(this.renderer.sigmaInstance);
  }

  zoomOut(): void {
    this.viewport.zoomOut(this.renderer.sigmaInstance);
  }

  selectAccessibleNode(id: string): void {
    this.selectedNodeId.set(id);
    this.topicSelected.emit(id);
    this.renderer.refresh();
  }
}
