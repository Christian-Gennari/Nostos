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
  MAX_MAP_TOPICS,
  drawnTopics,
  type MapCamera,
} from './topic-map.helpers';
import { TopicMapRenderer } from './topic-map-renderer';
import { TopicMapSimulation } from './topic-map-simulation';
import { TopicMapViewport } from './topic-map-viewport';

export { MAX_MAP_TOPICS } from './topic-map.helpers';
export type { MapCamera } from './topic-map.helpers';

/** How many related topics the selection card lists. */
const SELECTION_RELATED_MAX = 4;

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

  @Output() readonly cameraKept = new EventEmitter<MapCamera>();
  @Output() readonly topicSelected = new EventEmitter<string>();
  @Output() readonly openNotes = new EventEmitter<void>();
  @Output() readonly openTopic = new EventEmitter<string>();
  @Output() readonly selectionCleared = new EventEmitter<void>();

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

  /* State signals for the template. */
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly hoveredId = signal<string | null>(null);
  readonly selectedNodeId = signal<string | null>(null);
  readonly isFullscreen = signal(false);
  readonly sourceCount = signal(0);
  readonly isCapped = computed(() => this.sourceCount() > MAX_MAP_TOPICS);
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
          this.loadFailed.set(true);
          this.loading.set(false);
        },
      });
  }

  /* ── Graph construction ── */

  private rebuildSigma(): void {
    const keepsView = this.camera !== null || this.centerOnLoad;
    this.viewport.captureCarriedCamera(this.renderer.sigmaInstance, keepsView);
    this.disposeSigma();
    if (!this.graphData || !this.sigmaContainer?.nativeElement) return;

    const container = this.sigmaContainer.nativeElement;
    const visibleIds = new Set(drawnTopics(this.topics ?? []).map((c) => c.id));
    const visibleNodes = this.graphData.nodes.filter((n) => visibleIds.has(n.id));
    const visibleEdges = this.graphData.edges.filter(
      (e) => visibleIds.has(e.sourceId) && visibleIds.has(e.targetId)
    );

    if (visibleNodes.length === 0) {
      this.noConnections.set(true);
      this.accessibleNodes.set([]);
      return;
    }

    const { graph, hueById } = this.renderer.buildGraph(visibleNodes, visibleEdges);

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

    this.noConnections.set(graph.size === 0);

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
    });

    const sigma = this.renderer.mount(graph, container, {
      getHoveredId: () => this.hoveredId(),
      getSelectedId: () => this.selectedNodeId(),
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
