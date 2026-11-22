import Graph from 'graphology';

vi.mock('sigma', () => {
  const sigmaHandlers: Record<string, (payload?: unknown) => void> = {};
  const lastSettings: Record<string, unknown> = {};
  const layers: Record<string, { element: HTMLCanvasElement; options: Record<string, unknown> }> = {};
  const camera = {
    x: 0.5,
    y: 0.5,
    angle: 0,
    ratio: 1,
    getState: vi.fn(() => ({ x: camera.x, y: camera.y, angle: camera.angle, ratio: camera.ratio })),
  };

  class MockSigma {
    settings: Record<string, unknown>;
    styles: unknown;
    nodeReducer: unknown;
    edgeReducer: unknown;
    refresh = vi.fn();
    kill = vi.fn();

    constructor(
      _graph: unknown,
      _container: unknown,
      options: {
        settings?: Record<string, unknown>;
        styles?: unknown;
        nodeReducer?: unknown;
        edgeReducer?: unknown;
      } = {}
    ) {
      Object.assign(lastSettings, options.settings ?? {});
      this.settings = lastSettings;
      this.styles = options.styles;
      this.nodeReducer = options.nodeReducer;
      this.edgeReducer = options.edgeReducer;
    }
    on(event: string, handler: (payload?: unknown) => void) {
      sigmaHandlers[event] = handler;
      return this;
    }
    getSetting(key: string) {
      return lastSettings[key];
    }
    createCanvas(id: string, options: Record<string, unknown> = {}) {
      const element = document.createElement('canvas');
      const ctx = {
        font: '',
        fillStyle: '',
        strokeStyle: '',
        lineJoin: '',
        lineWidth: 1,
        textAlign: 'left',
        shadowOffsetX: 0,
        shadowOffsetY: 0,
        shadowBlur: 0,
        shadowColor: '',
        canvas: element,
        setTransform: vi.fn(),
        clearRect: vi.fn(),
        save: vi.fn(),
        restore: vi.fn(),
        measureText: (text: string) => ({ width: text.length * 7 }),
        strokeText: vi.fn(),
        fillText: vi.fn(),
        beginPath: vi.fn(),
        moveTo: vi.fn(),
        lineTo: vi.fn(),
        arc: vi.fn(),
        closePath: vi.fn(),
        fill: vi.fn(),
      };
      element.getContext = (() => ctx) as unknown as typeof element.getContext;
      layers[id] = { element, options };
      return element;
    }
    getCamera() {
      return camera;
    }
    getDimensions() {
      return { width: 800, height: 600 };
    }
    scaleSize(size: number) {
      return size;
    }
    graphToViewport(point: { x: number; y: number }) {
      return { x: 400 + point.x, y: 300 - point.y };
    }
    getNodeDisplayData(key: string) {
      return {
        label: key,
        size: 8,
        color: '#2f6e4e',
        visibility: 'visible',
        highlighted: key === 'a',
      };
    }
  }

  (globalThis as unknown as { __rendererSigmaHandlers: unknown }).__rendererSigmaHandlers = sigmaHandlers;
  (globalThis as unknown as { __rendererLayers: unknown }).__rendererLayers = layers;
  (globalThis as unknown as { __rendererSettings: unknown }).__rendererSettings = lastSettings;

  const DEFAULT_STYLES = {
    nodes: { backdropVisibility: 'visible', backdropColor: '#ffffff' },
    edges: {},
  };

  return { default: MockSigma, DEFAULT_STYLES };
});

import { LABEL_PRIORITY, NODE_SIZE_MAX, NODE_SIZE_MIN } from './topic-map.helpers';
import { TopicMapRenderer, type TopicMapRendererCallbacks } from './topic-map-renderer';

function createCallbacks(
  overrides: Partial<TopicMapRendererCallbacks> = {}
): TopicMapRendererCallbacks {
  return {
    getHoveredId: () => null,
    getSelectedId: () => null,
    isDragging: () => false,
    hasDraggedNode: () => false,
    onHoverChange: vi.fn(),
    onSelectNode: vi.fn(),
    onOpenTopic: vi.fn(),
    onClearSelection: vi.fn(),
    ...overrides,
  };
}

describe('TopicMapRenderer', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('builds a Graphology graph with scaled sizes, hub metadata, and edges while skipping duplicates', () => {
    const renderer = new TopicMapRenderer();
    const { graph } = renderer.buildGraph(
      [
        { id: 'a', name: 'Alpha', usageCount: 20 },
        { id: 'b', name: 'Beta', usageCount: 10 },
        { id: 'c', name: 'Gamma', usageCount: 1 },
        { id: 'd', name: 'Delta', usageCount: 1 },
      ],
      [
        { sourceId: 'a', targetId: 'b', sharedNotes: 4 },
        { sourceId: 'b', targetId: 'c', sharedNotes: 2 },
        { sourceId: 'a', targetId: 'c', sharedNotes: 1 },
        { sourceId: 'a', targetId: 'b', sharedNotes: 4 }, // duplicate ignored cleanly
      ]
    );

    expect(graph.order).toBe(4);
    expect(graph.size).toBe(3);
    expect(graph.getNodeAttributes('a')['size']).toBe(NODE_SIZE_MAX);
    expect(graph.getNodeAttributes('c')['size']).toBe(NODE_SIZE_MIN);
    expect(graph.getNodeAttributes('a')['forceLabel']).toBe(true);
    expect(graph.getNodeAttributes('a')['labelPriority']).toBe(LABEL_PRIORITY.hub);
  });

  it('mounts Sigma with WebGL reducers, computes focus emphasis, and paints custom 2D labels', () => {
    const renderer = new TopicMapRenderer();
    const graph = new Graph();
    graph.addNode('a', { x: 0, y: 0, size: 10, label: 'Alpha', labelColor: '#111', labelWeight: '600', forceLabel: true });
    graph.addNode('b', { x: 80, y: 0, size: 6, label: 'Beta', labelColor: '#222', labelWeight: '400', forceLabel: false });
    graph.addNode('d', { x: 240, y: 0, size: 5, label: 'Delta', labelColor: '#333', labelWeight: '400', forceLabel: false });
    const edgeAB = graph.addEdge('a', 'b', { size: 2 });
    const edgeBD = graph.addEdge('b', 'd', { size: 1 });

    let hoveredId: string | null = null;
    let selectedId: string | null = 'a';
    const callbacks = createCallbacks({
      getHoveredId: () => hoveredId,
      getSelectedId: () => selectedId,
    });

    const container = document.createElement('div');
    const sigma = renderer.mount(graph, container, callbacks);

    expect(renderer.emphasisOf('a')).toBe('active');
    expect(renderer.emphasisOf('b')).toBe('neighbour');
    expect(renderer.emphasisOf('d')).toBe('dimmed');

    const activeStyle = renderer.labelStyleOf(graph.getNodeAttributes('a'), 'active');
    expect(activeStyle.forced).toBe(true);
    expect(activeStyle.priority).toBe(LABEL_PRIORITY.active);

    const dimmedStyle = renderer.labelStyleOf(graph.getNodeAttributes('d'), 'dimmed');
    expect(dimmedStyle.priority).toBe(LABEL_PRIORITY.dimmed);

    // Reducers enforce active / neighbour / dimmed visual emphasis.
    const nodeReducer = (sigma as unknown as {
      nodeReducer: (node: string, data: Record<string, unknown>, attrs: Record<string, unknown>) => Record<string, unknown>;
    }).nodeReducer;
    expect(nodeReducer('a', {}, graph.getNodeAttributes('a'))['highlighted']).toBe(true);
    expect(nodeReducer('d', {}, graph.getNodeAttributes('d'))['zIndex']).toBe(0);

    const edgeReducer = (sigma as unknown as {
      edgeReducer: (edge: string, data: Record<string, unknown>) => Record<string, unknown>;
    }).edgeReducer;
    expect(edgeReducer(edgeAB, { size: 2 })['zIndex']).toBe(1);
    expect(edgeReducer(edgeBD, { size: 1 })['zIndex']).toBe(0);

    // Custom 2D label layer draws without error.
    hoveredId = 'b';
    const layers = (globalThis as unknown as {
      __rendererLayers: Record<string, { element: HTMLCanvasElement; options: Record<string, unknown> }>;
    }).__rendererLayers;
    expect(layers['nostosLabels'].options['beforeLayer']).toBe('mouse');
    const ctx = layers['nostosLabels'].element.getContext('2d')!;
    renderer.drawLabels();
    expect(ctx.clearRect).toHaveBeenCalledWith(0, 0, 800, 600);
    expect(ctx.fillText).toHaveBeenCalled();

    selectedId = null;
    hoveredId = null;
    expect(renderer.emphasisOf('a')).toBeNull();

    renderer.dispose();
  });

  it('routes non-drag Sigma events and resolves coarse-pointer nearestNodeForTouch hits', () => {
    const renderer = new TopicMapRenderer();
    const graph = new Graph();
    graph.addNode('a', { x: 0, y: 0, size: 8 }); // viewport (400, 300)
    const callbacks = createCallbacks();
    const container = document.createElement('div');
    renderer.mount(graph, container, callbacks);

    const handlers = (globalThis as unknown as {
      __rendererSigmaHandlers: Record<string, (payload?: unknown) => void>;
    }).__rendererSigmaHandlers;

    handlers['enterNode']({ node: 'a' });
    expect(callbacks.onHoverChange).toHaveBeenCalledWith('a');
    expect(container.style.cursor).toBe('grab');

    handlers['leaveNode']();
    expect(callbacks.onHoverChange).toHaveBeenCalledWith(null);
    expect(container.style.cursor).toBe('default');

    handlers['clickNode']({ node: 'a' });
    expect(callbacks.onSelectNode).toHaveBeenCalledWith('a');

    const preventSigmaDefault = vi.fn();
    handlers['doubleClickNode']({ node: 'a', preventSigmaDefault });
    expect(preventSigmaDefault).toHaveBeenCalled();
    expect(callbacks.onOpenTopic).toHaveBeenCalledWith('a');

    // Fine pointer click on stage clears selection.
    vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false })));
    handlers['clickStage']({ event: { x: 405, y: 300 } });
    expect(callbacks.onClearSelection).toHaveBeenCalledTimes(1);

    // Coarse pointer tap near node selects it instead of clearing.
    vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: true })));
    expect(renderer.nearestNodeForTouch({ x: 410, y: 300 })).toBe('a');
    handlers['clickStage']({ event: { x: 410, y: 300 } });
    expect(callbacks.onSelectNode).toHaveBeenCalledTimes(2);

    vi.unstubAllGlobals();
    renderer.dispose();
  });

  it('kills Sigma and clears references on dispose', () => {
    const renderer = new TopicMapRenderer();
    const graph = new Graph();
    graph.addNode('a', { x: 0, y: 0, size: 8 });
    const sigma = renderer.mount(graph, document.createElement('div'), createCallbacks());

    expect(renderer.getCameraState()).toEqual({ x: 0.5, y: 0.5, angle: 0, ratio: 1 });
    renderer.dispose();

    expect((sigma as unknown as { kill: ReturnType<typeof vi.fn> }).kill).toHaveBeenCalledTimes(1);
    expect(renderer.sigmaInstance).toBeNull();
    expect(renderer.graphInstance).toBeNull();
    expect(renderer.getCameraState()).toBeNull();
    expect(() => renderer.refresh()).not.toThrow();
  });

  it('colours nodes by topic kind in kind mode, neutral when untyped, and neutral in none mode', () => {
    document.documentElement.style.setProperty('--map-kind-1', '#3b73a8');
    document.documentElement.style.setProperty('--map-kind-2', '#b85c38');
    document.documentElement.style.setProperty('--map-kind-none', '#807972');
    document.documentElement.style.setProperty('--graph-edge', '#d4d0c8');

    const renderer = new TopicMapRenderer();
    const kinds = [
      { id: 'k1', name: 'Books', colorKey: 'kind-1', sortOrder: 1, createdAt: '', topicCount: 2 },
      { id: 'k2', name: 'Concepts', colorKey: 'kind-2', sortOrder: 2, createdAt: '', topicCount: 1 },
    ];
    const nodes = [
      { id: 'a', name: 'A', usageCount: 10, kindId: 'k1' },
      { id: 'b', name: 'B', usageCount: 8, kindId: 'k1' },
      { id: 'c', name: 'C', usageCount: 5, kindId: 'k2' },
      { id: 'd', name: 'D', usageCount: 2, kindId: null },
    ];
    const edges = [
      { sourceId: 'a', targetId: 'b', sharedNotes: 3 },
      { sourceId: 'b', targetId: 'c', sharedNotes: 3 },
    ];

    const kindBuilt = renderer.buildGraph(nodes, edges, { colorMode: 'kind', topicKinds: kinds });
    expect(kindBuilt.graph.getNodeAttributes('a')['color']).toBe('#3b73a8');
    expect(kindBuilt.graph.getNodeAttributes('c')['color']).toBe('#b85c38');
    expect(kindBuilt.graph.getNodeAttributes('d')['color']).toBe('#807972');
    expect(kindBuilt.hueById.get('a')).toBe('#3b73a8');
    expect(kindBuilt.hueById.get('c')).toBe('#b85c38');
    expect(kindBuilt.hueById.get('d')).toBeNull();

    // Same-kind edge a->b is tinted toward k1; cross-kind edge b->c stays neutral.
    const edgeAB = kindBuilt.graph.getEdgeAttributes('a', 'b')['color'];
    const edgeBC = kindBuilt.graph.getEdgeAttributes('b', 'c')['color'];
    expect(edgeAB).not.toBe(edgeBC);

    // Cluster mode keeps the community hues; the untyped node stays neutral.
    const clusterBuilt = renderer.buildGraph(nodes, edges, { colorMode: 'cluster', topicKinds: kinds });
    expect(clusterBuilt.hueById.get('a')).not.toBe('#3b73a8');

    // None mode paints the neutral node hierarchy (no kind or cluster hues).
    const noneBuilt = renderer.buildGraph(nodes, edges, { colorMode: 'none', topicKinds: kinds });
    expect(noneBuilt.hueById.get('a')).toBeNull();
    expect(noneBuilt.hueById.get('c')).toBeNull();
    expect(noneBuilt.graph.getNodeAttributes('a')['color']).not.toBe('#3b73a8');
    expect(noneBuilt.graph.getNodeAttributes('c')['color']).not.toBe('#b85c38');

    document.documentElement.style.removeProperty('--map-kind-1');
    document.documentElement.style.removeProperty('--map-kind-2');
    document.documentElement.style.removeProperty('--map-kind-none');
    document.documentElement.style.removeProperty('--graph-edge');
  });
});
