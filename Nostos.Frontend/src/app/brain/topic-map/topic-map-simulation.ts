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

import {
  DRAG_ALPHA,
  DRAG_THRESHOLD_PX,
  OBSIDIAN_FORCES,
  OBSIDIAN_LINK_STRENGTH,
  SETTLE_ALPHA,
  alignmentRotation,
  centerStrengths,
  compareTopics,
  shelfPositions,
  type LayoutNode,
} from './topic-map.helpers';
import { graphExtent } from './topic-map-viewport';
import type { TopicGraphEdgeDto, TopicGraphNodeDto } from '../../core/services/topics.service';

export interface SimulationInitOptions {
  graph: Graph;
  visibleNodes: readonly TopicGraphNodeDto[];
  visibleEdges: readonly TopicGraphEdgeDto[];
  stageWidth: number;
  stageHeight: number;
}

export interface SimulationDragOptions {
  sigma: Sigma;
  graph: Graph;
  container: HTMLElement;
  onRefresh: () => void;
}

/**
 * Manages the d3-force simulation lifecycle, initial synchronous settle,
 * animation-frame relaxation loop, home-layout snapshot/restore, and
 * mouse/touch drag pinning for TopicMapComponent.
 */
export class TopicMapSimulation {
  private layout: Simulation<LayoutNode, undefined> | null = null;
  private layoutFrame: number | null = null;
  private graph: Graph | null = null;
  private onRefresh: (() => void) | null = null;

  /* Drag-to-reposition state */
  private draggedNode: string | null = null;
  private dragging = false;
  private dragStart: { x: number; y: number } | null = null;
  /**
   * True between a node press and its release on ANY input source.
   * Guards teardown so it runs once per gesture.
   */
  private draggingActive = false;
  private detachDragSafetyNet: (() => void) | null = null;
  private dragResetTimeout: number | null = null;
  private disposed = false;

  /**
   * The settled layout, kept so "Reset layout" can restore node positions
   * after the user has dragged things around.
   */
  private readonly layoutHome = new Map<string, { x: number; y: number }>();

  get simulation(): Simulation<LayoutNode, undefined> | null {
    return this.layout;
  }

  get isDragging(): boolean {
    return this.dragging;
  }

  get activeDraggedNode(): string | null {
    return this.draggedNode;
  }

  /**
   * Build the d3-force simulation, settle it synchronously, align its long axis
   * to the stage, shelve unconnected isolates, and snapshot home positions.
   */
  initLayout(options: SimulationInitOptions): void {
    const { graph, visibleNodes, visibleEdges, stageWidth, stageHeight } = options;
    this.disposed = false;
    this.graph = graph;

    const connected = new Set<string>();
    for (const edge of visibleEdges) {
      if (edge.sourceId === edge.targetId) continue;
      connected.add(edge.sourceId);
      connected.add(edge.targetId);
    }
    const isolates = visibleNodes.filter((n) => !connected.has(n.id));
    const layoutNodes: LayoutNode[] = visibleNodes
      .filter((n) => connected.has(n.id))
      .map((n) => {
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

    const linkStrength = (link: { source: unknown; target: unknown }): number => {
      const source = link.source as LayoutNode;
      const target = link.target as LayoutNode;
      const degree = Math.min(graph.degree(source.id), graph.degree(target.id));
      return degree > 0 ? OBSIDIAN_LINK_STRENGTH / degree : OBSIDIAN_LINK_STRENGTH;
    };

    const centre = centerStrengths(stageWidth, stageHeight);

    this.layout = forceSimulation<LayoutNode>(layoutNodes)
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
          .radius(OBSIDIAN_FORCES.collideRadius)
          .strength(OBSIDIAN_FORCES.collideStrength)
      );

    this.layout.alpha(SETTLE_ALPHA);
    let settleTicks = 0;
    while (this.layout.alpha() > OBSIDIAN_FORCES.alphaMin && settleTicks < 300) {
      this.layout.tick();
      settleTicks += 1;
    }

    const turn = alignmentRotation(
      layoutNodes.map((n) => ({ x: Number(n.x) || 0, y: Number(n.y) || 0 })),
      stageWidth,
      stageHeight
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

    if (isolates.length) {
      const extent = graphExtent(graph, connected);
      const shelf = shelfPositions(
        isolates.length,
        extent,
        OBSIDIAN_FORCES.collideRadius * 3.6
      );
      [...isolates]
        .sort((a, b) => compareTopics(a, b))
        .forEach((node, index) => {
          graph.setNodeAttribute(node.id, 'x', shelf[index].x);
          graph.setNodeAttribute(node.id, 'y', shelf[index].y);
        });
    }

    this.layoutHome.clear();
    graph.forEachNode((node, attrs) => {
      this.layoutHome.set(node, { x: Number(attrs['x']) || 0, y: Number(attrs['y']) || 0 });
    });
  }

  /** Wire mouse, touch, and window safety-net drag handlers onto Sigma. */
  attachDrag(options: SimulationDragOptions): void {
    const { sigma, graph, container, onRefresh } = options;
    this.graph = graph;
    this.onRefresh = onRefresh;

    const endDrag = (): void => {
      if (!this.draggingActive) return;
      const wasPinned = !!this.draggedNode;
      this.releasePinnedNode();
      if (wasPinned) this.coolLayout();
      this.draggedNode = null;
      this.dragStart = null;
      this.draggingActive = false;
      sigma.getCamera().enabled = true;
      container.classList.remove('dragging');
      if (this.dragResetTimeout !== null) {
        window.clearTimeout(this.dragResetTimeout);
      }
      // Reset the drag flag after Sigma has dispatched the click that follows
      // the release, so a genuine drag never also selects the node it moved.
      this.dragResetTimeout = window.setTimeout(() => {
        this.dragResetTimeout = null;
        this.dragging = false;
      }, 0);
    };

    const beginDrag = (node: string, x: number, y: number): void => {
      this.dragging = false;
      this.dragStart = { x, y };
      this.draggedNode = node;
      this.draggingActive = true;
      this.pinNodeForDrag(node);
      sigma.getCamera().enabled = false;
      container.classList.add('dragging');
    };

    const moveDrag = (node: string, x: number, y: number): void => {
      if (!this.dragging && this.dragStart) {
        const travelled = Math.hypot(x - this.dragStart.x, y - this.dragStart.y);
        if (travelled < DRAG_THRESHOLD_PX) return;
        this.dragging = true;
      }

      const pos = sigma.viewportToGraph({ x, y });
      graph.setNodeAttribute(node, 'x', pos.x);
      graph.setNodeAttribute(node, 'y', pos.y);
      this.pinNodeForDrag(node, pos.x, pos.y);
      this.heatLayout(DRAG_ALPHA);
    };

    sigma.on('downNode', (e) => {
      this.draggingActive = false;
      beginDrag(e.node, e.event.x, e.event.y);
    });

    sigma.getMouseCaptor().on('mousemovebody', (e) => {
      if (!this.draggedNode) return;
      moveDrag(this.draggedNode, e.x, e.y);
      e.preventSigmaDefault();
    });

    sigma.getMouseCaptor().on('mouseup', () => endDrag());

    sigma.getTouchCaptor().on('touchmove', (e) => {
      if (!this.draggedNode) return;
      const point = e.touches[0] ?? e.previousTouches[0];
      if (!point) return;
      moveDrag(this.draggedNode, point.x, point.y);
      e.preventSigmaDefault();
    });

    sigma.getTouchCaptor().on('touchup', () => endDrag());

    this.detachDragSafetyNet?.();
    const safetyNet = (): void => endDrag();
    window.addEventListener('pointerup', safetyNet);
    window.addEventListener('pointercancel', safetyNet);
    this.detachDragSafetyNet = () => {
      window.removeEventListener('pointerup', safetyNet);
      window.removeEventListener('pointercancel', safetyNet);
    };
  }

  /** Restore the settled home positions and clear any lingering drag pins. */
  resetToHome(): boolean {
    if (!this.graph || !this.layoutHome.size) return false;
    const graph = this.graph;

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

    return true;
  }

  writeLayoutToGraph(): void {
    const graph = this.graph;
    const layout = this.layout;
    if (!graph || !layout) return;

    for (const node of layout.nodes()) {
      if (!graph.hasNode(node.id)) continue;
      graph.setNodeAttribute(node.id, 'x', Number(node.x) || 0);
      graph.setNodeAttribute(node.id, 'y', Number(node.y) || 0);
      const size = Number(node.size);
      if (Number.isFinite(size) && size > 0) {
        graph.setNodeAttribute(node.id, 'size', size);
      }
    }
  }

  heatLayout(alphaTarget: number): void {
    const layout = this.layout;
    if (!layout) return;
    layout.alphaTarget(alphaTarget);
    if (layout.alpha() < alphaTarget) layout.alpha(alphaTarget);
    this.startLayoutLoop();
  }

  coolLayout(): void {
    this.layout?.alphaTarget(0);
    this.startLayoutLoop();
  }

  startLayoutLoop(): void {
    if (this.layoutFrame !== null || !this.layout) return;

    const step = (): void => {
      this.layoutFrame = null;
      const layout = this.layout;
      if (!layout || this.disposed) return;

      layout.tick();
      this.writeLayoutToGraph();
      this.onRefresh?.();

      if (layout.alpha() <= OBSIDIAN_FORCES.alphaMin) return;
      this.layoutFrame = window.requestAnimationFrame(step);
    };

    this.layoutFrame = window.requestAnimationFrame(step);
  }

  stopLayoutLoop(): void {
    if (this.layoutFrame !== null) {
      window.cancelAnimationFrame(this.layoutFrame);
      this.layoutFrame = null;
    }
  }

  pinNodeForDrag(node: string, x?: number, y?: number): void {
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

  releasePinnedNode(): void {
    for (const entry of this.layout?.nodes() ?? []) {
      entry.fx = null;
      entry.fy = null;
    }
  }

  /** Stop the simulation, cancel frames/timers, and detach window listeners. */
  dispose(): void {
    this.disposed = true;
    this.stopLayoutLoop();
    if (this.dragResetTimeout !== null) {
      window.clearTimeout(this.dragResetTimeout);
      this.dragResetTimeout = null;
    }
    this.layout?.stop();
    this.releasePinnedNode();
    this.layout = null;
    this.graph = null;
    this.onRefresh = null;
    this.draggedNode = null;
    this.dragging = false;
    this.dragStart = null;
    this.draggingActive = false;
    this.detachDragSafetyNet?.();
    this.detachDragSafetyNet = null;
    this.layoutHome.clear();
  }
}
