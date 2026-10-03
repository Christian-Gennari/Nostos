import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  EventEmitter,
  inject,
  Input,
  OnChanges,
  OnDestroy,
  Output,
  HostListener,
  signal,
  SimpleChanges,
  ViewChild,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import Graph from 'graphology';
import Sigma from 'sigma';
import {
  forceCollide,
  forceLink,
  forceManyBody,
  forceSimulation,
  forceX,
  forceY,
  type Simulation,
} from 'd3-force';

import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { ButtonComponent } from '../../ui/button/button.component';
import { ChipComponent } from '../../ui/chip/chip.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import {
  TopicDto,
  TopicsService,
  TopicGraphDto,
} from '../../core/services/topics.service';

import {
  MAX_MAP_TOPICS,
  NODE_SIZE_MIN,
  NODE_SIZE_MAX,
  EDGE_SIZE_MIN,
  EDGE_SIZE_MAX,
  LABEL_DRAW_SIZE,
  EDGE_ALPHA_MIN,
  EDGE_ALPHA_RANGE,
  NODE_ALPHA_DIM,
  EDGE_ALPHA_DIM,
  LABEL_RENDER_MIN_SIZE,
  LABEL_EDGE_ALLOWANCE_FRACTION,
  FIT_OCCUPANCY,
  seedPositions,
  type LayoutNode,
  DRAG_THRESHOLD_PX,
  OBSIDIAN_FORCES,
  OBSIDIAN_LINK_STRENGTH,
  SETTLE_ALPHA,
  DRAG_ALPHA,
  hexToRgba,
  mixHex,
  type ThemeColors,
  readTheme,
  drawThemeNodeHover,
  compareTopics,
  COMMUNITY_HUES,
  COMMUNITY_MIN_SIZE,
  LABEL_PRIORITY,
  type LabelCandidate,
  placeLabels,
  drawPlacedLabels,
  logicalCanvasWidth,
  pickHubs,
  detectCommunities,
  centerStrengths,
  shelfPositions,
  alignmentRotation,
  type DiscObstacle,
} from './topic-map.helpers';

export { MAX_MAP_TOPICS } from './topic-map.helpers';

/** How many related topics the selection card lists. */
const SELECTION_RELATED_MAX = 4;

/**
 * How far from a node, in CSS px, a tap still selects it on a touch screen.
 *
 * Sigma hit-tests against the DRAWN radius, which is 3-4px for the many
 * topics at the low end of the usage range — far below the 44px a finger can
 * aim at. A tap that misses every disc picks the nearest node within this
 * radius instead of clearing the selection.
 */
const TOUCH_HIT_RADIUS_PX = 22;

interface MapModel {
  names: Map<string, string>;
  usage: Map<string, number>;
  /** Neighbours by shared notes, strongest first. */
  neighbours: Map<string, Array<{ id: string; sharedNotes: number }>>;
  hue: Map<string, string | null>;
}

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
  imports: [CommonModule, IconButtonComponent, ButtonComponent, ChipComponent, NostosIconComponent],
  templateUrl: './topic-map.component.html',
  styleUrl: './topic-map.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TopicMapComponent implements OnChanges, AfterViewInit, OnDestroy {
  @Input() topics: TopicDto[] = [];
  @Input() selectedId: string | null = null;
  /**
   * Name of the selected topic, supplied by the parent.
   *
   * The map owns the action rail, so the "what is selected" chip belongs here
   * too — otherwise the notes action sits in a second floating overlay outside
   * the control surface, which is the layout being reported as fragmented.
   */
  @Input() selectedName: string | null = null;
  /**
   * The surface search query, so the empty state can explain itself.
   *
   * The header's search filters the topic set the map draws. Without this, a
   * query matching nothing left the graph empty and the map claimed "No
   * connections yet" — which is false: the connections exist and the filter
   * excluded them. With the query the map can say which of the two it is.
   */
  @Input() searchQuery = '';
  @Output() readonly topicSelected = new EventEmitter<string>();
  /** Emitted by the rail's "Read notes" action. */
  @Output() readonly openNotes = new EventEmitter<void>();
  /** Emitted by a node double-click, to open that topic's notes. */
  @Output() readonly openTopic = new EventEmitter<string>();
  /**
   * Emitted when a click on empty space clears the selection.
   *
   * Separate from `topicSelected` rather than widening it to `string | null`:
   * the two mean different things ("this node is now the subject" versus "there
   * is no subject"), and a nullable id would let a consumer treat a deselect as
   * a selection of nothing.
   */
  @Output() readonly selectionCleared = new EventEmitter<void>();

  @ViewChild('sigmaContainer', { static: false }) sigmaContainer!: ElementRef<HTMLDivElement>;
  @ViewChild('mapStage', { static: false }) mapStage!: ElementRef<HTMLElement>;
  @ViewChild('mapHud', { static: false }) mapHud?: ElementRef<HTMLElement>;
  @ViewChild('mapLegend', { static: false }) mapLegend?: ElementRef<HTMLElement>;

  private sigma: Sigma | null = null;
  private graph: Graph | null = null;
  private graphData: TopicGraphDto | null = null;
  private theme: ThemeColors = readTheme();
  private destroyed = false;
  private viewInitialized = false;
  private pendingRebuild = false;
  private resizeObserver: ResizeObserver | null = null;

  /**
   * Obsidian's five-force layout, run live rather than as a one-shot settle.
   *
   * Held as a field because it is stepped from an animation frame (and from the
   * drag handler) rather than being fire-and-forget: alpha decays to `alphaMin`
   * and the loop then stops on its own, so this is the single place that knows
   * whether the graph is still moving.
   */
  private layout: Simulation<LayoutNode, undefined> | null = null;

  /** Handle for the animation frame that steps the layout. */
  private layoutFrame: number | null = null;

  /* Drag-to-reposition state */
  private draggedNode: string | null = null;
  private isDragging = false;
  private dragStart: { x: number; y: number } | null = null;
  /**
   * True between a node press and its release on ANY input source.
   *
   * Guards the teardown so it runs exactly once per gesture. Without it,
   * `mouseup` and the window-level `pointerup` safety net can both fire for one
   * drag, and the second call would clear the `fixed` pin of a node the user
   * has already moved on from.
   */
  private draggingActive = false;
  /** Removes the window-level drag safety net. Set while Sigma is alive. */
  private detachDragSafetyNet: (() => void) | null = null;
  /** True while Focus mode is the CSS fallback rather than the Fullscreen API. */
  private pseudoFullscreen = false;

  /**
   * The settled ForceAtlas2 layout, kept so "Reset layout" can restore node
   * positions after the user has dragged things around.
   */
  private readonly layoutHome = new Map<string, { x: number; y: number }>();

  /* State signals for the template. */
  readonly loading = signal(true);
  readonly hoveredId = signal<string | null>(null);
  readonly selectedNodeId = signal<string | null>(null);
  readonly isFullscreen = signal(false);
  readonly sourceCount = signal(0);
  readonly isCapped = computed(() => this.sourceCount() > MAX_MAP_TOPICS);
  readonly noConnections = signal(false);
  /**
   * Accessible nodes: the full set currently rendered, so the hidden list
   * stays in sync with the visual canvas.
   */
  readonly accessibleNodes = signal<
    Array<{ id: string; name: string; usageCount: number; connectionCount: number }>
  >([]);

  /**
   * What the selection card knows about every topic, rebuilt with the graph.
   *
   * Derived from the single `/api/topics/graph` response the map already
   * loads, so the card makes no request of its own — the map's one-request
   * contract (asserted in the spec) holds.
   */
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

  /** Label candidates Sigma offered during the frame being drawn. */
  private labelCandidates: LabelCandidate[] = [];

  /* Icons for the action rail. Exposed as fields because the template reads
     them; `strokeWidth` stays at the app default of 2.

     Icon choice is constrained by mutual distinctness, not just meaning: `Scan`
     (fit) and `Maximize` were BOTH the same four-corner-bracket glyph (vertical
     corner-marks at 3/17 vs 3/8), which read as one button repeated — reported as
     "you use the same icon for two different buttons" and confirmed by comparing
     the rendered SVG geometry. Focus mode uses the diagonal expand/shrink arrows
     instead, which also communicates fullscreen better than brackets. */
  private readonly topicsService = inject(TopicsService);

  /* ── Keyboard and fullscreen ── */
  private readonly handleFullscreenChange = (): void => {
    this.isFullscreen.set(document.fullscreenElement === this.mapStage?.nativeElement);
    window.setTimeout(() => this.sigma?.refresh(), 0);
  };

  @HostListener('document:keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && this.isFullscreen()) {
      void this.exitFullscreen();
    }
  }

  /* ── Lifecycle ── */

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['selectedId']) {
      this.selectedNodeId.set(this.selectedId);
      this.refreshRendering();
    }
    if (changes['topics']) {
      this.sourceCount.set((this.topics ?? []).length);
      this.loadGraphData();
    }
  }

  ngAfterViewInit(): void {
    this.viewInitialized = true;
    document.addEventListener('fullscreenchange', this.handleFullscreenChange);
    if (this.pendingRebuild) {
      this.pendingRebuild = false;
      this.rebuildSigma();
    }
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    document.removeEventListener('fullscreenchange', this.handleFullscreenChange);
    this.disposeSigma();
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
  }

  /* ── Data loading ── */

  private loadGraphData(): void {
    this.loading.set(true);
    this.topicsService.getGraph().subscribe({
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
        this.loading.set(false);
      },
    });
  }

  /* ── Graph construction ── */

  private rebuildSigma(): void {
    this.disposeSigma();
    if (!this.graphData || !this.sigmaContainer?.nativeElement) return;

    this.theme = readTheme();
    const container = this.sigmaContainer.nativeElement;

    const visibleIds = new Set(
      [...(this.topics ?? [])]
        .sort(compareTopics)
        .slice(0, MAX_MAP_TOPICS)
        .map((c) => c.id)
    );

    const visibleNodes = this.graphData.nodes.filter((n) => visibleIds.has(n.id));
    const visibleEdges = this.graphData.edges.filter(
      (e) => visibleIds.has(e.sourceId) && visibleIds.has(e.targetId)
    );

    if (visibleNodes.length === 0) {
      this.noConnections.set(true);
      this.accessibleNodes.set([]);
      return;
    }

    const graph = new Graph();
    this.graph = graph;

    const usages = visibleNodes.map((n) => Math.max(0, n.usageCount));
    const minUsage = Math.min(...usages);
    const maxUsage = Math.max(...usages);
    const usageRange = Math.max(1, maxUsage - minUsage);

    const maxShared = Math.max(1, ...visibleEdges.map((e) => e.sharedNotes));

    // Seed every node on Obsidian's ring, then add it to the graph with the
    // seeded coordinates. The simulation below owns the positions from here on.
    const seeds: Array<{ id: string; x: number; y: number }> = visibleNodes.map((n) => ({
      id: n.id,
      x: 0,
      y: 0,
    }));
    seedPositions(seeds, OBSIDIAN_FORCES.linkDistance);
    const seedById = new Map(seeds.map((s) => [s.id, s]));

    // Hierarchy: the most-referenced topics are hubs, which are always label
    // candidates and drawn at a heavier weight, so the eye has somewhere to start.
    const hubs = pickHubs(visibleNodes);

    // Structure: clusters of topics that share notes. The largest few take a
    // hue each (see COMMUNITY_HUES); the rest keep the neutral node ink.
    const clusterRank = detectCommunities(
      visibleNodes.map((n) => n.id),
      visibleEdges
    );
    const clusterSizes = new Map<number, number>();
    for (const rank of clusterRank.values()) {
      clusterSizes.set(rank, (clusterSizes.get(rank) ?? 0) + 1);
    }
    const hueOf = (id: string): string | null => {
      const rank = clusterRank.get(id);
      if (rank === undefined || rank >= COMMUNITY_HUES) return null;
      if ((clusterSizes.get(rank) ?? 0) < COMMUNITY_MIN_SIZE) return null;
      return this.theme.communities[rank] ?? null;
    };

    // Add nodes.
    for (const node of visibleNodes) {
      const ratio = (Math.max(0, node.usageCount) - minUsage) / usageRange;
      const size = NODE_SIZE_MIN + (NODE_SIZE_MAX - NODE_SIZE_MIN) * Math.sqrt(ratio);
      const hue = hueOf(node.id);
      // Hue strength follows usage, so a cluster reads as one family while its
      // big topics still stand out from its small ones.
      const nodeColor = hue
        ? mixHex(this.theme.node, hue, 0.55 + ratio * 0.45)
        : mixHex(this.theme.node, this.theme.nodeHead, 0.18 + ratio * 0.42);
      const seeded = seedById.get(node.id)!;
      const hub = hubs.has(node.id);

      graph.addNode(node.id, {
        label: node.name,
        size,
        color: nodeColor,
        hue,
        labelColor: hub ? this.theme.labelActive : hexToRgba(this.theme.label, 0.8 + ratio * 0.16),
        labelWeight: hub ? '600' : '400',
        labelPriority: hub ? LABEL_PRIORITY.hub : LABEL_PRIORITY.normal,
        forceLabel: hub,
        x: seeded.x,
        y: seeded.y,
        usageCount: node.usageCount,
      });
    }

    // Add edges. An edge inside a coloured cluster carries the cluster's hue;
    // an edge between clusters stays neutral, so bridges read as bridges.
    for (const edge of visibleEdges) {
      const strength = edge.sharedNotes / maxShared;
      const edgeSize = EDGE_SIZE_MIN + strength * (EDGE_SIZE_MAX - EDGE_SIZE_MIN);
      const alpha = EDGE_ALPHA_MIN + strength * EDGE_ALPHA_RANGE;
      const sourceHue = hueOf(edge.sourceId);
      const sameCluster = sourceHue !== null && sourceHue === hueOf(edge.targetId);
      try {
        graph.addEdge(edge.sourceId, edge.targetId, {
          size: edgeSize,
          color: sameCluster
            ? hexToRgba(mixHex(this.theme.edge, sourceHue, 0.6), alpha)
            : hexToRgba(this.theme.edge, alpha),
          sharedNotes: edge.sharedNotes,
        });
      } catch {
        // Duplicate edge or missing node — skip silently.
      }
    }

    // The selection card's view of the graph.
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
      hue: new Map(visibleNodes.map((n) => [n.id, hueOf(n.id)])),
    });

    this.noConnections.set(graph.size === 0);

    // Build the accessible node list with connection counts.
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

    // ── Build the force simulation ──
    //
    // Replaces a one-shot ForceAtlas2 settle. Two properties of d3-force matter
    // for the behaviour being fixed:
    //
    //  * `vx`/`vy` PERSIST between ticks (damped by `velocityDecay`), so stepping
    //    one tick per frame carries momentum and a re-heated graph glides to rest
    //    instead of freezing. ForceAtlas2's `assign()` rebuilt its matrices per
    //    call and discarded the previous call's velocity, which is why the old
    //    live loop needed six iterations per frame to look like anything.
    //  * Alpha is an energy budget that DECAYS to `alphaMin`, at which point the
    //    loop halts by itself. So the settle and the post-drag relaxation are the
    //    same code path, and neither leaves a permanent timer running.
    //
    // Topics with no connection stay out of the simulation: they are placed on
    // a shelf after the settle (see `shelfPositions`), not left to drift.
    const connected = new Set<string>();
    for (const edge of visibleEdges) {
      if (edge.sourceId === edge.targetId) continue;
      connected.add(edge.sourceId);
      connected.add(edge.targetId);
    }
    const isolates = visibleNodes.filter((n) => !connected.has(n.id));
    const layoutNodes: LayoutNode[] = visibleNodes.filter((n) => connected.has(n.id)).map((n) => {
      const attrs = graph.getNodeAttributes(n.id);
      return {
        id: n.id,
        x: Number(attrs['x']),
        y: Number(attrs['y']),
        size: Number(attrs['size']),
      };
    });
    const layoutLinks = visibleEdges
      .filter((e) => graph.hasNode(e.sourceId) && graph.hasNode(e.targetId))
      .map((e) => ({ source: e.sourceId, target: e.targetId }));

    // d3's own per-link strength, computed explicitly so Obsidian's slider can be
    // applied as a multiplier.
    //
    // d3's default is `1 / min(degree(source), degree(target))` and its `count`
    // array only exists after the force is initialised. Reading the default by
    // calling `.strength()` on a fresh force therefore hands back a function whose
    // closure is still empty, and invoking it throws on `count[...]`. Deriving the
    // same quantity from the graph's own degrees is both correct and readable.
    const linkStrength = (link: { source: unknown; target: unknown }): number => {
      const source = link.source as LayoutNode;
      const target = link.target as LayoutNode;
      const degree = Math.min(graph.degree(source.id), graph.degree(target.id));
      return degree > 0 ? OBSIDIAN_LINK_STRENGTH / degree : OBSIDIAN_LINK_STRENGTH;
    };

    // Centre pull per axis, weaker along the stage's long side so the settled
    // graph takes the stage's shape instead of a circle (see `centerStrengths`).
    const centre = centerStrengths(container.clientWidth, container.clientHeight);

    this.layout = forceSimulation<LayoutNode>(layoutNodes)
      // Stop before the first tick: the settle is driven explicitly below, and a
      // d3 simulation otherwise starts its own timer on construction.
      .stop()
      .force('x', forceX<LayoutNode>(0).strength(centre.x))
      .force('y', forceY<LayoutNode>(0).strength(centre.y))
      .force(
        'link',
        forceLink<LayoutNode, { source: string; target: string }>(layoutLinks)
          .id((node) => node.id)
          .distance(OBSIDIAN_FORCES.linkDistance)
          .strength(linkStrength)
      )
      .force(
        'charge',
        forceManyBody<LayoutNode>()
          .strength(-OBSIDIAN_FORCES.repelStrength)
          .distanceMin(OBSIDIAN_FORCES.distanceMin)
      )
      .force(
        'collide',
        forceCollide<LayoutNode>()
          // Obsidian's fixed values, verbatim. `radius(60)` collides
          // centre-to-centre and is deliberately NOT inflated by the node's drawn
          // size: measured on the live 53-node graph, the settled minimum
          // node-centre distance is 125-127 with `size` folded in and 122.2
          // without, against a 60 surplus over the 8px drawn radius — so adding
          // `size` changes nothing except by making the map's spacing depend on
          // whichever node size formula happens to be in force. Keeping the force
          // faithful to Obsidian is the point of this change, and the screen-space
          // guarantee is asserted separately (0 overlapping pairs) where it can be
          // measured rather than assumed.
          .radius(OBSIDIAN_FORCES.collideRadius)
          .strength(OBSIDIAN_FORCES.collideStrength)
      );

    // ── Settle ──
    //
    // Run the full cool-down synchronously so the first paint shows a finished
    // layout rather than one visibly uncoiling. 300 ticks is exactly d3's default
    // budget (alphaDecay is defined as "reach alphaMin in 300 ticks"), and the
    // loop exits on the alpha test rather than the counter, so this is the real
    // convergence point. Measured 248 ticks on the live 53-node graph.
    this.layout.alpha(SETTLE_ALPHA);
    let settleTicks = 0;
    while (this.layout.alpha() > OBSIDIAN_FORCES.alphaMin && settleTicks < 300) {
      this.layout.tick();
      settleTicks += 1;
    }

    // Turn the settled graph so its long axis follows the stage's. Rigid, about
    // the origin the centre forces pull toward, so the physics stay consistent.
    const turn = alignmentRotation(
      layoutNodes.map((n) => ({ x: Number(n.x) || 0, y: Number(n.y) || 0 })),
      container.clientWidth,
      container.clientHeight
    );
    if (turn) {
      const cos = Math.cos(turn);
      const sin = Math.sin(turn);
      for (const n of layoutNodes) {
        const x = Number(n.x) || 0;
        const y = Number(n.y) || 0;
        n.x = x * cos - y * sin;
        n.y = x * sin + y * cos;
      }
    }
    this.writeLayoutToGraph();

    // Shelve the unconnected topics under the settled graph.
    if (isolates.length) {
      const extent = this.graphExtent(connected);
      const shelf = shelfPositions(
        isolates.length,
        extent,
        // Wide enough for a label between two shelved dots.
        OBSIDIAN_FORCES.collideRadius * 3.6
      );
      [...isolates]
        .sort((a, b) => compareTopics(a, b))
        .forEach((node, index) => {
          graph.setNodeAttribute(node.id, 'x', shelf[index].x);
          graph.setNodeAttribute(node.id, 'y', shelf[index].y);
        });
    }

    // Snapshot the computed layout so "Reset" can restore it after drags.
    this.layoutHome.clear();
    graph.forEachNode((node, attrs) => {
      this.layoutHome.set(node, { x: Number(attrs['x']) || 0, y: Number(attrs['y']) || 0 });
    });

    // Create Sigma renderer.
    const sigma = new Sigma(graph, container, {
      renderLabels: true,
      renderEdgeLabels: false,
      labelRenderedSizeThreshold: LABEL_RENDER_MIN_SIZE,
      labelFont: "'Hanken Grotesk', sans-serif",
      // `attribute` is what makes Sigma honour the per-node `labelColor` this
      // component sets. `drawDiscNodeLabel` reads `data[settings.labelColor
      // .attribute]` only when that key is truthy:
      //
      //   color = settings.labelColor.attribute
      //     ? data[settings.labelColor.attribute] || settings.labelColor.color
      //     : settings.labelColor.color
      //
      // Without it, every per-node colour below was silently dead and the map
      // was painted from the static `--color-text-muted` fallback — which on
      // dark is #c4c7d0 drawn on Sigma's hardcoded white hover box (1.66:1).
      labelColor: { attribute: 'labelColor', color: this.theme.label },
      labelSize: LABEL_DRAW_SIZE,
      defaultEdgeType: 'line',
      enableEdgeEvents: false,
      allowInvalidContainer: true,
      // Node sizes are pixel sizes; the camera fit handles scale, so radii only
      // need to follow the camera (see `zoomToSizeRatioFunction` below) and the
      // visual scale does not collapse when the graph fills the stage.
      itemSizesReference: 'screen',
      // Node radii must scale with the camera, or zooming in makes the graph
      // collide with itself.
      //
      // Sigma maps positions through the camera but, with `itemSizesReference:
      // 'screen'`, draws radii at a fixed pixel size unless `zoomToSizeRatioFunction`
      // says otherwise. The two then disagree: positions shrink as `1/ratio` while
      // radii stay put, so the drawn gap between neighbours closes as you zoom in.
      // Measured on the live graph: at `ratio 2.09` (a normal zoom-in on desktop)
      // **30 of 53 nodes overlapped**; at the fitted `ratio 1.247`, none did.
      //
      // `Math.sqrt` is Sigma's OWN default for this setting, and it matches what
      // Obsidian does — its renderer sets `nodeScale = Math.sqrt(1/scale)` and
      // multiplies the node radius by it on every frame. So radii and distances
      // now follow the same law, node spacing in graph units is preserved at any
      // zoom, and the layout's guarantees (no overlaps, no collisions) hold
      // zoomed-in as well as fitted.
      zoomToSizeRatioFunction: Math.sqrt,
      // Keep the user's layout authoritative.
      //
      // With autoRescale on, Sigma re-maps the whole graph onto the viewport
      // whenever the extent changes, so dragging one node away shrinks every
      // other node (measured: width fill 57% -> 6.4%) and the Fit/Reset buttons
      // then return to an already-current camera and appear dead. Off, graph
      // units map 1:1 to screen pixels and a drag moves only what was dragged.
      autoRescale: false,
      autoCenter: false,
      // No stagePadding: Sigma's `getStagePadding()` returns 0 whenever
      // `autoRescale` is false, which is this configuration — the setting is
      // inert here. Room for labels comes from `fitOccupancy()` instead.
      stagePadding: 0,
      // Sigma's label grid only picks CANDIDATES now; overlaps are resolved by
      // `placeLabels` after the frame. So the density can be generous — the
      // collision pass, not the grid, decides what is drawn.
      labelDensity: 3,
      // Collect, don't draw. Sigma's own drawer paints each label the moment it
      // is chosen, with no knowledge of the others, so labels overprinted each
      // other and edges cut through them. The `afterRender` hook below lays the
      // collected candidates out without overlaps and paints them over a halo.
      defaultDrawNodeLabel: (_context, data) => {
        if (!data.label) return;
        const attrs = data as unknown as Record<string, unknown>;
        this.labelCandidates.push({
          key: String(attrs['key'] ?? data.label),
          x: data.x,
          y: data.y,
          size: data.size,
          label: data.label,
          color: String(attrs['labelColor'] ?? this.theme.label),
          weight: String(attrs['labelWeight'] ?? '400'),
          priority: Number(attrs['labelPriority'] ?? LABEL_PRIORITY.normal),
          // Sigma draws these on its hover plate; keep others off that box.
          reserveOnly: attrs['highlighted'] === true || attrs['key'] === this.hoveredId(),
        });
      },
      // Replace Sigma's `drawDiscNodeHover`, whose label box is a hardcoded
      // `#FFF`. Geometry is unchanged; only the fill follows the theme.
      defaultDrawNodeHover: (context, data, settings) =>
        drawThemeNodeHover(context, data, settings, this.theme.labelBox),
    });

    this.sigma = sigma;

    sigma.on('beforeRender', () => {
      this.labelCandidates = [];
    });
    sigma.on('afterRender', () => this.drawLabels());

    // Keep the renderer and the simulation reachable for diagnostics and for the
    // visual-verification harness, which measures graph geometry through the live
    // instances. Read-only introspection: no application behaviour depends on
    // these handles.
    //
    // `__nostosLayout` is the d3 simulation, which is where the physics questions
    // are answered from — alpha, whether the loop is still running, and each
    // node's `vx`/`vy` (the momentum that makes a release glide rather than stop).
    const globals = globalThis as unknown as {
      __nostosSigma?: unknown;
      __nostosGraph?: unknown;
      __nostosLayout?: unknown;
    };
    globals.__nostosSigma = sigma;
    globals.__nostosGraph = graph;
    globals.__nostosLayout = this.layout;

    // Set up node reducers for hover/selection highlighting.
    const component = this;

    sigma.setSetting('nodeReducer', (node: string, data: Record<string, unknown>) => {
      const res = { ...data };
      const hoveredId = component.hoveredId();
      const selectedId = component.selectedNodeId();
      const activeId = hoveredId ?? selectedId;

      if (activeId) {
        const neighbors = new Set<string>();
        try {
          graph.forEachNeighbor(activeId, (n) => neighbors.add(n));
        } catch {
          // activeId might not be in graph
        }
        neighbors.add(activeId);

        if (node === activeId) {
          res['color'] = component.theme.nodeHead;
          res['zIndex'] = 2;
          res['highlighted'] = true;
          res['labelColor'] = component.theme.labelActive;
          res['labelWeight'] = '600';
          res['labelPriority'] = LABEL_PRIORITY.active;
          res['forceLabel'] = true;
        } else if (neighbors.has(node)) {
          res['zIndex'] = 1;
          res['labelColor'] = component.theme.labelActive;
          res['labelPriority'] = LABEL_PRIORITY.active;
          res['forceLabel'] = true;
        } else {
          // Keep unconnected nodes visible AND labelled.
          //
          // Setting `label: ''` here erased them entirely, which is the other
          // half of the "everything else melts into the background" report: the
          // user loses both the dot and its name, so the map reads as if those
          // topics do not exist rather than that they are merely not the
          // active neighbourhood.
          res['color'] = hexToRgba(component.theme.node, NODE_ALPHA_DIM);
          res['labelColor'] = hexToRgba(component.theme.label, 0.62);
          res['labelWeight'] = '400';
          res['labelPriority'] = LABEL_PRIORITY.dimmed;
          res['zIndex'] = 0;
        }
      }

      return res;
    });

    sigma.setSetting('edgeReducer', (edge: string, data: Record<string, unknown>) => {
      const res = { ...data };
      const hoveredId = component.hoveredId();
      const selectedId = component.selectedNodeId();
      const activeId = hoveredId ?? selectedId;

      if (activeId) {
        const source = graph.source(edge);
        const target = graph.target(edge);
        if (source === activeId || target === activeId) {
          // Full-strength accent ink. At alpha 0.72 the pine washed toward the
          // normal edge grey in the light theme (5.09:1 against the field vs
          // 3.44:1 for a normal edge — indistinguishable in practice, and the
          // pixel classification found zero pixels bright enough to separate
          // them). At full alpha it is 11.90:1 against 3.44:1, so a line that
          // touches the focused node is unmistakable.
          //
          // A node in a coloured cluster lights its edges in the cluster's hue
          // (opaque, so the contrast above still holds); a neutral one in ink.
          const activeHue = graph.getNodeAttributes(activeId)['hue'];
          res['color'] =
            typeof activeHue === 'string' && activeHue ? activeHue : component.theme.edgeActive;
          res['size'] = Math.max((data['size'] as number) ?? 1, 2) * 1.6;
          res['zIndex'] = 1;
        } else {
          res['color'] = hexToRgba(component.theme.edge, EDGE_ALPHA_DIM);
          res['zIndex'] = 0;
        }
      }

      return res;
    });

    // Event listeners.
    sigma.on('enterNode', ({ node }) => {
      component.hoveredId.set(node);
      container.style.cursor = 'grab';
      sigma.refresh();
    });

    sigma.on('leaveNode', () => {
      component.hoveredId.set(null);
      if (!component.draggedNode) {
        container.style.cursor = 'default';
      }
      sigma.refresh();
    });

    sigma.on('clickNode', ({ node }) => {
      // If we just finished dragging, don't treat the mouseup as a click.
      if (component.isDragging) return;
      component.selectedNodeId.set(node);
      component.topicSelected.emit(node);
      sigma.refresh();
      component.revealSelected();
    });

    // Double-click opens the topic, the way Obsidian's graph does.
    //
    // Sigma's mouse captor counts its own clicks: the FIRST click emits `click`
    // (so the node is already selected by the time this fires) and the second
    // dispatches `doubleClick` INSTEAD of a second `click`, which is what makes
    // the two gestures compose rather than fight. `doubleClickNode` carries the
    // node under the pointer, so the id is ready to hand straight to the parent
    // — no round trip through the selection signal.
    //
    // `preventSigmaDefault()` is required, not decorative: without it Sigma also
    // zooms the camera into the node, so the map would lurch between the two
    // clicks of a gesture that is meant to leave the map entirely.
    sigma.on('doubleClickNode', ({ node, preventSigmaDefault }) => {
      if (component.isDragging) return;
      preventSigmaDefault();
      component.selectedNodeId.set(node);
      component.openTopic.emit(node);
      sigma.refresh();
    });

    // Clicking empty space clears the selection.
    //
    // Selection drives the index rail and the "Read notes" action, so without
    // this the map was stuck on the last node clicked — there was no gesture
    // that returned the graph to a neutral state, and the only remaining escape
    // (reloading, or selecting a different node) made the map feel like it was
    // holding a choice the user could not take back.
    //
    // `clickStage` is exactly the right event, because Sigma only emits it for a
    // GENUINE click: a camera pan bumps its `draggedEvents` counter past
    // `draggedEventsTolerance` and is suppressed, a touch drag is suppressed by
    // `tapMoveTolerance`, and a double-click dispatches `doubleClickStage`
    // instead of a second `clickStage`. So an empty-space click during a pan
    // release never lands here and cannot wipe a selection by accident.
    sigma.on('clickStage', (payload?: { event?: { x: number; y: number } }) => {
      // A tap that just missed a small node selects it rather than clearing.
      const near = component.nearestNodeForTouch(payload?.event);
      if (near) {
        component.selectedNodeId.set(near);
        component.topicSelected.emit(near);
        sigma.refresh();
        component.revealSelected();
        return;
      }
      component.hoveredId.set(null);
      component.selectedNodeId.set(null);
      component.selectionCleared.emit();
      sigma.refresh();
    });

    // ── Drag-to-reposition ──
    //
    // ONE lifecycle, driven by both input sources. Sigma's `downNode` fires for
    // mouse AND touch, but its `mouseup` captor event fires for MOUSE ONLY —
    // touch ends arrive as a separate `touchup`. That asymmetry was a shipped
    // bug: on a phone, every tap or drag on a node called these teardown steps
    // zero times, so `camera.disable()` was never undone.
    //
    // Measured before that fix, on a 390x844 touch viewport: a simple TAP on a
    // node left `camera.enabled === false` and the node pinned, and the node did
    // not even move (displacement 0.0px). Every camera control then silently
    // stopped working, including Fit: ratio stayed at 1.4286 before and after
    // clicking it. That is the "buttons get stuck" report, and one tap triggered
    // it.
    //
    // The teardown lives in one place (`endDrag`) wired to all three possible
    // endings: `mouseup`, `touchup`, and a window-level `pointerup` /
    // `pointercancel` safety net for a pointer released outside the canvas.
    //
    // The PIN itself moved when the layout engine did: it is now d3's `fx`/`fy`
    // on the simulation node, not ForceAtlas2's `fixed` graph attribute, and the
    // graph is energised with `alphaTarget` rather than stepped per event.

    const endDrag = (): void => {
      if (!component.draggingActive) return;
      const wasPinned = !!component.draggedNode;
      // Un-pin in the SIMULATION, which is where the pin lives now. Clearing
      // every node's `fx`/`fy` rather than only the dragged one is deliberate: a
      // pointerup lost off-canvas could otherwise strand a pin that no later
      // gesture owns, and the release is idempotent and cheap.
      component.releasePinnedNode();
      // Let the layout cool: `alphaTarget` back to 0, so alpha decays from the
      // drag energy to `alphaMin` and the graph settles under its own inertia.
      // This is the half that was missing before the physics rewrite — it is why
      // a release now glides to rest instead of freezing on the spot.
      if (wasPinned) component.coolLayout();
      component.draggedNode = null;
      component.dragStart = null;
      component.draggingActive = false;
      // Always hand the camera back. `enable()` is idempotent, so calling it on
      // a drag that never disabled it is harmless — but MISSING it once froze
      // the whole camera permanently.
      sigma.getCamera().enable();
      container.classList.remove('dragging');
      // Reset the drag flag after Sigma has dispatched the click that follows
      // the release, so a genuine drag never also selects the node it moved.
      window.setTimeout(() => {
        component.isDragging = false;
      }, 0);
    };

    const beginDrag = (node: string, x: number, y: number): void => {
      component.isDragging = false;
      component.dragStart = { x, y };
      component.draggedNode = node;
      component.draggingActive = true;
      // Pin the node for the duration of the drag. d3-force snaps a node with a
      // defined `fx`/`fy` to exactly that point and zeroes its velocity every
      // tick, so the rest of the graph relaxes around the pointer — the same
      // contract Obsidian's worker implements when it receives
      // `forceNode: { x, y }`.
      component.pinNodeForDrag(node);
      sigma.getCamera().disable();
      container.classList.add('dragging');
    };

    const moveDrag = (node: string, x: number, y: number): void => {
      // Only promote to a real drag once the pointer has travelled past a small
      // threshold. Without this, the sub-pixel movement of an ordinary click or
      // tap counts as a drag and the click-to-select that follows is swallowed.
      if (!component.isDragging && component.dragStart) {
        const travelled = Math.hypot(x - component.dragStart.x, y - component.dragStart.y);
        if (travelled < DRAG_THRESHOLD_PX) return;
        component.isDragging = true;
      }

      // Convert viewport coordinates to graph coordinates and move the pin.
      const pos = sigma.viewportToGraph({ x, y });
      graph.setNodeAttribute(node, 'x', pos.x);
      graph.setNodeAttribute(node, 'y', pos.y);
      component.pinNodeForDrag(node, pos.x, pos.y);

      // Let the rest of the graph respond to the node being pulled, so
      // neighbours follow it instead of the node moving alone.
      //
      // Obsidian re-posts `alpha: .3, alphaTarget: .3` on EVERY pointermove, so
      // the simulation runs hot and continuously for the whole gesture. Doing the
      // same here is what makes the pull read as physics: the neighbours do not
      // just get nudged, they keep relaxing as the pointer keeps moving.
      component.heatLayout(DRAG_ALPHA);
    };

    sigma.on('downNode', (e) => {
      component.draggingActive = false;
      beginDrag(e.node, e.event.x, e.event.y);
    });

    // Mouse path. Sigma v3 fires 'mousemovebody' on every pointer-move.
    sigma.getMouseCaptor().on('mousemovebody', (e) => {
      if (!component.draggedNode) return;
      moveDrag(component.draggedNode, e.x, e.y);
      // Prevent Sigma's default camera panning while dragging.
      e.preventSigmaDefault();
    });

    sigma.getMouseCaptor().on('mouseup', () => endDrag());

    // Touch path. `touchmove` is the touch equivalent of `mousemovebody`, and
    // `touchup` is the ONLY signal Sigma emits when a finger leaves the screen.
    sigma.getTouchCaptor().on('touchmove', (e) => {
      if (!component.draggedNode) return;
      const point = e.touches[0] ?? e.previousTouches[0];
      if (!point) return;
      moveDrag(component.draggedNode, point.x, point.y);
      e.preventSigmaDefault();
    });

    sigma.getTouchCaptor().on('touchup', () => endDrag());

    // Safety net for a pointer released or cancelled outside the canvas: without
    // this a drag that ends off-target leaves the camera disabled for good.
    component.detachDragSafetyNet?.();
    const safetyNet = (): void => endDrag();
    window.addEventListener('pointerup', safetyNet);
    window.addEventListener('pointercancel', safetyNet);
    component.detachDragSafetyNet = () => {
      window.removeEventListener('pointerup', safetyNet);
      window.removeEventListener('pointercancel', safetyNet);
    };

    // Sigma measures the container when it is constructed, which can be before
    // the surrounding layout has settled — on a portrait phone the stage is
    // narrower at build time than it ends up, and the fit computed from that
    // stale width under-zoomed and left 5 nodes off screen on first open.
    // Re-fit once the browser has laid the stage out, and whenever it changes
    // size without the user having navigated away.
    window.requestAnimationFrame(() => {
      if (this.destroyed || !this.sigma) return;
      this.sigma.refresh();
      this.fitGraph();
    });

    // Resize observer to keep Sigma in sync with container size changes.
    this.resizeObserver?.disconnect();
    if (typeof ResizeObserver !== 'undefined') {
      let lastWidth = container.clientWidth;
      let lastHeight = container.clientHeight;
      this.resizeObserver = new ResizeObserver(() => {
        if (!this.sigma || this.destroyed) return;
        const w = container.clientWidth;
        const h = container.clientHeight;
        this.sigma.refresh();
        // A genuine size change invalidates the framing, so re-fit — but only
        // when the size actually moved, so a sub-pixel observer tick does not
        // yank the camera back while the user is exploring.
        if (Math.abs(w - lastWidth) > 1 || Math.abs(h - lastHeight) > 1) {
          lastWidth = w;
          lastHeight = h;
          this.fitGraph();
        }
      });
      this.resizeObserver.observe(container);
    }

    // Frame the graph on first paint.
    this.fitGraph();
  }

  /**
   * Copy the simulation's positions back into the graphology graph.
   *
   * Sigma renders from the graph, d3 owns the physics, and this is the only
   * bridge between them — so there is exactly one writer of node coordinates
   * during a tick.
   */
  private writeLayoutToGraph(): void {
    const graph = this.graph;
    const layout = this.layout;
    if (!graph || !layout) return;

    for (const node of layout.nodes()) {
      if (!graph.hasNode(node.id)) continue;
      graph.setNodeAttribute(node.id, 'x', Number(node.x) || 0);
      graph.setNodeAttribute(node.id, 'y', Number(node.y) || 0);
      // Never write `size` back unconditionally: it is Sigma's pixel radius, not
      // a layout quantity, and a NaN here renders the node at zero and drops its
      // label (Sigma gates labels on drawn size). Guard rather than clobber.
      const size = Number(node.size);
      if (Number.isFinite(size) && size > 0) {
        graph.setNodeAttribute(node.id, 'size', size);
      }
    }
  }

  /**
   * Run the layout hot: hold `alphaTarget` at the drag energy and pump frames
   * until it is taken away again.
   *
   * Obsidian's worker does this by re-posting `alpha: .3, alphaTarget: .3` on
   * every pointermove, which keeps `alpha` pinned at 0.3 for the whole gesture.
   * Setting `alphaTarget` (rather than `alpha`) means alpha will *return* to that
   * level on its own, so the graph is energised for the entire drag without the
   * caller having to re-post on every event.
   */
  private heatLayout(alphaTarget: number): void {
    const layout = this.layout;
    if (!layout) return;
    layout.alphaTarget(alphaTarget);
    // Lift alpha immediately so the first frame after the gesture starts is
    // already energetic, rather than easing up to the target over many ticks.
    if (layout.alpha() < alphaTarget) layout.alpha(alphaTarget);
    this.startLayoutLoop();
  }

  /**
   * Let the layout cool: `alphaTarget` back to 0, so alpha decays to `alphaMin`
   * and the graph settles to rest under its own inertia.
   *
   * The loop keeps running through the decay — that IS the inertia — and stops
   * itself at `alphaMin`, so nothing is left ticking once the graph is still.
   */
  private coolLayout(): void {
    this.layout?.alphaTarget(0);
    this.startLayoutLoop();
  }

  /**
   * Pump the simulation one tick per animation frame until it goes quiet.
   *
   * d3-force persists `vx`/`vy` between ticks and damps them by `velocityDecay`,
   * so a single `tick()` per frame carries real momentum. That is the opposite of
   * the ForceAtlas2 arrangement this replaces, where `assign()` rebuilt its
   * matrices on every call and discarded the previous call's velocity — which
   * forced six iterations per frame just to register, and still produced no
   * inertia after the pointer was released (measured: 0 nodes moved at every
   * sample up to 2s after release).
   *
   * Idempotent: a second call while a frame is already queued does nothing, so
   * the per-pointermove heat is cheap.
   */
  private startLayoutLoop(): void {
    if (this.layoutFrame !== null || !this.layout) return;

    const step = (): void => {
      this.layoutFrame = null;
      const layout = this.layout;
      if (!layout || this.destroyed) return;

      layout.tick();
      this.writeLayoutToGraph();
      this.sigma?.refresh();

      // Stop at the alpha floor. Reporting the loop as finished is what lets the
      // idle case be asserted as "genuinely stopped" rather than "merely slow".
      if (layout.alpha() <= OBSIDIAN_FORCES.alphaMin) return;
      this.layoutFrame = window.requestAnimationFrame(step);
    };

    this.layoutFrame = window.requestAnimationFrame(step);
  }

  /** Cancel a queued frame. Used by Reset and teardown. */
  private stopLayoutLoop(): void {
    if (this.layoutFrame !== null) {
      window.cancelAnimationFrame(this.layoutFrame);
      this.layoutFrame = null;
    }
  }

  /**
   * Pin a node at the pointer by writing d3's own `fx`/`fy`.
   *
   * d3's integration step snaps a pinned node to `fx`/`fy` and zeroes its
   * velocity every tick, which is what keeps the dragged node exactly under the
   * pointer while everything else relaxes around it. Called for both the press
   * (id only, current graph position) and each move (explicit position).
   */
  private pinNodeForDrag(node: string, x?: number, y?: number): void {
    const layout = this.layout;
    const graph = this.graph;
    if (!layout || !graph) return;

    const attrs = graph.getNodeAttributes(node);
    const targetX = x ?? (Number(attrs['x']) || 0);
    const targetY = y ?? (Number(attrs['y']) || 0);

    for (const entry of layout.nodes()) {
      if (entry.id !== node) continue;
      entry.fx = targetX;
      entry.fy = targetY;
    }
  }

  /**
   * Clear the drag pin from every node.
   *
   * Every node rather than just the dragged one: the pin lives only in the
   * simulation, so a pin whose owner was lost (a pointerup outside the canvas,
   * a rebuild mid-gesture) would otherwise freeze that node against all future
   * layout with nothing left to clear it.
   */
  private releasePinnedNode(): void {
    for (const entry of this.layout?.nodes() ?? []) {
      entry.fx = null;
      entry.fy = null;
    }
  }

  private disposeSigma(): void {
    this.stopLayoutLoop();
    // d3 keeps its own internal timer; explicit stop guarantees no tick survives
    // the component (killing Sigma alone would leave the simulation running).
    this.layout?.stop();
    this.layout = null;
    if (this.sigma) {
      this.sigma.kill();
      this.sigma = null;
    }
    this.graph = null;
    this.draggedNode = null;
    this.isDragging = false;
    this.dragStart = null;
    this.draggingActive = false;
    // Drop the pins with the simulation, and the window-level net with Sigma's
    // captors: a rebuild would otherwise stack a second safety net on every
    // theme change.
    this.releasePinnedNode();
    this.detachDragSafetyNet?.();
    this.detachDragSafetyNet = null;
    this.layoutHome.clear();
  }

  /**
   * Lay out this frame's label candidates without overlaps and paint them.
   *
   * Runs on Sigma's `afterRender`, onto the label canvas Sigma has just cleared
   * and filled with nothing (the collector above draws nothing itself).
   */
  private drawLabels(): void {
    const sigma = this.sigma as (Sigma & { getCanvases?: () => Record<string, HTMLCanvasElement> }) | null;
    const canvas = sigma?.getCanvases?.()['labels'];
    const context = canvas?.getContext('2d');
    if (!sigma || !canvas || !context) return;

    const font = "'Hanken Grotesk', sans-serif";
    const measure = (label: string, weight: string): number => {
      context.font = `${weight} ${LABEL_DRAW_SIZE}px ${font}`;
      return context.measureText(label).width;
    };
    // Every node disc on screen, so labels can steer around them.
    const discs: DiscObstacle[] = [];
    const scaleSize = (sigma as unknown as { scaleSize?: (size: number) => number }).scaleSize;
    this.graph?.forEachNode((key, attrs) => {
      const point = sigma.graphToViewport({ x: Number(attrs['x']) || 0, y: Number(attrs['y']) || 0 });
      const size = Number(attrs['size']) || 0;
      discs.push({ key, x: point.x, y: point.y, size: scaleSize ? scaleSize.call(sigma, size) : size });
    });
    const placed = placeLabels(
      this.labelCandidates,
      measure,
      logicalCanvasWidth(canvas),
      LABEL_DRAW_SIZE,
      discs
    );
    drawPlacedLabels(context, placed, LABEL_DRAW_SIZE, font, hexToRgba(this.theme.field, 0.9));
  }

  /**
   * The node nearest a viewport point, within the touch hit radius.
   *
   * Only on a coarse pointer: a mouse can hit a 3px disc, a finger cannot.
   */
  private nearestNodeForTouch(point: { x: number; y: number } | undefined): string | null {
    const sigma = this.sigma;
    const graph = this.graph;
    if (!sigma || !graph || !point) return null;
    if (typeof window.matchMedia !== 'function' || !window.matchMedia('(pointer: coarse)').matches) {
      return null;
    }
    let best: string | null = null;
    let bestDistance = TOUCH_HIT_RADIUS_PX;
    graph.forEachNode((node, attrs) => {
      const viewport = sigma.graphToViewport({ x: Number(attrs['x']) || 0, y: Number(attrs['y']) || 0 });
      const distance = Math.hypot(viewport.x - point.x, viewport.y - point.y);
      if (distance < bestDistance) {
        best = node;
        bestDistance = distance;
      }
    });
    return best;
  }

  private refreshRendering(): void {
    if (this.sigma) {
      this.sigma.refresh();
    }
  }

  /* ── Template actions ── */

  /**
   * The fraction of each stage dimension the graph may occupy.
   *
   * `FIT_OCCUPANCY` alone assumes labels fit inside the drawing. They do not, but
   * they no longer need room reserved for them: `drawFlipsAtEdgeNodeLabel` keeps
   * every label on-canvas by flipping it to the other side of its node, so the
   * only thing left to reserve is a small constant edge allowance.
   *
   * Reserving a per-label gutter here was measurably harmful on a phone, where a
   * long topic name needs ~140px of a 369px stage: the reservation made the
   * width the binding axis of the fit and shrank every node instead of an
   * occasional word. See `LABEL_EDGE_ALLOWANCE_FRACTION`.
   */
  private fitOccupancy(): { x: number; y: number } {
    return {
      x: FIT_OCCUPANCY - LABEL_EDGE_ALLOWANCE_FRACTION,
      y: FIT_OCCUPANCY,
    };
  }

  /**
   * Stage height covered by the overlays, which the fit should frame around.
   *
   * Only on a portrait stage: there the legend spans the top and the rail sits
   * across the bottom, so a graph centred on the whole stage measured with its
   * top third empty and its lower nodes under the rail. On a landscape stage
   * both are small corner items and reserving full-width bands for them would
   * only shrink a graph that is already bound by height.
   */
  private overlayInsets(): { top: number; bottom: number } {
    const stage = this.sigmaContainer?.nativeElement?.getBoundingClientRect();
    if (!stage || !(stage.height > stage.width)) return { top: 0, bottom: 0 };
    const legend = this.mapLegend?.nativeElement?.getBoundingClientRect();
    const rail = this.mapHud?.nativeElement?.querySelector('.map-actions')?.getBoundingClientRect();
    return {
      top: legend && legend.height ? Math.max(0, legend.bottom - stage.top) : 0,
      bottom: rail && rail.height ? Math.max(0, stage.bottom - rail.top) : 0,
    };
  }

  /**
   * Keep the selected node out from under the selection card.
   *
   * On a phone the card spans the bottom of the stage, so tapping a node in the
   * lower part of the graph opened a card on top of the very node it describes.
   * Pan just enough to lift the node clear; the zoom is left alone.
   */
  private revealSelected(): void {
    window.requestAnimationFrame(() => {
      const sigma = this.sigma;
      const id = this.selectedNodeId();
      const card = this.mapHud?.nativeElement?.querySelector('.map-card')?.getBoundingClientRect();
      const stage = this.sigmaContainer?.nativeElement?.getBoundingClientRect();
      if (!sigma || !id || !card || !stage || !this.graph?.hasNode(id) || !card.height) return;
      const attrs = this.graph.getNodeAttributes(id);
      const point = sigma.graphToViewport({ x: Number(attrs['x']) || 0, y: Number(attrs['y']) || 0 });
      // Only when the card actually covers the node: in landscape it sits in the
      // left half, and a node to its right must not move.
      const margin = 32;
      const left = card.left - stage.left - margin;
      const right = card.right - stage.left + margin;
      const limit = card.top - stage.top - margin;
      if (point.x < left || point.x > right || point.y <= limit) return;
      const camera = sigma.getCamera();
      const state = camera.getState();
      const shift = sigma.viewportToFramedGraph({ x: point.x, y: point.y }, { cameraState: state });
      const target = sigma.viewportToFramedGraph({ x: point.x, y: limit }, { cameraState: state });
      camera.animate(
        { x: state.x + (shift.x - target.x), y: state.y + (shift.y - target.y) },
        { duration: 300 }
      );
    });
  }

  /**
   * Compute the graph's extent in graph coordinates.
   */
  private graphExtent(
    only?: Set<string>
  ): { minX: number; maxX: number; minY: number; maxY: number } | null {
    if (!this.graph || this.graph.order === 0) return null;
    let minX = Infinity;
    let maxX = -Infinity;
    let minY = Infinity;
    let maxY = -Infinity;
    this.graph.forEachNode((node, attrs) => {
      if (only && !only.has(node)) return;
      const x = Number(attrs['x']) || 0;
      const y = Number(attrs['y']) || 0;
      if (x < minX) minX = x;
      if (x > maxX) maxX = x;
      if (y < minY) minY = y;
      if (y > maxY) maxY = y;
    });
    if (!Number.isFinite(minX)) return null;
    return { minX, maxX, minY, maxY };
  }

  /**
   * The camera state that frames the whole graph, derived from Sigma's own
   * coordinate conversion rather than from hand-rolled maths.
   *
   * `sigma.graphToViewport(point, { cameraState })` evaluates the mapping for a
   * SUPPLIED camera state, so it is an exact oracle: probe the graph extent at
   * ratio 1, and since the on-screen span scales as 1/ratio, the ratio that just
   * fits is `max(spanX / (W * occupancy), spanY / (H * occupancy))`.
   *
   * Two earlier attempts got this wrong and both shipped:
   *   - `min(W * occupancy / spanX, ...)` is the reciprocal of the answer;
   *   - dividing both axes by `max(W, H)` matches Sigma's normalizer but is not
   *     the ratio maths, and silently under-fits whenever W < H.
   * The desktop stage (990x558, W > H) hid both errors behind a coincidence. On
   * a portrait phone (316x523) the second one shipped a ratio of 0.604 where 1.0
   * was needed, leaving **5 of 53 nodes off screen on first open**.
   *
   * Asking Sigma instead of re-deriving its algebra keeps this correct for any
   * viewport aspect and any future change to its internals.
   */
  private fitCameraState(
    occupancy: { x: number; y: number } = { x: FIT_OCCUPANCY, y: FIT_OCCUPANCY },
    insets: { top: number; bottom: number } = { top: 0, bottom: 0 }
  ): { x: number; y: number; ratio: number } | null {
    const extent = this.graphExtent();
    const sigma = this.sigma;
    if (!extent || !sigma) return null;

    const { width, height } = sigma.getDimensions();
    if (!width || !height) return null;

    // Probe the extent at ratio 1 through Sigma's own mapping.
    const probe = { x: 0.5, y: 0.5, angle: 0, ratio: 1 };
    const cornerA = sigma.graphToViewport({ x: extent.minX, y: extent.minY }, { cameraState: probe });
    const cornerB = sigma.graphToViewport({ x: extent.maxX, y: extent.maxY }, { cameraState: probe });
    const spanX = Math.abs(cornerB.x - cornerA.x);
    const spanY = Math.abs(cornerB.y - cornerA.y);

    // Prefer the oracle; fall back to the raw extent if a probe degenerates.
    const effectiveX = spanX > 0 ? spanX : Math.max(extent.maxX - extent.minX, 1e-6);
    const effectiveY = spanY > 0 ? spanY : Math.max(extent.maxY - extent.minY, 1e-6);

    const freeHeight = Math.max(height * 0.5, height - insets.top - insets.bottom);
    const ratio = Math.max(
      effectiveX / (width * occupancy.x),
      effectiveY / (freeHeight * occupancy.y),
      1e-6
    );

    // Centre in the free band between the overlays, not on the whole stage.
    // The camera state that shows the graph's centre at viewport y + dy is the
    // framed point that sits at y - dy under a centred camera.
    const dy = (insets.top - insets.bottom) / 2;
    if (dy === 0) return { x: 0.5, y: 0.5, ratio };
    const framed = sigma.viewportToFramedGraph(
      { x: width / 2, y: height / 2 - dy },
      { cameraState: { x: 0.5, y: 0.5, angle: 0, ratio } }
    );
    if (!Number.isFinite(framed.x) || !Number.isFinite(framed.y)) return { x: 0.5, y: 0.5, ratio };
    return { x: framed.x, y: framed.y, ratio };
  }

  /**
   * Convert a point in graph coordinates to the camera's framed coordinates,
   * using Sigma's own conversion so the result is exact for any viewport aspect.
   *
   * The earlier hand-derived `0.5 + (g - centre) / max(W, H)` matched Sigma's
   * normalizer but not its viewport mapping, so it only held when W > H. On a
   * portrait phone the same expression put the target node off centre.
   *
   * Method: the point shown at the CENTRE of the stage has camera coordinates
   * (0.5, 0.5). Placing our target there means asking Sigma what camera state
   * maps our target to the stage centre — which is exactly
   * `viewportToFramedGraph` evaluated on the point we want at the centre.
   */
  private graphPointToFramed(x: number, y: number): { x: number; y: number } | null {
    const sigma = this.sigma;
    if (!sigma) return null;

    const { width, height } = sigma.getDimensions();
    if (!width || !height) return null;

    // Where does the target land with the camera centred and unzoomed?
    const probe = { x: 0.5, y: 0.5, angle: 0, ratio: 1 };
    const viewportPoint = sigma.graphToViewport({ x, y }, { cameraState: probe });

    // Framed coordinates are the inverse: the camera state that would put this
    // viewport point at the centre.
    const framed = sigma.viewportToFramedGraph(viewportPoint, { cameraState: probe });
    if (!Number.isFinite(framed.x) || !Number.isFinite(framed.y)) return null;
    return { x: framed.x, y: framed.y };
  }

  /** Frame the whole graph. */
  fitGraph(): void {
    const state = this.fitCameraState(this.fitOccupancy(), this.overlayInsets());
    if (!state || !this.sigma) return;
    this.sigma.getCamera().animate(state, { duration: 350 });
  }

  /**
   * Restore the settled layout AND re-frame it.
   *
   * "Reset" used to call `animatedReset()`, which only touched the camera — so
   * after a user had dragged nodes around, Reset left every moved node exactly
   * where it was. It now restores the positions the simulation settled on, then
   * re-frames, which matches what the button's label promises.
   */
  resetView(): void {
    if (this.graph && this.layoutHome.size) {
      const graph = this.graph;

      // Halt any in-flight relaxation first, so the restore is not immediately
      // overwritten by a frame that was already queued.
      this.stopLayoutLoop();
      this.layout?.stop();
      this.layout?.alphaTarget(0);

      graph.forEachNode((node) => {
        const home = this.layoutHome.get(node);
        if (home) {
          graph.setNodeAttribute(node, 'x', home.x);
          graph.setNodeAttribute(node, 'y', home.y);
        }
      });

      // Restore the simulation to match, clearing any pin left behind by an
      // interrupted drag (a pointerup lost outside the window). The frame loop
      // stops at `alphaMin`, so without this the pin could survive into a later
      // drag's re-heat and hold a node the user is not touching.
      this.layout?.nodes().forEach((node) => {
        const home = this.layoutHome.get(node.id);
        if (!home) return;
        node.x = home.x;
        node.y = home.y;
        node.vx = 0;
        node.vy = 0;
        node.fx = null;
        node.fy = null;
      });

      this.refreshRendering();
    }
    this.fitGraph();
  }

  /**
   * Bring the selected node to the centre of the stage.
   *
   * The previous implementation animated the camera to the node's raw graph
   * coordinates while forcing `ratio: 0.45`, mixing two coordinate spaces. It
   * measured **53 of 53** nodes off screen, with the camera at y = -42.9.
   *
   * Centring is now: convert the node to framed coordinates, keep the user's
   * current zoom if it is already close, and otherwise pull in to a readable
   * neighbourhood. Verified to land the node within 0 px of the stage centre.
   */
  centerSelected(): void {
    const selectedId = this.selectedNodeId();
    const sigma = this.sigma;
    if (!selectedId || !this.graph || !sigma || !this.graph.hasNode(selectedId)) return;

    const attributes = this.graph.getNodeAttributes(selectedId);
    const framed = this.graphPointToFramed(
      Number(attributes['x']) || 0,
      Number(attributes['y']) || 0
    );
    if (!framed) return;

    const camera = sigma.getCamera();
    const fit = this.fitCameraState(this.fitOccupancy());

    // Stay where the user is if they are already zoomed in past the fit;
    // otherwise move in far enough to read the node's neighbourhood.
    const targetRatio = fit ? Math.min(camera.ratio, Math.max(fit.ratio * 0.6, 0.3)) : camera.ratio;

    camera.animate({ x: framed.x, y: framed.y, ratio: targetRatio }, { duration: 350 });
  }

  async toggleFullscreen(): Promise<void> {
    if (this.isFullscreen()) {
      await this.exitFullscreen();
      return;
    }
    const stage = this.mapStage?.nativeElement;
    if (!stage) return;
    if (document.fullscreenEnabled && stage.requestFullscreen) {
      await stage.requestFullscreen();
      return;
    }
    // iPhone Safari has no Fullscreen API for anything but video, so Focus mode
    // used to do nothing there — on the one device that needs the room most.
    // Fall back to pinning the stage over the page (`.is-fullscreen` in CSS).
    this.pseudoFullscreen = true;
    this.isFullscreen.set(true);
    window.setTimeout(() => this.sigma?.refresh(), 0);
  }

  private async exitFullscreen(): Promise<void> {
    if (this.pseudoFullscreen) {
      this.pseudoFullscreen = false;
      this.isFullscreen.set(false);
      window.setTimeout(() => this.sigma?.refresh(), 0);
      return;
    }
    if (document.fullscreenElement && document.exitFullscreen) {
      await document.exitFullscreen();
    }
  }

  /** Close the selection card: the same as clicking empty space. */
  clearSelection(): void {
    this.hoveredId.set(null);
    this.selectedNodeId.set(null);
    this.selectionCleared.emit();
    this.refreshRendering();
  }

  /** Select a related topic from the card and bring it into view. */
  selectRelated(id: string): void {
    this.selectAccessibleNode(id);
    this.centerSelected();
  }

  zoomIn(): void {
    const camera = this.sigma?.getCamera();
    if (camera) {
      camera.animatedZoom({ duration: 200 });
    }
  }

  zoomOut(): void {
    const camera = this.sigma?.getCamera();
    if (camera) {
      camera.animatedUnzoom({ duration: 200 });
    }
  }

  selectAccessibleNode(id: string): void {
    this.selectedNodeId.set(id);
    this.topicSelected.emit(id);
    this.refreshRendering();
  }
}
