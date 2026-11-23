import Graph from 'graphology';
import Sigma, { DEFAULT_STYLES } from 'sigma';

import type { TopicGraphEdgeDto, TopicGraphNodeDto, TopicKindDto } from '../../core/services/topics.service';
import type { MapColorMode } from '../brain.helpers';
import {
  COMMUNITY_HUES,
  COMMUNITY_MIN_SIZE,
  EDGE_ALPHA_DIM,
  EDGE_ALPHA_MIN,
  EDGE_ALPHA_RANGE,
  EDGE_SIZE_MAX,
  EDGE_SIZE_MIN,
  LABEL_DRAW_SIZE,
  LABEL_PRIORITY,
  LABEL_RENDER_MIN_SIZE,
  NODE_ALPHA_DIM,
  NODE_SIZE_MAX,
  NODE_SIZE_MIN,
  OBSIDIAN_FORCES,
  detectCommunities,
  drawPlacedLabels,
  drawThemeNodeHover,
  hashSeed,
  hexToRgba,
  kindIndexFromColorKey,
  logicalCanvasWidth,
  mixHex,
  pickHubs,
  placeLabels,
  readTheme,
  resolveKindColor,
  seedPositions,
  selectLabelCandidates,
  type DiscObstacle,
  type LabelCandidate,
  type LabelSource,
  type MapCamera,
  type ThemeColors,
} from './topic-map.helpers';

/** The typeface every map label is drawn in. */
const LABEL_FONT = "'Hanken Grotesk', sans-serif";

/**
 * How far from a node, in CSS px, a tap still selects it on a touch screen.
 */
const TOUCH_HIT_RADIUS_PX = 22;

/** A node's place in the focused neighbourhood. */
export type NodeEmphasis = 'active' | 'neighbour' | 'dimmed';

/** How one node's label is drawn this frame. */
export interface LabelStyle {
  color: string;
  weight: string;
  priority: number;
  /** Always a label candidate (hubs, and the focused neighbourhood). */
  forced: boolean;
}

/** A node as the label pass sees it, in viewport px. */
interface LabelNode extends LabelSource {
  style: LabelStyle;
  nodeColor: string;
  /** Drawn on a themed plate: the hovered node and the highlighted one. */
  plate: boolean;
}

export interface TopicMapRendererCallbacks {
  getHoveredId: () => string | null;
  getSelectedId: () => string | null;
  getRootId?: () => string | null;
  isDragging: () => boolean;
  hasDraggedNode: () => boolean;
  onHoverChange: (id: string | null) => void;
  onSelectNode: (id: string) => void;
  onOpenTopic: (id: string) => void;
  onClearSelection: () => void;
}

export interface BuildGraphOptions {
  rootId?: string | null;
  initialPositions?: ReadonlyMap<string, { x: number; y: number }> | null;
  colorMode?: MapColorMode;
  topicKinds?: readonly TopicKindDto[];
}

export interface BuiltTopicGraph {
  graph: Graph;
  hueById: Map<string, string | null>;
}

/**
 * Owns the Graphology graph construction, Sigma WebGL renderer,
 * node/edge reducers, custom 2D label canvas pass, and pointer/stage events.
 */
export class TopicMapRenderer {
  private sigma: Sigma | null = null;
  private graph: Graph | null = null;
  private labelCanvas: HTMLCanvasElement | null = null;
  private theme: ThemeColors = readTheme();
  private callbacks: TopicMapRendererCallbacks | null = null;

  get sigmaInstance(): Sigma | null {
    return this.sigma;
  }

  get graphInstance(): Graph | null {
    return this.graph;
  }

  get currentTheme(): ThemeColors {
    return this.theme;
  }

  getCameraState(): MapCamera | null {
    return this.sigma ? { ...this.sigma.getCamera().getState() } : null;
  }

  /**
   * Construct the Graphology graph with seeded coordinates, sizes, hubs,
   * mode-specific colours (kind / cluster / none), and styled edges.
   */
  buildGraph(
    visibleNodes: readonly TopicGraphNodeDto[],
    visibleEdges: readonly TopicGraphEdgeDto[],
    options: BuildGraphOptions = {}
  ): BuiltTopicGraph {
    this.theme = readTheme();
    const graph = new Graph();
    const rootId = options.rootId ?? null;
    const initialPositions = options.initialPositions ?? null;
    const colorMode: MapColorMode = options.colorMode ?? 'cluster';

    const usages = visibleNodes.map((n) => Math.max(0, n.usageCount));
    const minUsage = Math.min(...usages);
    const maxUsage = Math.max(...usages);
    const usageRange = Math.max(1, maxUsage - minUsage);
    const maxShared = Math.max(1, ...visibleEdges.map((e) => e.sharedNotes));

    const seedById = this.resolveInitialSeeds(visibleNodes, visibleEdges, rootId, initialPositions);

    const hubs = pickHubs([...visibleNodes]);

    for (const node of visibleNodes) {
      const ratio = (Math.max(0, node.usageCount) - minUsage) / usageRange;
      const size = NODE_SIZE_MIN + (NODE_SIZE_MAX - NODE_SIZE_MIN) * Math.sqrt(ratio);

      const seeded = seedById.get(node.id)!;
      const isRoot = rootId !== null && node.id === rootId;
      const hub = hubs.has(node.id);

      graph.addNode(node.id, {
        label: node.name,
        size,
        // The colour is written by `recolor`, so an in-place kind or mode
        // change goes through exactly the same path as the first build.
        color: this.theme.node,
        hue: null,
        kindId: node.kindId ?? null,
        labelColor: isRoot || hub ? this.theme.labelActive : hexToRgba(this.theme.label, 0.8 + ratio * 0.16),
        labelWeight: isRoot || hub ? '600' : '400',
        labelPriority: isRoot ? LABEL_PRIORITY.active : hub ? LABEL_PRIORITY.hub : LABEL_PRIORITY.normal,
        forceLabel: isRoot || hub,
        x: seeded.x,
        y: seeded.y,
        usageCount: node.usageCount,
      });
    }

    for (const edge of visibleEdges) {
      const strength = edge.sharedNotes / maxShared;
      const edgeSize = EDGE_SIZE_MIN + strength * (EDGE_SIZE_MAX - EDGE_SIZE_MIN);
      const alpha = EDGE_ALPHA_MIN + strength * EDGE_ALPHA_RANGE;
      try {
        graph.addEdge(edge.sourceId, edge.targetId, {
          size: edgeSize,
          color: hexToRgba(this.theme.edge, alpha),
          sharedNotes: edge.sharedNotes,
        });
      } catch {
        // Duplicate edge or missing node — skip silently.
      }
    }

    const hueById = this.recolor(colorMode, options.topicKinds ?? [], graph);
    return { graph, hueById };
  }

  /**
   * Recompute node and edge colours for `graph` without rebuilding it.
   *
   * Arriving kind metadata changes what a topic looks like, not where it is:
   * disposing Sigma for that rebuilds the graph and can carry an in-flight
   * camera animation, leaving the map at the default or half-zoomed view.
   * This writes the new colours into the existing graph attributes instead.
   */
  recolor(
    colorMode: MapColorMode,
    topicKinds: readonly TopicKindDto[] = [],
    graph: Graph | null = this.graph
  ): Map<string, string | null> {
    this.theme = readTheme();
    const hueById = new Map<string, string | null>();
    if (!graph || graph.order === 0) return hueById;

    const kindById = new Map(topicKinds.map((kind) => [kind.id, kind]));
    const usages: number[] = [];
    graph.forEachNode((_node, attrs) => usages.push(Math.max(0, Number(attrs['usageCount']) || 0)));
    const minUsage = Math.min(...usages);
    const maxUsage = Math.max(...usages);
    const usageRange = Math.max(1, maxUsage - minUsage);

    const ids: string[] = [];
    const edgeList: Array<{ sourceId: string; targetId: string; sharedNotes: number }> = [];
    let maxShared = 1;
    graph.forEachNode((node) => ids.push(node));
    graph.forEachEdge((_edge, attrs, source, target) => {
      const sharedNotes = Math.max(1, Number(attrs['sharedNotes']) || 1);
      edgeList.push({ sourceId: source, targetId: target, sharedNotes });
      if (sharedNotes > maxShared) maxShared = sharedNotes;
    });

    const clusterRank = colorMode === 'cluster' ? detectCommunities(ids, edgeList) : null;
    const clusterSizes = new Map<number, number>();
    if (clusterRank) {
      for (const rank of clusterRank.values()) {
        clusterSizes.set(rank, (clusterSizes.get(rank) ?? 0) + 1);
      }
    }

    const clusterHueOf = (id: string): string | null => {
      if (!clusterRank) return null;
      const rank = clusterRank.get(id);
      if (rank === undefined || rank >= COMMUNITY_HUES) return null;
      if ((clusterSizes.get(rank) ?? 0) < COMMUNITY_MIN_SIZE) return null;
      return this.theme.communities[rank] ?? null;
    };

    const resolveNodeKind = (kindId: string | null): { key: string | null; hue: string | null } => {
      const trimmed = kindId?.trim();
      if (!trimmed) return { key: null, hue: null };
      const kind = kindById.get(trimmed);
      if (kind) {
        return { key: kind.id, hue: resolveKindColor(kind.colorKey, this.theme) };
      }
      if (kindIndexFromColorKey(trimmed) !== null) {
        return { key: trimmed, hue: resolveKindColor(trimmed, this.theme) };
      }
      return { key: null, hue: null };
    };

    const groupKeyById = new Map<string, string | null>();

    graph.forEachNode((node, attrs) => {
      const ratio = (Math.max(0, Number(attrs['usageCount']) || 0) - minUsage) / usageRange;
      let hue: string | null = null;
      let nodeColor: string;
      let groupKey: string | null = null;

      if (colorMode === 'kind') {
        const resolved = resolveNodeKind(
          typeof attrs['kindId'] === 'string' ? (attrs['kindId'] as string) : null
        );
        hue = resolved.hue;
        groupKey = resolved.key;
        nodeColor = resolved.hue ?? this.theme.kindNone;
      } else if (colorMode === 'cluster') {
        hue = clusterHueOf(node);
        groupKey = hue;
        nodeColor = hue
          ? mixHex(this.theme.node, hue, 0.55 + ratio * 0.45)
          : mixHex(this.theme.node, this.theme.nodeHead, 0.18 + ratio * 0.42);
      } else {
        hue = null;
        groupKey = null;
        nodeColor = mixHex(this.theme.node, this.theme.nodeHead, 0.18 + ratio * 0.42);
      }

      hueById.set(node, hue);
      groupKeyById.set(node, groupKey);
      graph.setNodeAttribute(node, 'hue', hue);
      graph.setNodeAttribute(node, 'color', nodeColor);
    });

    graph.forEachEdge((edge, attrs, source, target) => {
      const sharedNotes = Math.max(1, Number(attrs['sharedNotes']) || 1);
      const strength = sharedNotes / maxShared;
      const alpha = EDGE_ALPHA_MIN + strength * EDGE_ALPHA_RANGE;
      const sourceGroup = groupKeyById.get(source) ?? null;
      const sourceHue = hueById.get(source) ?? null;
      const sameGroup =
        sourceGroup !== null && sourceHue !== null && sourceGroup === (groupKeyById.get(target) ?? null);
      graph.setEdgeAttribute(
        edge,
        'color',
        sameGroup
          ? hexToRgba(mixHex(this.theme.edge, sourceHue, 0.6), alpha)
          : hexToRgba(this.theme.edge, alpha)
      );
    });

    return hueById;
  }

  private resolveInitialSeeds(
    visibleNodes: readonly TopicGraphNodeDto[],
    visibleEdges: readonly TopicGraphEdgeDto[],
    rootId: string | null,
    initialPositions: ReadonlyMap<string, { x: number; y: number }> | null
  ): Map<string, { x: number; y: number }> {
    const seedById = new Map<string, { x: number; y: number }>();

    if (!initialPositions || initialPositions.size === 0) {
      const seeds = visibleNodes.map((n) => ({ id: n.id, x: 0, y: 0 }));
      seedPositions(seeds, OBSIDIAN_FORCES.linkDistance);
      for (const s of seeds) seedById.set(s.id, { x: s.x, y: s.y });
      if (rootId && seedById.has(rootId)) seedById.set(rootId, { x: 0, y: 0 });
      return seedById;
    }

    for (const n of visibleNodes) {
      if (rootId && n.id === rootId) {
        seedById.set(n.id, { x: 0, y: 0 });
        continue;
      }
      const existing = initialPositions.get(n.id);
      if (existing) seedById.set(n.id, { x: existing.x, y: existing.y });
    }

    const adjacency = new Map<string, string[]>();
    for (const edge of visibleEdges) {
      if (edge.sourceId === edge.targetId) continue;
      const a = adjacency.get(edge.sourceId) ?? [];
      a.push(edge.targetId);
      adjacency.set(edge.sourceId, a);
      const b = adjacency.get(edge.targetId) ?? [];
      b.push(edge.sourceId);
      adjacency.set(edge.targetId, b);
    }

    // Spawn newly added nodes near the centroid of already-placed neighbours (pushed slightly outward)
    // across a few passes so expanding depth places outer hops beyond inner ones.
    for (let pass = 0; pass < 3; pass += 1) {
      const additions = new Map<string, { x: number; y: number }>();
      for (const n of visibleNodes) {
        if (seedById.has(n.id)) continue;
        const placedNeighbours = (adjacency.get(n.id) ?? [])
          .map((id) => seedById.get(id))
          .filter((p): p is { x: number; y: number } => !!p);
        if (!placedNeighbours.length) continue;

        let cx = 0;
        let cy = 0;
        for (const p of placedNeighbours) {
          cx += p.x;
          cy += p.y;
        }
        cx /= placedNeighbours.length;
        cy /= placedNeighbours.length;

        const angle = hashSeed(`${n.id}:angle`) * 2 * Math.PI;
        const dist = Math.hypot(cx, cy);
        if (dist < 1) {
          const r = OBSIDIAN_FORCES.linkDistance * (0.8 + 0.4 * hashSeed(`${n.id}:radius`));
          additions.set(n.id, { x: r * Math.cos(angle), y: r * Math.sin(angle) });
        } else {
          const outward = 1 + OBSIDIAN_FORCES.linkDistance * 0.45 / dist;
          const jitter = OBSIDIAN_FORCES.collideRadius * (0.4 + 0.6 * hashSeed(`${n.id}:radius`));
          additions.set(n.id, {
            x: cx * outward + jitter * Math.cos(angle),
            y: cy * outward + jitter * Math.sin(angle),
          });
        }
      }
      if (additions.size === 0) break;
      for (const [id, pos] of additions) seedById.set(id, pos);
    }

    const unplaced = visibleNodes.filter((n) => !seedById.has(n.id)).map((n) => ({ id: n.id, x: 0, y: 0 }));
    if (unplaced.length) {
      seedPositions(unplaced, OBSIDIAN_FORCES.linkDistance);
      for (const s of unplaced) seedById.set(s.id, { x: s.x, y: s.y });
    }

    return seedById;
  }

  /**
   * Mount Sigma onto `container` with WebGL reducers, the 2D label layer,
   * and interaction event handlers.
   */
  mount(
    graph: Graph,
    container: HTMLElement,
    callbacks: TopicMapRendererCallbacks
  ): Sigma {
    this.graph = graph;
    this.callbacks = callbacks;

    const sigma = new Sigma(graph, container, {
      settings: {
        renderLabels: false,
        renderEdgeLabels: false,
        labelRenderedSizeThreshold: LABEL_RENDER_MIN_SIZE,
        labelDensity: 3,
        enableEdgeEvents: false,
        enableCameraMouseRotation: false,
        allowInvalidContainer: true,
        itemSizesReference: 'screen',
        zoomToSizeRatioFunction: Math.sqrt,
        autoRescale: false,
        stagePadding: 0,
      },
      styles: {
        nodes: [DEFAULT_STYLES.nodes, { backdropVisibility: 'hidden' }],
      },
      nodeReducer: (node, _data, attrs) => {
        const emphasis = this.emphasisOf(node);
        if (!emphasis) return {};
        const label = this.labelStyleOf(attrs, emphasis);
        const labelFields = {
          labelColor: label.color,
          labelVisibility: label.forced ? ('visible' as const) : ('auto' as const),
        };
        switch (emphasis) {
          case 'active':
            return { ...labelFields, color: this.theme.nodeHead, zIndex: 2, highlighted: true };
          case 'neighbour':
            return { ...labelFields, zIndex: 1 };
          case 'dimmed':
            return { ...labelFields, color: hexToRgba(this.theme.node, NODE_ALPHA_DIM), zIndex: 0 };
        }
      },
      edgeReducer: (edge, data) => {
        const activeId = this.activeNodeId();
        if (!activeId) return {};
        const source = graph.source(edge);
        const target = graph.target(edge);
        if (source === activeId || target === activeId) {
          const activeHue = graph.getNodeAttributes(activeId)['hue'];
          return {
            color:
              typeof activeHue === 'string' && activeHue ? activeHue : this.theme.edgeActive,
            size: Math.max(data.size, 2) * 1.6,
            zIndex: 1,
          };
        }
        return { color: hexToRgba(this.theme.edge, EDGE_ALPHA_DIM), zIndex: 0 };
      },
    });

    this.sigma = sigma;
    this.labelCanvas = sigma.createCanvas('nostosLabels', {
      beforeLayer: 'mouse',
      style: { pointerEvents: 'none' },
    });
    sigma.on('afterRender', () => this.drawLabels());

    sigma.on('enterNode', ({ node }) => {
      callbacks.onHoverChange(node);
      container.style.cursor = 'grab';
      sigma.refresh();
    });

    sigma.on('leaveNode', () => {
      callbacks.onHoverChange(null);
      if (!callbacks.hasDraggedNode()) {
        container.style.cursor = 'default';
      }
      sigma.refresh();
    });

    sigma.on('clickNode', ({ node }) => {
      if (callbacks.isDragging()) return;
      callbacks.onSelectNode(node);
      sigma.refresh();
    });

    sigma.on('doubleClickNode', ({ node, preventSigmaDefault }) => {
      if (callbacks.isDragging()) return;
      preventSigmaDefault();
      callbacks.onOpenTopic(node);
      sigma.refresh();
    });

    sigma.on('clickStage', (payload?: { event?: { x: number; y: number } }) => {
      const near = this.nearestNodeForTouch(payload?.event);
      if (near) {
        callbacks.onSelectNode(near);
        sigma.refresh();
        return;
      }
      callbacks.onClearSelection();
      sigma.refresh();
    });

    return sigma;
  }

  /** Where a node stands relative to the focused topic, or null when nothing is focused. */
  emphasisOf(node: string): NodeEmphasis | null {
    const activeId = this.activeNodeId();
    if (!activeId) return null;
    if (node === activeId) return 'active';
    const graph = this.graph;
    return graph?.hasNode(activeId) && graph.hasNode(node) && graph.areNeighbors(activeId, node)
      ? 'neighbour'
      : 'dimmed';
  }

  /** How a node's label is drawn, adjusted for the focused neighbourhood. */
  labelStyleOf(attrs: Record<string, unknown>, emphasis: NodeEmphasis | null): LabelStyle {
    const resting: LabelStyle = {
      color: String(attrs['labelColor'] ?? this.theme.label),
      weight: String(attrs['labelWeight'] ?? '400'),
      priority: Number(attrs['labelPriority'] ?? LABEL_PRIORITY.normal),
      forced: attrs['forceLabel'] === true,
    };
    switch (emphasis) {
      case 'active':
        return {
          color: this.theme.labelActive,
          weight: '600',
          priority: LABEL_PRIORITY.active,
          forced: true,
        };
      case 'neighbour':
        return {
          ...resting,
          color: this.theme.labelActive,
          priority: LABEL_PRIORITY.active,
          forced: true,
        };
      case 'dimmed':
        return {
          ...resting,
          color: hexToRgba(this.theme.label, 0.62),
          weight: '400',
          priority: LABEL_PRIORITY.dimmed,
        };
      default:
        return resting;
    }
  }

  /** Pick this frame's labels, lay them out without overlaps, and paint them. */
  drawLabels(): void {
    const sigma = this.sigma;
    const graph = this.graph;
    const canvas = this.labelCanvas;
    const context = canvas?.getContext('2d');
    if (!sigma || !graph || !canvas || !context) return;

    const { width, height } = sigma.getDimensions();
    const pixelRatio = window.devicePixelRatio > 0 ? window.devicePixelRatio : 1;
    const backingWidth = Math.round(width * pixelRatio);
    const backingHeight = Math.round(height * pixelRatio);
    if (canvas.style.width !== `${width}px`) canvas.style.width = `${width}px`;
    if (canvas.style.height !== `${height}px`) canvas.style.height = `${height}px`;
    if (canvas.width !== backingWidth) canvas.width = backingWidth;
    if (canvas.height !== backingHeight) canvas.height = backingHeight;
    context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
    context.clearRect(0, 0, width, height);

    const hoveredId = this.callbacks?.getHoveredId() ?? null;
    const nodes: LabelNode[] = [];
    graph.forEachNode((key, attrs) => {
      const display = sigma.getNodeDisplayData(key);
      if (!display || display.visibility === 'hidden') return;
      const point = sigma.graphToViewport({ x: Number(attrs['x']) || 0, y: Number(attrs['y']) || 0 });
      const style = this.labelStyleOf(attrs, this.emphasisOf(key));
      nodes.push({
        key,
        x: point.x,
        y: point.y,
        size: sigma.scaleSize(display.size),
        label: display.label,
        forced: style.forced,
        style,
        nodeColor: display.color,
        plate: display.highlighted || key === hoveredId,
      });
    });

    const measure = (label: string, weight: string): number => {
      context.font = `${weight} ${LABEL_DRAW_SIZE}px ${LABEL_FONT}`;
      return context.measureText(label).width;
    };
    const candidates: LabelCandidate[] = selectLabelCandidates(nodes, {
      width,
      height,
      cellSize: sigma.getSetting('labelGridCellSize'),
      density: sigma.getSetting('labelDensity'),
      threshold: sigma.getSetting('labelRenderedSizeThreshold'),
    }).map((node) => ({
      key: node.key,
      x: node.x,
      y: node.y,
      size: node.size,
      label: node.label ?? '',
      color: node.style.color,
      weight: node.style.weight,
      priority: node.style.priority,
      reserveOnly: node.plate,
    }));
    const discs: DiscObstacle[] = nodes.map(({ key, x, y, size }) => ({ key, x, y, size }));
    const placed = placeLabels(candidates, measure, logicalCanvasWidth(canvas), LABEL_DRAW_SIZE, discs);
    drawPlacedLabels(context, placed, LABEL_DRAW_SIZE, LABEL_FONT, hexToRgba(this.theme.field, 0.9));

    for (const node of nodes) {
      if (!node.plate) continue;
      drawThemeNodeHover(
        context,
        {
          x: node.x,
          y: node.y,
          size: node.size,
          label: node.label,
          nodeColor: node.nodeColor,
          labelColor: node.style.color,
          labelWeight: node.style.weight,
        },
        LABEL_DRAW_SIZE,
        LABEL_FONT,
        this.theme.labelBox
      );
    }
  }

  /** The node nearest a viewport point, within the touch hit radius. */
  nearestNodeForTouch(point: { x: number; y: number } | undefined): string | null {
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

  refresh(): void {
    this.sigma?.refresh();
  }

  dispose(): void {
    if (this.sigma) {
      this.sigma.kill();
      this.sigma = null;
    }
    this.labelCanvas = null;
    this.graph = null;
    this.callbacks = null;
  }

  /**
   * The node the map is focused on: hovered, selected, or the local map's root,
   * in that order — but only while it is still IN the graph. Hiding a topic's
   * kind removes its node, and an id the graph no longer holds matched nothing:
   * every other node fell through to "dimmed" and the whole map turned grey.
   */
  private activeNodeId(): string | null {
    const graph = this.graph;
    if (!graph) return null;
    const candidates = [
      this.callbacks?.getHoveredId() ?? null,
      this.callbacks?.getSelectedId() ?? null,
      this.callbacks?.getRootId?.() ?? null,
    ];
    for (const id of candidates) {
      if (id && graph.hasNode(id)) return id;
    }
    return null;
  }
}
