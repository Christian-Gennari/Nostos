import Graph from 'graphology';
import type Sigma from 'sigma';

import { FIT_OCCUPANCY, LABEL_EDGE_ALLOWANCE_FRACTION } from './topic-map.helpers';
import { graphExtent, TopicMapViewport } from './topic-map-viewport';

function createMockSigma(overrides: Partial<Record<string, unknown>> = {}) {
  const camera = {
    x: 0.5,
    y: 0.5,
    angle: 0,
    ratio: 1,
    enabled: true,
    zoomIn: vi.fn(() => Promise.resolve()),
    zoomOut: vi.fn(() => Promise.resolve()),
    animate: vi.fn(() => Promise.resolve()),
    isAnimating: vi.fn(() => false),
    setState: vi.fn((next: Record<string, number>) => {
      Object.assign(camera, next);
    }),
    getState: vi.fn(() => ({ x: camera.x, y: camera.y, angle: camera.angle, ratio: camera.ratio })),
  };

  const sigma = {
    refresh: vi.fn(),
    getCamera: () => camera,
    getDimensions: vi.fn(() => ({ width: 800, height: 600 })),
    graphToViewport: vi.fn((point: { x: number; y: number }) => ({
      x: 400 + point.x,
      y: 300 - point.y,
    })),
    viewportToFramedGraph: vi.fn((point: { x: number; y: number }) => ({
      x: (point.x - 400) / 800,
      y: (300 - point.y) / 600,
    })),
    ...overrides,
  };

  return { sigma: sigma as unknown as Sigma, mock: sigma, camera };
}

function rect(left: number, top: number, width: number, height: number): DOMRect {
  return {
    left,
    top,
    right: left + width,
    bottom: top + height,
    width,
    height,
    x: left,
    y: top,
    toJSON: () => ({}),
  } as DOMRect;
}

describe('graphExtent', () => {
  it('returns null for null or empty graphs', () => {
    expect(graphExtent(null)).toBeNull();
    expect(graphExtent(new Graph())).toBeNull();
  });

  it('computes min/max coordinates across all nodes or a filtered subset', () => {
    const graph = new Graph();
    graph.addNode('a', { x: -10, y: 20 });
    graph.addNode('b', { x: 40, y: -5 });
    graph.addNode('c', { x: 200, y: 300 });

    expect(graphExtent(graph)).toEqual({ minX: -10, maxX: 200, minY: -5, maxY: 300 });
    expect(graphExtent(graph, new Set(['a', 'b']))).toEqual({
      minX: -10,
      maxX: 40,
      minY: -5,
      maxY: 20,
    });
    expect(graphExtent(graph, new Set(['missing']))).toBeNull();
  });
});

describe('TopicMapViewport', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('computes fit occupancy and overlay insets relative to the stage', () => {
    const viewport = new TopicMapViewport();
    expect(viewport.fitOccupancy()).toEqual({
      x: FIT_OCCUPANCY - LABEL_EDGE_ALLOWANCE_FRACTION,
      y: FIT_OCCUPANCY,
    });
    expect(viewport.overlayInsets(undefined)).toEqual({ top: 0, bottom: 0 });

    const container = document.createElement('div');
    const legend = document.createElement('div');
    const hud = document.createElement('div');
    container.getBoundingClientRect = () => rect(0, 100, 800, 600);
    legend.getBoundingClientRect = () => rect(12, 112, 200, 28); // bottom 140 => top inset 40
    hud.getBoundingClientRect = () => rect(12, 620, 776, 60); // top 620 vs stage bottom 700 => bottom inset 80

    expect(viewport.overlayInsets(container, { legend, hud })).toEqual({ top: 40, bottom: 80 });
  });

  it('frames the graph, centres selected nodes, and animates zoom controls', () => {
    const viewport = new TopicMapViewport();
    const graph = new Graph();
    graph.addNode('a', { x: -100, y: -50 });
    graph.addNode('b', { x: 100, y: 50 });
    const { sigma, camera } = createMockSigma();

    viewport.fitGraph(sigma, graph);
    expect(camera.animate).toHaveBeenCalledTimes(1);
    const [fitState, fitOpts] = camera.animate.mock.calls[0] as unknown as [
      { x: number; y: number; ratio: number },
      { duration: number },
    ];
    expect(fitState.ratio).toBeGreaterThan(0);
    expect(fitOpts).toEqual({ duration: 350 });

    camera.animate.mockClear();
    viewport.centerSelected(sigma, graph, 'a');
    expect(camera.animate).toHaveBeenCalledTimes(1);
    const [centerState] = camera.animate.mock.calls[0] as unknown as [
      { x: number; y: number; ratio: number },
    ];
    expect(centerState.x).toBeCloseTo(-100 / 800);
    expect(centerState.y).toBeCloseTo(-50 / 600);

    viewport.zoomIn(sigma);
    viewport.zoomOut(sigma);
    expect(camera.zoomIn).toHaveBeenCalledWith({ duration: 200 });
    expect(camera.zoomOut).toHaveBeenCalledWith({ duration: 200 });
  });

  it('returns null from graphPointToFramed / fitCameraState when dimensions are zero', () => {
    const viewport = new TopicMapViewport();
    const graph = new Graph();
    graph.addNode('a', { x: 10, y: 20 });
    const { sigma } = createMockSigma({ getDimensions: () => ({ width: 0, height: 0 }) });

    expect(viewport.graphPointToFramed(sigma, 10, 20)).toBeNull();
    expect(viewport.fitCameraState(sigma, graph)).toBeNull();
  });

  it('carries camera across rebuilds and restricts host start view to the first build', () => {
    const viewport = new TopicMapViewport();
    const graph = new Graph();
    graph.addNode('a', { x: 0, y: 0 });
    const { sigma, camera } = createMockSigma();

    const saved = { x: 0.25, y: 0.75, angle: 0, ratio: 0.5 };
    viewport.frameOnLoad({
      sigma,
      graph,
      selectedId: 'a',
      cameraInput: saved,
      centerOnLoad: false,
    });
    expect(camera.setState).toHaveBeenCalledWith(saved);

    camera.x = 0.4;
    camera.y = 0.6;
    camera.ratio = 0.8;
    viewport.captureCarriedCamera(sigma, true);
    camera.setState.mockClear();

    viewport.frameOnLoad({
      sigma,
      graph,
      selectedId: 'a',
      cameraInput: saved,
      centerOnLoad: false,
    });
    expect(camera.setState).toHaveBeenCalledWith({ x: 0.4, y: 0.6, angle: 0, ratio: 0.8 });
  });

  it('does not carry an animating camera into a rebuild', () => {
    const viewport = new TopicMapViewport();
    const { sigma, camera } = createMockSigma();
    camera.ratio = 0.5;
    camera.isAnimating.mockReturnValue(true);

    viewport.captureCarriedCamera(sigma, true);

    // The next build must frame itself: the captured state was a mid-animation
    // frame, not the reader's view.
    const graph = new Graph();
    graph.addNode('a', { x: 0, y: 0 });
    graph.addNode('b', { x: 100, y: 100 });
    camera.setState.mockClear();
    camera.animate.mockClear();
    viewport.frameOnLoad({
      sigma,
      graph,
      selectedId: null,
      cameraInput: null,
      centerOnLoad: false,
    });
    expect(camera.setState).not.toHaveBeenCalled();
    expect(camera.animate).toHaveBeenCalled();
  });

  it('frames a focused topic from the settled extent instead of the stale default ratio', () => {
    const viewport = new TopicMapViewport();
    const graph = new Graph();
    graph.addNode('a', { x: -1000, y: -500 });
    graph.addNode('b', { x: 1000, y: 500 });
    const { sigma, camera } = createMockSigma();
    camera.ratio = 1; // Sigma's default on mount, not the graph's fit.

    viewport.frameFocused(sigma, graph, 'a');

    const [state] = camera.animate.mock.calls.at(-1) as unknown as [
      { x: number; y: number; ratio: number },
    ];
    // Fitted ratio is ~2.98 for this extent; the focus target must follow the
    // settled fit (0.6 of it) rather than being clamped to the camera's 1.
    expect(state.ratio).toBeGreaterThan(1.5);
    expect(state.x).toBeCloseTo(-1000 / 800);
    expect(state.y).toBeCloseTo(-500 / 600);
  });

  it('re-frames once on settle when the layout moved, but not after the reader takes the camera', () => {
    const viewport = new TopicMapViewport();
    const graph = new Graph();
    graph.addNode('a', { x: -100, y: -50 });
    graph.addNode('b', { x: 100, y: 50 });
    const { sigma, camera } = createMockSigma();

    const container = document.createElement('div');
    Object.defineProperty(container, 'clientWidth', { get: () => 800 });
    Object.defineProperty(container, 'clientHeight', { get: () => 600 });

    const frames: FrameRequestCallback[] = [];
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => {
      frames.push(cb);
      return frames.length;
    });
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe = vi.fn();
        disconnect = vi.fn();
        unobserve = vi.fn();
      }
    );

    viewport.observeStage({
      container,
      getSigma: () => sigma,
      getGraph: () => graph,
      getSelectedId: () => 'a',
      getCameraInput: () => null,
      getCenterOnLoad: () => true,
      getOverlayElements: () => ({}),
    });

    // Initial frame paints the focus from the seed extent.
    frames.shift()!(0);
    // The simulation settles wider after that first paint.
    graph.setNodeAttribute('a', 'x', -1000);
    graph.setNodeAttribute('a', 'y', -500);
    graph.setNodeAttribute('b', 'x', 1000);
    graph.setNodeAttribute('b', 'y', 500);
    camera.animate.mockClear();

    frames.shift()!(0);
    expect(camera.animate).toHaveBeenCalledTimes(1);
    const [settled] = camera.animate.mock.calls[0] as unknown as [
      { x: number; y: number; ratio: number },
    ];
    expect(settled.ratio).toBeGreaterThan(1.5);
    // The focused topic stays centred after the settled re-frame.
    expect(settled.x).toBeCloseTo(-1000 / 800);
    expect(settled.y).toBeCloseTo(-500 / 600);

    // A rebuild has its own kept camera; it must not get a settle re-frame.
    camera.animate.mockClear();
    graph.setNodeAttribute('a', 'x', -1500);
    viewport.observeStage({
      container,
      getSigma: () => sigma,
      getGraph: () => graph,
      getSelectedId: () => 'a',
      getCameraInput: () => null,
      getCenterOnLoad: () => true,
      getOverlayElements: () => ({}),
    });
    expect(frames).toHaveLength(1); // initial frame only, no settle frame

    vi.unstubAllGlobals();
  });

  it('does not re-frame on settle after the reader uses the camera', () => {
    const viewport = new TopicMapViewport();
    const graph = new Graph();
    graph.addNode('a', { x: -100, y: -50 });
    graph.addNode('b', { x: 100, y: 50 });
    const { sigma, camera } = createMockSigma();
    const container = document.createElement('div');
    Object.defineProperty(container, 'clientWidth', { get: () => 800 });
    Object.defineProperty(container, 'clientHeight', { get: () => 600 });

    const frames: FrameRequestCallback[] = [];
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => {
      frames.push(cb);
      return frames.length;
    });
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe = vi.fn();
        disconnect = vi.fn();
        unobserve = vi.fn();
      }
    );

    viewport.observeStage({
      container,
      getSigma: () => sigma,
      getGraph: () => graph,
      getSelectedId: () => 'a',
      getCameraInput: () => null,
      getCenterOnLoad: () => true,
      getOverlayElements: () => ({}),
    });

    frames.shift()!(0); // initial frame
    camera.animate.mockClear();
    container.dispatchEvent(new Event('wheel'));
    frames.shift()!(0); // the settle frame, which must stand down
    expect(camera.animate).not.toHaveBeenCalled();

    vi.unstubAllGlobals();
  });

  it('observes stage resizes, schedules initial frame, and cleans up RAF/ResizeObserver on disposeRebuildObservers', () => {
    const viewport = new TopicMapViewport();
    const graph = new Graph();
    graph.addNode('a', { x: 0, y: 0 });
    graph.addNode('b', { x: 100, y: 100 });
    const { sigma, camera } = createMockSigma();

    const container = document.createElement('div');
    let width = 800;
    let height = 600;
    Object.defineProperty(container, 'clientWidth', { get: () => width });
    Object.defineProperty(container, 'clientHeight', { get: () => height });

    const rafCallbacks: FrameRequestCallback[] = [];
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => {
      rafCallbacks.push(cb);
      return rafCallbacks.length;
    });
    const cancelSpy = vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});

    let resizeCb: ResizeObserverCallback | null = null;
    const disconnect = vi.fn();
    const observe = vi.fn();
    vi.stubGlobal(
      'ResizeObserver',
      class {
        constructor(cb: ResizeObserverCallback) {
          resizeCb = cb;
        }
        observe = observe;
        disconnect = disconnect;
        unobserve = vi.fn();
      }
    );

    viewport.observeStage({
      container,
      getSigma: () => sigma,
      getGraph: () => graph,
      getSelectedId: () => null,
      getCameraInput: () => null,
      getCenterOnLoad: () => false,
      getOverlayElements: () => ({}),
    });

    expect(observe).toHaveBeenCalledWith(container);
    camera.animate.mockClear();

    // Sub-pixel jitter does not re-fit the camera.
    resizeCb!([], {} as ResizeObserver);
    expect(camera.animate).not.toHaveBeenCalled();

    // Genuine dimension change re-fits the graph.
    width = 920;
    resizeCb!([], {} as ResizeObserver);
    expect(camera.animate).toHaveBeenCalledTimes(1);

    // Disposing cancels any queued initial animation frame and disconnects the observer.
    viewport.disposeRebuildObservers();
    expect(cancelSpy).toHaveBeenCalledWith(1);
    expect(disconnect).toHaveBeenCalled();

    vi.unstubAllGlobals();
  });

  it('nudges selected node out from under the HUD (card and controls rail) and cancels on dispose', () => {
    const viewport = new TopicMapViewport();
    const graph = new Graph();
    graph.addNode('a', { x: 0, y: -200 }); // viewport y = 300 - (-200) = 500
    const { sigma, camera } = createMockSigma();

    const container = document.createElement('div');
    container.getBoundingClientRect = () => rect(0, 0, 800, 600);
    const hud = document.createElement('div');
    hud.getBoundingClientRect = () => rect(12, 400, 776, 200);
    const card = document.createElement('div');
    card.className = 'map-card';
    card.getBoundingClientRect = () => rect(200, 420, 400, 160);
    hud.appendChild(card);

    const rafQueue = new Map<number, FrameRequestCallback>();
    let nextId = 1;
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => {
      const id = nextId++;
      rafQueue.set(id, cb);
      return id;
    });
    const cancelSpy = vi.spyOn(window, 'cancelAnimationFrame').mockImplementation((id) => {
      rafQueue.delete(id);
    });

    viewport.revealSelected({
      getSigma: () => sigma,
      getGraph: () => graph,
      getSelectedId: () => 'a',
      getContainerEl: () => container,
      getHudEl: () => hud,
    });
    // Calling a second time cancels the previous queued frame.
    viewport.revealSelected({
      getSigma: () => sigma,
      getGraph: () => graph,
      getSelectedId: () => 'a',
      getContainerEl: () => container,
      getHudEl: () => hud,
    });

    expect(cancelSpy).toHaveBeenCalledWith(1);
    expect(rafQueue.size).toBe(1);
    const frame = [...rafQueue.values()][0];
    rafQueue.clear();
    frame(0);
    expect(camera.animate).toHaveBeenCalledWith(expect.objectContaining({ y: expect.any(Number) }), {
      duration: 300,
    });

    // Pending reveal frame is cancelled when disposed.
    camera.animate.mockClear();
    viewport.revealSelected({
      getSigma: () => sigma,
      getGraph: () => graph,
      getSelectedId: () => 'a',
      getContainerEl: () => container,
      getHudEl: () => hud,
    });
    viewport.dispose();
    expect(rafQueue.size).toBe(0);
  });

  it('reveals a node hidden by the controls rail even when it sits above the card', () => {
    const viewport = new TopicMapViewport();
    const graph = new Graph();
    // viewport y = 450: below the rail's top edge (400) but above the card (420),
    // which is where the phone's focused topic used to disappear.
    graph.addNode('a', { x: 0, y: -150 });
    const { sigma, camera } = createMockSigma();

    const container = document.createElement('div');
    container.getBoundingClientRect = () => rect(0, 0, 800, 600);
    const hud = document.createElement('div');
    hud.getBoundingClientRect = () => rect(12, 400, 776, 200);
    const card = document.createElement('div');
    card.className = 'map-card';
    card.getBoundingClientRect = () => rect(12, 480, 776, 160);
    hud.appendChild(card);

    const frames: FrameRequestCallback[] = [];
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => {
      frames.push(cb);
      return frames.length;
    });

    viewport.revealSelected({
      getSigma: () => sigma,
      getGraph: () => graph,
      getSelectedId: () => 'a',
      getContainerEl: () => container,
      getHudEl: () => hud,
    });
    frames.shift()!(0);

    // Measured against the HUD's top edge, not the card's: the call moves it.
    expect(camera.animate).toHaveBeenCalledWith(expect.objectContaining({ y: expect.any(Number) }), {
      duration: 300,
    });
  });

  it('centres a focused topic in the visible band above the card on a phone', () => {
    const viewport = new TopicMapViewport();
    const graph = new Graph();
    graph.addNode('a', { x: 0, y: 0 });
    graph.addNode('b', { x: 100, y: 100 });
    const { sigma, camera } = createMockSigma();

    vi.stubGlobal(
      'matchMedia',
      vi.fn(() => ({ matches: true }) as MediaQueryList)
    );
    // The legend takes 40px at the top; the card and rail leave 180px covered
    // at the bottom. The visible band's centre is above the stage's centre.
    viewport.frameFocused(sigma, graph, 'a', viewport.fitOccupancy(), { top: 40, bottom: 180 });

    const [state] = camera.animate.mock.calls.at(-1) as unknown as [
      { x: number; y: number; ratio: number },
    ];
    expect(state.y).toBeLessThan(-0.05);
    expect(state.ratio).toBeGreaterThan(0);

    // A wide screen keeps the stage centre, exactly as before.
    vi.stubGlobal(
      'matchMedia',
      vi.fn(() => ({ matches: false }) as MediaQueryList)
    );
    camera.animate.mockClear();
    viewport.frameFocused(sigma, graph, 'a', viewport.fitOccupancy(), { top: 40, bottom: 180 });
    const [wide] = camera.animate.mock.calls.at(-1) as unknown as [{ x: number; y: number }];
    expect(wide.x).toBeCloseTo(0);
    expect(wide.y).toBeCloseTo(0);

    vi.unstubAllGlobals();
  });

  it('supports pseudo-fullscreen fallback and cancels pending refresh timeouts and listeners on dispose', async () => {
    vi.useFakeTimers();
    const origFullscreenEnabled = Object.getOwnPropertyDescriptor(document, 'fullscreenEnabled');
    Object.defineProperty(document, 'fullscreenEnabled', { value: false, configurable: true });
    try {
      const viewport = new TopicMapViewport();
      const stage = document.createElement('div');
      const onFullscreenChange = vi.fn();
      const onRefresh = vi.fn();

      const removeListenerSpy = vi.spyOn(document, 'removeEventListener');

      viewport.attachFullscreen(stage, onFullscreenChange, onRefresh);

      await viewport.toggleFullscreen(false);
      expect(onFullscreenChange).toHaveBeenCalledWith(true);

      // Trigger another refresh timer and dispose before timers flush.
      await viewport.exitFullscreen();
      expect(onFullscreenChange).toHaveBeenCalledWith(false);
      expect(onRefresh).not.toHaveBeenCalled();

      viewport.dispose();
      vi.advanceTimersByTime(100);

      expect(onRefresh).not.toHaveBeenCalled();
      expect(removeListenerSpy).toHaveBeenCalledWith('fullscreenchange', expect.any(Function));

      // Dispatched fullscreenchange after dispose is ignored.
      onFullscreenChange.mockClear();
      document.dispatchEvent(new Event('fullscreenchange'));
      expect(onFullscreenChange).not.toHaveBeenCalled();
    } finally {
      if (origFullscreenEnabled) {
        Object.defineProperty(document, 'fullscreenEnabled', origFullscreenEnabled);
      } else {
        delete (document as unknown as Record<string, unknown>)['fullscreenEnabled'];
      }
    }
  });
});
