import Graph from 'graphology';
import Sigma from 'sigma';

import {
  FIT_OCCUPANCY,
  LABEL_EDGE_ALLOWANCE_FRACTION,
  type MapCamera,
} from './topic-map.helpers';

export interface GraphExtent {
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
}

export interface OverlayElements {
  legend?: HTMLElement;
  hud?: HTMLElement;
}

export interface FrameLoadOptions {
  sigma: Sigma | null;
  graph: Graph | null;
  selectedId: string | null;
  cameraInput: MapCamera | null;
  centerOnLoad: boolean;
  containerEl?: HTMLElement;
  overlays?: OverlayElements;
}

export interface StageObserveOptions {
  container: HTMLElement;
  getSigma: () => Sigma | null;
  getGraph: () => Graph | null;
  getSelectedId: () => string | null;
  getCameraInput: () => MapCamera | null;
  getCenterOnLoad: () => boolean;
  getOverlayElements: () => OverlayElements;
}

export interface RevealSelectedOptions {
  getSigma: () => Sigma | null;
  getGraph: () => Graph | null;
  getSelectedId: () => string | null;
  getContainerEl: () => HTMLElement | undefined;
  getHudEl: () => HTMLElement | undefined;
}

/**
 * Compute the graph's extent in graph coordinates.
 */
export function graphExtent(graph: Graph | null, only?: Set<string>): GraphExtent | null {
  if (!graph || graph.order === 0) return null;
  let minX = Infinity;
  let maxX = -Infinity;
  let minY = Infinity;
  let maxY = -Infinity;
  graph.forEachNode((node, attrs) => {
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
 * Coordinates Sigma camera framing, overlay inset calculations, ResizeObserver
 * updates, and Focus mode (Fullscreen API + CSS fallback) for TopicMapComponent.
 */
export class TopicMapViewport {
  /** The host's start view (`camera` / `centerOnLoad`) applies to the first build only. */
  private framed = false;
  /**
   * The camera of the graph a rebuild replaced, when the host tracks the
   * camera: a refreshed topic set keeps the reader's view rather than jumping.
   */
  private carriedCamera: MapCamera | null = null;
  /** True while Focus mode is the CSS fallback rather than the Fullscreen API. */
  private pseudoFullscreen = false;
  private resizeObserver: ResizeObserver | null = null;
  private initialFrameId: number | null = null;
  private revealFrameId: number | null = null;
  private readonly pendingTimeouts = new Set<number>();

  private stageEl: HTMLElement | null = null;
  private fullscreenListenerAttached = false;
  private onFullscreenChangeCallback: ((fullscreen: boolean) => void) | null = null;
  private onRefreshCallback: (() => void) | null = null;
  private disposed = false;

  private readonly handleFullscreenChange = (): void => {
    this.onFullscreenChangeCallback?.(document.fullscreenElement === this.stageEl);
    this.scheduleRefresh(0);
  };

  /** Attach document fullscreenchange listener for the stage lifecycle. */
  attachFullscreen(
    stageEl: HTMLElement,
    onFullscreenChange: (fullscreen: boolean) => void,
    onRefresh: () => void
  ): void {
    this.stageEl = stageEl;
    this.onFullscreenChangeCallback = onFullscreenChange;
    this.onRefreshCallback = onRefresh;
    if (!this.fullscreenListenerAttached) {
      document.addEventListener('fullscreenchange', this.handleFullscreenChange);
      this.fullscreenListenerAttached = true;
    }
  }

  async toggleFullscreen(isCurrentlyFullscreen: boolean): Promise<void> {
    if (isCurrentlyFullscreen) {
      await this.exitFullscreen();
      return;
    }
    const stage = this.stageEl;
    if (!stage) return;
    if (document.fullscreenEnabled && stage.requestFullscreen) {
      await stage.requestFullscreen();
      return;
    }
    // iPhone Safari has no Fullscreen API for anything but video, so Focus mode
    // used to do nothing there — on the one device that needs the room most.
    // Fall back to pinning the stage over the page (`.is-fullscreen` in CSS).
    this.pseudoFullscreen = true;
    this.onFullscreenChangeCallback?.(true);
    this.scheduleRefresh(0);
  }

  async exitFullscreen(): Promise<void> {
    if (this.pseudoFullscreen) {
      this.pseudoFullscreen = false;
      this.onFullscreenChangeCallback?.(false);
      this.scheduleRefresh(0);
      return;
    }
    if (document.fullscreenElement && document.exitFullscreen) {
      await document.exitFullscreen();
    }
  }

  /** Capture the current camera before a rebuild if the host preserves view state. */
  captureCarriedCamera(sigma: Sigma | null, keepsView: boolean): void {
    this.carriedCamera = sigma && keepsView ? { ...sigma.getCamera().getState() } : null;
  }

  /**
   * Frame the newly mounted Sigma instance, schedule a post-layout re-fit, and
   * attach a ResizeObserver to keep Sigma in sync with container dimensions.
   */
  observeStage(options: StageObserveOptions): void {
    this.disposeRebuildObservers();

    const { container } = options;
    this.initialFrameId = window.requestAnimationFrame(() => {
      this.initialFrameId = null;
      if (this.disposed) return;
      const sigma = options.getSigma();
      if (!sigma) return;
      sigma.refresh();
      this.frameOnLoad({
        sigma,
        graph: options.getGraph(),
        selectedId: options.getSelectedId(),
        cameraInput: options.getCameraInput(),
        centerOnLoad: options.getCenterOnLoad(),
        containerEl: container,
        overlays: options.getOverlayElements(),
      });
      this.framed = true;
      this.carriedCamera = null;
    });

    if (typeof ResizeObserver !== 'undefined') {
      let lastWidth = container.clientWidth;
      let lastHeight = container.clientHeight;
      this.resizeObserver = new ResizeObserver(() => {
        const sigma = options.getSigma();
        if (!sigma || this.disposed) return;
        const w = container.clientWidth;
        const h = container.clientHeight;
        sigma.refresh();
        // A genuine size change invalidates the framing, so re-fit — but only
        // when the size actually moved, so a sub-pixel observer tick does not
        // yank the camera back while the user is exploring.
        if (Math.abs(w - lastWidth) > 1 || Math.abs(h - lastHeight) > 1) {
          lastWidth = w;
          lastHeight = h;
          this.fitGraph(
            sigma,
            options.getGraph(),
            container,
            options.getOverlayElements()
          );
        }
      });
      this.resizeObserver.observe(container);
    }

    // Frame the graph on first paint.
    this.frameOnLoad({
      sigma: options.getSigma(),
      graph: options.getGraph(),
      selectedId: options.getSelectedId(),
      cameraInput: options.getCameraInput(),
      centerOnLoad: options.getCenterOnLoad(),
      containerEl: container,
      overlays: options.getOverlayElements(),
    });
  }

  /** The first view of a built graph: where the host left it, its selection, or all of it. */
  frameOnLoad(options: FrameLoadOptions): void {
    const { sigma, graph, selectedId, cameraInput, centerOnLoad, containerEl, overlays } = options;
    const kept = this.carriedCamera ?? (this.framed ? null : cameraInput);
    if (kept) {
      sigma?.getCamera().setState(kept);
    } else if (!this.framed && centerOnLoad && selectedId && graph?.hasNode(selectedId)) {
      this.centerSelected(sigma, graph, selectedId);
    } else {
      this.fitGraph(sigma, graph, containerEl, overlays);
    }
  }

  /**
   * The fraction of each stage dimension the graph may occupy.
   */
  fitOccupancy(): { x: number; y: number } {
    return {
      x: FIT_OCCUPANCY - LABEL_EDGE_ALLOWANCE_FRACTION,
      y: FIT_OCCUPANCY,
    };
  }

  /**
   * Insets occupied by the overlays. The graph fills the whole canvas, while
   * its initial and fit framing keeps nodes in the open band between the legend
   * and the HUD.
   */
  overlayInsets(containerEl?: HTMLElement, overlays?: OverlayElements): { top: number; bottom: number } {
    const stage = containerEl?.getBoundingClientRect();
    if (!stage) return { top: 0, bottom: 0 };
    const legend = overlays?.legend?.getBoundingClientRect();
    const hud = overlays?.hud?.getBoundingClientRect();
    return {
      top: legend && legend.height ? Math.max(0, legend.bottom - stage.top) : 0,
      bottom: hud && hud.height ? Math.max(0, stage.bottom - hud.top) : 0,
    };
  }

  /**
   * Convert a point in graph coordinates to the camera's framed coordinates,
   * using Sigma's own conversion so the result is exact for any viewport aspect.
   */
  graphPointToFramed(sigma: Sigma | null, x: number, y: number): { x: number; y: number } | null {
    if (!sigma) return null;

    const { width, height } = sigma.getDimensions();
    if (!width || !height) return null;

    const probe = { x: 0.5, y: 0.5, angle: 0, ratio: 1 };
    const viewportPoint = sigma.graphToViewport({ x, y }, { cameraState: probe });
    const framed = sigma.viewportToFramedGraph(viewportPoint, { cameraState: probe });
    if (!Number.isFinite(framed.x) || !Number.isFinite(framed.y)) return null;
    return { x: framed.x, y: framed.y };
  }

  /**
   * The camera state that frames the whole graph, derived from Sigma's own
   * coordinate conversion rather than from hand-rolled maths.
   */
  fitCameraState(
    sigma: Sigma | null,
    graph: Graph | null,
    occupancy: { x: number; y: number } = { x: FIT_OCCUPANCY, y: FIT_OCCUPANCY },
    insets: { top: number; bottom: number } = { top: 0, bottom: 0 }
  ): { x: number; y: number; ratio: number } | null {
    const extent = graphExtent(graph);
    if (!extent || !sigma) return null;

    const { width, height } = sigma.getDimensions();
    if (!width || !height) return null;

    const probe = { x: 0.5, y: 0.5, angle: 0, ratio: 1 };
    const cornerA = sigma.graphToViewport({ x: extent.minX, y: extent.minY }, { cameraState: probe });
    const cornerB = sigma.graphToViewport({ x: extent.maxX, y: extent.maxY }, { cameraState: probe });
    const spanX = Math.abs(cornerB.x - cornerA.x);
    const spanY = Math.abs(cornerB.y - cornerA.y);

    const effectiveX = spanX > 0 ? spanX : Math.max(extent.maxX - extent.minX, 1e-6);
    const effectiveY = spanY > 0 ? spanY : Math.max(extent.maxY - extent.minY, 1e-6);

    const freeHeight = Math.max(1, height - insets.top - insets.bottom);
    const ratio = Math.max(
      effectiveX / (width * occupancy.x),
      effectiveY / (freeHeight * occupancy.y),
      1e-6
    );

    const centre =
      this.graphPointToFramed(sigma, (extent.minX + extent.maxX) / 2, (extent.minY + extent.maxY) / 2) ??
      { x: 0.5, y: 0.5 };
    const dy = (insets.top - insets.bottom) / 2;
    if (dy === 0) return { x: centre.x, y: centre.y, ratio };
    const framed = sigma.viewportToFramedGraph(
      { x: width / 2, y: height / 2 - dy },
      { cameraState: { x: centre.x, y: centre.y, angle: 0, ratio } }
    );
    if (!Number.isFinite(framed.x) || !Number.isFinite(framed.y)) return { x: centre.x, y: centre.y, ratio };
    return { x: framed.x, y: framed.y, ratio };
  }

  /** Frame the whole graph. */
  fitGraph(
    sigma: Sigma | null,
    graph: Graph | null,
    containerEl?: HTMLElement,
    overlays?: OverlayElements
  ): void {
    const state = this.fitCameraState(sigma, graph, this.fitOccupancy(), this.overlayInsets(containerEl, overlays));
    if (!state || !sigma) return;
    sigma.getCamera().animate(state, { duration: 350 });
  }

  /** Bring the selected node to the centre of the stage. */
  centerSelected(sigma: Sigma | null, graph: Graph | null, selectedId: string | null): void {
    if (!selectedId || !graph || !sigma || !graph.hasNode(selectedId)) return;

    const attributes = graph.getNodeAttributes(selectedId);
    const framed = this.graphPointToFramed(
      sigma,
      Number(attributes['x']) || 0,
      Number(attributes['y']) || 0
    );
    if (!framed) return;

    const camera = sigma.getCamera();
    const fit = this.fitCameraState(sigma, graph, this.fitOccupancy());
    const targetRatio = fit ? Math.min(camera.ratio, Math.max(fit.ratio * 0.6, 0.3)) : camera.ratio;

    camera.animate({ x: framed.x, y: framed.y, ratio: targetRatio }, { duration: 350 });
  }

  /** Keep the selected node out from under the selection card. */
  revealSelected(options: RevealSelectedOptions): void {
    if (this.revealFrameId !== null) {
      window.cancelAnimationFrame(this.revealFrameId);
    }
    this.revealFrameId = window.requestAnimationFrame(() => {
      this.revealFrameId = null;
      if (this.disposed) return;
      const sigma = options.getSigma();
      const graph = options.getGraph();
      const id = options.getSelectedId();
      const card = options.getHudEl()?.querySelector('.map-card')?.getBoundingClientRect();
      const stage = options.getContainerEl()?.getBoundingClientRect();
      if (!sigma || !id || !card || !stage || !graph?.hasNode(id) || !card.height) return;
      const attrs = graph.getNodeAttributes(id);
      const point = sigma.graphToViewport({ x: Number(attrs['x']) || 0, y: Number(attrs['y']) || 0 });
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

  zoomIn(sigma: Sigma | null): void {
    const camera = sigma?.getCamera();
    if (camera) {
      void camera.zoomIn({ duration: 200 });
    }
  }

  zoomOut(sigma: Sigma | null): void {
    const camera = sigma?.getCamera();
    if (camera) {
      void camera.zoomOut({ duration: 200 });
    }
  }

  /** Cancel per-build animation frames and ResizeObserver. */
  disposeRebuildObservers(): void {
    if (this.initialFrameId !== null) {
      window.cancelAnimationFrame(this.initialFrameId);
      this.initialFrameId = null;
    }
    if (this.revealFrameId !== null) {
      window.cancelAnimationFrame(this.revealFrameId);
      this.revealFrameId = null;
    }
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
  }

  /** Tear down all listeners, frames, and pending refresh timers on destroy. */
  dispose(): void {
    this.disposed = true;
    this.disposeRebuildObservers();
    for (const id of this.pendingTimeouts) {
      window.clearTimeout(id);
    }
    this.pendingTimeouts.clear();
    if (this.fullscreenListenerAttached) {
      document.removeEventListener('fullscreenchange', this.handleFullscreenChange);
      this.fullscreenListenerAttached = false;
    }
    this.stageEl = null;
    this.onFullscreenChangeCallback = null;
    this.onRefreshCallback = null;
  }

  private scheduleRefresh(delayMs: number): void {
    const id = window.setTimeout(() => {
      this.pendingTimeouts.delete(id);
      if (!this.disposed) {
        this.onRefreshCallback?.();
      }
    }, delayMs);
    this.pendingTimeouts.add(id);
  }
}
