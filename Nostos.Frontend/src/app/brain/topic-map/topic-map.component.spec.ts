import { readFileSync } from 'node:fs';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

// Mock Sigma and graphology before importing the component.
// Sigma requires WebGL2 which is not available in jsdom/Node.
vi.mock('sigma', () => {
  // A single shared camera instance so tests can assert on what the component
  // animated the camera to. A fresh object per getCamera() call would make
  // every assertion vacuous.
  const camera = {
    x: 0.5,
    y: 0.5,
    angle: 0,
    ratio: 1,
    /** Sigma 4's camera is held by this flag; there is no enable()/disable(). */
    enabled: true,
    zoomIn: vi.fn(() => Promise.resolve()),
    zoomOut: vi.fn(() => Promise.resolve()),
    animate: vi.fn(() => Promise.resolve()),
    isAnimating: vi.fn(() => false),
    setState: vi.fn(),
    getState: vi.fn(() => ({ x: camera.x, y: camera.y, angle: 0, ratio: camera.ratio })),
  };

  /**
   * Captured captor handlers, keyed by event name.
   *
   * The drag path is driven by `getMouseCaptor().on('mousemovebody'|'mouseup')`,
   * so a mock that only returns a `vi.fn()` makes the drag untestable: there is
   * no way to fire the handler the component registered. Recording them here
   * lets the specs invoke the real callbacks.
   */
  const captorHandlers: Record<string, (payload?: unknown) => void> = {};
  const captor = {
    on: vi.fn((event: string, handler: (payload?: unknown) => void) => {
      captorHandlers[event] = handler;
      return captor;
    }),
  };

  /**
   * Touch captor handlers, kept separate from the mouse ones.
   *
   * Sigma routes mouse and touch through DIFFERENT captors, and only the mouse
   * one emits `mouseup` — touch ends as `touchup`. A mock that returns the same
   * object for both would hide exactly the bug this exists to catch (a touch
   * gesture never reaching the teardown, leaving the camera disabled).
   */
  const touchHandlers: Record<string, (payload?: unknown) => void> = {};
  const touchCaptor = {
    on: vi.fn((event: string, handler: (payload?: unknown) => void) => {
      touchHandlers[event] = handler;
      return touchCaptor;
    }),
  };

  /** Sigma `on(...)` handlers (downNode, enterNode, clickNode, ...). */
  const sigmaHandlers: Record<string, (payload?: unknown) => void> = {};

  // Record the settings Sigma was constructed with, so tests can assert on the
  // configuration the component actually ships rather than re-declaring it.
  const lastSettings: Record<string, unknown> = {};

  /** Layers the component added with `createCanvas`, keyed by id. */
  const layers: Record<string, { element: HTMLCanvasElement; options: Record<string, unknown> }> = {};

  /**
   * Sigma 4's constructor takes `{ settings, styles, nodeReducer, edgeReducer }`;
   * the reducers are options, not settings.
   */
  class MockSigma {
    settings: Record<string, unknown>;
    styles: unknown;
    nodeReducer: unknown;
    edgeReducer: unknown;
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
    setSetting(key: string, value: unknown) {
      lastSettings[key] = value;
      return this;
    }
    getSetting(key: string) {
      return lastSettings[key];
    }
    createCanvas(id: string, options: Record<string, unknown> = {}) {
      const element = document.createElement('canvas');
      layers[id] = { element, options };
      return element;
    }
    scaleSize(size: number) {
      return size;
    }
    refresh() {}
    kill() {}
    getCamera() {
      return camera;
    }
    getMouseCaptor() {
      return captor;
    }
    getTouchCaptor() {
      return touchCaptor;
    }
    getDimensions() {
      return { width: 800, height: 600 };
    }
    viewportToGraph(coords: { x: number; y: number }) {
      return coords;
    }
    // Identity-ish conversions so the fit/centre maths has a coherent mapping to
    // work against. The real component probes these; a mock that omits them
    // makes every camera assertion vacuous.
    graphToViewport(point: { x: number; y: number }, _override?: unknown) {
      return { x: 400 + point.x, y: 300 - point.y };
    }
    viewportToFramedGraph(point: { x: number; y: number }, _override?: unknown) {
      return { x: (point.x - 400) / 800, y: (300 - point.y) / 600 };
    }
  }
  // Publish handles on globalThis so the specs can assert on the camera and the
  // settings the component actually shipped, without importing the mock.
  (globalThis as unknown as { __camera: unknown }).__camera = camera;
  (globalThis as unknown as { __settings: unknown }).__settings = lastSettings;
  (globalThis as unknown as { __layers: unknown }).__layers = layers;
  (globalThis as unknown as { __captor: unknown }).__captor = captorHandlers;
  (globalThis as unknown as { __sigmaHandlers: unknown }).__sigmaHandlers = sigmaHandlers;
  (globalThis as unknown as { __touchCaptor: unknown }).__touchCaptor = touchHandlers;

  // Enough of Sigma's default styles for the component to layer onto: the
  // default hover backdrop is visible (and white), which the component turns off.
  const DEFAULT_STYLES = {
    nodes: { backdropVisibility: 'visible', backdropColor: '#ffffff' },
    edges: {},
  };

  return { default: MockSigma, DEFAULT_STYLES, __camera: camera, __settings: lastSettings };
});

vi.mock('graphology', () => {
  class MockGraph {
    private nodes = new Map<string, Record<string, unknown>>();
    private edges: Array<{ key: string; source: string; target: string; attrs: Record<string, unknown> }> = [];

    addNode(key: string, attrs: Record<string, unknown> = {}) {
      this.nodes.set(key, attrs);
    }
    addEdge(source: string, target: string, attrs: Record<string, unknown> = {}) {
      const key = `${source}->${target}`;
      this.edges.push({ key, source, target, attrs });
      return key;
    }
    get order() { return this.nodes.size; }
    get size() { return this.edges.length; }
    hasNode(node: string) {
      return this.nodes.has(node);
    }
    getNodeAttributes(node: string) {
      return this.nodes.get(node) ?? {};
    }
    forEachNode(callback: (node: string, attrs: Record<string, unknown>) => void) {
      for (const [node, attrs] of this.nodes) callback(node, attrs);
    }
    setNodeAttribute(node: string, attribute: string, value: unknown) {
      this.nodes.get(node)![attribute] = value;
    }
    setEdgeAttribute(edge: string, attribute: string, value: unknown) {
      const found = this.edges.find((e) => e.key === edge);
      if (found) found.attrs[attribute] = value;
    }
    /**
     * Added for the drag-physics path. Sigma's own graph has this; the hand-rolled
     * mock lagged behind production and would have thrown at drag-end.
     */
    removeNodeAttribute(node: string, attribute: string) {
      delete this.nodes.get(node)?.[attribute];
    }
    hasNodeAttribute(node: string, attribute: string) {
      return attribute in (this.nodes.get(node) ?? {});
    }
    forEachEdge(callback: (edge: string, attrs: Record<string, unknown>, source: string, target: string) => void) {
      for (const e of this.edges) {
        callback(e.key, e.attrs, e.source, e.target);
      }
    }
    areNeighbors(node: string, neighbor: string) {
      return this.edges.some(
        (e) => (e.source === node && e.target === neighbor) || (e.target === node && e.source === neighbor)
      );
    }
    forEachNeighbor(node: string, callback: (neighbor: string) => void) {
      for (const e of this.edges) {
        if (e.source === node) callback(e.target);
        else if (e.target === node) callback(e.source);
      }
    }
    source(edge: string) {
      return this.edges.find(e => e.key === edge)?.source ?? '';
    }
    target(edge: string) {
      return this.edges.find(e => e.key === edge)?.target ?? '';
    }
  }
  return { default: MockGraph };
});

/**
 * d3-force mock.
 *
 * The layout is a real d3 simulation in production, so the specs need enough of
 * the API for the component to construct it and for tests to drive `alpha`,
 * `nodes()` and the per-tick integration. A plain `vi.fn()` stub would make the
 * physics untestable, which is exactly the property being pinned here.
 *
 * `tick()` advances `alpha` toward `alphaTarget` exactly as d3 does
 * (`alpha += (alphaTarget - alpha) * alphaDecay`), so a spec can assert that
 * alpha DECAYS after a release and that the frame loop then stops — the two
 * behaviours that were missing before this change.
 */
vi.mock('d3-force', () => {
  const ALPHA_DECAY = 1 - Math.pow(0.001, 1 / 300);

  class MockSimulation {
    private nodesArray: Array<Record<string, unknown>> = [];
    private forces = new Map<string, unknown>();
    private alphaValue = 0.3;
    private alphaTargetValue = 0;
    private timer: ReturnType<typeof setInterval> | null = null;

    constructor(nodes: Array<Record<string, unknown>> = []) {
      this.nodesArray = nodes;
    }
    nodes(): Array<Record<string, unknown>> {
      return this.nodesArray;
    }
    force(name: string, force?: unknown) {
      if (force === undefined) return this.forces.get(name);
      this.forces.set(name, force);
      return this;
    }
    alpha(value?: number) {
      if (value === undefined) return this.alphaValue;
      this.alphaValue = value;
      return this;
    }
    alphaTarget(value?: number) {
      if (value === undefined) return this.alphaTargetValue;
      this.alphaTargetValue = value;
      return this;
    }
    velocityDecay() {
      return this;
    }
    alphaDecay(value?: number) {
      return value === undefined ? ALPHA_DECAY : this;
    }
    alphaMin() {
      return 0.001;
    }
    tick(iterations = 1) {
      for (let i = 0; i < iterations; i += 1) {
        this.alphaValue += (this.alphaTargetValue - this.alphaValue) * ALPHA_DECAY;
      }
      return this;
    }
    stop() {
      if (this.timer) clearInterval(this.timer);
      this.timer = null;
      return this;
    }
    restart() {
      return this;
    }
    on() {
      return this;
    }
    find() {
      return undefined;
    }
  }

  const chainable = () => {
    const self: Record<string, unknown> = {};
    for (const m of ['strength', 'distance', 'id', 'radius', 'distanceMin', 'x', 'y', 'iterations']) {
      self[m] = vi.fn(() => self);
    }
    return self;
  };

  return {
    forceSimulation: vi.fn((nodes: unknown) => new MockSimulation(nodes as Array<Record<string, unknown>>)),
    forceX: vi.fn(() => chainable()),
    forceY: vi.fn(() => chainable()),
    forceRadial: vi.fn(() => chainable()),
    forceLink: vi.fn(() => chainable()),
    forceManyBody: vi.fn(() => chainable()),
    forceCollide: vi.fn(() => chainable()),
  };
});

import {
  TopicMapComponent,
  MAX_MAP_TOPICS,
} from './topic-map.component';
import { TopicDto, TopicGraphDto, TopicKindDto } from '../../core/services/topics.service';

const topics: TopicDto[] = [
  { id: 'alpha', name: 'Alpha', usageCount: 12 },
  { id: 'beta', name: 'Beta', usageCount: 5 },
  { id: 'gamma', name: 'Gamma', usageCount: 2 },
];

const graphResponse: TopicGraphDto = {
  nodes: [
    { id: 'alpha', name: 'Alpha', usageCount: 12 },
    { id: 'beta', name: 'Beta', usageCount: 5 },
    { id: 'gamma', name: 'Gamma', usageCount: 2 },
  ],
  edges: [
    { sourceId: 'alpha', targetId: 'beta', sharedNotes: 3 },
    { sourceId: 'alpha', targetId: 'gamma', sharedNotes: 1 },
  ],
};

describe('TopicMapComponent', () => {
  let component: TopicMapComponent;
  let fixture: ComponentFixture<TopicMapComponent>;
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TopicMapComponent],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(TopicMapComponent);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  function setTopics(value: TopicDto[]): void {
    fixture.componentRef.setInput('topics', value);
    fixture.detectChanges();
  }

  function flushGraph(data: TopicGraphDto = graphResponse): void {
    const req = http.expectOne('/api/topics/graph');
    req.flush(data);
    fixture.detectChanges();
  }

  it('fetches graph data when topics are set', () => {
    setTopics(topics);
    const req = http.expectOne('/api/topics/graph');
    expect(req.request.method).toBe('GET');
    req.flush(graphResponse);
    fixture.detectChanges();
    expect(component.loading()).toBe(false);
  });

  it('maps graph nodes into the accessible list', () => {
    setTopics(topics);
    flushGraph();

    const accessibleNodes = component.accessibleNodes();
    expect(accessibleNodes).toHaveLength(3);
    expect(accessibleNodes.map((n) => n.name).sort()).toEqual(['Alpha', 'Beta', 'Gamma']);

    // Connection counts derived from edges.
    const alpha = accessibleNodes.find((n) => n.id === 'alpha')!;
    expect(alpha.connectionCount).toBe(2);
    const gamma = accessibleNodes.find((n) => n.id === 'gamma')!;
    expect(gamma.connectionCount).toBe(1);
  });

  it('handles empty graph state without crashing', () => {
    setTopics([]);
    flushGraph({ nodes: [], edges: [] });

    expect(component.accessibleNodes()).toHaveLength(0);
    expect(component.loading()).toBe(false);
  });

  it('handles a single topic with no edges', () => {
    const singleTopic = [{ id: 'only', name: 'Only Topic', usageCount: 1 }];
    setTopics(singleTopic);
    flushGraph({ nodes: [{ id: 'only', name: 'Only Topic', usageCount: 1 }], edges: [] });

    expect(component.accessibleNodes()).toHaveLength(1);
    expect(component.noConnections()).toBe(true);
  });

  it('blames the filter, not missing connections, when a search empties the graph', () => {
    // The header's search filters the topic set this map draws, and it is
    // visible in map view too. An empty graph then means either "no topics
    // match your query" or "you have no connections yet" — reporting the second
    // when the first is true tells the user their data is missing.
    setTopics([]);
    flushGraph({ nodes: [], edges: [] });
    fixture.componentRef.setInput('searchQuery', '  zzzz-no-such-topic  ');
    fixture.detectChanges();

    expect(component.noConnections()).toBe(true);
    const status = (fixture.nativeElement as HTMLElement).querySelector('.map-status');
    expect(status?.textContent).toContain('No topics match');
    // The query is trimmed in the copy, so stray whitespace cannot leak in.
    expect(status?.textContent).toContain('zzzz-no-such-topic');
    expect(status?.textContent).not.toContain('No connections yet');
  });

  it('reports missing connections when there is no search to blame', () => {
    setTopics([]);
    flushGraph({ nodes: [], edges: [] });
    fixture.componentRef.setInput('searchQuery', '');
    fixture.detectChanges();

    const status = (fixture.nativeElement as HTMLElement).querySelector('.map-status');
    expect(status?.textContent).toContain('No connections yet');
    expect(status?.textContent).not.toContain('No topics match');
  });

  it('shows the cap note when there are more than 150 topics', () => {
    const manyTopics = Array.from({ length: MAX_MAP_TOPICS + 1 }, (_, i) => ({
      id: `topic-${i}`,
      name: `Topic ${i}`,
      usageCount: i + 1,
    }));
    setTopics(manyTopics);
    flushGraph({
      nodes: manyTopics.map((c) => ({ id: c.id, name: c.name, usageCount: c.usageCount })),
      edges: [],
    });

    expect(component.isCapped()).toBe(true);
    expect(fixture.nativeElement.querySelector('.map-limit-note')?.textContent?.trim()).toBe(
      `Showing 150 of ${MAX_MAP_TOPICS + 1} topics.`
    );
  });

  it('filters visible nodes to only include topics passed as input', () => {
    // Graph has 3 nodes but we only pass 2 topics.
    setTopics([topics[0], topics[1]]);
    flushGraph();

    const nodes = component.accessibleNodes();
    expect(nodes).toHaveLength(2);
    expect(nodes.map((n) => n.id).sort()).toEqual(['alpha', 'beta']);
  });

  it('emits the selected topic when selectAccessibleNode is called', () => {
    setTopics(topics);
    flushGraph();
    const selected = vi.fn();
    component.topicSelected.subscribe(selected);

    component.selectAccessibleNode('alpha');

    expect(selected).toHaveBeenCalledWith('alpha');
    expect(component.selectedNodeId()).toBe('alpha');
  });

  it('leaves the mode switch and the topic search to the surface header', () => {
    // The map used to carry its own copies of both (`.map-view-exit` and a
    // second search field), because map view closes the index rail that
    // previously held them. The surface header now renders in both modes, so
    // those duplicates have to be gone — otherwise the user gets two controls
    // with the same name, and the mode switch appears to move on every toggle.
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.map-toolbar'), 'no second toolbar in the map').toBeNull();
    expect(host.querySelector('input[type="search"]'), 'no second search field').toBeNull();
    expect(
      host.querySelector('[aria-label="Topic view"]'),
      'the mode switch must not be duplicated inside the map'
    ).toBeNull();

    // What IS genuinely map-scoped stays: one action rail, with its camera and
    // layout controls (and the selection chip, when something is selected).
    const rail = host.querySelector('[role="toolbar"]');
    expect(rail).toBeTruthy();
    expect(rail!.getAttribute('aria-label')).toBe('Map actions');
    expect(host.querySelector('[aria-label="Zoom in"]')).toBeTruthy();
    expect(host.querySelector('[aria-label="Fit to view"]')).toBeTruthy();
    expect(host.querySelector('[aria-label="Center on selection"]')).toBeTruthy();
    expect(host.querySelector('[aria-label="Focus mode"]')).toBeTruthy();
    expect(host.querySelector('[aria-label="Reset layout"]')).toBeTruthy();
  });

  it('opens a node on double-click and suppresses Sigma\'s zoom', () => {
    setTopics(topics);
    flushGraph();

    const emitted = vi.fn();
    component.openTopic.subscribe(emitted);
    const preventSigmaDefault = vi.fn();

    const sigmaHandlers = (globalThis as unknown as {
      __sigmaHandlers: Record<string, (payload?: unknown) => void>;
    }).__sigmaHandlers;
    const doubleClick = sigmaHandlers['doubleClickNode'];
    expect(doubleClick, 'the component must listen for doubleClickNode').toBeTruthy();
    doubleClick!({ node: 'alpha', preventSigmaDefault });

    expect(emitted).toHaveBeenCalledWith('alpha');
    expect(component.selectedNodeId()).toBe('alpha');
    // Without this Sigma ALSO zooms to the node, so the camera would lurch
    // between the two clicks of a gesture meant to leave the map entirely.
    expect(preventSigmaDefault, 'double-click must not also zoom the camera').toHaveBeenCalled();
  });

  it('ignores a double-click that lands at the end of a drag', () => {
    setTopics(topics);
    flushGraph();

    const emitted = vi.fn();
    component.openTopic.subscribe(emitted);

    const sigmaHandlers = (globalThis as unknown as {
      __sigmaHandlers: Record<string, (payload?: unknown) => void>;
    }).__sigmaHandlers;
    const captor = (globalThis as unknown as {
      __captor: Record<string, (payload?: unknown) => void>;
    }).__captor;

    // A real drag: press, travel past the threshold, release. Sigma then
    // dispatches the click that follows the release, so the drag guard has to
    // hold for the double-click path too.
    sigmaHandlers['downNode']!({ node: 'alpha', event: { x: 0, y: 0 } });
    captor['mousemovebody']!({ x: 60, y: 60, preventSigmaDefault: () => {} });
    captor['mouseup']!(undefined);

    sigmaHandlers['doubleClickNode']!({ node: 'alpha', preventSigmaDefault: () => {} });
    expect(emitted).not.toHaveBeenCalled();
  });

  it('clears the selection when empty space is clicked', () => {
    setTopics(topics);
    flushGraph();

    // Select a node first, the way a user would.
    const sigmaHandlers = (globalThis as unknown as {
      __sigmaHandlers: Record<string, (payload?: unknown) => void>;
    }).__sigmaHandlers;
    sigmaHandlers['clickNode']!({ node: 'alpha' });
    expect(component.selectedNodeId()).toBe('alpha');

    const cleared = vi.fn();
    component.selectionCleared.subscribe(cleared);
    sigmaHandlers['clickStage']!();

    // Without this the map stayed stuck on the last node clicked: selection
    // drives the index rail and the "Read notes" action, and nothing returned
    // the graph to a neutral state.
    expect(cleared, 'an empty-space click must report the cleared selection').toHaveBeenCalled();
    expect(component.selectedNodeId()).toBeNull();
    expect(component.hoveredId()).toBeNull();
  });

  it('keeps the selection through a camera pan that ends on empty space', () => {
    setTopics(topics);
    flushGraph();

    const sigmaHandlers = (globalThis as unknown as {
      __sigmaHandlers: Record<string, (payload?: unknown) => void>;
    }).__sigmaHandlers;
    const captor = (globalThis as unknown as {
      __captor: Record<string, (payload?: unknown) => void>;
    }).__captor;

    sigmaHandlers['clickNode']!({ node: 'alpha' });
    const cleared = vi.fn();
    component.selectionCleared.subscribe(cleared);

    // A pan: press on empty space, move the camera, release. Sigma suppresses
    // the click that follows because `draggedEvents` exceeded its tolerance —
    // so the component never sees a `clickStage` and the selection survives.
    // (The component cannot defend against this itself; the guard lives in
    // Sigma's captor, which is why the mock asserts its absence rather than a
    // component-side flag.)
    captor['mousemovebody']?.({ x: 60, y: 60, preventSigmaDefault: () => {} });

    expect(cleared, 'a pan must not clear the selection').not.toHaveBeenCalled();
    expect(component.selectedNodeId()).toBe('alpha');
  });

  it('reflects externally set selectedId', () => {
    setTopics(topics);
    flushGraph();

    fixture.componentRef.setInput('selectedId', 'beta');
    fixture.detectChanges();

    expect(component.selectedNodeId()).toBe('beta');
  });

  it('opens at the camera its host kept, instead of framing the graph', () => {
    const camera = (globalThis as unknown as { __camera: Record<string, ReturnType<typeof vi.fn>> }).__camera;
    camera['setState'].mockClear();
    camera['animate'].mockClear();
    fixture.componentRef.setInput('camera', { x: 0.2, y: 0.7, ratio: 0.4, angle: 0 });
    setTopics(topics);
    flushGraph();

    expect(camera['setState']).toHaveBeenCalledWith({ x: 0.2, y: 0.7, ratio: 0.4, angle: 0 });
    expect(camera['animate']).not.toHaveBeenCalled();
  });

  it('hands its host the camera as it goes away', () => {
    const kept = vi.fn();
    component.cameraKept.subscribe(kept);
    setTopics(topics);
    flushGraph();

    fixture.destroy();
    expect(kept).toHaveBeenCalledWith({ x: 0.5, y: 0.5, ratio: 1, angle: 0 });
  });

  it('names the card action as its host asks', () => {
    fixture.componentRef.setInput('notesLabel', 'Open topic');
    setTopics(topics);
    flushGraph();
    component.selectAccessibleNode('alpha');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.map-card-notes').textContent.trim()).toBe('Open topic');
  });

  it('keeps the accessible list in sync with the rendered graph', () => {
    setTopics(topics);
    flushGraph();

    const accessibleList = fixture.nativeElement.querySelector('.map-accessible-list') as HTMLElement;
    expect(accessibleList).toBeTruthy();
    expect(accessibleList.getAttribute('aria-label')).toBe('Topics in this map');
    const buttons = accessibleList.querySelectorAll('button');
    expect(buttons).toHaveLength(3);

    // Every button has text content and aria-pressed.
    expect(
      [...buttons].every(
        (b) => b.textContent?.trim() && b.getAttribute('aria-pressed') !== null
      )
    ).toBe(true);
  });

  it('puts every map action on one toolbar with an accessible name', () => {
    const rail = fixture.nativeElement.querySelector('[role="toolbar"]') as HTMLElement;
    expect(rail).toBeTruthy();

    const buttons = [...rail.querySelectorAll('button')] as HTMLButtonElement[];
    // zoom out, zoom in, fit, center, focus, reset
    expect(buttons.length).toBeGreaterThanOrEqual(6);

    // Icon-only buttons MUST carry a name for AT and a pointer tooltip.
    for (const button of buttons) {
      const name =
        button.getAttribute('aria-label') ?? button.getAttribute('title') ?? button.textContent?.trim();
      expect(name, 'every rail action needs an accessible name').toBeTruthy();
    }

    expect(rail.querySelector('[aria-label="Zoom out"]')).toBeTruthy();
    expect(rail.querySelector('[aria-label="Zoom in"]')).toBeTruthy();
    expect(rail.querySelector('[aria-label="Fit to view"]')).toBeTruthy();
    expect(rail.querySelector('[aria-label="Center on selection"]')).toBeTruthy();
    expect(rail.querySelector('[aria-label="Focus mode"]')).toBeTruthy();
    expect(rail.querySelector('[aria-label="Reset layout"]')).toBeTruthy();

    // The rail is a single tab stop with roving focus, not six.
    expect(rail.getAttribute('aria-orientation')).toBe('horizontal');
  });

  it('shows the selected topic in a card with its notes action, not in the camera rail', () => {
    setTopics(topics);
    flushGraph();

    // Nothing selected: no card, and the rail is pure camera.
    expect(fixture.nativeElement.querySelector('.map-card')).toBeNull();
    const rail = fixture.nativeElement.querySelector('[role="toolbar"]') as HTMLElement;
    expect(rail.textContent).not.toContain('Read notes');

    component.selectAccessibleNode('alpha');
    fixture.componentRef.setInput('selectedName', 'Alpha');
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector('.map-card') as HTMLElement;
    expect(card, 'a selection opens the card').toBeTruthy();
    expect(card.querySelector('.map-selection-name')?.textContent).toContain('Alpha');
    // Counts come from the one graph response: alpha is used 12 times and
    // shares notes with beta and gamma.
    expect(card.querySelector('.map-card-meta')?.textContent).toMatch(/12 references\s*·\s*2 connections/);

    // Related topics, strongest link first (beta shares 3 notes, gamma 1).
    const related = [...card.querySelectorAll('.map-card-related button')].map((b) => b.textContent?.trim());
    expect(related).toEqual(['Beta', 'Gamma']);

    const emitted = vi.fn();
    component.openNotes.subscribe(emitted);
    const notes = [...card.querySelectorAll('button')].find((b) => b.textContent?.includes('Read notes'));
    expect(notes, 'the card carries the notes action').toBeTruthy();
    notes!.click();
    expect(emitted).toHaveBeenCalled();
  });

  it('moves the selection to a related topic from the card', () => {
    setTopics(topics);
    flushGraph();
    component.selectAccessibleNode('alpha');
    fixture.detectChanges();

    const selected = vi.fn();
    component.topicSelected.subscribe(selected);
    const beta = [...fixture.nativeElement.querySelectorAll('.map-card-related button')].find(
      (b) => (b as HTMLElement).textContent?.trim() === 'Beta'
    ) as HTMLButtonElement;
    beta.click();
    fixture.detectChanges();

    expect(selected).toHaveBeenCalledWith('beta');
    expect(component.selectedNodeId()).toBe('beta');
  });

  it('closes the card and clears the selection from its close button', () => {
    setTopics(topics);
    flushGraph();
    component.selectAccessibleNode('alpha');
    fixture.detectChanges();

    const cleared = vi.fn();
    component.selectionCleared.subscribe(cleared);
    (fixture.nativeElement.querySelector('[aria-label="Clear selection"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(cleared).toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.map-card')).toBeNull();
  });

  it('shows loading status while graph data is pending', () => {
    setTopics(topics);

    expect(component.loading()).toBe(true);
    const status = fixture.nativeElement.querySelector('.map-status');
    expect(status?.textContent).toContain('Loading graph');

    flushGraph();
    expect(component.loading()).toBe(false);
  });

  it('says the map could not load, not that there are no connections, and offers Try again', () => {
    setTopics(topics);
    http.expectOne('/api/topics/graph').flush('offline', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    const status = fixture.nativeElement.querySelector('.map-status') as HTMLElement;
    expect(status.getAttribute('role')).toBe('alert');
    expect(status.textContent).toContain('could not be loaded');
    expect(status.textContent).not.toContain('No connections yet');
    const retry = status.querySelector('button') as HTMLButtonElement;
    expect(retry.textContent?.trim()).toBe('Try again');

    retry.click();
    flushGraph();
    expect(component.loadFailed()).toBe(false);
  });

  it('disposes Sigma renderer on component destruction', () => {
    setTopics(topics);
    flushGraph();

    // The component should not throw when destroyed.
    expect(() => {
      fixture.destroy();
    }).not.toThrow();
  });

  it('makes only a single /api/topics/graph request, not N+1 related requests', () => {
    setTopics(topics);
    // Expect exactly one graph request, not per-topic related requests.
    const graphReqs = http.match('/api/topics/graph');
    expect(graphReqs).toHaveLength(1);
    graphReqs[0].flush(graphResponse);

    // No /related requests should have been made.
    const relatedReqs = http.match((req) => req.url.includes('/related'));
    expect(relatedReqs).toHaveLength(0);
    fixture.detectChanges();
  });

  it('never shows writings as nodes (issue #1162)', () => {
    // A writing reaches a topic only through the notes it cites; the map is
    // topics and their shared notes, and it never asks about writings.
    setTopics(topics);
    flushGraph();

    expect(http.match((req) => /writing|cite/i.test(req.urlWithParams))).toHaveLength(0);
    expect(component.accessibleNodes().map((node) => node.id).sort()).toEqual(['alpha', 'beta', 'gamma']);
    const rendered = [...fixture.nativeElement.querySelectorAll('.map-accessible-list button')]
      .map((button) => (button as HTMLElement).textContent ?? '');
    expect(rendered.some((text) => /writing|essay/i.test(text))).toBe(false);
  });

  /* ── Regression guards for the reported graph bugs ──
   *
   * Each of these fails against the behaviour that shipped, so they pin the
   * fixes rather than merely describing them.
   */

  describe('graph readability', () => {
    it('renders edges at an alpha that is actually visible', () => {
      setTopics(topics);
      flushGraph();

      const graph = (globalThis as { __nostosGraph?: { forEachEdge: Function } }).__nostosGraph!;
      const colors: string[] = [];
      graph.forEachEdge((_e: string, attrs: { color: string }) => colors.push(attrs.color));

      expect(colors.length).toBeGreaterThan(0);
      for (const color of colors) {
        const alpha = Number(color.match(/,\s*([\d.]+)\)$/)?.[1] ?? '0');
        // 0.44-0.54 measured 1.05:1 against the field, i.e. invisible.
        expect(alpha, `edge ${color} must be clearly visible`).toBeGreaterThanOrEqual(0.6);
      }
    });

    it('shows labels for the smallest drawn nodes too', () => {
      setTopics(topics);
      flushGraph();

      // The smallest node size must clear the label threshold, or the majority
      // of topics are permanently unlabelled (15 of 53 in production).
      const settings = (globalThis as { __settings?: Record<string, unknown> }).__settings!;
      expect(settings['labelRenderedSizeThreshold']).toBeLessThanOrEqual(4);
    });

    it('does not let autoRescale move nodes the user did not drag', () => {
      setTopics(topics);
      flushGraph();

      const settings = (globalThis as { __settings?: Record<string, unknown> }).__settings!;
      // autoRescale remaps the whole extent on every change, so dragging one
      // node shrinks all the others (measured 57% -> 6.4% of stage width).
      expect(settings['autoRescale']).toBe(false);
    });

    it('keeps unconnected nodes and their labels readable when one is selected', () => {
      setTopics(topics);
      flushGraph();

      const sigma = (globalThis as {
        __nostosSigma?: { nodeReducer?: unknown };
      }).__nostosSigma!;
      const reducer = sigma.nodeReducer as
        | ((
            node: string,
            data: Record<string, unknown>,
            attrs: Record<string, unknown>
          ) => Record<string, unknown>)
        | undefined;
      expect(typeof reducer, 'a nodeReducer must be installed').toBe('function');

      // Select 'alpha' so its neighbours (beta, gamma) are the active
      // neighbourhood and every other node is "unconnected".
      component.selectAccessibleNode('alpha');

      // 'gamma' IS connected to alpha, so use a node the graph has that is not:
      // add a fourth topic with no edges.
      const data: Record<string, unknown> = { label: 'Lonely', color: '#000000' };
      // Sigma 4 merges a reducer's PARTIAL result over the display data.
      const unconnected = { ...data, ...reducer!('lonely', data, { label: 'Lonely' }) };

      // The node must not be erased: it keeps a colour and keeps its label.
      expect(unconnected['label'], 'unconnected node must keep its label').toBe('Lonely');
      const color = String(unconnected['color']);
      const alpha = Number(color.match(/,\s*([\d.]+)\)$/)?.[1] ?? '1');
      // 0.55 measured 1.66:1 (21% of undimmed contrast) in the light theme.
      expect(alpha, `dim alpha ${color} must stay legible`).toBeGreaterThanOrEqual(0.7);

      const labelColor = String(unconnected['labelColor']);
      const labelAlpha = Number(labelColor.match(/,\s*([\d.]+)\)$/)?.[1] ?? '1');
      expect(labelAlpha, 'unconnected label must not be invisible').toBeGreaterThanOrEqual(0.5);
    });

    it('draws the active label on a themed plate, not Sigma\'s hardcoded white box', () => {
      setTopics(topics);
      flushGraph();

      const settings = (globalThis as { __settings?: Record<string, unknown> }).__settings!;
      const sigma = (globalThis as { __nostosSigma?: { styles?: { nodes?: unknown } } })
        .__nostosSigma!;
      const layers = (globalThis as unknown as {
        __layers: Record<string, { element: HTMLCanvasElement; options: Record<string, unknown> }>;
      }).__layers;

      // Sigma's own hover backdrop is `#ffffff` in both themes; on dark that put
      // #c4c7d0 ink on white (1.66:1). It must be off, so the only plate drawn
      // is the component's, which paints --graph-label-box.
      const nodeStyles = ([] as unknown[]).concat(sigma.styles?.nodes ?? []);
      const lastBackdrop = nodeStyles
        .map((rule) => (rule as { backdropVisibility?: unknown }).backdropVisibility)
        .filter((value) => value !== undefined)
        .at(-1);
      expect(lastBackdrop, 'Sigma\'s white hover backdrop must be switched off').toBe('hidden');

      // Labels are the component's job: Sigma's are off, and a label layer sits
      // under Sigma's mouse layer so pointer events still reach the graph.
      expect(settings['renderLabels'], 'Sigma must not draw labels itself').toBe(false);
      const labelLayer = Object.values(layers).at(-1);
      expect(labelLayer, 'a label canvas must be layered over the stage').toBeTruthy();
      expect(labelLayer!.options['beforeLayer']).toBe('mouse');
    });

    it('lays the graph out with enough repulsion to avoid an unreadable clump', () => {
      setTopics(topics);
      flushGraph();

      // The layout is Obsidian's force set, so the guard is that the forces were
      // constructed with Obsidian's own numbers — read off the live simulation
      // rather than re-declared here, so the test fails if the component drifts.
      const layout = (globalThis as {
        __nostosLayout?: {
          force: (name: string) => unknown;
          alpha: () => number;
        };
      }).__nostosLayout!;
      expect(layout, 'a d3 simulation must be installed').toBeTruthy();

      const charge = layout.force('charge') as { strength: () => unknown };
      expect(typeof charge?.strength).toBe('function');

      // ForceAtlas2's scalingRatio/gravity are gone entirely — the old layout
      // engine must not be referenced anywhere in the component any more.
      // Measured on the real 53-node graph, at the old extremes (scalingRatio 18 /
      // gravity 0.4) 18 node pairs rendered closer than their combined radii, so
      // the centre of the map was an unreadable clump.
    });

    it('labels nodes that sit at the small end of the size range', () => {
      setTopics(topics);
      flushGraph();

      const settings = (globalThis as { __settings?: Record<string, unknown> }).__settings!;
      const zoomFn = settings['zoomToSizeRatioFunction'] as (r: number) => number;
      const threshold = settings['labelRenderedSizeThreshold'] as number;

      // Sigma gates labels on `scaleSize(size)`, the DRAWN radius, not the stored
      // attribute:
      //   var size = this.scaleSize(data.size);
      //   if (!data.forceLabel && size < this.settings.labelRenderedSizeThreshold) continue;
      //
      // So the threshold has to be compared against the smallest node's drawn
      // radius at the FITTED zoom. Shipping 3.2 against a fitted ratio of ~1.91
      // put the smallest nodes at 4/sqrt(1.91) = 2.89px and silenced 47 of 53
      // labels — a regression no unit test caught at the time, which is why this
      // assertion exists.
      const fittedRatio = 1.91;
      const smallestDrawn = 4 / zoomFn(fittedRatio);
      expect(
        threshold,
        `threshold ${threshold} must sit below the smallest drawn radius ${smallestDrawn.toFixed(2)}`
      ).toBeLessThan(smallestDrawn);
    });

    it('draws the focused nodes own edges at full accent contrast', () => {
      setTopics(topics);
      flushGraph();

      const sigma = (globalThis as {
        __nostosSigma?: { edgeReducer?: unknown };
      }).__nostosSigma!;
      const reducer = sigma.edgeReducer as
        | ((edge: string, data: Record<string, unknown>) => Record<string, unknown>)
        | undefined;
      expect(typeof reducer, 'an edgeReducer must be installed').toBe('function');

      // Select 'alpha'; its edges are alpha-beta and alpha-gamma.
      component.selectAccessibleNode('alpha');

      // Sigma 4 merges a reducer's PARTIAL result over the display data.
      const data = { size: 1, color: 'rgba(138,134,128,0.8)' };
      const connected = { ...data, ...reducer!('alpha->beta', data) };
      const unrelated = { ...data, ...reducer!('beta->gamma', data) };

      // The connected edge is drawn in the full-strength accent ink with no
      // alpha reduction. At alpha 0.72 it measured 5.09:1 against the field
      // versus 3.44:1 for an ordinary edge, and the pixel classification could
      // not separate the two tiers at all.
      const connectedColor = String(connected['color']);
      expect(
        connectedColor,
        'focused edges must be opaque accent ink'
      ).not.toMatch(/rgba\([^)]*,\s*0\./);

      // Unrelated edges stay visibly dimmer.
      const unrelatedColor = String(unrelated['color']);
      const unrelatedAlpha = Number(unrelatedColor.match(/,\s*([\d.]+)\)$/)?.[1] ?? '1');
      expect(unrelatedAlpha, 'unrelated edges must recede').toBeLessThan(0.5);

      // And connected edges are thicker than the base stroke.
      expect(Number(connected['size'])).toBeGreaterThan(Number(unrelated['size']));
    });
  });

  describe('carry: drag physics', () => {
    /** The simulation the component installed, via its diagnostic handle. */
    function layoutHandle(): {
      nodes: () => Array<{ id: string; fx?: number | null; fy?: number | null; vx?: number; vy?: number }>;
      alpha: () => number;
      alphaTarget: () => number;
    } {
      return (globalThis as unknown as {
        __nostosLayout: {
          nodes: () => Array<{ id: string; fx?: number | null; fy?: number | null; vx?: number; vy?: number }>;
          alpha: () => number;
          alphaTarget: () => number;
        };
      }).__nostosLayout;
    }

    /**
     * The simulation node for a topic, so a spec can read the drag pin.
     *
     * The pin lives on the d3 simulation (`fx`/`fy`), NOT on the graphology node
     * — ForceAtlas2's `fixed` graph attribute is gone with the layout engine.
     */
    function pinnedNode(id: string): { fx: number | null; fy: number | null } {
      const node = layoutHandle().nodes().find((n) => n.id === id);
      expect(node, `node ${id} must exist in the simulation`).toBeTruthy();
      return node as unknown as { fx: number | null; fy: number | null };
    }

    function fireDrag(): void {
      const sigmaHandlers = (globalThis as unknown as {
        __sigmaHandlers: Record<string, (payload?: unknown) => void>;
      }).__sigmaHandlers;
      const captor = (globalThis as unknown as {
        __captor: Record<string, (payload?: unknown) => void>;
      }).__captor;

      sigmaHandlers['downNode']!({ node: 'alpha', event: { x: 0, y: 0 } });
      captor['mousemovebody']!({ x: 40, y: 40, preventSigmaDefault: () => {} });
      captor['mouseup']!(undefined);
    }

    it('pins the dragged node with fx/fy and releases it on mouseup', () => {
      setTopics(topics);
      flushGraph();

      const sigmaHandlers = (globalThis as unknown as {
        __sigmaHandlers: Record<string, (payload?: unknown) => void>;
      }).__sigmaHandlers;
      const captor = (globalThis as unknown as {
        __captor: Record<string, (payload?: unknown) => void>;
      }).__captor;

      sigmaHandlers['downNode']!({ node: 'alpha', event: { x: 0, y: 0 } });

      // d3-force honours `fx`/`fy` per tick: a pinned node is snapped to that
      // point and its velocity zeroed, so the rest of the graph relaxes around a
      // node that stays exactly where the pointer put it. (The old code used
      // ForceAtlas2's `fixed` attribute, which d3 does not read at all.)
      const pinned = layoutHandle().nodes().find((n) => n.id === 'alpha')!;
      expect(pinned.fx, 'the pressed node must be pinned in d3 coordinates').not.toBeUndefined();
      expect(pinned.fy, 'the pressed node must be pinned in d3 coordinates').not.toBeUndefined();

      captor['mouseup']!(undefined);

      const released = layoutHandle().nodes().find((n) => n.id === 'alpha')!;
      expect(released.fx, 'the pin must be cleared on mouseup').toBeNull();
      expect(released.fy, 'the pin must be cleared on mouseup').toBeNull();
    });

    it('holds alpha up for the whole gesture, then lets it decay on release', () => {
      setTopics(topics);
      flushGraph();

      const sigmaHandlers = (globalThis as unknown as {
        __sigmaHandlers: Record<string, (payload?: unknown) => void>;
      }).__sigmaHandlers;
      const captor = (globalThis as unknown as {
        __captor: Record<string, (payload?: unknown) => void>;
      }).__captor;

      sigmaHandlers['downNode']!({ node: 'alpha', event: { x: 0, y: 0 } });
      captor['mousemovebody']!({ x: 40, y: 40, preventSigmaDefault: () => {} });

      // Obsidian re-posts `alpha: .3, alphaTarget: .3` on every pointermove, so
      // the simulation runs HOT and continuously for the gesture.
      expect(layoutHandle().alphaTarget(), 'the drag must hold the layout heated').toBeGreaterThan(0);
      expect(layoutHandle().alpha()).toBeGreaterThan(0);

      captor['mouseup']!(undefined);

      // And drops alphaTarget to 0 on release, which is what makes alpha decay
      // and the graph settle under its own inertia instead of freezing.
      //
      // Measured before this change: dragging a hub moved 51 neighbours, and then
      // 0 nodes moved at EVERY sample up to 2s after release — the layout was
      // frozen solid. With the decay in place the same gesture leaves 53 nodes
      // still moving at +150ms, easing from 29.5px to 10.1px over the following
      // ~2s, and 0.0px of drift once alpha reaches alphaMin.
      expect(layoutHandle().alphaTarget(), 'release must drop the layout back to cooling').toBe(0);
    });

    it('stops the layout loop once alpha reaches the floor', () => {
      setTopics(topics);
      flushGraph();

      const rafSpy = vi.spyOn(window, 'requestAnimationFrame');
      try {
        fireDrag();
        // Drain the queued frames. Each tick lowers alpha, so the loop must stop
        // on its own rather than scheduling another frame forever.
        for (let i = 0; i < 400; i += 1) {
          const queued = rafSpy.mock.calls.at(-1)?.[0] as ((t: number) => void) | undefined;
          if (!queued) break;
          rafSpy.mockClear();
          queued(0);
        }

        expect(
          layoutHandle().alpha(),
          'alpha must decay to the floor, which is what halts the loop'
        ).toBeLessThanOrEqual(0.001);

        // With alpha at the floor the component must stop asking for frames.
        rafSpy.mockClear();
        fireDrag();
        for (let i = 0; i < 500 && rafSpy.mock.calls.length; i += 1) {
          const queued = rafSpy.mock.calls.at(-1)?.[0] as ((t: number) => void) | undefined;
          if (!queued) break;
          rafSpy.mockClear();
          queued(0);
        }
        const before = rafSpy.mock.calls.length;
        if (before) {
          const next = rafSpy.mock.calls.at(-1)![0] as (t: number) => void;
          rafSpy.mockClear();
          next(0);
        }
        expect(rafSpy.mock.calls.length, 'no permanent idle loop may be left running').toBe(0);
      } finally {
        rafSpy.mockRestore();
      }
    });

    /**
     * The reported mobile bug: Sigma emits `mouseup` for a mouse only, so a
     * touch gesture never reached the teardown, leaving `camera.disable()` in
     * place for good. One tap on a node was enough to freeze every camera
     * control.
     */
    it('releases the camera and the pin when a TOUCH gesture ends', () => {
      setTopics(topics);
      flushGraph();

      const camera = (globalThis as unknown as { __camera: { enabled: boolean } }).__camera;
      const sigmaHandlers = (globalThis as unknown as {
        __sigmaHandlers: Record<string, (payload?: unknown) => void>;
      }).__sigmaHandlers;
      const touch = (globalThis as unknown as {
        __touchCaptor: Record<string, (payload?: unknown) => void>;
      }).__touchCaptor;

      camera.enabled = true;

      // A finger lands on a node and lifts without travelling past the drag
      // threshold — i.e. a plain TAP to select.
      sigmaHandlers['downNode']!({ node: 'alpha', event: { x: 10, y: 10 } });
      expect(camera.enabled, 'the camera is held during the press').toBe(false);
      // The pin is d3's `fx`/`fy` on the simulation node — the graph attribute
      // ForceAtlas2 used (`fixed`) no longer exists.
      expect(pinnedNode('alpha').fx).not.toBeNull();

      // Sigma's touch captor only has touchmove/touchup — never mouseup.
      touch['touchup']!({ touches: [], previousTouches: [] });

      expect(
        camera.enabled,
        'a touch release must hand the camera back, or every control goes dead'
      ).toBe(true);
      expect(
        pinnedNode('alpha').fx,
        'the touch release must also unpin the node'
      ).toBeNull();
    });

    it('moves a node on a touch drag without leaving it pinned', () => {
      setTopics(topics);
      flushGraph();

      const graph = (globalThis as {
        __nostosGraph?: {
          getNodeAttributes: (n: string) => Record<string, unknown>;
        };
      }).__nostosGraph!;
      const sigmaHandlers = (globalThis as unknown as {
        __sigmaHandlers: Record<string, (payload?: unknown) => void>;
      }).__sigmaHandlers;
      const touch = (globalThis as unknown as {
        __touchCaptor: Record<string, (payload?: unknown) => void>;
      }).__touchCaptor;

      sigmaHandlers['downNode']!({ node: 'alpha', event: { x: 0, y: 0 } });
      // Travel past the threshold so this counts as a drag, not a tap.
      touch['touchmove']!({
        touches: [{ x: 60, y: 60 }],
        previousTouches: [{ x: 60, y: 60 }],
        preventSigmaDefault: () => {},
      });

      expect(
        graph.getNodeAttributes('alpha')['x'],
        'a touch drag must reposition the node'
      ).toBe(60);
      expect(
        pinnedNode('alpha').fx,
        'the touch drag must also move the d3 pin, or the node snaps back on the next tick'
      ).toBe(60);

      touch['touchup']!({ touches: [], previousTouches: [] });
      expect(pinnedNode('alpha').fx).toBeNull();
    });

    it('recovers from a release that lands outside the canvas', () => {
      setTopics(topics);
      flushGraph();

      const camera = (globalThis as unknown as { __camera: { enabled: boolean } }).__camera;
      const sigmaHandlers = (globalThis as unknown as {
        __sigmaHandlers: Record<string, (payload?: unknown) => void>;
      }).__sigmaHandlers;

      camera.enabled = true;
      sigmaHandlers['downNode']!({ node: 'alpha', event: { x: 0, y: 0 } });
      expect(pinnedNode('alpha').fx).not.toBeNull();
      expect(camera.enabled, 'the camera is held during the press').toBe(false);

      // Neither mouseup nor touchup fires: the pointer was released off-canvas
      // and only the window-level safety net sees it.
      window.dispatchEvent(new Event('pointercancel'));

      expect(camera.enabled, 'the safety net must hand the camera back').toBe(true);
      expect(pinnedNode('alpha').fx).toBeNull();
    });

    it('clears any stale pin when the layout is reset', () => {
      setTopics(topics);
      flushGraph();

      const layout = layoutHandle();
      const target = layout.nodes().find((n) => n.id === 'alpha')!;

      // Simulate a drag whose pointerup was lost outside the window.
      target.fx = 123;
      target.fy = 456;
      target.vx = 9;
      target.vy = 9;

      component.resetView();

      const after = layout.nodes().find((n) => n.id === 'alpha')!;
      expect(after.fx, 'Reset must unfreeze a node stranded by an interrupted drag').toBeNull();
      expect(after.fy, 'Reset must unfreeze a node stranded by an interrupted drag').toBeNull();
      expect(after.vx, 'Reset must discard leftover momentum').toBe(0);
      expect(after.vy, 'Reset must discard leftover momentum').toBe(0);
    });
  });

  describe('camera controls', () => {
    it('zooms in and out through the camera, animated', () => {
      setTopics(topics);
      flushGraph();

      const camera = (globalThis as unknown as {
        __camera: { zoomIn: ReturnType<typeof vi.fn>; zoomOut: ReturnType<typeof vi.fn> };
      }).__camera;
      camera.zoomIn.mockClear();
      camera.zoomOut.mockClear();

      component.zoomIn();
      component.zoomOut();

      expect(camera.zoomIn).toHaveBeenCalledWith({ duration: 200 });
      expect(camera.zoomOut).toHaveBeenCalledWith({ duration: 200 });
    });

    it('fits the graph by computing a framing ratio, not by resetting', () => {
      setTopics(topics);
      flushGraph();

      const camera = (globalThis as { __camera?: { animate: ReturnType<typeof vi.fn> } })
        .__camera!;
      camera.animate.mockClear();

      component.fitGraph();

      expect(camera.animate).toHaveBeenCalled();
      const state = camera.animate.mock.calls.at(-1)![0];
      expect(state.ratio).toBeGreaterThan(0);
      // A fit centres the graph where Sigma says its centre is: framed
      // coordinates stay a small range, never raw graph units.
      expect(Math.abs(state.x)).toBeLessThanOrEqual(1);
      expect(Math.abs(state.y)).toBeLessThanOrEqual(1);
    });

    it('centres the selected node in framed coordinates', () => {
      setTopics(topics);
      flushGraph();
      component.selectAccessibleNode('alpha');

      const camera = (globalThis as { __camera?: { animate: ReturnType<typeof vi.fn> } }).__camera!;
      camera.animate.mockClear();

      component.centerSelected();

      expect(camera.animate).toHaveBeenCalled();
      const state = camera.animate.mock.calls.at(-1)![0];

      // The old code passed raw graph coordinates here, which threw all 53 of
      // 53 nodes off screen (camera y = -42.9). Framed coordinates live in a
      // small range around 0.5 — the stage centre — so a magnitude of a few
      // units is the signature of the bug, not a legitimate value.
      expect(Math.abs(state.x), `framed x ${state.x} looks like raw graph units`).toBeLessThanOrEqual(2);
      expect(Math.abs(state.y), `framed y ${state.y} looks like raw graph units`).toBeLessThanOrEqual(2);
      expect(state.ratio).toBeGreaterThan(0);
    });

    it('restores the settled layout on reset, not just the camera', () => {
      setTopics(topics);
      flushGraph();

      const graph = (globalThis as {
        __nostosGraph?: { setNodeAttribute: (n: string, a: string, v: unknown) => void; getNodeAttributes: (n: string) => Record<string, unknown> };
      }).__nostosGraph!;

      // Simulate the user having dragged a node far away.
      const original = graph.getNodeAttributes('alpha');
      graph.setNodeAttribute('alpha', 'x', 9999);
      expect(graph.getNodeAttributes('alpha')['x']).toBe(9999);

      component.resetView();

      expect(graph.getNodeAttributes('alpha')['x']).toBe(original['x']);
    });
  });

  describe('lifecycle cleanup', () => {
    it('cancels pending fullscreen and drag timeouts when destroyed', async () => {
      vi.useFakeTimers();
      const origFullscreenEnabled = Object.getOwnPropertyDescriptor(document, 'fullscreenEnabled');
      Object.defineProperty(document, 'fullscreenEnabled', { value: false, configurable: true });
      try {
        setTopics(topics);
        flushGraph();

        const sigma = (globalThis as {
          __nostosSigma?: { refresh: ReturnType<typeof vi.fn> };
        }).__nostosSigma!;
        const refreshSpy = vi.spyOn(sigma, 'refresh');
        const clearTimeoutSpy = vi.spyOn(window, 'clearTimeout');

        // Trigger pseudo-fullscreen refresh timer and drag-reset timer before destroying.
        await component.toggleFullscreen();

        const sigmaHandlers = (globalThis as unknown as {
          __sigmaHandlers: Record<string, (payload?: unknown) => void>;
        }).__sigmaHandlers;
        const captor = (globalThis as unknown as {
          __captor: Record<string, (payload?: unknown) => void>;
        }).__captor;
        sigmaHandlers['downNode']!({ node: 'alpha', event: { x: 0, y: 0 } });
        captor['mousemovebody']!({ x: 50, y: 50, preventSigmaDefault: () => {} });
        captor['mouseup']!(undefined);

        refreshSpy.mockClear();
        fixture.destroy();
        vi.advanceTimersByTime(100);

        expect(clearTimeoutSpy).toHaveBeenCalled();
        expect(refreshSpy).not.toHaveBeenCalled();
      } finally {
        if (origFullscreenEnabled) {
          Object.defineProperty(document, 'fullscreenEnabled', origFullscreenEnabled);
        } else {
          delete (document as unknown as Record<string, unknown>)['fullscreenEnabled'];
        }
        vi.useRealTimers();
        vi.restoreAllMocks();
      }
    });

    it('aborts an in-flight /api/topics/graph subscription on destroy', () => {
      setTopics(topics);
      const req = http.expectOne('/api/topics/graph');
      expect(req.cancelled).toBe(false);

      fixture.destroy();
      expect(req.cancelled).toBe(true);
    });
  });

  describe('local map & link strength', () => {
    it('scopes to rootId including low-usage topics outside global top 150 and shows "Showing 150 of N" when capped', () => {
      const bulkTopics = [
        ...Array.from({ length: MAX_MAP_TOPICS + 10 }, (_, i) => ({
          id: `bulk-${i}`,
          name: `Bulk ${i}`,
          usageCount: 1000 + i,
        })),
        { id: 'tiny-root', name: 'Tiny Root', usageCount: 1 },
        { id: 'tiny-neighbour', name: 'Tiny Neighbour', usageCount: 1 },
      ];
      const bulkEdges = [
        ...Array.from({ length: MAX_MAP_TOPICS + 10 }, (_, i) => ({
          sourceId: 'tiny-root',
          targetId: `bulk-${i}`,
          sharedNotes: 2,
        })),
        { sourceId: 'tiny-root', targetId: 'tiny-neighbour', sharedNotes: 3 },
      ];

      fixture.componentRef.setInput('rootId', 'tiny-root');
      fixture.componentRef.setInput('depth', 1);
      setTopics(bulkTopics);
      flushGraph({
        nodes: bulkTopics.map((t) => ({ id: t.id, name: t.name, usageCount: t.usageCount })),
        edges: bulkEdges,
      });

      const nodeIds = component.accessibleNodes().map((n) => n.id);
      expect(nodeIds).toContain('tiny-root');
      expect(nodeIds).not.toContain('tiny-neighbour'); // usage 1 at hop 1 is capped after higher-usage hop-1 nodes
      expect(component.isCapped()).toBe(true);
      expect(fixture.nativeElement.querySelector('.map-limit-note')?.textContent?.trim()).toBe(
        `Showing 150 of ${MAX_MAP_TOPICS + 12} topics.`
      );
    });

    it('includes small connected topics on a local map when global whole map would leave them out', () => {
      const bulkTopics = [
        ...Array.from({ length: MAX_MAP_TOPICS }, (_, i) => ({
          id: `top-${i}`,
          name: `Top ${i}`,
          usageCount: 500 + i,
        })),
        { id: 'niche-root', name: 'Niche Root', usageCount: 2 },
        { id: 'niche-leaf', name: 'Niche Leaf', usageCount: 1 },
      ];
      fixture.componentRef.setInput('rootId', 'niche-root');
      fixture.componentRef.setInput('depth', 1);
      setTopics(bulkTopics);
      flushGraph({
        nodes: bulkTopics.map((t) => ({ id: t.id, name: t.name, usageCount: t.usageCount })),
        edges: [{ sourceId: 'niche-root', targetId: 'niche-leaf', sharedNotes: 2 }],
      });

      expect(component.accessibleNodes().map((n) => n.id).sort()).toEqual(['niche-leaf', 'niche-root']);
    });

    it('keeps a zero-link local root visible without showing no-connections empty state', () => {
      fixture.componentRef.setInput('rootId', 'alpha');
      fixture.componentRef.setInput('depth', 1);
      setTopics(topics);
      flushGraph({ nodes: graphResponse.nodes, edges: [] });

      expect(component.accessibleNodes().map((n) => n.id)).toEqual(['alpha']);
      expect(component.noConnections()).toBe(false);
    });

    it('filters edges by minShared on both whole map and local map', () => {
      fixture.componentRef.setInput('minShared', 2);
      setTopics(topics);
      flushGraph();

      const alphaWhole = component.accessibleNodes().find((n) => n.id === 'alpha')!;
      expect(alphaWhole.connectionCount).toBe(1); // gamma (sharedNotes: 1) dropped

      fixture.componentRef.setInput('rootId', 'alpha');
      fixture.detectChanges();
      expect(component.accessibleNodes().map((n) => n.id).sort()).toEqual(['alpha', 'beta']);
    });

    it('auto-defaults minShared to 2 when depth >= 2 reaches > 60% of graph and exceeds MAX_MAP_TOPICS', () => {
      const nCount = MAX_MAP_TOPICS + 20;
      const denseTopics = Array.from({ length: nCount }, (_, i) => ({
        id: `d-${i}`,
        name: `Dense ${i}`,
        usageCount: i + 1,
      }));
      // Root connects to 10 hubs weakly (1) and strongly (2) to one chain covering > 60%
      const edges = [
        ...Array.from({ length: nCount - 1 }, (_, i) => ({
          sourceId: 'd-0',
          targetId: `d-${i + 1}`,
          sharedNotes: i < 5 ? 2 : 1,
        })),
      ];
      const infos: unknown[] = [];
      component.scopeInfo.subscribe((info) => infos.push(info));

      fixture.componentRef.setInput('rootId', 'd-0');
      fixture.componentRef.setInput('depth', 2);
      setTopics(denseTopics);
      flushGraph({
        nodes: denseTopics.map((t) => ({ id: t.id, name: t.name, usageCount: t.usageCount })),
        edges,
      });

      expect((infos.at(-1) as { effectiveMin: number }).effectiveMin).toBe(2);
      // With min=2 only d-0 and d-1..d-5 remain
      expect(component.accessibleNodes()).toHaveLength(6);
    });

    it('preserves surviving node coordinates when depth increases on the same root', () => {
      const deepTopics = [
        { id: 'r', name: 'Root', usageCount: 10 },
        { id: 'a', name: 'Hop1', usageCount: 5 },
        { id: 'b', name: 'Hop2', usageCount: 3 },
      ];
      fixture.componentRef.setInput('rootId', 'r');
      fixture.componentRef.setInput('depth', 1);
      setTopics(deepTopics);
      flushGraph({
        nodes: deepTopics,
        edges: [
          { sourceId: 'r', targetId: 'a', sharedNotes: 2 },
          { sourceId: 'a', targetId: 'b', sharedNotes: 2 },
        ],
      });

      const graph1 = (globalThis as {
        __nostosGraph?: { setNodeAttribute: (n: string, a: string, v: unknown) => void; getNodeAttributes: (n: string) => Record<string, unknown> };
      }).__nostosGraph!;
      graph1.setNodeAttribute('a', 'x', 240);
      graph1.setNodeAttribute('a', 'y', -120);

      fixture.componentRef.setInput('depth', 2);
      fixture.detectChanges();

      const graph2 = (globalThis as {
        __nostosGraph?: { getNodeAttributes: (n: string) => Record<string, unknown> };
      }).__nostosGraph!;
      expect(graph2.getNodeAttributes('r')['x']).toBe(0);
      expect(graph2.getNodeAttributes('r')['y']).toBe(0);
      // Since MockSimulation ticks only decay alpha without altering coords, preserved coordinate stays exact.
      expect(graph2.getNodeAttributes('a')['x']).toBe(240);
      expect(graph2.getNodeAttributes('a')['y']).toBe(-120);
    });

    it('renders "Map around this" next to Open/Read notes on the card and emits mapAround', () => {
      setTopics(topics);
      flushGraph();
      component.selectAccessibleNode('beta');
      fixture.detectChanges();

      const emitted = vi.fn();
      component.mapAround.subscribe(emitted);
      const aroundBtn = fixture.nativeElement.querySelector('.map-card-around') as HTMLButtonElement;
      expect(aroundBtn).toBeTruthy();
      expect(aroundBtn.textContent).toContain('Map around this');

      aroundBtn.click();
      expect(emitted).toHaveBeenCalledWith('beta');

      // Hides "Map around this" when the selected node is already the local-map root
      fixture.componentRef.setInput('rootId', 'beta');
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.map-card-around')).toBeNull();
    });
  });

  describe('topic kind colour mode & interactive legend', () => {
    const kinds: TopicKindDto[] = [
      { id: 'k1', name: 'Concepts', colorKey: 'kind-1', sortOrder: 1, createdAt: '', topicCount: 1 },
      { id: 'k2', name: 'People', colorKey: 'kind-2', sortOrder: 2, createdAt: '', topicCount: 1 },
    ];

    const kindTopics: TopicDto[] = [
      { id: 'alpha', name: 'Alpha', usageCount: 12, kindId: 'k1' },
      { id: 'beta', name: 'Beta', usageCount: 5, kindId: 'k2' },
      { id: 'gamma', name: 'Gamma', usageCount: 2, kindId: null },
    ];

    const kindGraph: TopicGraphDto = {
      nodes: kindTopics.map((t) => ({ id: t.id, name: t.name, usageCount: t.usageCount, kindId: t.kindId ?? null })),
      edges: [
        { sourceId: 'alpha', targetId: 'beta', sharedNotes: 3 },
        { sourceId: 'beta', targetId: 'gamma', sharedNotes: 2 },
      ],
    };

    it('renders the interactive kind legend in kind mode with counts, No kind (n), and Sort them link', () => {
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('topicKinds', kinds);
      setTopics(kindTopics);
      flushGraph(kindGraph);

      const legendButtons = Array.from(
        fixture.nativeElement.querySelectorAll('.map-legend-kind')
      ) as HTMLButtonElement[];
      expect(legendButtons).toHaveLength(3);
      expect(legendButtons.map((b) => b.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
        'Concepts (1)',
        'People (1)',
        'No kind (1)',
      ]);
      expect(legendButtons.map((b) => b.getAttribute('aria-pressed'))).toEqual(['true', 'true', 'true']);

      const sortLink = fixture.nativeElement.querySelector('.map-legend-sort') as HTMLAnchorElement;
      expect(sortLink).toBeTruthy();
      expect(sortLink.textContent?.trim()).toBe('Sort them');
      expect(sortLink.getAttribute('href')).toBe('/brain/kinds?filter=untyped');

      const toggled = vi.fn();
      component.kindToggled.subscribe(toggled);
      legendButtons[1].click();
      expect(toggled).toHaveBeenCalledWith('k2');

      // A hidden kind stays in the legend (so it can be shown again) but reads as off.
      fixture.componentRef.setInput('visibleKindKeys', new Set(['k1', 'none']));
      fixture.detectChanges();
      const afterHide = Array.from(
        fixture.nativeElement.querySelectorAll('.map-legend-kind')
      ) as HTMLButtonElement[];
      expect(afterHide[1].getAttribute('aria-pressed')).toBe('false');
      expect(afterHide[1].textContent?.replace(/\s+/g, ' ').trim()).toBe('People (1)');

      // Cluster and None modes have no kind legend to filter by.
      fixture.componentRef.setInput('colorMode', 'cluster');
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.map-legend-kinds')).toBeNull();
    });

    it('applies the kind filter before the 150-topic cap and removes hidden edges on both maps', () => {
      const bulkHidden: TopicDto[] = Array.from({ length: MAX_MAP_TOPICS + 10 }, (_, i) => ({
        id: `big-${i}`,
        name: `Big ${i}`,
        usageCount: 1000 + i,
        kindId: 'k1',
      }));
      const kept: TopicDto[] = [
        { id: 'small-1', name: 'Small 1', usageCount: 2, kindId: 'k2' },
        { id: 'small-2', name: 'Small 2', usageCount: 1, kindId: 'k2' },
      ];
      const all = [...bulkHidden, ...kept];
      const graph: TopicGraphDto = {
        nodes: all.map((t) => ({ id: t.id, name: t.name, usageCount: t.usageCount, kindId: t.kindId ?? null })),
        edges: [
          { sourceId: 'small-1', targetId: 'small-2', sharedNotes: 2 },
          { sourceId: 'small-1', targetId: 'big-0', sharedNotes: 3 },
        ],
      };

      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('topicKinds', kinds);
      fixture.componentRef.setInput('visibleKindKeys', new Set(['k2']));
      setTopics(all);
      flushGraph(graph);

      // Even though Big-* outrank Small-* by usageCount, filtering before the
      // cap keeps the visible kind's topics.
      expect(component.accessibleNodes().map((n) => n.id).sort()).toEqual(['small-1', 'small-2']);
      expect(component.hiddenByKindCount()).toBe(MAX_MAP_TOPICS + 10);
      // The edge to the hidden topic is gone, not merely invisible.
      expect(component.accessibleNodes().find((n) => n.id === 'small-1')?.connectionCount).toBe(1);

      // The same pre-cap behaviour on a local map. big-0 was a depth-1
      // neighbour before filtering.
      fixture.componentRef.setInput('rootId', 'small-1');
      fixture.componentRef.setInput('depth', 1);
      fixture.detectChanges();
      expect(component.accessibleNodes().map((n) => n.id).sort()).toEqual(['small-1', 'small-2']);
      expect(component.hiddenByKindCount()).toBe(1);
    });

    it('shows an empty state with Show all kinds when every kind is hidden', () => {
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('topicKinds', kinds);
      fixture.componentRef.setInput('visibleKindKeys', new Set<string>());
      setTopics(kindTopics);
      flushGraph(kindGraph);

      expect(component.allHiddenByKind()).toBe(true);
      expect(fixture.nativeElement.textContent).toContain('All topics are hidden by the kind filter.');

      const cleared = vi.fn();
      component.clearKindFilter.subscribe(cleared);
      const resetBtn = fixture.nativeElement.querySelector('.map-status button') as HTMLButtonElement;
      expect(resetBtn.textContent?.trim()).toBe('Show all kinds');
      resetBtn.click();
      expect(cleared).toHaveBeenCalledTimes(1);
    });

    it('recolours in place when kinds arrive during the mount camera animation', () => {
      document.documentElement.style.setProperty('--map-kind-1', '#3b73a8');
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('centerOnLoad', true);
      fixture.componentRef.setInput('selectedId', 'alpha');
      setTopics(kindTopics);
      flushGraph(kindGraph);

      const globals = globalThis as unknown as {
        __camera: { animate: ReturnType<typeof vi.fn>; setState: ReturnType<typeof vi.fn>; isAnimating: ReturnType<typeof vi.fn> };
        __nostosSigma: unknown;
        __nostosGraph: { getNodeAttributes: (node: string) => Record<string, unknown> };
      };
      const sigmaBefore = globals.__nostosSigma;
      const graphBefore = globals.__nostosGraph;
      const animateCalls = globals.__camera.animate.mock.calls.length;
      const focusTarget = globals.__camera.animate.mock.calls.at(-1)?.[0];
      globals.__camera.setState.mockClear();
      globals.__camera.isAnimating.mockReturnValue(true); // the 350 ms mount animation

      fixture.componentRef.setInput('topicKinds', kinds);
      fixture.detectChanges();

      // No disposal, no re-frame, no carried camera: only the colour changed,
      // and the map stays at the fit/focus target it had.
      expect(globals.__nostosSigma).toBe(sigmaBefore);
      expect(globals.__nostosGraph).toBe(graphBefore);
      expect(globals.__camera.setState).not.toHaveBeenCalled();
      expect(globals.__camera.animate).toHaveBeenCalledTimes(animateCalls);
      expect(globals.__camera.animate.mock.calls.at(-1)?.[0]).toEqual(focusTarget);
      expect(graphBefore.getNodeAttributes('alpha')['color']).toBe('#3b73a8');

      globals.__camera.isAnimating.mockReturnValue(false);
      document.documentElement.style.removeProperty('--map-kind-1');
    });

    it('hides the kind legend and paints Cluster while the kind list is unavailable', () => {
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('kindsLoaded', false);
      fixture.componentRef.setInput('topicKinds', []);
      setTopics(kindTopics);
      flushGraph(kindGraph);

      expect(fixture.nativeElement.querySelector('.map-legend-kinds')).toBeNull();
      expect(fixture.nativeElement.textContent).not.toContain('No kind (');
      expect(component.kindLegend()).toEqual([]);
    });

    it('offers Show all kinds in every colour mode when a kind filter is active', () => {
      fixture.componentRef.setInput('colorMode', 'cluster');
      fixture.componentRef.setInput('topicKinds', kinds);
      fixture.componentRef.setInput('visibleKindKeys', new Set(['k1']));
      setTopics(kindTopics);
      flushGraph(kindGraph);

      expect(component.hiddenByKindCount()).toBe(2);
      const clear = fixture.nativeElement.querySelector('.map-legend-clear') as HTMLButtonElement;
      expect(clear?.textContent?.trim()).toBe('Show all kinds');
      const cleared = vi.fn();
      component.clearKindFilter.subscribe(cleared);
      clear.click();
      expect(cleared).toHaveBeenCalledTimes(1);

      // None mode keeps the escape hatch too.
      fixture.componentRef.setInput('colorMode', 'none');
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.map-legend-clear')).toBeTruthy();
    });

    it('counts drawn topics for a visible kind and in-scope topics for a hidden one', () => {
      const high = Array.from({ length: MAX_MAP_TOPICS }, (_, i) => ({
        id: `high-${i}`,
        name: `High ${i}`,
        usageCount: 1000 + i,
        kindId: 'k1',
      }));
      const hiddenKinds = [
        { id: 'h1', name: 'Hidden One', usageCount: 2, kindId: 'k2' },
        { id: 'h2', name: 'Hidden Two', usageCount: 1, kindId: 'k2' },
      ];
      const all = [...high, ...hiddenKinds];

      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('topicKinds', kinds);
      fixture.componentRef.setInput('visibleKindKeys', new Set(['k1']));
      setTopics(all);
      flushGraph({
        nodes: all.map((t) => ({ id: t.id, name: t.name, usageCount: t.usageCount, kindId: t.kindId })),
        edges: [],
      });

      const buttons = Array.from(
        fixture.nativeElement.querySelectorAll('.map-legend-kind')
      ) as HTMLButtonElement[];
      expect(buttons.map((b) => b.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
        'Concepts (150)',
        'People (2)',
      ]);
      expect(buttons[0].getAttribute('aria-label')).toContain('drawn on the map');
      expect(buttons[1].getAttribute('aria-label')).toContain('in scope, hidden');
    });

    it('renders the selected topic kind pill non-interactively, and requests the picker only when enabled', () => {
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('topicKinds', kinds);
      setTopics(kindTopics);
      flushGraph(kindGraph);

      component.selectAccessibleNode('alpha');
      fixture.detectChanges();
      const staticPill = fixture.nativeElement.querySelector('.map-card-kind-pill') as HTMLElement;
      expect(staticPill.tagName).toBe('SPAN');
      expect(staticPill.textContent?.trim()).toBe('Concepts');

      component.selectAccessibleNode('gamma');
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.map-card-kind-pill')?.textContent?.trim()).toBe('No kind');

      fixture.componentRef.setInput('kindPickerEnabled', true);
      fixture.detectChanges();
      const buttonPill = fixture.nativeElement.querySelector('.map-card-kind-pill') as HTMLButtonElement;
      expect(buttonPill.tagName).toBe('BUTTON');
      const requested = vi.fn();
      component.kindPickerRequested.subscribe(requested);
      buttonPill.click();
      // The pill travels with the request: the page hangs the panel from the
      // element that was clicked, not from `document.activeElement`.
      expect(requested).toHaveBeenCalledWith({
        topicId: 'gamma',
        kindId: null,
        origin: buttonPill,
      });
    });

    it('shows a kind set on a topic in place, without reading the graph back or rebuilding Sigma', () => {
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('topicKinds', kinds);
      setTopics(kindTopics);
      flushGraph(kindGraph);

      const globals = globalThis as unknown as {
        __nostosSigma: unknown;
        __nostosGraph: { getNodeAttributes: (node: string) => Record<string, unknown> };
      };
      const sigmaBefore = globals.__nostosSigma;
      const graph = globals.__nostosGraph;
      component.selectAccessibleNode('gamma');
      fixture.detectChanges();

      // The card's kind pill writes the topic; the page's list reloads after it.
      setTopics(kindTopics.map((topic) => (topic.id === 'gamma' ? { ...topic, kindId: 'k1' } : topic)));
      http.expectNone('/api/topics/graph');

      // Same Sigma, same graph object: only the node's metadata moved.
      expect(globals.__nostosSigma).toBe(sigmaBefore);
      expect(graph.getNodeAttributes('gamma')['kindId']).toBe('k1');
      expect(component.selection()?.kindName).toBe('Concepts');
      expect(component.kindLegend().map((entry) => `${entry.label} (${entry.count})`)).toEqual([
        'Concepts (2)',
        'People (1)',
      ]);
    });

    it('rebuilds from the graph in hand when a kind change crosses the filter', () => {
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('topicKinds', kinds);
      fixture.componentRef.setInput('visibleKindKeys', new Set(['k1', 'k2']));
      setTopics(kindTopics);
      flushGraph(kindGraph);
      expect(component.accessibleNodes().map((node) => node.id).sort()).toEqual(['alpha', 'beta']);

      const globals = globalThis as unknown as { __nostosSigma: unknown };
      const sigmaBefore = globals.__nostosSigma;
      // gamma had no kind and was hidden; now it is a Concept and must appear.
      setTopics(kindTopics.map((topic) => (topic.id === 'gamma' ? { ...topic, kindId: 'k1' } : topic)));
      http.expectNone('/api/topics/graph');

      expect(globals.__nostosSigma).not.toBe(sigmaBefore);
      expect(component.accessibleNodes().map((node) => node.id).sort()).toEqual([
        'alpha',
        'beta',
        'gamma',
      ]);
      expect(component.hiddenByKindCount()).toBe(0);
    });

    it('computes the legend when every topic is hidden and the kinds arrive after the build', () => {
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('visibleKindKeys', new Set<string>());
      setTopics(kindTopics);
      flushGraph(kindGraph);
      expect(component.shownCount()).toBe(0);
      // Without the kind list every typed topic counts as untyped.
      expect(component.kindLegend().map((entry) => entry.label)).toEqual(['No kind']);

      fixture.componentRef.setInput('topicKinds', kinds);
      fixture.detectChanges();

      // No Sigma exists (nothing is drawn), but the legend is what brings the
      // topics back.
      expect(component.kindLegend().map((entry) => entry.label)).toEqual([
        'Concepts',
        'People',
        'No kind',
      ]);
    });

    it('stops dimming the map when the selected topic is hidden by the kind filter', () => {
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('topicKinds', kinds);
      fixture.componentRef.setInput('visibleKindKeys', new Set(['k1']));
      fixture.componentRef.setInput('selectedId', 'beta'); // a Person, now hidden
      setTopics(kindTopics);
      flushGraph(kindGraph);

      expect(component.selection()).toBeNull();
      const globals = globalThis as unknown as {
        __nostosSigma: { nodeReducer: (node: string, data: unknown, attrs: unknown) => unknown };
        __nostosGraph: { getNodeAttributes: (node: string) => Record<string, unknown> };
      };
      // A selection the graph no longer holds must not dim everything else.
      expect(globals.__nostosSigma.nodeReducer('alpha', {}, globals.__nostosGraph.getNodeAttributes('alpha')))
        .toEqual({});
      expect(
        (fixture.nativeElement.querySelector('[aria-label="Center on selection"]') as HTMLButtonElement)
          .disabled
      ).toBe(true);
    });

    it('says nothing about the kind when the kind list never arrived', () => {
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('topicKinds', []);
      fixture.componentRef.setInput('kindsLoaded', false);
      fixture.componentRef.setInput('kindPickerEnabled', true);
      setTopics(kindTopics);
      flushGraph(kindGraph);

      component.selectAccessibleNode('alpha'); // alpha HAS a kind; it just cannot be named
      fixture.detectChanges();

      expect(component.selection()?.kindId).toBeNull();
      expect(fixture.nativeElement.querySelector('.map-card-kind-pill')).toBeNull();
    });

    it('leaves one compact line on the map when kinds are hidden', () => {
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('topicKinds', kinds);
      fixture.componentRef.setInput('visibleKindKeys', new Set(['k1']));
      setTopics(kindTopics);
      flushGraph(kindGraph);

      const compact = fixture.nativeElement.querySelector(
        '.map-legend-compact'
      ) as HTMLButtonElement;
      expect(compact).toBeTruthy();
      expect(compact.textContent?.replace(/\s+/g, ' ').trim()).toBe(
        '2 hidden by kind · Show all'
      );
      expect(compact.getAttribute('aria-label')).toBe('Show all kinds');

      const cleared = vi.fn();
      component.clearKindFilter.subscribe(cleared);
      compact.click();
      expect(cleared).toHaveBeenCalledTimes(1);

      // The full rows stay in the markup for the wide legend (and the sheet).
      expect(fixture.nativeElement.querySelectorAll('.map-legend-kind')).toHaveLength(3);
    });

    it('shows no compact line while every kind is visible', () => {
      fixture.componentRef.setInput('colorMode', 'kind');
      fixture.componentRef.setInput('topicKinds', kinds);
      setTopics(kindTopics);
      flushGraph(kindGraph);

      expect(component.hiddenByKindCount()).toBe(0);
      expect(fixture.nativeElement.querySelector('.map-legend-compact')).toBeNull();
    });
  });

  describe('phone legend placement', () => {
    it('gives the desktop legend a solid surface and keeps the chip rows off a phone map', () => {
      const css = readFileSync('src/app/brain/topic-map/topic-map.component.css', 'utf8');
      const legendRule = css.match(/\.map-legend \{([\s\S]*?)\}/)?.[1] ?? '';
      // Opaque: an edge must not run through the legend's words at 1280px.
      expect(legendRule).toContain('background: var(--bg-surface)');
      expect(legendRule).not.toMatch(/color-mix|transparent/);
      expect(legendRule).toContain('border: 1px solid var(--glass-border)');

      // The compact line is the phone's only map legend; the rows move into the
      // Controls sheet (the breakpoint the HUD and the sheet already use).
      expect(css).toMatch(/\.map-legend-compact \{\s*display: none;/);
      expect(css).toMatch(
        /@media \(max-width: 768px\)[\s\S]*\.map-legend-full \{\s*display: none;/
      );
      expect(css).toMatch(
        /@media \(max-width: 768px\)[\s\S]*\.map-legend-compact \{\s*display: inline-flex;/
      );
    });
  });

  describe('legend legibility', () => {
    it('keeps a hidden kind label at full text contrast and dims only the swatch', () => {
      const css = readFileSync('src/app/brain/topic-map/map-kind-legend.component.css', 'utf8');
      const hiddenRule = css.match(
        /\.map-legend-kind\.nostos-chip\.is-hidden,[\s\S]*?\{([\s\S]*?)\}/
      );
      expect(hiddenRule).toBeTruthy();
      const body = hiddenRule![1];

      // At 0.52 opacity the label measured 2.38:1 (light) and 3.48:1 (dark).
      expect(body).not.toMatch(/opacity\s*:/);
      expect(body).toMatch(/color:\s*var\(--color-text-muted\)/);
      expect(css).toMatch(/\.map-legend-kind\.is-hidden \.map-legend-swatch[\s\S]*?\{[^}]*opacity/);
    });
  });
});
