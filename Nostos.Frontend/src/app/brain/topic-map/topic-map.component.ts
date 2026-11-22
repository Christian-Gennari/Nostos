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
  TopicsService,
  TopicGraphDto,
} from '../../core/services/topics.service';

import {
  LOCAL_MAP_DEPTH_MAX,
  LOCAL_MAP_DEPTH_MIN,
  MAX_MAP_TOPICS,
  drawnTopics,
  scopeGraph,
  type MapCamera,
} from './topic-map.helpers';
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
}

interface MapModel {
  names: Map<string, string>;
  usage: Map<string, number>;
  /** Neighbours by shared notes, strongest first. */
  neighbours: Map<string, Array<{ id: string; sharedNotes: number }>>;
  hue: Map<string, string | null>;
}

/** Where the map's camera stands, as Sigma reports it. */
export interface MapSelection {
  id: string;
  name: string;
  usageCount: number;
  connectionCount: number;
  hue: string | null;
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

  @Output() readonly cameraKept = new EventEmitter<MapCamera>();
  @Output() readonly topicSelected = new EventEmitter<string>();
  @Output() readonly openNotes = new EventEmitter<void>();
  @Output() readonly openTopic = new EventEmitter<string>();
  @Output() readonly selectionCleared = new EventEmitter<void>();
  @Output() readonly mapAround = new EventEmitter<string>();
  @Output() readonly scopeInfo = new EventEmitter<MapScopeInfo>();

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

  /* State signals for the template. */
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly hoveredId = signal<string | null>(null);
  readonly selectedNodeId = signal<string | null>(null);
  readonly isFullscreen = signal(false);
  readonly sourceCount = signal(0);
  readonly scopeTotal = signal(0);
  readonly shownCount = signal(0);
  readonly isLocal = signal(false);
  readonly isCapped = computed(() =>
    this.isLocal() ? this.scopeTotal() > MAX_MAP_TOPICS : this.sourceCount() > MAX_MAP_TOPICS
  );
  readonly noConnections = signal(false);
  readonly accessibleNodes = signal<
    Array<{ id: string; name: string; usageCount: number; connectionCount: number }>
  >([]);

  private readonly model = signal<MapModel | null>(null);

  /** The selected topic, as the selection card presents it. */
  readonly selection = computed<MapSelection | null>(() => {
    const id = this.selectedNodeId();
    const model = this.model();
    if (!id || !model || !model.names.has(id)) return null;
    const neighbours = model.neighbours.get(id) ?? [];
    return {
      id,
      name: model.names.get(id)!,
      usageCount: model.usage.get(id) ?? 0,
      connectionCount: neighbours.length,
      hue: model.hue.get(id) ?? null,
      related: neighbours.slice(0, SELECTION_RELATED_MAX).map((n) => ({
        id: n.id,
        name: model.names.get(n.id) ?? n.id,
        sharedNotes: n.sharedNotes,
      })),
    };
  });

  @HostListener('document:keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && this.isFullscreen()) {
      void this.viewport.exitFullscreen();
    }
  }

  /* ── Lifecycle ── */

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['selectedId']) {
      this.selectedNodeId.set(this.selectedId);
      this.renderer.refresh();
    }
    if (changes['topics']) {
      this.sourceCount.set((this.topics ?? []).length);
      this.loadGraphData();
      return;
    }
    if ((changes['rootId'] || changes['depth'] || changes['minShared']) && this.graphData && !this.loading()) {
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

    let visibleNodes = this.graphData.nodes;
    let visibleEdges = this.graphData.edges;
    let hops: Map<string, number> | null = null;
    let totalInScope = 0;
    let maxShared = 1;
    let effectiveMin = explicitMin ?? 1;

    if (normalizedRootId) {
      const allowedIds = new Set((this.topics ?? []).map((t) => t.id));
      const allowedGraph: TopicGraphDto = {
        nodes: this.graphData.nodes.filter((n) => allowedIds.has(n.id)),
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
      const scoped = scopeGraph(allowedGraph, normalizedRootId, clampedDepth, effectiveMin, MAX_MAP_TOPICS);
      visibleNodes = scoped.nodes;
      visibleEdges = scoped.edges;
      hops = scoped.hops;
      totalInScope = scoped.totalInScope;
    } else {
      const visibleIds = new Set(drawnTopics(this.topics ?? []).map((c) => c.id));
      visibleNodes = this.graphData.nodes.filter((n) => visibleIds.has(n.id));
      const candidateEdges = this.graphData.edges.filter(
        (e) => visibleIds.has(e.sourceId) && visibleIds.has(e.targetId) && e.sourceId !== e.targetId
      );
      for (const edge of candidateEdges) {
        if (edge.sharedNotes > maxShared) maxShared = edge.sharedNotes;
      }
      visibleEdges = candidateEdges.filter((e) => e.sharedNotes >= effectiveMin);
      totalInScope = (this.topics ?? []).length;
    }

    const scopeChanged =
      this.lastBuilt &&
      (this.lastRootId !== normalizedRootId ||
        this.lastDepth !== clampedDepth ||
        this.lastEffectiveMin !== effectiveMin);
    const keepPositions =
      this.lastBuilt &&
      normalizedRootId !== null &&
      this.lastRootId === normalizedRootId &&
      (this.lastDepth !== clampedDepth || this.lastEffectiveMin !== effectiveMin);

    let previousPositions: Map<string, { x: number; y: number }> | undefined;
    if (keepPositions && this.renderer.graphInstance) {
      previousPositions = new Map<string, { x: number; y: number }>();
      this.renderer.graphInstance.forEachNode((node, attrs) => {
        previousPositions!.set(node, { x: Number(attrs['x']) || 0, y: Number(attrs['y']) || 0 });
      });
    }

    const keepsView = !scopeChanged && (this.camera !== null || this.centerOnLoad);
    this.viewport.captureCarriedCamera(this.renderer.sigmaInstance, keepsView);
    this.disposeSigma();

    this.lastBuilt = true;
    this.lastRootId = normalizedRootId;
    this.lastDepth = clampedDepth;
    this.lastEffectiveMin = effectiveMin;
    this.activeRootId = normalizedRootId;

    this.isLocal.set(normalizedRootId !== null);
    this.scopeTotal.set(totalInScope);
    this.shownCount.set(visibleNodes.length);
    this.scopeInfo.emit({
      totalInScope,
      shownCount: visibleNodes.length,
      maxShared,
      effectiveMin,
      capped: this.isCapped(),
    });

    if (visibleNodes.length === 0) {
      this.model.set(null);
      this.noConnections.set(true);
      this.accessibleNodes.set([]);
      return;
    }

    const { graph, hueById } = this.renderer.buildGraph(visibleNodes, visibleEdges, {
      rootId: normalizedRootId,
      initialPositions: previousPositions,
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
      {
        legend: this.mapLegend?.nativeElement,
        hud: this.mapHud?.nativeElement,
      }
    );
  }

  /** Restore the settled layout and re-frame it. */
  resetView(): void {
    if (this.simulation.resetToHome()) {
      this.renderer.refresh();
    }
    this.fitGraph();
  }

  /** Bring the selected node to the centre of the stage. */
  centerSelected(): void {
    this.viewport.centerSelected(
      this.renderer.sigmaInstance,
      this.renderer.graphInstance,
      this.selectedNodeId()
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
    this.centerSelected();
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
