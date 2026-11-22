import Graph from 'graphology';
import type Sigma from 'sigma';

import { OBSIDIAN_FORCES } from './topic-map.helpers';
import { TopicMapSimulation } from './topic-map-simulation';

function createDragSigma() {
  const camera = { enabled: true };
  const sigmaHandlers: Record<string, (payload: unknown) => void> = {};
  const mouseHandlers: Record<string, (payload: unknown) => void> = {};
  const touchHandlers: Record<string, (payload: unknown) => void> = {};

  const mouseCaptor = {
    on: vi.fn((event: string, handler: (payload: unknown) => void) => {
      mouseHandlers[event] = handler;
      return mouseCaptor;
    }),
  };
  const touchCaptor = {
    on: vi.fn((event: string, handler: (payload: unknown) => void) => {
      touchHandlers[event] = handler;
      return touchCaptor;
    }),
  };

  const sigma = {
    on: vi.fn((event: string, handler: (payload: unknown) => void) => {
      sigmaHandlers[event] = handler;
      return sigma;
    }),
    getCamera: () => camera,
    getMouseCaptor: () => mouseCaptor,
    getTouchCaptor: () => touchCaptor,
    viewportToGraph: vi.fn((coords: { x: number; y: number }) => coords),
  };

  return {
    sigma: sigma as unknown as Sigma,
    camera,
    sigmaHandlers,
    mouseHandlers,
    touchHandlers,
  };
}

function buildSampleGraph(): {
  graph: Graph;
  nodes: Array<{ id: string; name: string; usageCount: number }>;
  edges: Array<{ sourceId: string; targetId: string; sharedNotes: number }>;
} {
  const graph = new Graph();
  const nodes = [
    { id: 'a', name: 'Alpha', usageCount: 10 },
    { id: 'b', name: 'Beta', usageCount: 6 },
    { id: 'c', name: 'Gamma', usageCount: 4 },
    { id: 'iso', name: 'Isolated', usageCount: 1 },
  ];
  const edges = [
    { sourceId: 'a', targetId: 'b', sharedNotes: 2 },
    { sourceId: 'b', targetId: 'c', sharedNotes: 1 },
  ];

  graph.addNode('a', { x: 10, y: 0, size: 12 });
  graph.addNode('b', { x: -10, y: 5, size: 8 });
  graph.addNode('c', { x: 0, y: -10, size: 6 });
  graph.addNode('iso', { x: 500, y: 500, size: 4 });
  graph.addEdge('a', 'b', { size: 1 });
  graph.addEdge('b', 'c', { size: 1 });

  return { graph, nodes, edges };
}

describe('TopicMapSimulation', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('settles connected nodes synchronously, shelves isolates, and snapshots home positions', () => {
    const sim = new TopicMapSimulation();
    const { graph, nodes, edges } = buildSampleGraph();

    sim.initLayout({
      graph,
      visibleNodes: nodes,
      visibleEdges: edges,
      stageWidth: 1000,
      stageHeight: 600,
    });

    expect(sim.simulation).toBeTruthy();
    expect(sim.simulation!.nodes().map((n) => n.id).sort()).toEqual(['a', 'b', 'c']);
    expect(sim.simulation!.alpha()).toBeLessThanOrEqual(OBSIDIAN_FORCES.alphaMin);

    // Isolate is placed cleanly below the connected extent, not left at (500, 500).
    const connectedYs = ['a', 'b', 'c'].map((id) => Number(graph.getNodeAttributes(id)['y']));
    const isoY = Number(graph.getNodeAttributes('iso')['y']);
    expect(isoY).toBeLessThan(Math.min(...connectedYs));

    sim.dispose();
  });

  it('restores settled home positions and clears lingering pins on resetToHome', () => {
    const sim = new TopicMapSimulation();
    expect(sim.resetToHome()).toBe(false);

    const { graph, nodes, edges } = buildSampleGraph();
    sim.initLayout({
      graph,
      visibleNodes: nodes,
      visibleEdges: edges,
      stageWidth: 800,
      stageHeight: 600,
    });

    const settledX = Number(graph.getNodeAttributes('a')['x']);
    graph.setNodeAttribute('a', 'x', 9999);
    const simNode = sim.simulation!.nodes().find((n) => n.id === 'a')!;
    simNode.fx = 9999;
    simNode.fy = 8888;
    simNode.vx = 5;
    simNode.vy = -5;

    expect(sim.resetToHome()).toBe(true);
    expect(graph.getNodeAttributes('a')['x']).toBe(settledX);
    expect(simNode.fx).toBeNull();
    expect(simNode.fy).toBeNull();
    expect(simNode.vx).toBe(0);
    expect(simNode.vy).toBe(0);

    sim.dispose();
  });

  it('pins and unpins nodes on mouse and touch drag, cools layout, and removes safety net on dispose', () => {
    vi.useFakeTimers();
    const sim = new TopicMapSimulation();
    const { graph, nodes, edges } = buildSampleGraph();
    sim.initLayout({
      graph,
      visibleNodes: nodes,
      visibleEdges: edges,
      stageWidth: 800,
      stageHeight: 600,
    });

    const { sigma, camera, sigmaHandlers, mouseHandlers, touchHandlers } = createDragSigma();
    const container = document.createElement('div');
    const onRefresh = vi.fn();

    vi.spyOn(window, 'requestAnimationFrame').mockImplementation(() => 1);
    const cancelSpy = vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
    const removeListenerSpy = vi.spyOn(window, 'removeEventListener');

    sim.attachDrag({ sigma, graph, container, onRefresh });

    // Sub-threshold movement keeps isDragging false.
    sigmaHandlers['downNode']({ node: 'a', event: { x: 0, y: 0 } });
    expect(camera.enabled).toBe(false);
    expect(container.classList.contains('dragging')).toBe(true);
    mouseHandlers['mousemovebody']({ x: 1, y: 1, preventSigmaDefault: vi.fn() });
    expect(sim.isDragging).toBe(false);

    // Moving past threshold sets isDragging, updates graph coordinates, and heats layout.
    mouseHandlers['mousemovebody']({ x: 40, y: 30, preventSigmaDefault: vi.fn() });
    expect(sim.isDragging).toBe(true);
    expect(graph.getNodeAttributes('a')['x']).toBe(40);
    expect(graph.getNodeAttributes('a')['y']).toBe(30);
    expect(sim.simulation!.alphaTarget()).toBeGreaterThan(0);

    // Releasing cools the layout and schedules the synchronous-click guard reset timeout.
    mouseHandlers['mouseup'](undefined);
    expect(camera.enabled).toBe(true);
    expect(container.classList.contains('dragging')).toBe(false);
    expect(sim.isDragging).toBe(true);
    expect(sim.simulation!.alphaTarget()).toBe(0);

    // Advancing fake timer resets isDragging.
    vi.runAllTimers();
    expect(sim.isDragging).toBe(false);

    // Touch drag moves the node and ends cleanly via touchup.
    sigmaHandlers['downNode']({ node: 'b', event: { x: 0, y: 0 } });
    touchHandlers['touchmove']({
      touches: [{ x: 75, y: 80 }],
      previousTouches: [],
      preventSigmaDefault: vi.fn(),
    });
    expect(graph.getNodeAttributes('b')['x']).toBe(75);
    touchHandlers['touchup']({});
    expect(camera.enabled).toBe(true);

    // Disposing cancels any pending dragResetTimeout before it runs, cancels RAF, and detaches window listeners.
    const spyClearTimeout = vi.spyOn(window, 'clearTimeout');
    sigmaHandlers['downNode']({ node: 'a', event: { x: 0, y: 0 } });
    mouseHandlers['mousemovebody']({ x: 50, y: 50, preventSigmaDefault: vi.fn() });
    mouseHandlers['mouseup'](undefined);

    sim.dispose();
    expect(spyClearTimeout).toHaveBeenCalled();
    expect(cancelSpy).toHaveBeenCalled();
    expect(removeListenerSpy).toHaveBeenCalledWith('pointerup', expect.any(Function));
    expect(removeListenerSpy).toHaveBeenCalledWith('pointercancel', expect.any(Function));
    expect(sim.simulation).toBeNull();
    expect(sim.isDragging).toBe(false);
  });

  it('stops the layout RAF loop once alpha cools to alphaMin', () => {
    const sim = new TopicMapSimulation();
    const { graph, nodes, edges } = buildSampleGraph();
    sim.initLayout({
      graph,
      visibleNodes: nodes,
      visibleEdges: edges,
      stageWidth: 800,
      stageHeight: 600,
    });
    const { sigma } = createDragSigma();
    const container = document.createElement('div');
    const onRefresh = vi.fn();

    const rafQueue: FrameRequestCallback[] = [];
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => {
      rafQueue.push(cb);
      return rafQueue.length;
    });

    sim.attachDrag({ sigma, graph, container, onRefresh });
    sim.heatLayout(0.3);
    expect(rafQueue.length).toBe(1);

    sim.coolLayout();
    while (rafQueue.length > 0 && onRefresh.mock.calls.length < 400) {
      const next = rafQueue.shift()!;
      next(0);
    }

    expect(rafQueue.length).toBe(0);
    expect(sim.simulation!.alpha()).toBeLessThanOrEqual(OBSIDIAN_FORCES.alphaMin);
    expect(onRefresh).toHaveBeenCalled();

    sim.dispose();
  });

  it('pins the local-map root at (0, 0), adds forceRadial, and blocks dragging the root', () => {
    const sim = new TopicMapSimulation();
    const { graph, nodes, edges } = buildSampleGraph();
    const hops = new Map([
      ['a', 0],
      ['b', 1],
      ['c', 2],
    ]);

    sim.initLayout({
      graph,
      visibleNodes: nodes.slice(0, 3),
      visibleEdges: edges,
      stageWidth: 1000,
      stageHeight: 700,
      rootId: 'a',
      hops,
    });

    const rootNode = sim.simulation!.nodes().find((n) => n.id === 'a')!;
    expect(rootNode.fx).toBe(0);
    expect(rootNode.fy).toBe(0);
    expect(rootNode.hop).toBe(0);
    expect(graph.getNodeAttributes('a')['x']).toBe(0);
    expect(graph.getNodeAttributes('a')['y']).toBe(0);
    expect(sim.simulation!.force('radial')).toBeTruthy();

    const { sigma, sigmaHandlers, mouseHandlers } = createDragSigma();
    const container = document.createElement('div');
    sim.attachDrag({ sigma, graph, container, onRefresh: vi.fn() });

    sigmaHandlers['downNode']({ node: 'a', event: { x: 0, y: 0 } });
    mouseHandlers['mousemovebody']?.({ x: 120, y: 90, preventSigmaDefault: vi.fn() });
    expect(graph.getNodeAttributes('a')['x']).toBe(0);
    expect(graph.getNodeAttributes('a')['y']).toBe(0);
    expect(rootNode.fx).toBe(0);
    expect(rootNode.fy).toBe(0);

    sim.resetToHome();
    expect(rootNode.fx).toBe(0);
    expect(rootNode.fy).toBe(0);

    sim.dispose();
  });

  it('keeps an isolated local-map root at (0, 0) instead of moving it to a shelf', () => {
    const sim = new TopicMapSimulation();
    const graph = new Graph();
    graph.addNode('root', { x: 40, y: -30, size: 10 });

    sim.initLayout({
      graph,
      visibleNodes: [{ id: 'root', name: 'Root', usageCount: 4 }],
      visibleEdges: [],
      stageWidth: 800,
      stageHeight: 600,
      rootId: 'root',
      hops: new Map([['root', 0]]),
    });

    expect(graph.getNodeAttributes('root')['x']).toBe(0);
    expect(graph.getNodeAttributes('root')['y']).toBe(0);
    expect(sim.simulation!.nodes().map((n) => n.id)).toEqual(['root']);

    sim.dispose();
  });

  it('skips axis alignment rotation when keepPositions is true so surviving nodes keep orientation', () => {
    const sim = new TopicMapSimulation();
    const graph = new Graph();
    graph.addNode('a', { x: 0, y: 0, size: 12 });
    graph.addNode('b', { x: 250, y: 5, size: 8 });
    graph.addEdge('a', 'b', { size: 1 });

    // Alpha decay ticks will nudge b slightly, but keepPositions prevents a 90-degree alignment flip on portrait stage.
    sim.initLayout({
      graph,
      visibleNodes: [
        { id: 'a', name: 'Alpha', usageCount: 10 },
        { id: 'b', name: 'Beta', usageCount: 5 },
      ],
      visibleEdges: [{ sourceId: 'a', targetId: 'b', sharedNotes: 1 }],
      stageWidth: 390,
      stageHeight: 720,
      rootId: 'a',
      hops: new Map([['a', 0], ['b', 1]]),
      keepPositions: true,
    });

    const bx = Number(graph.getNodeAttributes('b')['x']);
    const by = Number(graph.getNodeAttributes('b')['y']);
    expect(bx).toBeGreaterThan(0);
    expect(Math.abs(bx)).toBeGreaterThan(Math.abs(by));

    sim.dispose();
  });
});
