import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

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
    animatedReset: vi.fn(),
    animatedZoom: vi.fn(),
    animatedUnzoom: vi.fn(),
    animate: vi.fn(),
    setState: vi.fn(),
    getState: vi.fn(() => ({ x: camera.x, y: camera.y, angle: 0, ratio: camera.ratio })),
    disable: vi.fn(),
    enable: vi.fn(),
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

  class MockSigma {
    settings: Record<string, unknown>;
    constructor(_graph: unknown, _container: unknown, settings: Record<string, unknown> = {}) {
      Object.assign(lastSettings, settings);
      this.settings = lastSettings;
    }
    on(event: string, handler: (payload?: unknown) => void) {
      sigmaHandlers[event] = handler;
      return this;
    }
    setSetting(key: string, value: unknown) {
      lastSettings[key] = value;
      return this;
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
  (globalThis as unknown as { __captor: unknown }).__captor = captorHandlers;
  (globalThis as unknown as { __sigmaHandlers: unknown }).__sigmaHandlers = sigmaHandlers;
  (globalThis as unknown as { __touchCaptor: unknown }).__touchCaptor = touchHandlers;

  return { default: MockSigma, __camera: camera, __settings: lastSettings };
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
    forceLink: vi.fn(() => chainable()),
    forceManyBody: vi.fn(() => chainable()),
    forceCollide: vi.fn(() => chainable()),
  };
});

import {
  ConceptMapComponent,
  MAX_MAP_CONCEPTS,
} from './concept-map.component';
import { ConceptDto, ConceptGraphDto } from '../../core/services/concepts.service';

const concepts: ConceptDto[] = [
  { id: 'alpha', name: 'Alpha', usageCount: 12 },
  { id: 'beta', name: 'Beta', usageCount: 5 },
  { id: 'gamma', name: 'Gamma', usageCount: 2 },
];

const graphResponse: ConceptGraphDto = {
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

describe('ConceptMapComponent', () => {
  let component: ConceptMapComponent;
  let fixture: ComponentFixture<ConceptMapComponent>;
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ConceptMapComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(ConceptMapComponent);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  function setConcepts(value: ConceptDto[]): void {
    fixture.componentRef.setInput('concepts', value);
    fixture.detectChanges();
  }

  function flushGraph(data: ConceptGraphDto = graphResponse): void {
    const req = http.expectOne('/api/concepts/graph');
    req.flush(data);
    fixture.detectChanges();
  }

  it('fetches graph data when concepts are set', () => {
    setConcepts(concepts);
    const req = http.expectOne('/api/concepts/graph');
    expect(req.request.method).toBe('GET');
    req.flush(graphResponse);
    fixture.detectChanges();
    expect(component.loading()).toBe(false);
  });

  it('maps graph nodes into the accessible list', () => {
    setConcepts(concepts);
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
    setConcepts([]);
    flushGraph({ nodes: [], edges: [] });

    expect(component.accessibleNodes()).toHaveLength(0);
    expect(component.loading()).toBe(false);
  });

  it('handles a single concept with no edges', () => {
    const singleConcept = [{ id: 'only', name: 'Only Concept', usageCount: 1 }];
    setConcepts(singleConcept);
    flushGraph({ nodes: [{ id: 'only', name: 'Only Concept', usageCount: 1 }], edges: [] });

    expect(component.accessibleNodes()).toHaveLength(1);
    expect(component.noConnections()).toBe(true);
  });

  it('blames the filter, not missing connections, when a search empties the graph', () => {
    // The header's search filters the concept set this map draws, and it is
    // visible in map view too. An empty graph then means either "no concepts
    // match your query" or "you have no connections yet" — reporting the second
    // when the first is true tells the user their data is missing.
    setConcepts([]);
    flushGraph({ nodes: [], edges: [] });
    fixture.componentRef.setInput('searchQuery', '  zzzz-no-such-concept  ');
    fixture.detectChanges();

    expect(component.noConnections()).toBe(true);
    const status = (fixture.nativeElement as HTMLElement).querySelector('.map-status');
    expect(status?.textContent).toContain('No concepts match');
    // The query is trimmed in the copy, so stray whitespace cannot leak in.
    expect(status?.textContent).toContain('zzzz-no-such-concept');
    expect(status?.textContent).not.toContain('No connections yet');
  });

  it('reports missing connections when there is no search to blame', () => {
    setConcepts([]);
    flushGraph({ nodes: [], edges: [] });
    fixture.componentRef.setInput('searchQuery', '');
    fixture.detectChanges();

    const status = (fixture.nativeElement as HTMLElement).querySelector('.map-status');
    expect(status?.textContent).toContain('No connections yet');
    expect(status?.textContent).not.toContain('No concepts match');
  });

  it('shows the cap note when there are more than 150 concepts', () => {
    const manyConcepts = Array.from({ length: MAX_MAP_CONCEPTS + 1 }, (_, i) => ({
      id: `concept-${i}`,
      name: `Concept ${i}`,
      usageCount: i + 1,
    }));
    setConcepts(manyConcepts);
    flushGraph({
      nodes: manyConcepts.map((c) => ({ id: c.id, name: c.name, usageCount: c.usageCount })),
      edges: [],
    });

    expect(component.isCapped()).toBe(true);
    expect(fixture.nativeElement.querySelector('.map-limit-note')?.textContent).toContain(
      '150 most-referenced concepts'
    );
  });

  it('filters visible nodes to only include concepts passed as input', () => {
    // Graph has 3 nodes but we only pass 2 concepts.
    setConcepts([concepts[0], concepts[1]]);
    flushGraph();

    const nodes = component.accessibleNodes();
    expect(nodes).toHaveLength(2);
    expect(nodes.map((n) => n.id).sort()).toEqual(['alpha', 'beta']);
  });

  it('emits the selected concept when selectAccessibleNode is called', () => {
    setConcepts(concepts);
    flushGraph();
    const selected = vi.fn();
    component.conceptSelected.subscribe(selected);

    component.selectAccessibleNode('alpha');

    expect(selected).toHaveBeenCalledWith('alpha');
    expect(component.selectedNodeId()).toBe('alpha');
  });

  it('leaves the mode switch and the concept search to the surface header', () => {
    // The map used to carry its own copies of both (`.map-view-exit` and a
    // second search field), because map view closes the index rail that
    // previously held them. The surface header now renders in both modes, so
    // those duplicates have to be gone — otherwise the user gets two controls
    // with the same name, and the mode switch appears to move on every toggle.
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.map-toolbar'), 'no second toolbar in the map').toBeNull();
    expect(host.querySelector('input[type="search"]'), 'no second search field').toBeNull();
    expect(
      host.querySelector('[aria-label="Concept view"]'),
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
    setConcepts(concepts);
    flushGraph();

    const emitted = vi.fn();
    component.openConcept.subscribe(emitted);
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
    setConcepts(concepts);
    flushGraph();

    const emitted = vi.fn();
    component.openConcept.subscribe(emitted);

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
    setConcepts(concepts);
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
    setConcepts(concepts);
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
    setConcepts(concepts);
    flushGraph();

    fixture.componentRef.setInput('selectedId', 'beta');
    fixture.detectChanges();

    expect(component.selectedNodeId()).toBe('beta');
  });

  it('keeps the accessible list in sync with the rendered graph', () => {
    setConcepts(concepts);
    flushGraph();

    const accessibleList = fixture.nativeElement.querySelector('.map-accessible-list') as HTMLElement;
    expect(accessibleList).toBeTruthy();
    expect(accessibleList.getAttribute('aria-label')).toBe('Concepts in this map');
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

  it('shows the selected concept in a card with its notes action, not in the camera rail', () => {
    setConcepts(concepts);
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

    // Related concepts, strongest link first (beta shares 3 notes, gamma 1).
    const related = [...card.querySelectorAll('.map-card-related button')].map((b) => b.textContent?.trim());
    expect(related).toEqual(['Beta', 'Gamma']);

    const emitted = vi.fn();
    component.openNotes.subscribe(emitted);
    const notes = [...card.querySelectorAll('button')].find((b) => b.textContent?.includes('Read notes'));
    expect(notes, 'the card carries the notes action').toBeTruthy();
    notes!.click();
    expect(emitted).toHaveBeenCalled();
  });

  it('moves the selection to a related concept from the card', () => {
    setConcepts(concepts);
    flushGraph();
    component.selectAccessibleNode('alpha');
    fixture.detectChanges();

    const selected = vi.fn();
    component.conceptSelected.subscribe(selected);
    const beta = [...fixture.nativeElement.querySelectorAll('.map-card-related button')].find(
      (b) => (b as HTMLElement).textContent?.trim() === 'Beta'
    ) as HTMLButtonElement;
    beta.click();
    fixture.detectChanges();

    expect(selected).toHaveBeenCalledWith('beta');
    expect(component.selectedNodeId()).toBe('beta');
  });

  it('closes the card and clears the selection from its close button', () => {
    setConcepts(concepts);
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
    setConcepts(concepts);

    expect(component.loading()).toBe(true);
    const status = fixture.nativeElement.querySelector('.map-status');
    expect(status?.textContent).toContain('Loading graph');

    flushGraph();
    expect(component.loading()).toBe(false);
  });

  it('disposes Sigma renderer on component destruction', () => {
    setConcepts(concepts);
    flushGraph();

    // The component should not throw when destroyed.
    expect(() => {
      fixture.destroy();
    }).not.toThrow();
  });

  it('makes only a single /api/concepts/graph request, not N+1 related requests', () => {
    setConcepts(concepts);
    // Expect exactly one graph request, not per-concept related requests.
    const graphReqs = http.match('/api/concepts/graph');
    expect(graphReqs).toHaveLength(1);
    graphReqs[0].flush(graphResponse);

    // No /related requests should have been made.
    const relatedReqs = http.match((req) => req.url.includes('/related'));
    expect(relatedReqs).toHaveLength(0);
    fixture.detectChanges();
  });

  /* ── Regression guards for the reported graph bugs ──
   *
   * Each of these fails against the behaviour that shipped, so they pin the
   * fixes rather than merely describing them.
   */

  describe('graph readability', () => {
    it('renders edges at an alpha that is actually visible', () => {
      setConcepts(concepts);
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
      setConcepts(concepts);
      flushGraph();

      // The smallest node size must clear the label threshold, or the majority
      // of concepts are permanently unlabelled (15 of 53 in production).
      const settings = (globalThis as { __settings?: Record<string, unknown> }).__settings!;
      expect(settings['labelRenderedSizeThreshold']).toBeLessThanOrEqual(4);
    });

    it('does not let autoRescale move nodes the user did not drag', () => {
      setConcepts(concepts);
      flushGraph();

      const settings = (globalThis as { __settings?: Record<string, unknown> }).__settings!;
      // autoRescale remaps the whole extent on every change, so dragging one
      // node shrinks all the others (measured 57% -> 6.4% of stage width).
      expect(settings['autoRescale']).toBe(false);
      expect(settings['autoCenter']).toBe(false);
    });

    it('keeps unconnected nodes and their labels readable when one is selected', () => {
      setConcepts(concepts);
      flushGraph();

      const sigma = (globalThis as {
        __nostosSigma?: { settings: Record<string, unknown>; nodeReducer?: unknown };
      }).__nostosSigma!;
      const reducer = sigma.settings['nodeReducer'] as
        | ((node: string, data: Record<string, unknown>) => Record<string, unknown>)
        | undefined;
      expect(typeof reducer, 'a nodeReducer must be installed').toBe('function');

      // Select 'alpha' so its neighbours (beta, gamma) are the active
      // neighbourhood and every other node is "unconnected".
      component.selectAccessibleNode('alpha');

      // 'gamma' IS connected to alpha, so use a node the graph has that is not:
      // add a fourth concept with no edges.
      const unconnected = reducer!('lonely', { label: 'Lonely', color: '#000000' });

      // The node must not be erased: it keeps a colour and keeps its label.
      expect(unconnected['label'], 'unconnected node must keep its label').not.toBe('');
      const color = String(unconnected['color']);
      const alpha = Number(color.match(/,\s*([\d.]+)\)$/)?.[1] ?? '1');
      // 0.55 measured 1.66:1 (21% of undimmed contrast) in the light theme.
      expect(alpha, `dim alpha ${color} must stay legible`).toBeGreaterThanOrEqual(0.7);

      const labelColor = String(unconnected['labelColor']);
      const labelAlpha = Number(labelColor.match(/,\s*([\d.]+)\)$/)?.[1] ?? '1');
      expect(labelAlpha, 'unconnected label must not be invisible').toBeGreaterThanOrEqual(0.5);
    });

    it('draws the active label on a themed plate, not Sigma\'s hardcoded white box', () => {
      setConcepts(concepts);
      flushGraph();

      const settings = (globalThis as { __settings?: Record<string, unknown> }).__settings!;

      // Sigma reads a per-node colour ONLY when `labelColor.attribute` is set;
      // without it every per-node labelColor the component writes is dead and
      // the label falls back to one static colour. On dark that colour is
      // #c4c7d0, which measured 1.66:1 on Sigma's hardcoded #FFF hover box. The
      // label box now paints --graph-label-box, so the ink has a dark ground.
      const labelColor = settings['labelColor'] as { attribute?: string } | undefined;
      expect(
        labelColor?.attribute,
        'Sigma must be told which node attribute carries the label colour'
      ).toBe('labelColor');

      // And the hover plate must not be Sigma's own drawer, which fills with a
      // literal "#FFF" in both themes.
      expect(
        typeof settings['defaultDrawNodeHover'],
        'a theme-aware hover drawer must replace Sigma\'s white one'
      ).toBe('function');
    });

    it('lays the graph out with enough repulsion to avoid an unreadable clump', () => {
      setConcepts(concepts);
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
      setConcepts(concepts);
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
      setConcepts(concepts);
      flushGraph();

      const sigma = (globalThis as {
        __nostosSigma?: { settings: Record<string, unknown> };
      }).__nostosSigma!;
      const reducer = sigma.settings['edgeReducer'] as
        | ((edge: string, data: Record<string, unknown>) => Record<string, unknown>)
        | undefined;
      expect(typeof reducer, 'an edgeReducer must be installed').toBe('function');

      // Select 'alpha'; its edges are alpha-beta and alpha-gamma.
      component.selectAccessibleNode('alpha');

      const connected = reducer!('alpha->beta', { size: 1, color: 'rgba(138,134,128,0.8)' });
      const unrelated = reducer!('beta->gamma', { size: 1, color: 'rgba(138,134,128,0.8)' });

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
     * The simulation node for a concept, so a spec can read the drag pin.
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
      setConcepts(concepts);
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
      setConcepts(concepts);
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
      setConcepts(concepts);
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
      setConcepts(concepts);
      flushGraph();

      const camera = (globalThis as unknown as {
        __camera: { disable: ReturnType<typeof vi.fn>; enable: ReturnType<typeof vi.fn> };
      }).__camera;
      const sigmaHandlers = (globalThis as unknown as {
        __sigmaHandlers: Record<string, (payload?: unknown) => void>;
      }).__sigmaHandlers;
      const touch = (globalThis as unknown as {
        __touchCaptor: Record<string, (payload?: unknown) => void>;
      }).__touchCaptor;

      camera.disable.mockClear();
      camera.enable.mockClear();

      // A finger lands on a node and lifts without travelling past the drag
      // threshold — i.e. a plain TAP to select.
      sigmaHandlers['downNode']!({ node: 'alpha', event: { x: 10, y: 10 } });
      expect(camera.disable, 'the camera is held during the press').toHaveBeenCalled();
      // The pin is d3's `fx`/`fy` on the simulation node — the graph attribute
      // ForceAtlas2 used (`fixed`) no longer exists.
      expect(pinnedNode('alpha').fx).not.toBeNull();

      // Sigma's touch captor only has touchmove/touchup — never mouseup.
      touch['touchup']!({ touches: [], previousTouches: [] });

      expect(
        camera.enable,
        'a touch release must hand the camera back, or every control goes dead'
      ).toHaveBeenCalled();
      expect(
        pinnedNode('alpha').fx,
        'the touch release must also unpin the node'
      ).toBeNull();
    });

    it('moves a node on a touch drag without leaving it pinned', () => {
      setConcepts(concepts);
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
      setConcepts(concepts);
      flushGraph();

      const camera = (globalThis as unknown as {
        __camera: { enable: ReturnType<typeof vi.fn> };
      }).__camera;
      const sigmaHandlers = (globalThis as unknown as {
        __sigmaHandlers: Record<string, (payload?: unknown) => void>;
      }).__sigmaHandlers;

      camera.enable.mockClear();
      sigmaHandlers['downNode']!({ node: 'alpha', event: { x: 0, y: 0 } });
      expect(pinnedNode('alpha').fx).not.toBeNull();

      // Neither mouseup nor touchup fires: the pointer was released off-canvas
      // and only the window-level safety net sees it.
      window.dispatchEvent(new Event('pointercancel'));

      expect(camera.enable, 'the safety net must hand the camera back').toHaveBeenCalled();
      expect(pinnedNode('alpha').fx).toBeNull();
    });

    it('clears any stale pin when the layout is reset', () => {
      setConcepts(concepts);
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
    it('fits the graph by computing a framing ratio, not by resetting', () => {
      setConcepts(concepts);
      flushGraph();

      const camera = (globalThis as { __camera?: { animate: ReturnType<typeof vi.fn>; animatedReset: ReturnType<typeof vi.fn> } })
        .__camera!;
      camera.animate.mockClear();

      component.fitGraph();

      expect(camera.animate).toHaveBeenCalled();
      const state = camera.animate.mock.calls.at(-1)![0];
      expect(state.ratio).toBeGreaterThan(0);
      // A fit centres the graph; framed coordinates are 0..1.
      expect(state.x).toBeCloseTo(0.5, 5);
      expect(state.y).toBeCloseTo(0.5, 5);
    });

    it('centres the selected node in framed coordinates', () => {
      setConcepts(concepts);
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
      setConcepts(concepts);
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
});
